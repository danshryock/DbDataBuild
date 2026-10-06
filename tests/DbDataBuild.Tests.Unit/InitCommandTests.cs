using DbDataBuild.Cli;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

public class InitCommandTests
{
    private static string Project(string config)
    {
        var dir = NewProjectDir();
        File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), config);
        return dir;
    }

    private static (int Exit, string Out, string Err) Run(Func<string, string?>? env, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        return (CliApp.Run(args, o, e, environment: env ?? (_ => null)), o.ToString(), e.ToString());
    }

    [Fact]
    public void By_default_it_prints_the_script_and_touches_nothing()
    {
        var dir = Project("default_connections: [sqlserver]\ntracking_schema: ddb_state\n");
        var before = Snapshot(dir);
        var (exit, output, err) = Run(null, "init", "--project", dir);
        Assert.Equal(0, exit);
        Assert.Equal("", err);
        Assert.Contains("effect: Tracking tables only", output);
        Assert.Contains("connection: sqlserver", output);
        Assert.Contains("login: none (not applying)", output);
        Assert.Contains("CREATE TABLE [ddb_state].[run_log]", output);
        Assert.Contains("--apply", output);
        Assert.Equal(before, Snapshot(dir)); // no statement-log directory either
    }

    [Fact]
    public void The_target_comes_from_the_flag_or_the_single_default_target()
    {
        var dir = Project("default_connections: [sqlserver, postgres]\n");
        var (exit, _, err) = Run(null, "init", "--project", dir);
        Assert.Equal(CliApp.ExitUsage, exit);
        Assert.Contains("--connection is required", err);

        var (pgExit, pgOut, _) = Run(null, "init", "--project", dir, "--connection", "postgres");
        Assert.Equal(0, pgExit);
        Assert.Contains("CREATE TABLE IF NOT EXISTS \"dbdatabuild\".\"run_log\"", pgOut);
    }

    [Fact]
    public void An_unknown_target_is_a_usage_error()
    {
        var (exit, _, err) = Run(null, "init", "--project", Project("default_connections: [sqlserver]\n"), "--connection", "oracle");
        Assert.Equal(CliApp.ExitUsage, exit);
        Assert.Contains("Unknown connection `oracle`", err);
    }

    [Fact]
    public void Apply_without_a_write_login_stops_before_connecting_or_logging()
    {
        var dir = Project("default_connections: [sqlserver]\n");
        // a read login being present must not stand in for the write login
        var env = new Dictionary<string, string?> { ["DBDATABUILD_SQLSERVER_READ"] = "Server=127.0.0.1,1;User Id=r;Password=hunter2" };
        var before = Snapshot(dir);
        var (exit, output, err) = Run(env.GetValueOrDefault, "init", "--project", dir, "--apply");
        Assert.Equal(CliApp.ExitFindings, exit);
        Assert.Contains("DDB-501", err);
        Assert.Contains("DBDATABUILD_SQLSERVER_WRITE", err);
        Assert.DoesNotContain("hunter2", output + err);
        Assert.Equal(before, Snapshot(dir));
    }

    [Fact]
    public void A_broken_config_is_reported_and_nothing_is_printed_as_a_script()
    {
        var (exit, output, err) = Run(null, "init", "--project", Project("tracking_schema: [oops]\n"));
        Assert.Equal(CliApp.ExitFindings, exit);
        Assert.Contains("error DDB-", err);
        Assert.DoesNotContain("CREATE TABLE", output);
    }

    [Fact]
    public void Fabric_scripts_say_they_are_unverified()
    {
        var (exit, output, _) = Run(null, "init", "--project", Project("default_connections: [fabric]\n"));
        Assert.Equal(0, exit);
        Assert.Contains("has not been run on fabric", output);
    }
}
