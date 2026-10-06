using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Models;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>Macros and types (`macros/`): DuckDB statements the tool loads on demand, expanded by DuckDB while it lowers a query, so the SQL of each model is written for the table the macro was given.</summary>
public class MacroTests
{
    private const string Snapshots = """
        -- a live table or a snapshot of it: the rows with the date they are as of; col names the snapshot date column, NULL for a live table
        CREATE MACRO snapshot_at(tbl, col) AS TABLE
          SELECT * EXCLUDE (__none), CASE WHEN col IS NULL THEN current_date ELSE COLUMNS(lambda c: c = coalesce(col, '__none')) END AS as_of_date
          FROM (SELECT *, NULL::DATE AS __none FROM query_table(tbl));

        CREATE MACRO status_totals(tbl, col) AS TABLE
          SELECT as_of_date, status, sum(amount) AS total FROM snapshot_at(tbl, col) GROUP BY as_of_date, status;
        """;

    private static void Write(string dir, string path, string text)
    {
        var full = Path.Combine(dir, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private static string Project(string macros = Snapshots, string? config = null)
    {
        var dir = NewProjectDir();
        Write(dir, "dbdatabuild.yml", config ?? "defaults: {connections: [sqlserver]}\ntracking: none\n");
        Write(dir, "models/src/orders.yml", "name: src.orders\nkind: {type: mapped}\ngrain: [order_id]\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: amount, type: \"DECIMAL(18,2)\", nullable: false}\n  - {name: status, type: \"VARCHAR(20)\", nullable: false}\n");
        Write(dir, "models/src/orders_snap.yml", "name: src.orders_snap\nkind: {type: mapped}\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: amount, type: \"DECIMAL(18,2)\", nullable: false}\n  - {name: status, type: \"VARCHAR(20)\", nullable: false}\n  - {name: snap_date, type: DATE, nullable: false}\n");
        const string cols = "grain: [as_of_date, status]\ncolumns:\n  - {name: as_of_date, type: DATE, nullable: false}\n  - {name: status, type: \"VARCHAR(20)\", nullable: false}\n  - {name: total, type: \"DECIMAL(38,2)\", nullable: false}\n";
        Write(dir, "models/marts/live_totals.yml", "name: marts.live_totals\nkind: {type: full}\n" + cols);
        Write(dir, "models/marts/live_totals.sql", "SELECT * FROM status_totals('src.orders', NULL)\n");
        Write(dir, "models/marts/snap_totals.yml", "name: marts.snap_totals\nkind: {type: full}\n" + cols);
        Write(dir, "models/marts/snap_totals.sql", "SELECT * FROM status_totals('src.orders_snap', 'snap_date')\n");
        if (macros.Length > 0) Write(dir, "macros/snapshots.sql", macros);
        return dir;
    }

    private static (int Exit, string Out, string Err) Cli(params string[] args)
    {
        var o = new StringWriter(); var e = new StringWriter();
        return (CliApp.Run(args, o, e, environment: _ => null), o.ToString(), e.ToString());
    }

    private static string Lowered(string dir, string model) => File.ReadAllText(Path.Combine(dir, "rendered", "lowered", model, "lowered.sql"));

    // ---- the files ----

    [Fact]
    public void A_file_holds_several_statements_and_a_semicolon_in_text_or_a_comment_does_not_end_one()
    {
        var statements = MacroLibrary.SplitStatements("""
            CREATE MACRO a(x) AS x || ';';   -- not the end; here
            /* nor ; this */
            CREATE MACRO b(x) AS $$a;b$$ || x;
            CREATE TYPE mood AS ENUM ('a;b', 'c')
            """);
        Assert.Equal(["CREATE MACRO a(x) AS x || ';'", "CREATE MACRO b(x) AS $$a;b$$ || x", "CREATE TYPE mood AS ENUM ('a;b', 'c')"], statements.Select(s => s.Sql.Replace("   -- not the end; here", "").Trim()));
        Assert.Equal([1, 3, 4], statements.Select(s => s.Line));
    }

    [Fact]
    public void Only_macros_and_types_may_be_in_a_macro_file()
    {
        var diags = new List<Diagnostic>();
        var found = MacroLibrary.Parse("macros/x.sql", "CREATE MACRO a(x) AS x; CREATE TABLE t(a INT); INSERT INTO t VALUES (1); INSTALL httpfs; CREATE TYPE m AS ENUM ('a')", diags);
        Assert.Equal(["a", "m"], found.Select(f => f.Name));
        Assert.Equal(3, diags.Count);
        Assert.All(diags, d => Assert.Contains("is not allowed in a macro file", d.Found));
    }

    [Fact]
    public void A_macro_defined_twice_and_macros_that_call_each_other_in_a_circle_are_refused()
    {
        var dir = Project("CREATE MACRO a(x) AS x;\nCREATE MACRO a(y) AS y + 1;\nCREATE MACRO p(x) AS q(x);\nCREATE MACRO q(x) AS p(x);\nCREATE MACRO s(x) AS s(x);");
        var text = string.Join("\n", ProjectValidator.Validate(dir).Diagnostics.Select(d => d.Found));
        Assert.Contains("`a` is defined twice", text);
        Assert.Contains("circle: p -> q -> p", text);
        Assert.Contains("circle: s -> s", text);
    }

    [Fact]
    public void A_binding_gets_only_the_macros_its_queries_reach_with_callees_first_and_types_before_both()
    {
        var library = MacroLibrary.Load(Project("""
            CREATE MACRO outer_one(x) AS inner_one(x) + 1;
            CREATE MACRO inner_one(x) AS x * 2;
            CREATE MACRO unrelated(x) AS nowhere.missing(x);
            CREATE TYPE mood AS ENUM ('sad', 'happy');
            CREATE MACRO feeling(x) AS CAST(x AS mood);
            """), []);
        var prelude = library.PreludeFor(["SELECT outer_one(1), feeling('sad')"]);
        Assert.Equal(["CREATE TYPE mood AS ENUM ('sad', 'happy')"], prelude.Types);
        Assert.Equal(["CREATE MACRO inner_one(x) AS x * 2", "CREATE MACRO outer_one(x) AS inner_one(x) + 1", "CREATE MACRO feeling(x) AS CAST(x AS mood)"], prelude.Macros);   // `unrelated` is not created
        Assert.True(library.PreludeFor(["SELECT 1"]).IsEmpty);
    }

    // ---- lowering ----

    [Fact]
    public void A_macro_expands_to_the_query_for_the_table_it_was_given_with_nothing_left_for_the_engine_to_decide()
    {
        var dir = Project();
        var render = Cli("render", "--project", dir, "--write");
        Assert.True(render.Exit == 0, render.Out + render.Err);
        var snap = Lowered(dir, "marts.snap_totals");
        Assert.Contains("SELECT snap_date AS as_of_date, status, sum(amount) AS total\nFROM src.orders_snap\nGROUP BY snap_date, status", snap);
        var live = Lowered(dir, "marts.live_totals");
        Assert.Contains("SELECT current_date() AS as_of_date, status, sum(amount) AS total\nFROM src.orders\nGROUP BY status", live);
        foreach (var text in new[] { snap, live }) { Assert.DoesNotContain("CASE", text); Assert.DoesNotContain("UNION", text); }
        Assert.Contains("FROM src.orders_snap", File.ReadAllText(Path.Combine(dir, "rendered", "sqlserver", "marts.snap_totals", "load.default.sql")));
    }

    [Fact]
    public void Dependencies_show_the_tables_the_macro_reads_and_the_macros_themselves()
    {
        var dir = Project();
        var ctx = ProjectContext.Load(dir);
        Assert.Equal(["src.orders_snap"], ctx.BaseTablesOf(ctx.Project.Sources.Single(s => s.Definition.Name == "marts.snap_totals"), "SELECT * FROM status_totals('src.orders_snap', 'snap_date')"));
        Assert.Equal(["src.orders_snap", "status_totals()"], ctx.Graph.Reads("marts.snap_totals").Order(StringComparer.Ordinal));
        Assert.Equal(["snapshot_at()"], ctx.Graph.Reads("status_totals()"));
        Assert.Contains("src.orders", ctx.Graph.Ancestors("marts.live_totals").Keys);
        Assert.Contains("snapshot_at()", ctx.Graph.Ancestors("marts.live_totals").Keys);
        Assert.Contains("marts.live_totals", ctx.Graph.Descendants("snapshot_at()").Keys);

        var graph = System.Text.Json.Nodes.JsonNode.Parse(Cli("graph", "--project", dir, "--format", "json", "marts.snap_totals").Out)!["data"]!;
        Assert.Equal(["marts.snap_totals:model", "snapshot_at():macro", "src.orders_snap:source", "status_totals():macro"], graph["nodes"]!.AsArray().Select(n => $"{(string?)n!["name"]}:{(string?)n["kind"]}").Order(StringComparer.Ordinal));

        var metadata = System.Text.Json.Nodes.JsonNode.Parse(Cli("metadata", "--project", dir, "--format", "json").Out)!["data"]!["models"]!.AsArray();
        var upstream = metadata.Single(m => (string?)m!["name"] == "marts.snap_totals")!["upstream"]!.AsArray().Select(u => $"{(string?)u!["name"]}:{(string?)u["kind"]}");
        Assert.Contains("status_totals():macro", upstream);
        Assert.Contains("src.orders_snap:source", upstream);
    }

    [Fact]
    public void A_change_to_a_macro_changes_the_definition_hash_of_every_model_that_reaches_it_and_no_other()
    {
        var dir = Project();
        var before = ProjectContext.Load(dir);
        var snap = before.DefinitionHashOf("SELECT * FROM status_totals('src.orders_snap', 'snap_date')");
        var plain = before.DefinitionHashOf("SELECT 1 AS n");
        File.WriteAllText(Path.Combine(dir, "macros", "snapshots.sql"), Snapshots.Replace("sum(amount)", "max(amount)"));
        var after = ProjectContext.Load(dir);
        Assert.NotEqual(snap, after.DefinitionHashOf("SELECT * FROM status_totals('src.orders_snap', 'snap_date')"));
        Assert.Equal(plain, after.DefinitionHashOf("SELECT 1 AS n"));
    }

    [Fact]
    public void A_macro_that_cannot_be_created_is_a_warning_and_only_a_model_that_reaches_it_fails()
    {
        var dir = Project(Snapshots + "\nCREATE MACRO broken(x) AS TABLE SELECT * FROM nowhere.orders WHERE a = x;");
        var validate = Cli("validate", "--project", dir);
        Assert.Contains("`broken` could not be created", validate.Err);
        Assert.Contains("warning DDB-106", validate.Err);
        Assert.DoesNotContain("snap_totals cannot be lowered", validate.Err);            // the models that do not reach it are fine
        Write(dir, "models/marts/uses_broken.yml", "name: marts.uses_broken\nkind: {type: full}\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n");
        Write(dir, "models/marts/uses_broken.sql", "SELECT order_id FROM broken(1)\n");
        Assert.Contains("uses_broken cannot be lowered", Cli("validate", "--project", dir).Err);
    }

    [Fact]
    public void A_macro_call_with_lowering_switched_off_is_refused_and_a_wrong_column_name_is_a_bind_error()
    {
        var off = Project(config: "defaults: {connections: [sqlserver]}\ntracking: none\nlowering: { enabled: false }\n");
        Assert.Contains("a macro is expanded by DuckDB while the query is lowered, which is switched off", Cli("validate", "--project", off).Err);

        var dir = Project();
        Write(dir, "models/marts/snap_totals.sql", "SELECT * FROM status_totals('src.orders_snap', 'nope')\n");
        Assert.Contains("snap_totals cannot be lowered", Cli("validate", "--project", dir).Err);
    }

    [Fact]
    public void A_model_that_calls_a_macro_can_be_defined_sampled_and_tested()
    {
        var dir = Project();
        var define = Cli("define", "--project", dir, "--check");
        Assert.True(define.Exit == 0, define.Out + define.Err);
        var sample = Cli("sample", "--project", dir, "--format", "json", "marts.snap_totals");
        Assert.True(sample.Exit == 0, sample.Out + sample.Err);
        Assert.Contains("as_of_date", sample.Out);
    }
}
