using DbDataBuild.Core;
using DbDataBuild.Models;
using static DbDataBuild.Tests.Unit.SchemaConformanceTests;

namespace DbDataBuild.Tests.Unit;

public class ProjectConfigTests
{
    private const string Full = """
        default_targets: [sqlserver, postgres]
        connections:
          sqlserver: { version: 16 }
          postgres: { version: "17" }
          fabric: {}
        tracking_schema: ddb_meta
        string_semantics:
          case: sensitive
          accent: insensitive
          trailing_space: significant
          collations:
            default:
              duckdb: NOCASE
              sqlserver: Latin1_General_100_CS_AI
            legacy:
              sqlserver: SQL_Latin1_General_CP1_CI_AS
        policy:
          severity:
            approximated: error
            emulated: warning
            unverified: note
            not_covered: error
        """;

    public sealed record Case(string Name, string Yaml, bool SchemaValid, params string[] Codes)
    {
        public override string ToString() => Name;
    }

    public static readonly Case[] Cases =
    [
        new("full example", Full, true),
        new("empty mapping", "{}", true),
        new("only default targets", "default_targets: [fabric]\n", true),
        new("version as integer and string", "connections:\n  sqlserver: { version: 17 }\n  postgres: { version: \"16\" }\n", true),
        new("unknown key", "surprise: 1\n", false, "DDB-104"),
        new("invalid default connection name", "default_targets: [\"9 bad\"]\n", false, "DDB-106"),
        new("empty default targets", "default_targets: []\n", false, "DDB-106"),
        new("duplicate default targets", "default_targets: [fabric, fabric]\n", false, "DDB-106"),
        new("default targets not a list", "default_targets: sqlserver\n", false, "DDB-106"),
        new("connection without an engine", "connections:\n  oracle: { version: 19 }\n", false, "DDB-105"),
        new("unknown target setting", "connections:\n  sqlserver: { edition: enterprise }\n", false, "DDB-104"),
        new("version zero", "connections:\n  sqlserver: { version: 0 }\n", false, "DDB-106"),
        new("version not a number", "connections:\n  sqlserver: { version: sixteen }\n", false, "DDB-106"),
        new("version with a fraction", "connections:\n  sqlserver: { version: 16.5 }\n", false, "DDB-106"),
        new("target settings not a mapping", "connections:\n  sqlserver: 16\n", false, "DDB-106"),
        new("tracking schema with a dash", "tracking_schema: my-schema\n", false, "DDB-106"),
        new("tracking schema starting with a digit", "tracking_schema: 1abc\n", false, "DDB-106"),
        new("bad case value", "string_semantics: { case: maybe }\n", false, "DDB-106"),
        new("bad trailing_space value", "string_semantics: { trailing_space: yes }\n", false, "DDB-106"),
        new("unknown string_semantics key", "string_semantics: { unicode: nfc }\n", false, "DDB-104"),
        new("unknown collation engine", "string_semantics:\n  collations:\n    default: { oracle: X }\n", false, "DDB-104"),
        new("empty collation name", "string_semantics:\n  collations:\n    default: { duckdb: \"\" }\n", false, "DDB-106"),
        new("collation entry not a mapping", "string_semantics:\n  collations:\n    default: NOCASE\n", false, "DDB-106"),
        new("unsupported severity is not configurable", "policy:\n  severity:\n    unsupported: warning\n", false, "DDB-104"),
        new("bad severity value", "policy:\n  severity:\n    approximated: loud\n", false, "DDB-106"),
        new("unknown policy key", "policy:\n  mode: strict\n", false, "DDB-104"),
        new("hook groups", "hook_groups:\n  standard:\n    - {name: grant, event: post_create, script: hooks/grant.sql}\n    - {name: stats, event: post_load, script: {sqlserver: hooks/s.sql, postgres: hooks/p.sql}, effect: data}\n  empty: []\n", true),
        new("hook group not a list", "hook_groups:\n  standard: nope\n", false, "DDB-106"),
        new("hook group entry without event", "hook_groups:\n  standard:\n    - {name: grant, script: hooks/grant.sql}\n", false, "DDB-105"),
        new("hook groups do not nest", "hook_groups:\n  standard:\n    - {use: other}\n", false, "DDB-106"),
        new("hook group name with a space", "hook_groups:\n  \"my group\":\n    - {name: x, event: post_load, script: hooks/x.sql}\n", false, "DDB-106"),
        new("hook group duplicate hook name", "hook_groups:\n  g:\n    - {name: x, event: post_load, script: hooks/x.sql}\n    - {name: x, event: pre_load, script: hooks/y.sql}\n", true, "DDB-102"),
    ];

    public static TheoryData<Case> Data
    {
        get { var d = new TheoryData<Case>(); foreach (var c in Cases) d.Add(c); return d; }
    }

    [Theory, MemberData(nameof(Data))]
    public void Config_schema_and_loader_agree(Case c)
    {
        Assert.Equal(c.SchemaValid, SchemaAccepts(LoadSchema("config"), c.Yaml));
        var diags = new List<Diagnostic>();
        var cfg = ProjectConfigLoader.Load(c.Yaml, "dbdatabuild.yml", diags);
        if (c.Codes.Length == 0)
        {
            Assert.Empty(diags.Select(DiagnosticFormatter.Format));
            Assert.NotNull(cfg);
        }
        else
        {
            foreach (var code in c.Codes) Assert.Contains(diags, d => d.Code == code);
            Assert.Null(cfg);
        }
    }

    [Fact]
    public void Full_example_is_read_completely()
    {
        var cfg = ProjectConfigLoader.Load(Full, "dbdatabuild.yml", [])!;
        Assert.Equal(["sqlserver", "postgres"], cfg.DefaultTargets);
        Assert.Equal(16, cfg.TargetVersions["sqlserver"]);
        Assert.Equal(17, cfg.TargetVersions["postgres"]);
        Assert.False(cfg.TargetVersions.ContainsKey("fabric"));
        Assert.Equal("ddb_meta", cfg.TrackingSchema);
        Assert.Equal(CaseSensitivity.Sensitive, cfg.StringSemantics.Case);
        Assert.Equal(AccentSensitivity.Insensitive, cfg.StringSemantics.Accent);
        Assert.Equal(TrailingSpace.Significant, cfg.StringSemantics.TrailingSpace);
        Assert.Equal("Latin1_General_100_CS_AI", cfg.StringSemantics.Collations["default"]["sqlserver"]);
        Assert.Equal("SQL_Latin1_General_CP1_CI_AS", cfg.StringSemantics.Collations["legacy"]["sqlserver"]);
        Assert.Equal(Severity.Error, cfg.Policy[PolicyKeys.Approximated]);
        Assert.Equal(Severity.Warning, cfg.Policy[PolicyKeys.Emulated]);
        Assert.Equal(Severity.Note, cfg.Policy[PolicyKeys.Unverified]);
        Assert.Equal(Severity.Error, cfg.Policy[PolicyKeys.NotCovered]);
    }

    [Fact]
    public void Absent_keys_take_the_documented_defaults()
    {
        var cfg = ProjectConfigLoader.Load("tracking_schema: x\n", "dbdatabuild.yml", [])!;
        var d = ProjectConfig.Default;
        Assert.Equal(["sqlserver"], cfg.DefaultTargets);
        Assert.Empty(cfg.TargetVersions);
        Assert.Equal(CaseSensitivity.Insensitive, d.StringSemantics.Case);
        Assert.Equal(AccentSensitivity.Sensitive, d.StringSemantics.Accent);
        Assert.Equal(TrailingSpace.Ignored, d.StringSemantics.TrailingSpace);
        Assert.Equal("NOCASE", d.StringSemantics.Collations["default"]["duckdb"]);
        Assert.Equal("dbdatabuild", d.TrackingSchema);
        Assert.Equal(Severity.Warning, d.Policy[PolicyKeys.Approximated]);
        Assert.Equal(Severity.Note, d.Policy[PolicyKeys.Emulated]);
    }

    [Fact]
    public void Empty_file_means_all_defaults_and_is_not_an_error()
    {
        var diags = new List<Diagnostic>();
        Assert.Equal(ProjectConfig.Default, ProjectConfigLoader.Load("", "dbdatabuild.yml", diags));
        Assert.Empty(diags);
    }

    [Fact]
    public void Problems_are_reported_together_with_positions()
    {
        var diags = new List<Diagnostic>();
        var cfg = ProjectConfigLoader.Load("default_targets: [oracle]\ntracking_schema: a-b\nsurprise: 1\n", "dbdatabuild.yml", diags);
        Assert.Null(cfg);
        Assert.Equal(["DDB-104", "DDB-106"], diags.Select(d => d.Code).Distinct().Order());
        Assert.Equal(3, diags.Count);
        Assert.All(diags, d => Assert.True(d.Location.Line >= 1));
    }

    [Fact]
    public void Missing_file_yields_defaults_and_one_warning()
    {
        var dir = TestSupport.NewProjectDir();
        var diags = new List<Diagnostic>();
        Assert.Equal(ProjectConfig.Default, ProjectConfigLoader.LoadFromProject(dir, diags));
        var d = Assert.Single(diags);
        Assert.Equal("DDB-109", d.Code);
        Assert.Equal(Severity.Warning, d.Severity);
    }

    [Fact]
    public void Broken_file_reports_errors_and_falls_back_to_defaults()
    {
        var dir = TestSupport.NewProjectDir();
        File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), "default_targets: [oracle]\n");
        var diags = new List<Diagnostic>();
        Assert.Equal(ProjectConfig.Default, ProjectConfigLoader.LoadFromProject(dir, diags));
        Assert.Contains(diags, d => d.Code == "DDB-106" && d.Location.File == "dbdatabuild.yml");
    }

    [Fact]
    public void Malformed_config_never_throws()
    {
        string[] inputs = [":", "[", "- a", "a: &x 1\nb: *x", "default_targets: {a: b}", "targets: 5", "targets: [a]", "string_semantics: x",
            "string_semantics: {collations: [a]}", "policy: [a]", "policy: {severity: 3}", "---\n---\n", "tracking_schema: [a]"];
        foreach (var input in inputs)
            Assert.Null(Record.Exception(() => ProjectConfigLoader.Load(input, "dbdatabuild.yml", [])));
    }

    [Fact]
    public void Describe_prints_the_effective_settings()
    {
        var text = ProjectConfigLoader.Load(Full, "dbdatabuild.yml", [])!.Describe();
        Assert.Contains("default targets: sqlserver, postgres", text);
        Assert.Contains("case=sensitive, accent=insensitive, trailing_space=significant", text);
        Assert.Contains("postgres 17, sqlserver 16", text);
        Assert.Contains("target versions: not set", ProjectConfig.Default.Describe());
    }
}
