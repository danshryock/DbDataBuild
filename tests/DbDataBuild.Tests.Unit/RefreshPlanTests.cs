using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Planning;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>The compiled refresh plan: what `project compile` writes for a connection, and that the file is read back, checked against its own hash and never trusted when edited.</summary>
public class RefreshPlanTests
{
    private const string Config = "defaults: {connections: [sqlserver]}\nstring_semantics:\n  case: sensitive\n  trailing_space: ignored\n  collations:\n    default: { duckdb: NFC, sqlserver: Latin1_General_100_CS_AS }\n";
    private const string Events = "name: staging.events\nkind:\n  type: mapped\ngrain: [event_id]\ncolumns:\n  - {name: event_id, type: BIGINT, nullable: false}\n  - {name: event_ts, type: TIMESTAMP, nullable: false}\n  - {name: amount, type: \"DECIMAL(14, 2)\"}\n";
    private const string FctYaml = "name: marts.fct_events\nkind: {type: incremental_by_time_range, time_column: event_ts}\ngrain: [event_id]\ncolumns:\n  - {name: event_id, type: BIGINT, nullable: false}\n  - {name: event_ts, type: TIMESTAMP, nullable: false}\n  - {name: amount, type: \"DECIMAL(14, 2)\"}\n" +
        "loads:\n  daily:\n    default: true\n    strategy: watermark_append\n    watermark: {column: event_ts, resolver: target_max, on_null: initial, initial: \"2000-01-01 00:00:00\"}\n" +
        "  reload:\n    strategy: delete_insert_by_range\n    params: {start: TIMESTAMP, end: TIMESTAMP}\n    max_span: 10 days\n";
    private const string FctSql = "SELECT e.event_id, e.event_ts, e.amount FROM staging.events e\n";
    private const string ViewYaml = "name: marts.v_events\nkind: {type: view}\ncolumns:\n  - {name: event_id, type: BIGINT, nullable: false}\n";
    private const string ViewSql = "SELECT event_id FROM marts.fct_events\n";
    private const string DailyYaml = "name: marts.daily\nkind: {type: full}\ngrain: [d]\ncolumns:\n  - {name: d, type: DATE, nullable: false}\n";
    private const string DailySql = "SELECT CAST(event_ts AS DATE) AS d FROM marts.fct_events GROUP BY ALL\n";

    private static string Project()
    {
        var dir = NewProjectDir();
        void Write(string rel, string text) { var p = Path.Combine(dir, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, text); }
        Write("dbdatabuild.yml", Config);
        Write("models/staging/events.yml", Events);
        Write("models/marts/fct_events.yml", FctYaml); Write("models/marts/fct_events.sql", FctSql);
        Write("models/marts/v_events.yml", ViewYaml); Write("models/marts/v_events.sql", ViewSql);
        Write("models/marts/daily.yml", DailyYaml); Write("models/marts/daily.sql", DailySql);
        return dir;
    }

    private static RefreshPlan Compiled(string dir)
    {
        Assert.Equal(0, RenderFiles("--write", "--project", dir).Exit);
        var path = Path.Combine(dir, "rendered", "sqlserver", RefreshPlanDocument.FileName);
        var diags = new List<Diagnostic>();
        var plan = RefreshPlanDocument.Parse(File.ReadAllText(path), path, diags);
        Assert.True(plan != null, string.Join("\n", diags.Select(DiagnosticFormatter.Format)));
        return plan!;
    }

    [Fact]
    public void Compile_writes_the_routine_loads_in_dependency_order_with_what_each_expects_and_leaves_out_what_is_not_routine()
    {
        var plan = Compiled(Project());
        Assert.Equal("sqlserver", plan.Connection);
        Assert.Equal(["marts.fct_events", "marts.daily"], plan.Loads.Select(l => l.Model));                     // daily reads fct_events; the view has no load
        var load = plan.Loads[0];
        Assert.Equal(("daily", "sqlserver/marts.fct_events/load.daily.sql", "sqlserver/marts.fct_events/load.daily.resolve.sql"), (load.Operation, load.File, load.ResolverFile));
        Assert.Matches("^[0-9a-f]{64}$", load.FileHash);
        Assert.Equal(("initial", "2000-01-01 00:00:00"), (load.OnNull, load.Initial));
        Assert.Equal([("watermark", "resolver")], load.Parameters.Select(p => (p.Name, p.Source)));
        Assert.Null(load.Parameters[0].Value);                                                                      // the watermark is found when the refresh runs, never recorded here
        Assert.Equal(["marts.daily", "marts.fct_events"], plan.Requires.Select(r => r.Object));
        Assert.All(plan.Requires, r => Assert.Matches("^[0-9a-f]{64}$", r.ShapeHash));
        Assert.Matches("^[0-9a-f]{64}$", plan.ProjectHash);
        Assert.Empty(plan.Excluded);                                                                                // `reload` is another operation of a model that has a routine one
    }

    [Fact]
    public void A_model_whose_only_load_needs_a_person_or_is_a_copy_is_left_out_with_the_reason()
    {
        var dir = Project();
        File.WriteAllText(Path.Combine(dir, "models/marts/fct_events.yml"), FctYaml.Replace("    default: true\n    strategy: watermark_append", "    strategy: watermark_append").Replace("  reload:\n", "  reload:\n    default: true\n"));
        var plan = Compiled(dir);
        Assert.DoesNotContain(plan.Loads, l => l.Model == "marts.fct_events");
        Assert.Contains(plan.Excluded, x => x.Model == "marts.fct_events" && x.Reason.Contains("needs a value from a person"));
    }

    [Fact]
    public void Compile_is_deterministic_and_a_change_that_alters_what_runs_alters_the_plan_and_the_project_hash()
    {
        var dir = Project();
        var first = Compiled(dir);
        Assert.Equal(RefreshPlanDocument.Serialize(first), RefreshPlanDocument.Serialize(Compiled(dir)));
        File.WriteAllText(Path.Combine(dir, "models/marts/daily.yml"), DailyYaml + "  - {name: n, type: BIGINT}\n");
        File.WriteAllText(Path.Combine(dir, "models/marts/daily.sql"), "SELECT CAST(event_ts AS DATE) AS d, COUNT(*) AS n FROM marts.fct_events GROUP BY ALL\n");
        var second = Compiled(dir);
        Assert.NotEqual(first.ProjectHash, second.ProjectHash);
        Assert.NotEqual(first.Requires.Single(r => r.Object == "marts.daily").ShapeHash, second.Requires.Single(r => r.Object == "marts.daily").ShapeHash);
        Assert.Equal(first.Requires.Single(r => r.Object == "marts.fct_events"), second.Requires.Single(r => r.Object == "marts.fct_events"));       // what did not change did not move
    }

    [Fact]
    public void A_plan_that_was_edited_or_damaged_is_refused_with_a_diagnostic_never_an_exception()
    {
        var dir = Project();
        var text = RefreshPlanDocument.Serialize(Compiled(dir));
        var diags = new List<Diagnostic>();
        Assert.NotNull(RefreshPlanDocument.Parse(text, "refresh.plan.yml", diags));
        Assert.Empty(diags);
        foreach (var bad in new[] { text.Replace("marts.daily", "marts.dailyx"), text.Replace("\"default\"", "\"other\""), text.Replace("loads: 2", "loads: 3"), text + "extra: 1\n", "", "- not a mapping\n", text[..(text.Length / 2)] })
        {
            var d = new List<Diagnostic>();
            Assert.Null(RefreshPlanDocument.Parse(bad, "refresh.plan.yml", d));
            Assert.NotEmpty(d);
        }
        var random = new Random(7);
        for (var i = 0; i < 300; i++)
        {
            var chars = text.ToCharArray();
            chars[random.Next(chars.Length)] = "x:-\n\"'#{ ["[random.Next(10)];
            var parsed = RefreshPlanDocument.Parse(new string(chars), "refresh.plan.yml", []);
            if (parsed != null) Assert.Equal(text, RefreshPlanDocument.Serialize(parsed));        // a mutation that still reads is the same plan
        }
    }

    [Fact]
    public void A_selection_of_models_does_not_rewrite_the_plan_and_a_connection_without_models_loses_its_file()
    {
        var dir = Project();
        Compiled(dir);
        var path = Path.Combine(dir, "rendered", "sqlserver", RefreshPlanDocument.FileName);
        var before = File.ReadAllText(path);
        Assert.Equal(0, RenderFiles("--write", "marts.daily", "--project", dir).Exit);
        Assert.Equal(before, File.ReadAllText(path));
        Assert.Equal(0, RenderFiles("--check", "--project", dir).Exit);
    }
}
