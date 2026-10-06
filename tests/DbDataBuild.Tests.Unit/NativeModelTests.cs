using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Models;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>Native models: a table computed by an engine-native query, bound by queries from its declared columns and inlined as a derived table.</summary>
public class NativeModelTests
{
    private const string Cols = "grain: [n]\ncolumns:\n  - {name: n, type: INTEGER, nullable: false}\n";

    private static void Write(string dir, string path, string text)
    {
        var full = Path.Combine(dir, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private static string Project(string config = "defaults: {connections: [sqlserver]}\nparameters:\n  top: { type: INTEGER, value: 5 }\n")
    {
        var dir = NewProjectDir();
        Write(dir, "dbdatabuild.yml", config);
        return dir;
    }

    private static (int Exit, string Out, string Err) Cli(params string[] args)
    {
        var o = new StringWriter(); var e = new StringWriter();
        return (CliApp.Run(args, o, e, environment: _ => null), o.ToString(), e.ToString());
    }

    private static string Diags(string dir) => string.Join("\n", ProjectValidator.Validate(dir).Diagnostics.Select(DiagnosticFormatter.Format));

    [Fact]
    public void The_text_is_inline_or_in_a_file_beside_the_definition_and_declares_columns_like_a_mapped_model()
    {
        var dir = Project();
        Write(dir, "models/src/a.yml", "name: src.a\nkind:\n  type: native\n  query: SELECT 1 AS n\n" + Cols);
        Write(dir, "models/src/b.yml", "name: src.b\nkind: {type: native}\n" + Cols);
        Write(dir, "models/src/b.native.sql", "SELECT 2 AS n\n");
        var result = ProjectValidator.Validate(dir);
        Assert.False(result.HasErrors, Diags(dir));
        Assert.Equal(["src.a", "src.b"], result.NativeModels.Select(n => n.Name).Order());
        Assert.Equal("SELECT 2 AS n", result.NativeModels.Single(n => n.Name == "src.b").Native!.Text);
        Assert.Empty(result.Descriptors);                                                           // not a mapped model, and not a model
        Assert.Equal(2, result.AllDescriptors.Count(d => d.IsNative));                              // but a queryable table
        Assert.Equal(["n"], result.NativeModels[0].Columns.Select(c => c.Name));
    }

    [Theory]
    [InlineData("kind:\n  type: native\n  query: SELECT 1 AS n\n  access: command\n", "the connection must allow it")]
    [InlineData("kind:\n  type: native\n  query: SELECT 1 AS n\n  access: maybe\n", "not `maybe`")]
    [InlineData("kind: {type: native}\n", "needs its text")]
    [InlineData("kind:\n  type: native\n  query: EXEC dbo.p\n", "not a single SELECT")]
    [InlineData("kind:\n  type: native\n  query: SELECT 1 AS n; DROP TABLE t\n", "not a single SELECT")]
    [InlineData("kind:\n  type: native\n  query: WITH x AS (SELECT 1 AS n) SELECT n FROM x\n", "WITH cannot sit inside the derived table")]
    [InlineData("kind:\n  type: native\n  query: SELECT ${project.nothing} AS n\n", "has no value")]
    [InlineData("kind:\n  type: native\n  query: SELECT ${origin.x} AS n\n", "is not a parameter a native text can use")]
    [InlineData("kind:\n  type: native\n  query: SELECT 1 AS n\nconnections=: [sqlserver, postgres]\n", "exactly one connection")]
    public void A_native_model_that_cannot_work_says_why(string kind, string expected)
    {
        var dir = Project();
        Write(dir, "models/src/a.yml", "name: src.a\n" + kind + Cols);
        var text = Diags(dir);
        Assert.Contains(expected, text);
    }

    [Fact]
    public void Text_in_both_places_or_text_with_no_native_model_is_refused()
    {
        var dir = Project();
        Write(dir, "models/src/a.yml", "name: src.a\nkind:\n  type: native\n  query: SELECT 1 AS n\n" + Cols);
        Write(dir, "models/src/a.native.sql", "SELECT 2 AS n\n");
        Assert.Contains("not both", Diags(dir));
        Write(dir, "models/src/c.native.sql", "SELECT 3 AS n\n");
        Assert.Contains("is native text, but `models/src/c.yml` is not a native model", Diags(dir));
    }

    [Fact]
    public void A_model_that_reads_a_native_select_gets_its_text_as_a_derived_table_with_the_native_models_own_placeholders()
    {
        var dir = Project();
        Write(dir, "models/src/nums.yml", "name: src.nums\nkind:\n  type: native\n  query: SELECT CAST(value AS int) AS n FROM STRING_SPLIT('1,2', ',') WHERE 1 < ${project.top}\n" + Cols);
        Write(dir, "models/marts/m.yml", "name: marts.m\nkind: {type: full}\n" + Cols);
        Write(dir, "models/marts/m.sql", "SELECT x.n FROM src.nums AS x WHERE x.n > 0\n");
        var (exit, _, err) = Cli("render", "--write", "--project", dir);
        Assert.True(exit == 0, err);
        var script = File.ReadAllText(Path.Combine(dir, "rendered/sqlserver/marts.m/load.default.sql"));
        Assert.Contains("FROM (SELECT CAST(value AS int) AS n FROM STRING_SPLIT('1,2', ',') WHERE 1 < @p_native_src_nums__project_top) AS [nums]", script);   // the table's own name is its alias (the lowered query is unaliased), the text is the engine's own
        Assert.Contains("@p_native_src_nums__project_top (INTEGER, parameter)", script);
        Assert.Equal(0, Cli("render", "--check", "--project", dir).Exit);
    }

    [Fact]
    public void A_native_model_is_a_table_on_its_connection_only()
    {
        var dir = Project("defaults: {connections: [sqlserver]}\nconnections:\n  pg: { engine: postgres }\n");
        Write(dir, "models/src/nums.yml", "name: src.nums\nkind:\n  type: native\n  query: SELECT 1 AS n\nconnections=: [pg]\n" + Cols);
        Write(dir, "models/marts/m.yml", "name: marts.m\nkind: {type: full}\n" + Cols);
        Write(dir, "models/marts/m.sql", "SELECT n FROM src.nums\n");
        var (exit, _, err) = Cli("validate", "--project", dir);
        Assert.NotEqual(0, exit);
        Assert.Contains("DDB-231", err);
    }

    [Fact]
    public void A_native_model_can_be_copied_locally_or_to_another_connection_and_never_alone()
    {
        var dir = Project("defaults: {connections: [sqlserver]}\nconnections:\n  pg: { engine: postgres }\n");
        Write(dir, "models/src/nums.yml", "name: src.nums\nkind:\n  type: native\n  query: SELECT 1 AS n\nconnections=: [pg]\n" + Cols);
        Write(dir, "models/pgcopy/snap.yml", "name: pgcopy.snap\nkind:\n  type: copy\n  from: src.nums\nconnections=: [pg]\n");   // local
        Write(dir, "models/dst/snap.yml", "name: dst.snap\nkind:\n  type: copy\n  from: src.nums\n");                              // remote (the default connection)
        var result = ProjectValidator.Validate(dir);
        Assert.False(result.HasErrors, Diags(dir));
        Assert.True(result.Sources.Single(s => s.Definition.Name == "pgcopy.snap").Definition.LocalCopy);
        Assert.False(result.Sources.Single(s => s.Definition.Name == "dst.snap").Definition.LocalCopy);
    }

    [Fact]
    public void A_view_over_a_native_select_renders_and_the_plan_is_where_parameters_are_refused()
    {
        var dir = Project();
        Write(dir, "models/src/plain.yml", "name: src.plain\nkind:\n  type: native\n  query: SELECT 1 AS n\n" + Cols);
        Write(dir, "models/src/bound.yml", "name: src.bound\nkind:\n  type: native\n  query: SELECT 1 AS n WHERE 1 < ${project.top}\n" + Cols);
        Write(dir, "models/marts/v1.yml", "name: marts.v1\nkind: {type: view}\n" + Cols);
        Write(dir, "models/marts/v1.sql", "SELECT n FROM src.plain\n");
        Write(dir, "models/marts/v2.yml", "name: marts.v2\nkind: {type: view}\n" + Cols);
        Write(dir, "models/marts/v2.sql", "SELECT n FROM src.bound\n");
        Assert.Equal(0, Cli("render", "--write", "--project", dir).Exit);                   // rendering views has no load script; the plan is where the refusal is (conformance tests plan and apply)
    }
}
