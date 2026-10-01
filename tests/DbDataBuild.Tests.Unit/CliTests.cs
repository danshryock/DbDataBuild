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
