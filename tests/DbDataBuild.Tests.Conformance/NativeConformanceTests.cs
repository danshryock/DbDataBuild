using System.Text.RegularExpressions;
using DbDataBuild.Cli;
using DbDataBuild.Execution;
using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>Native models (a query the engine runs: a table function, an engine-native select) on the real engines: inlined into a table and a view, bound parameters, a local copy, and a copy to another engine.</summary>
[Trait("Group", "apply")]
public partial class NativeConformanceTests
{
    public static TheoryData<string> Engines => new() { "sqlserver", "postgres" };

    private static string NumsText(string engine) => engine == "postgres"
        ? "SELECT CAST(x AS integer) AS n FROM unnest(string_to_array(${project.list}, ',')) AS x"
        : "SELECT CAST(value AS int) AS n FROM STRING_SPLIT(${project.list}, ',')";

    private static string FixedText(string engine) => "SELECT 1 AS n UNION ALL SELECT 2";

    private static string Config(string name) =>
        $"defaults: {{connections: [{name}]}}\ntracking: {{ connection: {name} }}\nparameters:\n  list: \"1,2,3,4\"\n" +
        (name == "postgres" ? "string_semantics:\n  case: sensitive\n  trailing_space: significant\n  collations:\n    default: { duckdb: NFC, postgres: C }\n" : "");

    private const string Cols = "grain: [n]\ncolumns:\n  - {name: n, type: INTEGER, nullable: false}\n";

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task A_native_select_is_inlined_with_bound_parameters_into_a_table_and_a_view_and_landed_by_a_local_copy(string name)
    {
        var engine = EngineEnv.Require(name);
        await engine.StartAsync();
        await using var _ = engine;
        var dir = Path.Combine(Path.GetTempPath(), "ddb-native-" + Guid.NewGuid().ToString("N"));
        string? Env(string v) => v == LoginSettings.VariableName(name, Login.Read) || v == LoginSettings.VariableName(name, Login.Write) ? engine.ConnectionString : null;
        (int Exit, string Out, string Err) Cli(params string[] args)
        {
            var o = new StringWriter(); var e = new StringWriter();
            var exit = CliApp.Run([args[0], "--project", dir, .. args.Skip(1)], o, e, environment: Env);
            return (exit, o.ToString(), e.ToString());
        }
        void Write(string rel, string text) { var p = Path.Combine(dir, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, text); }
        void Ok((int Exit, string Out, string Err) r, string what) => Assert.True(r.Exit == 0, $"{what} failed:\n{r.Out}\n{r.Err}");
        string PlanOf(string output) => Path.Combine(dir, Regex.Match(output, @"plan:\s+(\S+\.plan\.yml)").Groups[1].Value);
        try
        {
            Write("dbdatabuild.yml", Config(name));
            Write("models/src/nums.yml", "name: src.nums\nkind:\n  type: native\n  query: |\n    " + NumsText(name) + "\n" + Cols);             // the text inline in the YAML
            Write("models/src/fixed.yml", "name: src.fixed\nkind: {type: native}\n" + Cols);                                                       // the text in a file beside it
            Write("models/src/fixed.native.sql", FixedText(name) + "\n");
            Write("models/marts/big.yml", "name: marts.big\nkind: {type: full}\n" + Cols);
            Write("models/marts/big.sql", "SELECT n FROM src.nums WHERE n > 1\n");
            Write("models/marts/v.yml", "name: marts.v\nkind: {type: view}\n" + Cols);
            Write("models/marts/v.sql", "SELECT n FROM src.fixed\n");
            Write("models/marts/snapshot.yml", "name: marts.snapshot\nkind:\n  type: copy\n  from: src.nums\n");                           // a local copy: the native select, landed on its own connection

            Ok(Cli("init", "--connection", name, "--apply"), "init");
            Ok(Cli("render", "--write"), "render");
            var script = File.ReadAllText(Path.Combine(dir, "rendered", name, "marts.big", "load.default.sql"));
            Assert.Contains("@p_native_src_nums__project_list", script);                                         // the native's own placeholder, bound at run time
            Assert.DoesNotContain("1,2,3,4", script);
            var plan = Cli("plan", "--connection", name);
            Ok(plan, "plan");
            Assert.DoesNotContain("type: transfer", File.ReadAllText(PlanOf(plan.Out)));                         // a local copy moves nothing across connections
            Ok(Cli("apply", PlanOf(plan.Out)), "apply");
            Assert.Equal(["2", "3", "4"], await engine.RowsAsync("SELECT CAST(n AS VARCHAR(10)) FROM marts.big"));
            Assert.Equal(["1", "2"], await engine.RowsAsync("SELECT CAST(n AS VARCHAR(10)) FROM marts.v"));      // a view over a native select: its text is in the view
            Assert.Equal(["1", "2", "3", "4"], await engine.RowsAsync("SELECT CAST(n AS VARCHAR(10)) FROM marts.snapshot"));
            Assert.Equal(4, int.Parse((await engine.RowsAsync("SELECT COUNT(*) FROM marts.snapshot")).Single()));

            // a different value is a new plan, the same rendered files
            Write("dbdatabuild.yml", Config(name).Replace("1,2,3,4", "5,6"));
            Ok(Cli("render", "--check"), "render --check");
            var again = Cli("plan", "--connection", name);
            Ok(again, "second plan");
            Ok(Cli("apply", PlanOf(again.Out)), "second apply");
            Assert.Equal(["5", "6"], await engine.RowsAsync("SELECT CAST(n AS VARCHAR(10)) FROM marts.big"));
            Assert.Equal(["5", "6"], await engine.RowsAsync("SELECT CAST(n AS VARCHAR(10)) FROM marts.snapshot"));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [SkippableFact]
    public async Task A_native_select_on_one_engine_can_be_copied_to_another()
    {
        var origin = EngineEnv.Require("postgres");
        var destination = EngineEnv.Require("sqlserver");
        await origin.StartAsync(); await destination.StartAsync();
        await using var _o = origin; await using var _d = destination;
        var dir = Path.Combine(Path.GetTempPath(), "ddb-native-copy-" + Guid.NewGuid().ToString("N"));
        string? Env(string v) => v == "DBDATABUILD_POSTGRES_READ" ? origin.ConnectionString : v is "DBDATABUILD_SQLSERVER_READ" or "DBDATABUILD_SQLSERVER_WRITE" ? destination.ConnectionString : null;
        (int Exit, string Out, string Err) Cli(params string[] args)
        {
            var o = new StringWriter(); var e = new StringWriter();
            var exit = CliApp.Run([args[0], "--project", dir, .. args.Skip(1)], o, e, environment: Env);
            return (exit, o.ToString(), e.ToString());
        }
        void Write(string rel, string text) { var p = Path.Combine(dir, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, text); }
        void Ok((int Exit, string Out, string Err) r, string what) => Assert.True(r.Exit == 0, $"{what} failed:\n{r.Out}\n{r.Err}");
        try
        {
            Write("dbdatabuild.yml", "defaults: {connections: [sqlserver]}\ntracking: { connection: sqlserver }\nparameters:\n  list: \"7,8,9\"\n");
            Write("models/src/nums.yml", "name: src.nums\nkind:\n  type: native\n  query: " + NumsText("postgres").Replace("${project.list}", "${project.list}") + "\nconnections=: [postgres]\n" + Cols);
            Write("models/dst/nums.yml", "name: dst.nums\nkind:\n  type: copy\n  from: src.nums\n");
            Ok(Cli("init", "--connection", "sqlserver", "--apply"), "init");
            Ok(Cli("render", "--write"), "render");
            var plan = Cli("plan", "--connection", "sqlserver");
            Ok(plan, "plan");
            var file = Path.Combine(dir, Regex.Match(plan.Out, @"plan:\s+(\S+\.plan\.yml)").Groups[1].Value);
            var text = File.ReadAllText(file);
            Assert.Contains("type: transfer", text);
            Assert.Contains("p_native_src_nums__project_list", text);                                            // the native's parameter travels in the transfer step, bound on the origin
            Assert.DoesNotContain("7,8,9", text.Replace("value: \"7,8,9\"", ""));                                // never in the read text
            Ok(Cli("apply", file), "apply");
            Assert.Equal(["7", "8", "9"], await destination.RowsAsync("SELECT CAST(n AS VARCHAR(10)) FROM dst.nums"));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
