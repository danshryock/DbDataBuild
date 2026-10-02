using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataBuild.Cli;
using Json.Schema;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>`--format json`: one document on standard output, the same exit codes as text mode, structured diagnostics, and a data payload per command.</summary>
public class JsonOutputTests
{
    private static readonly JsonSchema Schema = SchemaConformanceTests.LoadSchema("output");

    private const string Orders = "name: staging.orders\ngrain: [order_id]\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: amount, type: \"DECIMAL(14, 2)\"}\n";
    private const string FctYaml = "name: marts.fct_orders\nkind: {type: incremental_by_unique_key, unique_key: [order_id]}\ngrain: [order_id]\ntargets: [sqlserver]\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: amount, type: \"DECIMAL(14, 2)\"}\nindexes:\n  - {name: ix_amount, columns: [amount]}\n";

    private static string Project()
    {
        var dir = NewProjectDir();
        Directory.CreateDirectory(Path.Combine(dir, "sources/staging"));
        File.WriteAllText(Path.Combine(dir, "sources/staging/orders.yml"), Orders);
        File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), "default_targets: [sqlserver]\nstring_semantics:\n  case: sensitive\n  trailing_space: ignored\n  collations:\n    default: { duckdb: NFC, sqlserver: Latin1_General_100_CS_AS }\n");
        File.WriteAllText(Path.Combine(dir, "models/marts/fct_orders.yml"), FctYaml);
        File.WriteAllText(Path.Combine(dir, "models/marts/fct_orders.sql"), "SELECT o.order_id, o.amount FROM staging.orders o\n");
        return dir;
    }

    private static (int Exit, JsonNode Doc, string Raw, string Err) Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var exit = CliApp.Run([.. args, "--format", "json"], o, e, environment: _ => null);
        var raw = o.ToString();
        var doc = JsonNode.Parse(raw)!;                                    // exactly one JSON document, nothing else on standard output
        var result = Schema.Evaluate(JsonSerializer.SerializeToNode(doc), new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(result.IsValid, $"{args[0]} output does not satisfy output.schema.json:\n{raw}");
        Assert.Equal(exit, doc["exit_code"]!.GetValue<int>());
        Assert.Equal(exit == 0, doc["ok"]!.GetValue<bool>());
        Assert.Equal(args[0], doc["command"]!.GetValue<string>());
        return (exit, doc, raw, e.ToString());
    }

    [Fact]
    public void Validate_reports_counts_and_the_full_metadata_of_every_model()
    {
        var dir = Project();
        var (exit, doc, _, err) = Run("validate", "--project", dir);
        Assert.Equal(0, exit);
        Assert.Equal("", err);                                              // diagnostics are in the document, not on standard error
        var data = doc["data"]!;
        Assert.Equal(1, data["counts"]!["models"]!.GetValue<int>());
        var model = data["models"]![0]!;
        Assert.Equal("dbdatabuild.model/1", model["schema"]!.GetValue<string>());
        Assert.Equal("marts.fct_orders", model["name"]!.GetValue<string>());
        Assert.Matches("^[0-9a-f]{64}$", model["definition_hash"]!.GetValue<string>());
        var amount = model["columns"]!.AsArray().Single(c => c!["name"]!.GetValue<string>() == "amount")!;
        Assert.Equal("DECIMAL(14, 2)", amount["logical_type"]!.GetValue<string>());
        Assert.Equal("decimal(14, 2)", amount["native"]!["sqlserver"]!["type"]!.GetValue<string>());
        Assert.Null(amount["native"]!["postgres"]);                         // only the model's own targets are described
        Assert.Equal("staging.orders", model["upstream"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("source", model["upstream"]![0]!["kind"]!.GetValue<string>());
        Assert.Equal("ix_amount", model["indexes"]![0]!["name"]!.GetValue<string>());
        Assert.Matches("^[0-9a-f]{64}$", model["expected_shape_hash"]!["sqlserver"]!.GetValue<string>());
        Assert.Equal("delete_insert_by_key", model["loads"]![0]!["strategy"]!.GetValue<string>());
        Assert.NotNull(data["project"]!["matrix_version"]);
    }

    [Fact]
    public void Findings_are_structured_diagnostics_with_the_same_exit_code_as_text_mode()
    {
        var dir = Project();
        File.WriteAllText(Path.Combine(dir, "models/marts/fct_orders.sql"), "SELECT o.order_id, o.amount FROM staging.orders o GROUP BY ALL\n");
        var (exit, doc, _, err) = Run("validate", "--project", dir);
        Assert.Equal(1, exit);
        Assert.Equal("", err);
        var d = doc["diagnostics"]!.AsArray().First(x => x!["severity"]!.GetValue<string>() == "error")!;
        Assert.Matches("^DDB-\\d{3}$", d["code"]!.GetValue<string>());
        Assert.Equal("models/marts/fct_orders.sql", d["location"]!["file"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(d["fix"]!.GetValue<string>()));
        Assert.Contains(doc["messages"]!.AsArray(), m => m!.GetValue<string>().StartsWith("FAILED"));
    }

    [Fact]
    public void Matrix_explain_loads_render_init_and_metadata_each_carry_their_data()
    {
        var dir = Project();
        var matrix = Run("matrix");
        Assert.True(matrix.Doc["data"]!["constructs"]!.AsArray().Count > 10);
        Assert.NotNull(matrix.Doc["data"]!["constructs"]![0]!["targets"]!["sqlserver"]!["status"]);

        var explain = Run("explain", "DDB-430");
        Assert.Equal("DDB-430", explain.Doc["data"]!["code"]!["code"]!.GetValue<string>());
        Assert.Equal(2, Run("explain", "DDB-999").Exit);                    // usage errors are JSON too

        var loads = Run("loads", "--project", dir);
        Assert.Equal("delete_insert_by_key", loads.Doc["data"]!["operations"]![0]!["strategy"]!.GetValue<string>());

        var render = Run("render", "--project", dir);
        Assert.Equal(0, render.Exit);
        Assert.Equal(["rendered/sqlserver/marts.fct_orders/load.default.sql", "rendered/sqlserver/marts.fct_orders/manifest.yml"], render.Doc["data"]!["files"]!.AsArray().Select(f => f!["path"]!.GetValue<string>()));
        var write = Run("render", "--project", dir, "--write");
        Assert.Equal(0, write.Exit);
        Assert.NotEmpty(write.Doc["data"]!["wrote"]!.AsArray());
        var check = Run("render", "--project", dir, "--check");
        Assert.Empty(check.Doc["data"]!["out_of_date"]!.AsArray());
        File.AppendAllText(Path.Combine(dir, "rendered/sqlserver/marts.fct_orders/load.default.sql"), "-- edited\n");
        var stale = Run("render", "--project", dir, "--check");
        Assert.Equal(1, stale.Exit);
        Assert.Contains("DDB-424", stale.Doc["diagnostics"]!.AsArray().Select(x => x!["code"]!.GetValue<string>()));

        var init = Run("init", "--project", dir);
        Assert.False(init.Doc["data"]!["applied"]!.GetValue<bool>());
        Assert.Equal(9, init.Doc["data"]!["statements"]!.AsArray().Count);
        Assert.Contains("CREATE TABLE", init.Doc["data"]!["statements"]![1]!["text"]!.GetValue<string>());

        var meta = Run("metadata", "--project", dir);
        Assert.Equal("marts.fct_orders", meta.Doc["data"]!["models"]![0]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void Missing_logins_and_usage_errors_still_produce_one_valid_document()
    {
        var dir = Project();
        var plan = Run("plan", "--project", dir);
        Assert.Equal(1, plan.Exit);
        Assert.Equal("DDB-501", plan.Doc["diagnostics"]![0]!["code"]!.GetValue<string>());
        Assert.DoesNotContain("password", plan.Raw, StringComparison.OrdinalIgnoreCase);
        var ack = Run("ack", "banana", "x", "--reason", "r", "--project", dir);
        Assert.Equal(2, ack.Exit);
        Assert.Contains(ack.Doc["errors"]!.AsArray(), m => m!.GetValue<string>().Contains("Unknown acknowledgement"));
    }

    [Fact]
    public void The_default_format_is_unchanged_text()
    {
        var o = new StringWriter();
        Assert.Equal(0, CliApp.Run(["matrix"], o, new StringWriter()));
        Assert.StartsWith("dbdatabuild matrix", o.ToString());
        var bad = new StringWriter();
        Assert.NotEqual(0, CliApp.Run(["matrix", "--format", "yaml"], new StringWriter(), bad));
    }

    [Fact]
    public void An_internal_failure_in_json_mode_is_a_document_not_a_stack_trace()
    {
        var o = new StringWriter();
        var exit = CliApp.Guarded(["check", "--format", "json"], new StringWriter(), () => throw new InvalidOperationException("boom: secret-row-value"), o);
        Assert.Equal(CliApp.ExitInternal, exit);
        var doc = JsonNode.Parse(o.ToString())!;
        Assert.Equal("DDB-900", doc["diagnostics"]![0]!["code"]!.GetValue<string>());
        Assert.DoesNotContain("secret-row-value", o.ToString());
        Assert.DoesNotContain("   at ", o.ToString());
        Assert.True(Schema.Evaluate(JsonSerializer.SerializeToNode(doc)).IsValid);
    }
}
