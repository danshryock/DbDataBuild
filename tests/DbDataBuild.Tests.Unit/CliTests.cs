using DbDataBuild.Cli;
using DbDataBuild.Core;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

public class CliTests
{
    private static (int Exit, string Out, string Err) Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        return (CliApp.Run(args, o, e), o.ToString(), e.ToString());
    }

    [Fact]
    public void Every_command_declares_an_effect_class_shown_in_help()
    {
        Assert.Equal(CommandSpecs.All.Count, CommandSpecs.All.Select(c => c.Name).Distinct().Count());
        foreach (var spec in CommandSpecs.All)
        {
            var (exit, help, _) = Run(spec.Name, "--help");
            Assert.Equal(0, exit);
            Assert.Contains(spec.Effect.Describe(), help);
        }
    }

    [Fact]
    public void Command_surface_matches_the_design_document()
    {
        string[] expected = ["validate", "render", "loads", "matrix", "explain", "define", "check", "plan", "report", "apply", "run", "ack", "init"];
        Assert.Equal(expected.OrderBy(x => x), CommandSpecs.All.Select(c => c.Name).OrderBy(x => x));
    }

    [Fact]
    public void Target_commands_that_are_not_built_yet_refuse_and_do_nothing()
    {
        foreach (var spec in CommandSpecs.All.Where(c => !c.Implemented))
        {
            var (exit, _, err) = Run(spec.Name);
            Assert.Equal(CliApp.ExitNotImplemented, exit);
            Assert.Contains("not implemented yet", err);
        }
    }

    [Fact]
    public void Validate_prints_header_and_reports_ok()
    {
        var dir = NewProjectDir();
        File.WriteAllText(Path.Combine(dir, "models/marts/fct_orders.yml"), ValidModel);
        File.WriteAllText(Path.Combine(dir, "models/marts/fct_orders.sql"), "SELECT 1");
        var (exit, output, err) = Run("validate", "--project", dir);
        Assert.Equal(0, exit);
        Assert.Contains("effect: Offline only", output);
        Assert.Contains("OK: 1 model(s) valid.", output);
        Assert.Equal("", err);
    }

    [Fact]
    public void Validate_fails_with_formatted_diagnostics()
    {
        var dir = NewProjectDir();
        File.WriteAllText(Path.Combine(dir, "models/marts/fct_orders.yml"), ValidModel.Replace("  unique_key: [order_id]\n", ""));
        File.WriteAllText(Path.Combine(dir, "models/marts/fct_orders.sql"), "SELECT 1");
        var (exit, _, err) = Run("validate", "--project", dir);
        Assert.Equal(1, exit);
        Assert.Contains("error DDB-214  models/marts/fct_orders.yml:3", err);
    }

    private static string ProjectWith(string sql, string targets = "[sqlserver, fabric]")
    {
        var dir = NewProjectDir();
        File.WriteAllText(Path.Combine(dir, "models/marts/fct_orders.yml"), ValidModel.Replace("targets: [sqlserver, fabric]", $"targets: {targets}"));
        File.WriteAllText(Path.Combine(dir, "models/marts/fct_orders.sql"), sql);
        return dir;
    }

    [Fact]
    public void Validate_runs_the_matrix_linter_per_declared_target_and_fails_on_unsupported_constructs()
    {
        var dir = ProjectWith("SELECT a, COUNT(*) AS n FROM t GROUP BY 1");
        var before = Snapshot(dir);
        var (exit, output, err) = Run("validate", "--project", dir);
        Assert.Equal(CliApp.ExitFindings, exit);
        Assert.Contains("error DDB-301  models/marts/fct_orders.sql", err);
        Assert.Contains("on sqlserver", err);
        Assert.Contains("warning DDB-304", err);                 // fabric: unverified, not an error
        Assert.Contains("FAILED: 1 error(s)", output);
        Assert.Equal(before, Snapshot(dir)); // read-only
    }

    [Fact]
    public void Validate_passes_with_warnings_and_notes_when_nothing_is_unsupported()
    {
        var dir = ProjectWith("SELECT a / b AS x FROM t ORDER BY a", "[sqlserver]");
        var (exit, output, err) = Run("validate", "--project", dir);
        Assert.Equal(CliApp.ExitOk, exit);
        Assert.Contains("warning DDB-302", err);
        Assert.Contains("note DDB-303", err);
        Assert.Matches(@"OK: 1 model\(s\) valid\. 1 warning\(s\), 1 note\(s\)\.", output);
    }

    [Fact]
    public void Validate_reports_unparseable_sql()
    {
        var (exit, _, err) = Run("validate", "--project", ProjectWith("SELEC FROM FROM ("));
        Assert.Equal(CliApp.ExitFindings, exit);
        Assert.Contains("DDB-306", err);
    }

    [Fact]
    public void Matrix_command_prints_every_row_with_notes_and_the_coverage_rule()
    {
        var (exit, output, _) = Run("matrix");
        Assert.Equal(CliApp.ExitOk, exit);
        Assert.Contains("effect: Offline only", output);
        Assert.Contains("syntax.group_by_ordinal", output);
        Assert.Contains("approximated", output);
        Assert.Contains("regexp", output);
        Assert.Contains("(>= 17)", output);
        Assert.Contains("DDB-305", output);
    }

    [Fact]
    public void Explain_prints_long_form_and_rejects_unknown_codes()
    {
        var (exit, output, _) = Run("explain", "ddb-214");
        Assert.Equal(0, exit);
        Assert.Contains("Missing unique_key", output);

        var (exit2, _, err) = Run("explain", "DDB-000");
        Assert.Equal(CliApp.ExitUsage, exit2);
        Assert.Contains("Unknown diagnostic code", err);
    }

    [Fact]
    public void Internal_failures_are_reported_as_tool_bugs_without_stack_traces()
    {
        var err = new StringWriter();
        var exit = CliApp.Guarded(["validate"], err, () => throw new InvalidOperationException("boom: secret-row-value"));
        Assert.Equal(CliApp.ExitInternal, exit);
        Assert.Contains("DDB-900", err.ToString());
        Assert.Contains("InvalidOperationException", err.ToString());
        Assert.DoesNotContain("   at ", err.ToString());
        Assert.DoesNotContain("secret-row-value", err.ToString()); // exception messages may carry data (DESIGN.md 14.2)
    }
}
