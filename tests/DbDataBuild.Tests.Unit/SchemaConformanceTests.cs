using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataBuild.Core;
using DbDataBuild.Models;
using Json.Schema;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using static DbDataBuild.Tests.Unit.PolyglotBindingTests;

namespace DbDataBuild.Tests.Unit;

/// <summary>
/// The published JSON Schemas (schemas/) and the C# loaders must agree. One corpus runs through both: structural problems
/// must fail in the schema and the loader, and semantic-only problems (name against path, grain against unique_key, column
/// references) must pass the schema and fail the loader. The schema sees the YAML the way an editor does (typed scalars).
/// </summary>
public class SchemaConformanceTests
{
    internal static JsonSchema LoadSchema(string name) =>
        JsonSchema.FromFile(Path.Combine(RepoRoot(), "schemas", name + ".schema.json"));

    /// <summary>YAML as a YAML-aware editor reads it: unquoted true/false/numbers are typed.</summary>
    internal static JsonNode? EditorView(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        return stream.Documents.Count == 0 ? null : Convert(stream.Documents[0].RootNode);
    }

    private static JsonNode? Convert(YamlNode node) => node switch
    {
        YamlMappingNode m => new JsonObject(m.Children.Select(c => KeyValuePair.Create(((YamlScalarNode)c.Key).Value!, Convert(c.Value)))),
        YamlSequenceNode q => new JsonArray(q.Children.Select(Convert).ToArray()),
        YamlScalarNode { Style: ScalarStyle.Plain } s => Typed(s.Value ?? ""),
        YamlScalarNode s => JsonValue.Create(s.Value ?? ""),
        _ => null,
    };

    /// <summary>YAML 1.2 core schema typing of a plain scalar, as language servers apply it.</summary>
    private static JsonNode? Typed(string v) => v switch
    {
        "" or "~" or "null" or "Null" or "NULL" => null,
        "true" or "True" or "TRUE" => JsonValue.Create(true),
        "false" or "False" or "FALSE" => JsonValue.Create(false),
        _ when long.TryParse(v, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var l) => JsonValue.Create(l),
        _ when double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) && v.Any(char.IsDigit) && !v.Contains(' ') => JsonValue.Create(d),
        _ => JsonValue.Create(v),
    };

    internal static bool SchemaAccepts(JsonSchema schema, string yaml) =>
        schema.Evaluate(EditorView(yaml), new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid;

    public sealed record Fixture(string Name, string Yaml, bool SchemaValid, string[] Codes, string ExpectedName = "marts.fct_orders")
    {
        public override string ToString() => Name;
    }

    private const string Cols = "columns:\n  - name: a\n    type: INT\n";

    private static Fixture Ok(string name, string yaml) => new(name, yaml, true, []);
    private static Fixture Bad(string name, string yaml, params string[] codes) => new(name, yaml, false, codes);
    private static Fixture Semantic(string name, string yaml, params string[] codes) => new(name, yaml, true, codes);

    public static readonly Fixture[] ModelFixtures =
    [
        Ok("design example", TestSupport.ValidModel),
        Ok("view minimal", "name: marts.fct_orders\nkind:\n  type: view\n" + Cols),
        Ok("full, nullable unquoted and quoted, collation",
            "name: marts.fct_orders\nkind: {type: full}\ncolumns:\n  - {name: a, type: VARCHAR(20), nullable: false, collation: default}\n  - {name: b, type: INT, nullable: \"true\"}\n"),
        Ok("time range with lookback", "name: marts.fct_orders\nkind: {type: incremental_by_time_range, time_column: d, lookback: 3 days}\ngrain: [d]\ncolumns:\n  - {name: d, type: DATE}\n"),
        Ok("composite key", "name: marts.fct_orders\nkind: {type: incremental_by_unique_key, unique_key: [a, b]}\ngrain: [b, a]\ncolumns:\n  - {name: a, type: INT}\n  - {name: b, type: INT}\n"),
        Ok("all three targets", "name: marts.fct_orders\nkind: {type: full}\ntargets: [sqlserver, fabric, postgres]\n" + Cols),
        Ok("renames", "name: marts.fct_orders\nkind: {type: full}\n" + Cols + "renames:\n  - from: old\n    to: a\n"),
        Ok("loads mapping", "name: marts.fct_orders\nkind: {type: full}\n" + Cols + "loads:\n  daily:\n    default: true\n"),

        Bad("unknown top key", "name: marts.fct_orders\nkind: {type: full}\n" + Cols + "surprise: 1\n", "DDB-104"),
        Bad("missing name", "kind: {type: full}\n" + Cols, "DDB-105"),
        Bad("missing kind", "name: marts.fct_orders\n" + Cols, "DDB-105"),
        Bad("missing columns", "name: marts.fct_orders\nkind: {type: full}\n", "DDB-105"),
        Bad("unknown kind", "name: marts.fct_orders\nkind: {type: nope}\n" + Cols, "DDB-106"),
        Bad("kind as a string", "name: marts.fct_orders\nkind: full\n" + Cols, "DDB-106"),
        Bad("view with unique_key", "name: marts.fct_orders\nkind: {type: view, unique_key: [a]}\n" + Cols, "DDB-104"),
        Bad("unique_key missing", "name: marts.fct_orders\nkind: {type: incremental_by_unique_key}\ngrain: [a]\n" + Cols, "DDB-214"),
        Bad("unique_key empty", "name: marts.fct_orders\nkind: {type: incremental_by_unique_key, unique_key: []}\ngrain: [a]\n" + Cols, "DDB-214"),
        Bad("time_column missing", "name: marts.fct_orders\nkind: {type: incremental_by_time_range}\ngrain: [a]\n" + Cols, "DDB-215"),
        Bad("grain missing on incremental", "name: marts.fct_orders\nkind: {type: incremental_by_unique_key, unique_key: [a]}\n" + Cols, "DDB-216"),
        Bad("grain empty on incremental", "name: marts.fct_orders\nkind: {type: incremental_by_unique_key, unique_key: [a]}\ngrain: []\n" + Cols, "DDB-216"),
        Bad("grain not a list", "name: marts.fct_orders\nkind: {type: full}\ngrain: a\n" + Cols, "DDB-106"),
        Bad("unknown target", "name: marts.fct_orders\nkind: {type: full}\ntargets: [oracle]\n" + Cols, "DDB-106"),
        Bad("empty targets", "name: marts.fct_orders\nkind: {type: full}\ntargets: []\n" + Cols, "DDB-106"),
        Bad("duplicate targets", "name: marts.fct_orders\nkind: {type: full}\ntargets: [sqlserver, sqlserver]\n" + Cols, "DDB-106"),
        Bad("empty columns", "name: marts.fct_orders\nkind: {type: full}\ncolumns: []\n", "DDB-106"),
        Bad("columns not a list", "name: marts.fct_orders\nkind: {type: full}\ncolumns: a\n", "DDB-106"),
        Bad("column without type", "name: marts.fct_orders\nkind: {type: full}\ncolumns:\n  - name: a\n", "DDB-105"),
        Bad("column unknown key", "name: marts.fct_orders\nkind: {type: full}\ncolumns:\n  - {name: a, type: INT, default: 0}\n", "DDB-104"),
        Bad("column empty name", "name: marts.fct_orders\nkind: {type: full}\ncolumns:\n  - {name: \"\", type: INT}\n", "DDB-106"),
        Bad("nullable maybe", "name: marts.fct_orders\nkind: {type: full}\ncolumns:\n  - {name: a, type: INT, nullable: maybe}\n", "DDB-106"),
        Bad("rename missing to", "name: marts.fct_orders\nkind: {type: full}\n" + Cols + "renames:\n  - from: old\n", "DDB-105"),
        Bad("rename unknown key", "name: marts.fct_orders\nkind: {type: full}\n" + Cols + "renames:\n  - {from: old, to: a, why: x}\n", "DDB-104"),
        Bad("loads as a scalar", "name: marts.fct_orders\nkind: {type: full}\n" + Cols + "loads: 5\n", "DDB-106"),

        // Semantic rules: the schema cannot express them, so it accepts and the loader rejects.
        Semantic("name does not match path", "name: marts.other\nkind: {type: full}\n" + Cols, "DDB-107"),
        Semantic("grain differs from unique_key", TestSupport.ValidModel.Replace("grain: [order_id]", "grain: [customer_id]"), "DDB-216"),
        Semantic("grain names an undeclared column", TestSupport.ValidModel.Replace("grain: [order_id]", "grain: [order_id, ghost]"), "DDB-217"),
        Semantic("duplicate column names", "name: marts.fct_orders\nkind: {type: full}\ncolumns:\n  - {name: a, type: INT}\n  - {name: A, type: INT}\n", "DDB-102"),
        Semantic("rename to an undeclared column", "name: marts.fct_orders\nkind: {type: full}\n" + Cols + "renames:\n  - {from: old, to: ghost}\n", "DDB-217"),
    ];

    public static TheoryData<Fixture> ModelData
    {
        get { var d = new TheoryData<Fixture>(); foreach (var f in ModelFixtures) d.Add(f); return d; }
    }

    [Theory, MemberData(nameof(ModelData))]
    public void Model_schema_and_loader_agree(Fixture f)
    {
        Assert.Equal(f.SchemaValid, SchemaAccepts(LoadSchema("model"), f.Yaml));

        var (def, diags) = TestSupport.Load(f.Yaml, f.ExpectedName);
        if (f.Codes.Length == 0)
        {
            Assert.Empty(diags.Select(DiagnosticFormatter.Format));
            Assert.NotNull(def);
        }
        else
        {
            foreach (var code in f.Codes) Assert.Contains(diags, d => d.Code == code);
            Assert.Null(def);
        }
    }

    [Fact]
    public void Corpus_covers_every_structural_code_the_model_loader_can_raise()
    {
        var raised = ModelFixtures.SelectMany(f => f.Codes).ToHashSet();
        foreach (var code in new[] { "DDB-102", "DDB-104", "DDB-105", "DDB-106", "DDB-107", "DDB-214", "DDB-215", "DDB-216", "DDB-217" })
            Assert.Contains(code, raised);
    }

    [Fact]
    public void Editor_view_of_a_valid_example_is_typed_as_an_editor_would_see_it()
    {
        var node = EditorView("nullable: false\nversion: 16\n")!;
        Assert.Equal(JsonValueKind.False, node["nullable"]!.GetValueKind());
        Assert.Equal(JsonValueKind.Number, node["version"]!.GetValueKind());
    }

    [Fact]
    public void Schemas_are_valid_json_schema_documents_with_descriptions()
    {
        foreach (var name in new[] { "model", "config", "answers", "source" })
        {
            var schema = LoadSchema(name);
            Assert.NotNull(schema);
            var text = File.ReadAllText(Path.Combine(RepoRoot(), "schemas", name + ".schema.json"));
            using var doc = JsonDocument.Parse(text);
            Assert.Equal("https://json-schema.org/draft/2020-12/schema", doc.RootElement.GetProperty("$schema").GetString());
            Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("description").GetString()));
        }
    }
}

public class EditorAssociationTests
{
    [Fact]
    public void Editor_settings_map_model_and_config_globs_to_schemas_that_exist()
    {
        var root = PolyglotBindingTests.RepoRoot();
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root, ".vscode", "settings.json")));
        var map = doc.RootElement.GetProperty("yaml.schemas").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == System.Text.Json.JsonValueKind.String ? p.Value.GetString()! : "");
        Assert.Equal("models/**/*.yml", map["schemas/model.schema.json"]);
        Assert.Equal(DbDataBuild.Core.ProductInfo.ConfigFile, map["schemas/config.schema.json"]);
        Assert.Equal("sources/**/*.yml", map["schemas/source.schema.json"]);
        Assert.Contains("answers.yml", doc.RootElement.GetProperty("yaml.schemas").GetProperty("schemas/answers.schema.json").EnumerateArray().Select(e => e.GetString()));
        foreach (var schema in map.Keys) Assert.True(File.Exists(Path.Combine(root, schema)), schema);
    }
}

public class DesignDocExampleTests
{
    private static string YamlBlockAfter(string marker)
    {
        var text = File.ReadAllText(Path.Combine(PolyglotBindingTests.RepoRoot(), "DESIGN.md"));
        var from = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(from >= 0, $"marker not found: {marker}");
        var start = text.IndexOf("```yaml\n", from, StringComparison.Ordinal) + "```yaml\n".Length;
        return text[start..text.IndexOf("```", start, StringComparison.Ordinal)];
    }

    [Fact]
    public void Model_example_in_section_6_is_valid_for_the_schema_and_the_loader()
    {
        var yaml = YamlBlockAfter("## 6. Model definitions");
        Assert.Contains("name: marts.fct_orders", yaml);
        Assert.True(SchemaConformanceTests.SchemaAccepts(SchemaConformanceTests.LoadSchema("model"), yaml));
        var (def, diags) = TestSupport.Load(yaml);
        Assert.Empty(diags.Select(DbDataBuild.Core.DiagnosticFormatter.Format));
        Assert.NotNull(def);
    }

    [Fact]
    public void Answers_example_in_section_10_1_is_valid_for_the_schema_and_the_loader()
    {
        var yaml = YamlBlockAfter("### 10.1");
        Assert.True(SchemaConformanceTests.SchemaAccepts(SchemaConformanceTests.LoadSchema("answers"), yaml));
        var diags = new List<DbDataBuild.Core.Diagnostic>();
        var file = DbDataBuild.Models.AnswerFileLoader.Load(yaml, "answers.yml", diags);
        Assert.Empty(diags.Select(DbDataBuild.Core.DiagnosticFormatter.Format));
        Assert.Equal(3, file!.Answers.Count);
        Assert.Equal("customer_name", file.Answers.Single(a => a.Choice == "rename_to").Value);
        Assert.Single(file.Answers, a => a.AcceptInferred);
    }

    [Fact]
    public void Config_example_in_section_9_4_is_valid_for_the_schema_and_the_loader()
    {
        var yaml = YamlBlockAfter("### 9.4 Project configuration");
        Assert.True(SchemaConformanceTests.SchemaAccepts(SchemaConformanceTests.LoadSchema("config"), yaml));
        var diags = new List<DbDataBuild.Core.Diagnostic>();
        var cfg = DbDataBuild.Models.ProjectConfigLoader.Load(yaml, "dbdatabuild.yml", diags);
        Assert.Empty(diags.Select(DbDataBuild.Core.DiagnosticFormatter.Format));
        Assert.Equal(DbDataBuild.Models.ProjectConfig.Default.StringSemantics, cfg!.StringSemantics with { Collations = DbDataBuild.Models.ProjectConfig.Default.StringSemantics.Collations });
        Assert.Equal(17, cfg.TargetVersions["postgres"]);
    }
}
