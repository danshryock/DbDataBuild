using System.Text.RegularExpressions;
using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Execution;
using DbDataBuild.Planning;
using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>
/// The whole loop on real engines, through the command surface: init, render, check, plan, dry run, apply, a refused re-apply, a model change that needs
/// answers and acknowledgements, drift, risk allowances, a stale plan, and a failure followed by a resume. Each stage asserts what the tracking tables and the
/// target hold afterwards, not just the exit code.
/// </summary>
public partial class ApplyConformanceTests
{
    public static TheoryData<string> Engines => new() { "sqlserver", "postgres" };

    private sealed class Run(Engine engine, string dir, string name)
    {
        public string Dir => dir;
        private Func<string, string?> Env => v =>
            v == LoginSettings.VariableName(name, DbDataBuild.Execution.Login.Read) || v == LoginSettings.VariableName(name, DbDataBuild.Execution.Login.Write) ? engine.ConnectionString : null;

        public (int Exit, string Out, string Err) Cli(params string[] args)
        {
            var o = new StringWriter();
            var e = new StringWriter();
            var exit = CliApp.Run([args[0], "--project", dir, .. args.Skip(1)], o, e, environment: Env);
            return (exit, o.ToString(), e.ToString());
        }

        public (int Exit, string Out, string Err) CliNoProject(params string[] args)
        {
            var o = new StringWriter();
            var e = new StringWriter();
            var exit = CliApp.Run(args, o, e, environment: Env);
            return (exit, o.ToString(), e.ToString());
        }

        public string PlanFile(string output) => Path.Combine(dir, PlanPath().Match(output).Groups[1].Value);

        public void Write(string rel, string text)
        {
            var path = Path.Combine(dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        public Engine Engine => engine;
        public string Q(string id) => engine.QuoteIdent(id);
    }

    [GeneratedRegex(@"plan:\s+(\S+\.plan\.yml)")]
    private static partial Regex PlanPath();

    private const string Staging = "name: staging.orders\ngrain: [order_id]\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: amount, type: \"DECIMAL(14, 2)\"}\n";
    private const string FctYaml = "name: marts.fct_orders\nkind: {type: incremental_by_unique_key, unique_key: [order_id]}\ngrain: [order_id]\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: amount, type: \"DECIMAL(14, 2)\"}\n";
    private const string FctSql = "SELECT o.order_id, o.amount FROM staging.orders o\n";
    private const string FctYaml2 = FctYaml + "  - {name: discount_code, type: VARCHAR(20)}\n";
    private const string FctSql2 = "SELECT o.order_id, o.amount, CAST(NULL AS VARCHAR(20)) AS discount_code FROM staging.orders o\n";
    private const string ViewYaml = "name: marts.v_orders\nkind: {type: view}\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n";
    private const string ViewSql = "SELECT order_id FROM marts.fct_orders\n";

    private static async Task<Run> SetUp(string name)
    {
        var engine = EngineEnv.Require(name);
        await engine.StartAsync();
        var dir = Path.Combine(Path.GetTempPath(), "ddb-e2e-" + Guid.NewGuid().ToString("N"));
        var run = new Run(engine, dir, name);
        run.Write("dbdatabuild.yml", name == "postgres"
            ? "default_targets: [postgres]\nstring_semantics:\n  case: sensitive\n  trailing_space: significant\n  collations:\n    default: { duckdb: NFC, postgres: C }\n"
            : "default_targets: [sqlserver]\n");
        run.Write("sources/staging/orders.yml", Staging);
        run.Write("models/marts/fct_orders.yml", FctYaml);
        run.Write("models/marts/fct_orders.sql", FctSql);
        run.Write("models/marts/v_orders.yml", ViewYaml);
        run.Write("models/marts/v_orders.sql", ViewSql);
        await engine.ExecAsync(name == "postgres" ? "CREATE SCHEMA staging" : "EXEC('CREATE SCHEMA staging')");
        await engine.ExecAsync($"CREATE TABLE staging.orders ({run.Q("order_id")} BIGINT NOT NULL, {run.Q("amount")} {engine.ColumnType("DECIMAL(14,2)")})");
        await engine.ExecAsync("INSERT INTO staging.orders VALUES (1, 10.00), (2, 20.50), (3, NULL)");
        return run;
    }

    private static void Ok((int Exit, string Out, string Err) r, string what) => Assert.True(r.Exit == 0, $"{what} failed ({r.Exit}):\n{r.Out}\n{r.Err}");
    private static void Refused((int Exit, string Out, string Err) r, string code, string what)
    {
        Assert.True(r.Exit != 0, $"{what} should have been refused:\n{r.Out}");
        Assert.True(r.Err.Contains(code) || r.Out.Contains(code), $"{what} should name {code}:\n{r.Out}\n{r.Err}");
    }

    private static async Task<int> CountAsync(Run run, string table, string? where = null) =>
        int.Parse((await run.Engine.RowsAsync($"SELECT COUNT(*) FROM {table}{(where == null ? "" : " WHERE " + where)}")).Single());

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task The_whole_loop_works_and_every_refusal_holds(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            var T = (string t) => $"{run.Q("dbdatabuild")}.{run.Q(t)}";

            // ---- init, render, check ----
            Ok(run.Cli("init", "--apply"), "init");
            Ok(run.Cli("render", "--write"), "render --write");
            var check = run.Cli("check");
            Ok(check, "check");
            Assert.Contains("missing (a plan would create it)", check.Out);

            // ---- plan: nothing to ask, structure then load, written as a plan and a report ----
            var plan1 = run.Cli("plan");
            Ok(plan1, "plan");
            var planFile = run.PlanFile(plan1.Out);
            var diags = new List<Diagnostic>();
            var parsed = PlanDocument.Parse(File.ReadAllText(planFile), planFile, diags)!;
            Assert.Empty(diags);
            Assert.Equal(["create schema marts", "create table marts.fct_orders", "create view marts.v_orders", "load marts.fct_orders (default)"], parsed.Steps.Select(s => s.Description));
            Assert.True(File.Exists(planFile.Replace(".plan.yml", ".plan.md")));

            // ---- dry run changes nothing, and has the same checks ----
            var dry = run.Cli("apply", planFile, "--dry-run");
            Ok(dry, "apply --dry-run");
            Assert.Contains("CREATE TABLE", dry.Out);
            Assert.Equal(0, await CountAsync(run, "information_schema.tables", "table_schema = 'marts'"));
            Assert.Equal(0, await CountAsync(run, T("migration_log")));

            // ---- apply ----
            var applied = run.Cli("apply", planFile);
            Ok(applied, "apply");
            Assert.Equal(3, await CountAsync(run, "marts.fct_orders"));
            Assert.Equal(3, await CountAsync(run, "marts.v_orders"));
            Assert.Equal(["completed", "started"], (await run.Engine.RowsAsync($"SELECT status FROM {T("migration_log")}")));
            Assert.Equal(3, await CountAsync(run, T("ddl_log"), "status = 'ok'"));               // the schema, the table and the view
            Assert.Equal(1, await CountAsync(run, T("run_log"), "status = 'ok'"));
            Assert.Equal(2, await CountAsync(run, T("schema_version"), "source = 'tool'"));
            Assert.NotEqual(0, await CountAsync(run, T("run_log"), "definition_hash IS NOT NULL"));

            // a plan is applied once
            Refused(run.Cli("apply", planFile), "DDB-438", "re-applying a completed plan");

            // ---- the next plan is a routine load only; it applies cleanly and is idempotent ----
            var plan2 = run.Cli("plan");
            Ok(plan2, "second plan");
            Assert.Contains("1 step(s)", plan2.Out);
            await engine.ExecAsync("INSERT INTO staging.orders VALUES (4, 40.00)");
            Ok(run.Cli("apply", run.PlanFile(plan2.Out)), "routine load");
            Assert.Equal(4, await CountAsync(run, "marts.fct_orders"));

            // ---- a model change: stale rendered files, then the incremental definition block, then answers ----
            run.Write("models/marts/fct_orders.yml", FctYaml2);
            run.Write("models/marts/fct_orders.sql", FctSql2);
            Refused(run.Cli("plan"), "DDB-424", "planning with stale rendered files");
            Ok(run.Cli("render", "--write"), "render --write after the change");
            var blocked = run.Cli("plan");
            Refused(blocked, "DDB-431", "planning an incremental model whose query changed");
            Assert.Contains("DDB-433", blocked.Err);                                              // the view that reads it is skipped
            Refused(run.Cli("ack", "definition", "marts.fct_orders"), "--reason", "an acknowledgement without a reason");
            Ok(run.Cli("ack", "definition", "marts.fct_orders", "--reason", "Added discount_code, no semantic change to existing columns"), "ack definition");
            Assert.Equal(1, await CountAsync(run, T("block_log"), "ack_utc IS NOT NULL"));

            var unanswered = run.Cli("plan");
            Refused(unanswered, "DDB-414", "planning with an open question");
            Assert.Contains("Q-history-marts.fct_orders.discount_code", unanswered.Err);
            run.Write("answers.yml", "answers:\n  - id: Q-history-marts.fct_orders.discount_code\n    choice: not_backfilled\n    note: \"No history exists in source.\"\n");
            var plan3 = run.Cli("plan", "--answers", Path.Combine(run.Dir, "answers.yml"));
            Ok(plan3, "plan with answers");
            var plan3File = run.PlanFile(plan3.Out);
            var report = File.ReadAllText(plan3File.Replace(".plan.yml", ".plan.md"));
            Assert.Contains("No history exists in source.", report);                              // the note travels with the decision
            Assert.Contains("Noticed but NOT done", report);

            // risk allowance: this plan is safe (a nullable column), so it applies without flags
            Ok(run.Cli("apply", plan3File), "apply the column change");
            await using (var read = await ReadSession.OpenAsync(LoginSettings.FromEnvironment(name, DbDataBuild.Execution.Login.Read, _ => engine.ConnectionString).Settings!))
            {
                var shape = (await DbDataBuild.Execution.CatalogReader.ReadSchemaAsync(read, name, "marts"))["marts.fct_orders"];
                Assert.Contains(shape.Columns, c => c.Name == "discount_code");
            }

            // the report keeps the decision, the note and who applied it
            var history = run.Cli("report");
            Ok(history, "report after the column change");
            Assert.Contains("Column history (1)", history.Out);
            Assert.Contains("`discount_code` was added to marts.fct_orders by plan", history.Out);
            Assert.Contains("not_backfilled (file, \"No history exists in source.\")", history.Out);

            // ---- drift: an out-of-band column blocks, an acknowledgement unblocks, a destructive step needs its allowance ----
            await engine.ExecAsync($"ALTER TABLE marts.fct_orders ADD {run.Q("sneaky")} {engine.ColumnType("VARCHAR(5)")} NULL");
            var driftCheck = run.Cli("check");
            Refused(driftCheck, "DDB-430", "check after an out-of-band change");
            Assert.Contains("CHANGED OUTSIDE THE TOOL", driftCheck.Out);
            Refused(run.Cli("plan"), "DDB-430", "plan after an out-of-band change");
            Refused(run.Cli("ack", "drift", "marts.fct_orders"), "--reason", "ack without reason");
            Ok(run.Cli("ack", "drift", "marts.fct_orders", "--reason", "DBA added a scratch column"), "ack drift");
            var again = run.Cli("ack", "drift", "marts.fct_orders", "--reason", "again");
            Ok(again, "a second ack of the same state");
            Assert.Contains("already acknowledged", again.Out);
            Assert.Equal(1, await CountAsync(run, T("block_log"), "code = 'DDB-430'"));                // nothing was recorded twice

            var plan4 = run.Cli("plan");
            Ok(plan4, "plan after ack");
            var plan4File = run.PlanFile(plan4.Out);
            var p4 = PlanDocument.Parse(File.ReadAllText(plan4File), plan4File, new List<Diagnostic>())!;
            Assert.Contains(p4.Steps, s => s.Type == StepType.Track && s.ShapeSource == "out_of_band");
            Assert.Contains(p4.Steps, s => s.Risk == RiskClass.Destructive && s.Description == "drop column sneaky");
            var noAllowance = run.Cli("apply", plan4File);
            Refused(noAllowance, "DDB-436", "a destructive plan without its allowance");
            Assert.Contains("--allow-destructive marts.fct_orders", noAllowance.Err);
            Refused(run.Cli("apply", plan4File, "--allow-destructive", "marts.other"), "DDB-436", "an allowance naming another object");
            Assert.Equal(0, await CountAsync(run, T("migration_log"), "plan_id = '" + p4.Id + "'"));        // refused before anything was recorded

            // ---- stale: the target changes after the plan was made ----
            await engine.ExecAsync($"ALTER TABLE marts.fct_orders ADD {run.Q("late")} {engine.ColumnType("VARCHAR(5)")} NULL");
            Refused(run.Cli("apply", plan4File, "--allow-destructive", "marts.fct_orders"), "DDB-437", "a stale plan");
            Assert.Equal(0, await CountAsync(run, T("migration_log"), "plan_id = '" + p4.Id + "'"));
            await engine.ExecAsync($"ALTER TABLE marts.fct_orders DROP COLUMN {run.Q("late")}");

            // ---- an edited plan is refused ----
            var tampered = plan4File.Replace(".plan.yml", ".edited.plan.yml");
            File.WriteAllText(tampered, File.ReadAllText(plan4File).Replace("DROP COLUMN", "DROP  COLUMN"));
            Refused(run.Cli("apply", tampered, "--allow-destructive", "marts.fct_orders"), "DDB-435", "an edited plan");

            // ---- failure part-way, then resume ----
            await engine.ExecAsync("DROP TABLE staging.orders");                                       // the load will fail: its source is gone
            var failed = run.Cli("apply", plan4File, "--allow-destructive", "marts.fct_orders");
            Refused(failed, "DDB-440", "an apply whose load fails");
            Assert.Contains("failed", failed.Out);
            Assert.DoesNotContain("staging.orders", failed.Err + failed.Out.Replace("staging.orders", "")); // no driver text with object names reaches the output beyond our own lines
            Assert.Equal(["failed", "started"], await run.Engine.RowsAsync($"SELECT status FROM {T("migration_log")} WHERE plan_id = '{p4.Id}'"));
            Assert.Equal(1, await CountAsync(run, T("run_log"), "status = 'failed'"));
            Refused(run.Cli("apply", plan4File, "--allow-destructive", "marts.fct_orders"), "DDB-438", "re-running a partly applied plan without --resume");

            await engine.ExecAsync($"CREATE TABLE staging.orders ({run.Q("order_id")} BIGINT NOT NULL, {run.Q("amount")} {engine.ColumnType("DECIMAL(14,2)")})");
            await engine.ExecAsync("INSERT INTO staging.orders VALUES (1, 10.00), (2, 20.50), (3, NULL), (4, 40.00), (5, 50.00)");
            var resumed = run.Cli("apply", plan4File, "--allow-destructive", "marts.fct_orders", "--resume");
            Ok(resumed, "apply --resume");
            Assert.Contains("skipped", resumed.Out);                                                   // the steps that finished before are not repeated
            Assert.Equal(5, await CountAsync(run, "marts.fct_orders"));
            Assert.Equal(["completed", "failed", "started", "started"], await run.Engine.RowsAsync($"SELECT status FROM {T("migration_log")} WHERE plan_id = '{p4.Id}'"));
            Refused(run.Cli("apply", plan4File, "--allow-destructive", "marts.fct_orders", "--resume"), "DDB-438", "resuming a plan that completed");

            // the end state is in sync: the next plan is the routine load again
            var final = run.Cli("check");
            Ok(final, "final check");
            Assert.Contains("in sync", final.Out);

            // ---- every statement the tool ran is in a statement log, and none contains a connection string ----
            var logs = Directory.GetFiles(Path.Combine(run.Dir, ".dbdatabuild", "statement-log"), "*.jsonl");
            Assert.NotEmpty(logs);
            Assert.DoesNotContain(logs.SelectMany(File.ReadAllLines), l => l.Contains("Password") || l.Contains("ddbconf") || l.Contains("Ddb!Conf"));
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task Two_applies_cannot_run_at_once(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            Ok(run.Cli("init", "--apply"), "init");
            Ok(run.Cli("render", "--write"), "render");
            var plan = run.Cli("plan");
            Ok(plan, "plan");
            var planFile = run.PlanFile(plan.Out);

            // hold the same application lock from another connection
            var login = LoginSettings.FromEnvironment(name, DbDataBuild.Execution.Login.Write, _ => engine.ConnectionString).Settings!;
            await using var holder = await MutationGate.OpenAsync(login, "hold", StatementKind.Tracking, new MemoryStatementLog(), Guid.NewGuid());
            Assert.True(await holder.TryAcquireApplicationLockAsync("dbdatabuild:dbdatabuild"));

            var blocked = run.Cli("apply", planFile);
            Refused(blocked, "DDB-439", "an apply while another holds the lock");
            Assert.Equal(0, await CountAsync(run, "information_schema.tables", "table_schema = 'marts'"));

            await holder.ReleaseApplicationLockAsync("dbdatabuild:dbdatabuild");
            Ok(run.Cli("apply", planFile), "apply after the lock was released");
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task Run_only_runs_routine_loads_and_report_shows_what_happened(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            Ok(run.Cli("init", "--apply"), "init");
            Ok(run.Cli("render", "--write"), "render");

            // nothing exists yet: run would have to create structure, so it refuses and points to plan; nothing is executed or written
            var refused = run.Cli("run");
            Refused(refused, "only runs routine loads", "run on a project that needs DDL");
            Assert.Contains("`dbdatabuild plan`", refused.Out);
            Assert.Equal(0, await CountAsync(run, "information_schema.tables", "table_schema = 'marts'"));
            Assert.False(Directory.Exists(Path.Combine(run.Dir, "plans")));

            var plan = run.Cli("plan");
            Ok(plan, "plan");
            Ok(run.Cli("apply", run.PlanFile(plan.Out)), "apply");

            // from here on a routine load is a one-liner
            await engine.ExecAsync("INSERT INTO staging.orders VALUES (4, 40.00)");
            var routine = run.Cli("run");
            Ok(routine, "run");
            Assert.Contains("1 routine load(s)", routine.Out);
            Assert.Equal(4, await CountAsync(run, "marts.fct_orders"));
            Assert.Equal(2, await CountAsync(run, run.Q("dbdatabuild") + "." + run.Q("run_log"), "status = 'ok'"));

            // the report shows the history and is clean
            var report = run.Cli("report");
            Ok(report, "report");
            Assert.Contains("Applied plans", report.Out);
            Assert.Contains("completed", report.Out);
            Assert.Contains("marts.fct_orders", report.Out);
            Assert.Contains("in sync", report.Out);
            Assert.Contains("nothing", report.Out.Split("Needs attention")[1]);

            // a change that needs DDL is refused by run, even though loads are also due
            run.Write("models/marts/fct_orders.yml", FctYaml2);
            run.Write("models/marts/fct_orders.sql", FctSql2);
            Ok(run.Cli("render", "--write"), "render after the change");
            Refused(run.Cli("run"), "DDB-431", "run after an incremental model changed");           // blocked, so not routine

            // the report notices an out-of-band change and says what to do
            run.Write("models/marts/fct_orders.yml", FctYaml);
            run.Write("models/marts/fct_orders.sql", FctSql);
            Ok(run.Cli("render", "--write"), "render back");
            await engine.ExecAsync($"ALTER TABLE marts.fct_orders ADD {run.Q("sneaky")} {engine.ColumnType("VARCHAR(5)")} NULL");
            var drift = run.Cli("report");
            Refused(drift, "CHANGED OUTSIDE THE TOOL", "report after drift");
            Assert.Contains("ack drift marts.fct_orders", drift.Out);
            Refused(run.Cli("run"), "DDB-430", "run on a drifted object");
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }

    private const string EventsSource = "name: staging.events\ngrain: [event_id]\ncolumns:\n  - {name: event_id, type: BIGINT, nullable: false}\n  - {name: event_ts, type: TIMESTAMP, nullable: false}\n  - {name: amount, type: \"DECIMAL(14, 2)\"}\n";
    private const string EventsYaml = "name: marts.fct_events\nkind: {type: incremental_by_time_range, time_column: event_ts}\ngrain: [event_id]\ncolumns:\n  - {name: event_id, type: BIGINT, nullable: false}\n  - {name: event_ts, type: TIMESTAMP, nullable: false}\n  - {name: amount, type: \"DECIMAL(14, 2)\"}\n" +
        "loads:\n  daily:\n    default: true\n    strategy: watermark_append\n    watermark: {column: event_ts, resolver: target_max, on_null: initial, initial: \"2000-01-01 00:00:00\"}\n" +
        "  reload:\n    strategy: delete_insert_by_range\n    params: {start: TIMESTAMP, end: TIMESTAMP}\n    max_span: 10 days\n";
    private const string EventsSql = "SELECT e.event_id, e.event_ts, e.amount FROM staging.events e\n";

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task Operations_can_be_chosen_and_a_backfill_is_a_risky_recorded_step(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            // a different project: one time-range model with a routine operation and a reload operation
            File.Delete(Path.Combine(run.Dir, "models/marts/v_orders.yml")); File.Delete(Path.Combine(run.Dir, "models/marts/v_orders.sql"));
            File.Delete(Path.Combine(run.Dir, "models/marts/fct_orders.yml")); File.Delete(Path.Combine(run.Dir, "models/marts/fct_orders.sql"));
            run.Write("sources/staging/events.yml", EventsSource);
            run.Write("models/marts/fct_events.yml", EventsYaml);
            run.Write("models/marts/fct_events.sql", EventsSql);
            await engine.ExecAsync($"CREATE TABLE staging.events ({run.Q("event_id")} BIGINT NOT NULL, {run.Q("event_ts")} {engine.ColumnType("TIMESTAMP")} NOT NULL, {run.Q("amount")} {engine.ColumnType("DECIMAL(14,2)")})");
            await engine.ExecAsync("INSERT INTO staging.events VALUES (1, '2024-01-01 10:00:00', 1.00), (2, '2024-01-02 10:00:00', 2.00), (3, '2024-01-05 10:00:00', 3.00)");
            var T = (string t) => $"{run.Q("dbdatabuild")}.{run.Q(t)}";

            Ok(run.Cli("init", "--apply"), "init");
            Ok(run.Cli("render", "--write"), "render");
            var first = run.Cli("plan");
            Ok(first, "first plan");                                                  // the watermark is NULL on a new table: the declared initial literal is used
            var firstPlan = PlanDocument.Parse(File.ReadAllText(run.PlanFile(first.Out)), "p", new List<Diagnostic>())!;
            Assert.Equal("2000-01-01 00:00:00", firstPlan.Steps.Single(s => s.Type == StepType.Load).Parameters.Single().Value);
            Ok(run.Cli("apply", run.PlanFile(first.Out)), "first apply");
            Assert.Equal(3, await CountAsync(run, "marts.fct_events"));
            Assert.Equal(1, await CountAsync(run, T("operation_interval"), "operation = 'load'"));   // the watermark it started from is recorded

            // usage errors before anything else
            Assert.Equal(CliApp.ExitUsage, run.Cli("plan", "--backfill", "marts.fct_events").Exit);
            Assert.Equal(CliApp.ExitUsage, run.Cli("plan", "--backfill", "marts.nope=reload").Exit);
            Refused(run.Cli("plan", "--op", "marts.fct_events=missing"), "DDB-434", "an operation that is not rendered");

            // a backfill of the reload operation: parameters are questions; the span is bounded by max_span
            var startId = "Q-param-marts.fct_events-reload-start";
            var endId = "Q-param-marts.fct_events-reload-end";
            run.Write("tooLong.yml", $"answers:\n  - {{id: {startId}, choice: provide, value: \"2024-01-01 00:00:00\"}}\n  - {{id: {endId}, choice: provide, value: \"2024-03-01 00:00:00\"}}\n");
            var tooLong = run.Cli("plan", "--backfill", "marts.fct_events=reload", "--answers", Path.Combine(run.Dir, "tooLong.yml"));
            Refused(tooLong, "DDB-412", "a range longer than max_span");
            Assert.Contains("max_span of 10 days", tooLong.Err);

            run.Write("ok.yml", $"answers:\n  - {{id: {startId}, choice: provide, value: \"2024-01-01 00:00:00\"}}\n  - {{id: {endId}, choice: provide, value: \"2024-01-04 00:00:00\"}}\n");
            var plan = run.Cli("plan", "--backfill", "marts.fct_events=reload", "--answers", Path.Combine(run.Dir, "ok.yml"));
            Ok(plan, "backfill plan");
            Assert.Contains("1 backfill", plan.Out);
            var planFile = run.PlanFile(plan.Out);
            Assert.Equal(["backfill marts.fct_events (reload)"], PlanDocument.Parse(File.ReadAllText(planFile), "p", new List<Diagnostic>())!.Steps.Select(s => s.Description));

            await engine.ExecAsync("UPDATE marts.fct_events SET amount = 999 WHERE event_id IN (1, 2)");   // the backfill must replace these rows from the source
            Refused(run.Cli("apply", planFile), "DDB-436", "a backfill without --allow-risky");
            Assert.Equal(0, await CountAsync(run, T("run_log"), "operation = 'reload'"));
            Ok(run.Cli("apply", planFile, "--allow-risky"), "backfill apply");
            Assert.Equal(["1", "2"], await run.Engine.RowsAsync("SELECT amount FROM marts.fct_events WHERE event_id IN (1, 2)"));
            Assert.Equal(1, await CountAsync(run, T("operation_interval"), "operation = 'backfill' AND range_start = '2024-01-01 00:00:00' AND range_end = '2024-01-04 00:00:00'"));
            Assert.Equal(1, await CountAsync(run, T("run_log"), "operation = 'reload' AND status = 'ok'"));

            // the report mentions the backfill's load
            Assert.Contains("reload", run.Cli("report").Out);
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task Declared_indexes_are_planned_verified_and_never_dropped(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            Ok(run.Cli("init", "--apply"), "init");
            Ok(run.Cli("render", "--write"), "render");
            Ok(run.Cli("apply", run.PlanFile(run.Cli("plan").Out)), "apply the tables");

            // an index and a unique index, declared by the operator; the model's unique_key alone never created a constraint
            run.Write("models/marts/fct_orders.yml", FctYaml + "indexes:\n  - {name: ix_amount, columns: [amount]}\n  - {name: uq_order, columns: [order_id], unique: true}\n");
            Ok(run.Cli("render", "--check"), "render --check (indexes do not change rendered loads)");
            var plan = run.Cli("plan");
            Ok(plan, "plan with indexes");
            var p = PlanDocument.Parse(File.ReadAllText(run.PlanFile(plan.Out)), "p", new List<Diagnostic>())!;
            Assert.Equal(["create index ix_amount", "create index uq_order", "load marts.fct_orders (default)"], p.Steps.Select(s => s.Description));
            Ok(run.Cli("apply", run.PlanFile(plan.Out)), "apply the indexes");

            await using var read = await ReadSession.OpenAsync(LoginSettings.FromEnvironment(name, DbDataBuild.Execution.Login.Read, _ => engine.ConnectionString).Settings!);
            async Task<IReadOnlyList<DbDataBuild.State.PhysicalItem>> Physical() => (await CatalogReader.ReadSchemaAsync(read, name, "marts"))["marts.fct_orders"].Physical;
            var physical = await Physical();
            Assert.Contains(physical, i => i is { Kind: "index", Name: "ix_amount", Definition: "unique=0;keys=amount;include=" });
            Assert.Contains(physical, i => i is { Kind: "index", Name: "uq_order", Definition: "unique=1;keys=order_id;include=" });
            await Assert.ThrowsAnyAsync<Exception>(() => engine.ExecAsync("INSERT INTO marts.fct_orders (order_id, amount) VALUES (1, 1.00)"));   // the unique index is enforced by the engine

            // in sync now: the next plan is the routine load only
            Assert.Contains("1 step(s)", run.Cli("plan").Out);

            // changing a declared index rebuilds it (risky); the plan shows drop and create together
            run.Write("models/marts/fct_orders.yml", FctYaml + "indexes:\n  - {name: ix_amount, columns: [amount], include: [order_id]}\n  - {name: uq_order, columns: [order_id], unique: true}\n");
            var rebuild = run.Cli("plan");
            Ok(rebuild, "plan with a changed index");
            var rebuildFile = run.PlanFile(rebuild.Out);
            Refused(run.Cli("apply", rebuildFile), "DDB-436", "a rebuild without --allow-risky");
            Ok(run.Cli("apply", rebuildFile, "--allow-risky"), "apply the rebuild");
            Assert.Contains(await Physical(), i => i is { Kind: "index", Name: "ix_amount", Definition: "unique=0;keys=amount;include=order_id" });

            // an index someone else added is left alone and mentioned; removing a declaration does not drop the index either
            await engine.ExecAsync($"CREATE INDEX dba_extra ON marts.fct_orders ({run.Q("order_id")}, {run.Q("amount")})");
            run.Write("models/marts/fct_orders.yml", FctYaml);
            var left = run.Cli("plan");
            Ok(left, "plan after removing the declarations");
            Assert.DoesNotContain("drop", PlanDocument.Parse(File.ReadAllText(run.PlanFile(left.Out)), "p", new List<Diagnostic>())!.Steps.Select(s => s.Description.ToLowerInvariant()).Aggregate("", (a, b) => a + b));
            Assert.Contains("`dba_extra`", File.ReadAllText(run.PlanFile(left.Out).Replace(".plan.yml", ".plan.md")));
            Ok(run.Cli("apply", run.PlanFile(left.Out)), "apply (loads only)");
            var after = await Physical();
            Assert.Contains(after, i => i.Name == "dba_extra");
            Assert.Contains(after, i => i.Name == "ix_amount");

            // physical changes are not drift: the shape is what blocks
            Ok(run.Cli("check"), "check with extra indexes on the target");
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task Hooks_run_around_their_events_in_order_and_are_checked_before_anything_is_planned(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            // a table the hooks write to (one extra column keeps their order), and the hook scripts: native SQL per engine, committed as written
            await engine.ExecAsync(name == "postgres"
                ? "CREATE TABLE staging.hook_log (seq serial PRIMARY KEY, event varchar(50) NOT NULL)"
                : "CREATE TABLE staging.hook_log (seq int IDENTITY PRIMARY KEY, event nvarchar(50) NOT NULL)");
            void Hook(string path, string eventName) => run.Write(path, $"INSERT INTO staging.hook_log (event) VALUES ('{eventName}');\n");
            Hook("hooks/created.sql", "created");
            Hook("hooks/pre_load.sql", "group_pre_load");
            Hook("hooks/post_load.sql", "group_post_load");
            Hook("hooks/sqlserver/native.sql", "native_sqlserver");
            Hook("hooks/postgres/native.sql", "native_postgres");
            run.Write("dbdatabuild.yml", File.ReadAllText(Path.Combine(run.Dir, "dbdatabuild.yml")) +
                "hook_groups:\n  audit:\n    - {name: before, event: pre_load, script: hooks/pre_load.sql, effect: data}\n    - {name: after, event: post_load, script: hooks/post_load.sql, effect: data}\n");
            var hooks = "hooks:\n  - {name: created, event: post_create, script: hooks/created.sql}\n  - {use: audit}\n" +
                        "  - {name: native, event: post_create, script: {sqlserver: hooks/sqlserver/native.sql, postgres: hooks/postgres/native.sql}}\n";
            run.Write("models/marts/fct_orders.yml", FctYaml + hooks);
            // read in insertion order (RowsAsync sorts, which would hide the order that matters here)
            async Task<List<string>> Log() => ((string?)await engine.ScalarAsync(name == "postgres"
                ? "SELECT string_agg(event, ',' ORDER BY seq) FROM staging.hook_log"
                : "SELECT STRING_AGG(event, ',') WITHIN GROUP (ORDER BY seq) FROM staging.hook_log"))?.Split(',').ToList() ?? [];
            Ok(run.Cli("init", "--apply"), "init");
            Ok(run.Cli("render", "--write"), "render");
            Ok(run.Cli("validate"), "validate with hooks");

            // plan: the hooks are steps, placed around their events, with the script text in the plan
            var plan = run.Cli("plan");
            Ok(plan, "plan");
            var file = run.PlanFile(plan.Out);
            var parsed = PlanDocument.Parse(File.ReadAllText(file), "p", new List<Diagnostic>())!;
            Assert.Equal(["create schema marts", "create table marts.fct_orders", "hook created (post_create)", $"hook native (post_create)", "create view marts.v_orders",
                "hook audit.before (pre_load)", "load marts.fct_orders (default)", "hook audit.after (post_load)"], parsed.Steps.Select(s => s.Description));
            Assert.Equal(name == "postgres" ? "native_postgres" : "native_sqlserver", System.Text.RegularExpressions.Regex.Match(parsed.Steps.Single(s => s.Hook == "native").Text, "'([a-z_]+)'").Groups[1].Value);
            Assert.Contains("hook audit.before (pre_load)", File.ReadAllText(file.Replace(".plan.yml", ".plan.md")));

            // dry run shows them and runs none
            var dry = run.Cli("apply", file, "--dry-run");
            Ok(dry, "dry run");
            Assert.Contains("INSERT INTO staging.hook_log", dry.Out);
            Assert.Empty(await Log());

            Ok(run.Cli("apply", file), "apply");
            Assert.Equal(["created", name == "postgres" ? "native_postgres" : "native_sqlserver", "group_pre_load", "group_post_load"], await Log());     // the order the plan gave
            Assert.Equal(4, await CountAsync(run, run.Q("dbdatabuild") + "." + run.Q("run_log"), "operation IN ('post_create', 'pre_load', 'post_load') AND status = 'ok'"));

            // a routine load with only data hooks is still routine for `run`; create hooks do not fire for an existing table
            await engine.ExecAsync("DELETE FROM staging.hook_log");
            var routine = run.Cli("run");
            Ok(routine, "run with data hooks");
            Assert.Equal(["group_pre_load", "group_post_load"], await Log());

            // a hook that is not a data hook (the default effect is ddl) makes the load not routine: run refuses, plan and apply do it
            run.Write("models/marts/fct_orders.yml", FctYaml + hooks + "  - {name: ddl_hook, event: post_load, script: hooks/created.sql}\n");
            var refused = run.Cli("run");
            Refused(refused, "only runs routine loads", "run with a ddl hook around the load");
            Assert.Contains("hook: hook ddl_hook (post_load)", refused.Out);

            // checked before anything is planned: a missing script, a script that does not parse, an unknown group
            run.Write("models/marts/fct_orders.yml", FctYaml + "hooks:\n  - {name: gone, event: post_create, script: hooks/nowhere.sql}\n");
            Refused(run.Cli("plan"), "DDB-323", "a hook whose script does not exist");
            Refused(run.Cli("validate"), "DDB-323", "validate with a missing hook script");
            run.Write("hooks/broken.sql", "SELEC nothing FRM nowhere ((;\n");
            run.Write("models/marts/fct_orders.yml", FctYaml + "hooks:\n  - {name: broken, event: post_create, script: hooks/broken.sql}\n");
            Refused(run.Cli("plan"), "DDB-323", "a hook script that does not parse");
            run.Write("models/marts/fct_orders.yml", FctYaml + "hooks:\n  - {use: no_such_group}\n");
            var group = run.Cli("plan");
            Assert.NotEqual(0, group.Exit);
            Assert.Contains("no_such_group", group.Err + group.Out);

            // a hook that fails stops the apply, is recorded as failed, and the plan resumes after it is fixed
            run.Write("hooks/failing.sql", "INSERT INTO staging.no_such_table (x) VALUES (1);\n");
            run.Write("models/marts/fct_orders.yml", FctYaml + "hooks:\n  - {name: failing, event: post_load, script: hooks/failing.sql, effect: data}\n");
            await engine.ExecAsync("INSERT INTO staging.orders VALUES (9, 90.00)");
            var failPlan = run.Cli("plan");
            Ok(failPlan, "plan with a failing hook");
            var failFile = run.PlanFile(failPlan.Out);
            var failed = run.Cli("apply", failFile);
            Refused(failed, "DDB-440", "a failing hook");
            Assert.Equal(1, await CountAsync(run, run.Q("dbdatabuild") + "." + run.Q("run_log"), "operation = 'post_load' AND status = 'failed'"));
            Assert.Equal(4, await CountAsync(run, "marts.fct_orders"));                                   // the load before the hook had already committed (3 rows plus order 9)
            await engine.ExecAsync("CREATE TABLE staging.no_such_table (x int)");
            Ok(run.Cli("apply", failFile, "--resume"), "resume after fixing the hook's cause");
            Assert.Equal(1, await CountAsync(run, "staging.no_such_table"));
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task A_history_warning_can_be_accepted_without_touching_data(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            // a `full` model, so adding a column needs no definition acknowledgement
            run.Write("models/marts/fct_orders.yml", FctYaml.Replace("kind: {type: incremental_by_unique_key, unique_key: [order_id]}", "kind: {type: full}").Replace("grain: [order_id]\n", ""));
            Ok(run.Cli("init", "--apply"), "init");
            Ok(run.Cli("render", "--write"), "render");
            Ok(run.Cli("apply", run.PlanFile(run.Cli("plan").Out)), "apply");

            run.Write("models/marts/fct_orders.yml", FctYaml2.Replace("kind: {type: incremental_by_unique_key, unique_key: [order_id]}", "kind: {type: full}").Replace("grain: [order_id]\n", ""));
            run.Write("models/marts/fct_orders.sql", FctSql2);
            Ok(run.Cli("render", "--write"), "render");
            run.Write("answers.yml", "answers:\n  - {id: Q-history-marts.fct_orders.discount_code, choice: backfill_later}\n");
            Ok(run.Cli("apply", run.PlanFile(run.Cli("plan", "--answers", Path.Combine(run.Dir, "answers.yml")).Out)), "apply the new column");
            var rowsBefore = await run.Engine.RowsAsync("SELECT order_id FROM marts.fct_orders");

            // the report warns and exits non-zero, saying how to deal with it
            var warn = run.Cli("report");
            Refused(warn, "backfill was requested and none", "report with an unfulfilled backfill request");
            Assert.Contains("ack history marts.fct_orders.discount_code", warn.Out);

            // the operator accepts it: a reason is required, nothing in the data changes, and the report keeps the facts
            Refused(run.Cli("ack", "history", "marts.fct_orders.discount_code"), "--reason", "an acknowledgement without a reason");
            Refused(run.Cli("ack", "history", "marts.fct_orders.nothing_here", "--reason", "x"), "nothing to acknowledge", "acknowledging something that is not there");
            var ack = run.Cli("ack", "history", "marts.fct_orders.discount_code", "--reason", "The source never had this column; NULL is correct.");
            Ok(ack, "ack history");
            Assert.Equal(rowsBefore, await run.Engine.RowsAsync("SELECT order_id FROM marts.fct_orders"));

            var calm = run.Cli("report");
            Ok(calm, "report after the acknowledgement");
            Assert.Contains("Acknowledged by", calm.Out);
            Assert.Contains("The source never had this column; NULL is correct.", calm.Out);
            Assert.Contains("A backfill was requested and none has been recorded.", calm.Out);
            Assert.Contains("nothing", calm.Out.Split("Needs attention")[1]);

            // repeating it is a no-op
            Assert.Contains("already acknowledged", run.Cli("ack", "history", "marts.fct_orders.discount_code", "--reason", "again").Err);
            Assert.Equal(1, await CountAsync(run, run.Q("dbdatabuild") + "." + run.Q("block_log"), "code = 'DDB-443'"));
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task The_database_commands_speak_json_with_their_data(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            System.Text.Json.Nodes.JsonNode Json((int Exit, string Out, string Err) r, int expectedExit = 0)
            {
                Assert.Equal(expectedExit, r.Exit);
                var doc = System.Text.Json.Nodes.JsonNode.Parse(r.Out)!;                            // standard output is exactly one document
                Assert.Equal("dbdatabuild.output/1", (string?)doc["schema"]);
                Assert.Equal(r.Exit, (int)doc["exit_code"]!);
                Assert.Equal("", r.Err);
                return doc;
            }
            var init = Json(run.Cli("init", "--apply", "--format", "json"));
            Assert.True((bool)init["data"]!["applied"]!);
            Json(run.Cli("render", "--write", "--format", "json"));

            var check = Json(run.Cli("check", "--format", "json"));
            Assert.Equal(["missing", "missing"], check["data"]!["objects"]!.AsArray().Select(o => (string)o!["state"]!).ToArray());
            Assert.Equal(4, (int)check["data"]!["preview"]!["steps"]!);

            // JSON mode never prompts: an open question is data, with its options, and the exit code says findings
            run.Write("models/marts/fct_orders.yml", FctYaml);
            var plan = Json(run.Cli("plan", "--format", "json"));
            Assert.Equal(4, plan["data"]!["plan"]!["steps"]!.AsArray().Count);
            Assert.Equal("ddl", (string?)plan["data"]!["plan"]!["steps"]![0]!["type"]);
            Assert.Equal("obj.missing.table", (string?)plan["data"]!["plan"]!["steps"]![1]!["reasons"]![0]);
            var planFile = Path.Combine(run.Dir, (string)plan["data"]!["files"]!["plan"]!);
            Assert.True(File.Exists(planFile));

            var dry = Json(run.Cli("apply", planFile, "--dry-run", "--format", "json"));
            Assert.True((bool)dry["data"]!["dry_run"]!);
            Assert.Contains("CREATE TABLE", (string)dry["data"]!["statements"]![1]!["text"]!);

            var applied = Json(run.Cli("apply", planFile, "--format", "json"));
            Assert.True((bool)applied["data"]!["success"]!);
            Assert.All(applied["data"]!["outcomes"]!.AsArray(), o => Assert.Equal("ok", (string?)o!["status"]));
            Assert.EndsWith(".jsonl", (string)applied["data"]!["statement_log"]!);

            var refused = Json(run.Cli("apply", planFile, "--format", "json"), expectedExit: 1);   // a plan is applied once
            Assert.Equal("DDB-438", (string?)refused["diagnostics"]![0]!["code"]);

            // a model change that needs an answer: an open question as data
            run.Write("models/marts/fct_orders.yml", FctYaml2);
            run.Write("models/marts/fct_orders.sql", FctSql2);
            Json(run.Cli("render", "--write", "--format", "json"));
            Json(run.Cli("ack", "definition", "marts.fct_orders", "--reason", "added a column", "--format", "json"));
            var asked = Json(run.Cli("plan", "--format", "json"), expectedExit: 1);
            var question = asked["data"]!["open_questions"]![0]!;
            Assert.Equal("Q-history-marts.fct_orders.discount_code", (string?)question["id"]);
            Assert.Equal(["not_backfilled", "backfill_later"], question["options"]!.AsArray().Select(o => (string)o!["key"]!).ToArray());
            Assert.Contains(asked["diagnostics"]!.AsArray(), d => (string?)d!["code"] == "DDB-414");

            var report = Json(run.Cli("report", "--format", "json"));
            Assert.Equal("completed", (string?)report["data"]!["applied_plans"]![0]!["status"]);
            Assert.Contains(report["data"]!["objects"]!.AsArray(), o => (string?)o!["object"] == "marts.fct_orders" && (string?)o["now"] == "in sync");
            Assert.Empty(report["data"]!["needs_attention"]!.AsArray());
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task Metadata_can_be_stored_in_the_target_queried_with_sql_and_republished_without_noise(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            System.Text.Json.Nodes.JsonNode Json((int Exit, string Out, string Err) r)
            {
                Assert.Equal(0, r.Exit);
                return System.Text.Json.Nodes.JsonNode.Parse(r.Out)!;
            }
            var meta = (string table) => $"{run.Q("dbdatabuild")}.{run.Q(table)}";
            Ok(run.Cli("init", "--apply"), "init");
            Ok(run.Cli("render", "--write"), "render");

            // the operator's choice of when to store: nothing is stored until asked
            Assert.Equal(0, await CountAsync(run, meta("metadata_document")));
            var first = Json(run.Cli("publish-metadata", "--format", "json"));
            Assert.Equal(3, first["data"]!["written"]!.AsArray().Count);              // the project, and the two models
            Assert.Empty(first["data"]!["unchanged"]!.AsArray());
            Assert.Equal(3, await CountAsync(run, meta("metadata_document")));

            // unchanged documents are not written again
            var second = Json(run.Cli("publish-metadata", "--format", "json"));
            Assert.Empty(second["data"]!["written"]!.AsArray());
            Assert.Equal(3, await CountAsync(run, meta("metadata_document")));

            // introspection with SQL: the stored document is the one `metadata` prints, and the column view unpacks it
            var printed = Json(run.Cli("metadata", "--format", "json"))["data"]!["models"]!.AsArray().Single(m => (string?)m!["name"] == "marts.fct_orders")!;
            var storedHash = (string?)await engine.ScalarAsync(name == "postgres"
                ? $"SELECT document ->> 'definition_hash' FROM {meta("metadata_current")} WHERE kind = 'model' AND subject = 'marts.fct_orders'"
                : $"SELECT JSON_VALUE(document, '$.definition_hash') FROM {meta("metadata_current")} WHERE kind = 'model' AND subject = 'marts.fct_orders'");
            Assert.Equal((string?)printed["definition_hash"], storedHash);
            var amount = await engine.RowsAsync($"SELECT {run.Q("logical_type")}, {run.Q(name == "postgres" ? "postgres_type" : "sqlserver_type")}, {run.Q("nullable")} FROM {meta("metadata_columns")} WHERE model = 'marts.fct_orders' AND column_name = 'amount'");
            Assert.Equal(new List<string> { name == "postgres" ? "DECIMAL(14, 2)|numeric(14, 2)|True" : "DECIMAL(14, 2)|decimal(14, 2)|True" }, amount);
            Assert.Equal(new[] { "order_id", "amount" }.Order(), (await engine.RowsAsync($"SELECT column_name FROM {meta("metadata_columns")} WHERE model = 'marts.fct_orders'")).Order());

            // a changed model changes its own document and the project document (which lists every model's hash), and nothing else
            run.Write("models/marts/v_orders.sql", "SELECT order_id FROM marts.fct_orders WHERE order_id > 0\n");
            var third = Json(run.Cli("publish-metadata", "--format", "json"));
            Assert.Equal(["marts.v_orders", "project"], third["data"]!["written"]!.AsArray().Select(d => (string)d!["subject"]!).Order().ToArray());
            Assert.Equal(5, await CountAsync(run, meta("metadata_document")));      // history is kept; metadata_current shows the latest
            Assert.Equal(3, await CountAsync(run, meta("metadata_current")));

            // only the models named are stored
            run.Write("models/marts/fct_orders.sql", "SELECT o.order_id, o.amount FROM staging.orders o WHERE 1 = 1\n");
            var only = Json(run.Cli("publish-metadata", "marts.fct_orders", "--format", "json"));
            Assert.Equal(["marts.fct_orders", "project"], only["data"]!["written"]!.AsArray().Select(d => (string)d!["subject"]!).Order().ToArray());
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task An_older_tracking_layout_is_upgraded_by_init_and_apply_can_store_metadata_itself(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            var T = (string t) => $"{run.Q("dbdatabuild")}.{run.Q(t)}";
            Ok(run.Cli("init", "--apply"), "init");
            Ok(run.Cli("render", "--write"), "render");

            // turn the tracking tables back into layout 1: no metadata objects, version row 1
            await engine.ExecAsync($"DROP VIEW {T("metadata_columns")}");
            await engine.ExecAsync($"DROP VIEW {T("metadata_current")}");
            await engine.ExecAsync($"DROP TABLE {T("metadata_document")}");
            await engine.ExecAsync($"UPDATE {T("tracking_version")} SET {run.Q("version")} = 1");
            var old = run.Cli("plan");
            Refused(old, "DDB-505", "planning against an older layout");
            Assert.Contains("layout version 1", old.Err);
            Ok(run.Cli("init", "--apply"), "init upgrades");
            Assert.Equal(["1", "2"], await engine.RowsAsync($"SELECT {run.Q("version")} FROM {T("tracking_version")}"));
            Ok(run.Cli("plan"), "plan after the upgrade");

            // metadata.store_on_apply: a successful apply also stores the project, the models it touched and the plan
            run.Write("dbdatabuild.yml", File.ReadAllText(Path.Combine(run.Dir, "dbdatabuild.yml")) + "metadata:\n  store_on_apply: true\n");
            var plan = run.Cli("plan");
            Ok(plan, "plan");
            var applied = run.Cli("apply", run.PlanFile(plan.Out));
            Ok(applied, "apply");
            Assert.Contains("Metadata stored: 4 document(s) written", applied.Out);       // project, two models, the plan
            Assert.Equal(["model", "model", "plan", "project"], await engine.RowsAsync($"SELECT kind FROM {T("metadata_document")}"));
            var planId = PlanDocument.Parse(File.ReadAllText(run.PlanFile(plan.Out)), "p", new List<Diagnostic>())!.Id;
            Assert.Equal(1, await CountAsync(run, T("metadata_document"), $"kind = 'plan' AND subject = '{planId}'"));
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task Lowering_makes_what_the_engines_compute_match_what_DuckDB_computes(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            // two constructs that went wrong when the author's text was transpiled directly: GROUP BY ALL, and AVG over integers (T-SQL and PostgreSQL truncate or widen differently)
            File.Delete(Path.Combine(run.Dir, "models/marts/v_orders.yml")); File.Delete(Path.Combine(run.Dir, "models/marts/v_orders.sql"));
            File.Delete(Path.Combine(run.Dir, "models/marts/fct_orders.yml")); File.Delete(Path.Combine(run.Dir, "models/marts/fct_orders.sql"));
            run.Write("models/marts/fct_stats.yml", "name: marts.fct_stats\nkind: {type: full}\ncolumns:\n  - {name: size, type: \"VARCHAR(5)\"}\n  - {name: n, type: BIGINT}\n  - {name: mean_id, type: DOUBLE}\n");
            run.Write("models/marts/fct_stats.sql", "SELECT CASE WHEN o.order_id > 2 THEN 'big' ELSE 'small' END AS size, COUNT(*) AS n, AVG(o.order_id) AS mean_id FROM staging.orders o GROUP BY ALL\n");
            await engine.ExecAsync("INSERT INTO staging.orders VALUES (4, 40.00)");

            Ok(run.Cli("init", "--apply"), "init");
            Ok(run.Cli("render", "--write"), "render");
            var lowered = File.ReadAllText(Path.Combine(run.Dir, "rendered/lowered/marts.fct_stats/lowered.sql"));
            Assert.Contains("avg(CAST(order_id AS DOUBLE))", lowered);                 // the plan said DOUBLE, so the query says so
            Assert.Contains("-- type rules:   avg-double", lowered);
            Assert.DoesNotContain("GROUP BY ALL", lowered);
            Ok(run.Cli("apply", run.PlanFile(run.Cli("plan").Out)), "apply");

            // DuckDB's answer is 1.5 and 3.5; an engine that averages integers as integers would say 1 and 3
            Assert.Equal(new List<string> { "big|2|3.5", "small|2|1.5" }, await engine.RowsAsync("SELECT size, n, mean_id FROM marts.fct_stats"));
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task Correlated_subqueries_give_the_same_rows_on_both_engines(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            File.Delete(Path.Combine(run.Dir, "models/marts/v_orders.yml")); File.Delete(Path.Combine(run.Dir, "models/marts/v_orders.sql"));
            File.Delete(Path.Combine(run.Dir, "models/marts/fct_orders.yml")); File.Delete(Path.Combine(run.Dir, "models/marts/fct_orders.sql"));
            run.Write("models/marts/fct_rank.yml", "name: marts.fct_rank\nkind: {type: full}\ncolumns:\n  - {name: order_id, type: BIGINT}\n  - {name: earlier, type: BIGINT}\n  - {name: rank_tag, type: \"VARCHAR(10)\"}\n");
            // a correlated scalar subquery with an aggregate, a correlated EXISTS inside a CASE, and NOT IN against a set that contains a NULL-amount row's key
            run.Write("models/marts/fct_rank.sql",
                "SELECT o.order_id,\n  (SELECT count(*) FROM staging.orders p WHERE p.order_id < o.order_id) AS earlier,\n" +
                "  CASE WHEN EXISTS (SELECT 1 FROM staging.orders q WHERE q.amount > o.amount) THEN 'has_bigger' ELSE 'top' END AS rank_tag\n" +
                "FROM staging.orders o\nWHERE o.order_id NOT IN (SELECT r.order_id FROM staging.orders r WHERE r.amount IS NULL)\n");
            await engine.ExecAsync("INSERT INTO staging.orders VALUES (4, 40.00)");

            Ok(run.Cli("init", "--apply"), "init");
            Ok(run.Cli("render", "--write"), "render");
            var lowered = File.ReadAllText(Path.Combine(run.Dir, "rendered/lowered/marts.fct_rank/lowered.sql"));
            Assert.Contains("EXISTS (", lowered);
            Assert.DoesNotContain("DELIM", lowered);
            Ok(run.Cli("apply", run.PlanFile(run.Cli("plan").Out)), "apply");

            Assert.Equal(new List<string> { "1|0|has_bigger", "2|1|has_bigger", "4|3|top" }, await engine.RowsAsync("SELECT order_id, earlier, rank_tag FROM marts.fct_rank"));
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task DISTINCT_ON_with_a_deciding_order_gives_the_same_rows_on_both_engines(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            File.Delete(Path.Combine(run.Dir, "models/marts/v_orders.yml")); File.Delete(Path.Combine(run.Dir, "models/marts/v_orders.sql"));
            File.Delete(Path.Combine(run.Dir, "models/marts/fct_orders.yml")); File.Delete(Path.Combine(run.Dir, "models/marts/fct_orders.sql"));
            run.Write("models/marts/fct_first.yml", "name: marts.fct_first\nkind: {type: full}\ncolumns:\n  - {name: amount, type: \"DECIMAL(14, 2)\"}\n  - {name: order_id, type: BIGINT}\n");
            // the first order per amount; order_id (the source's declared grain) is in the ORDER BY, so the row kept is decided, not arbitrary. NULL is a group of its own.
            run.Write("models/marts/fct_first.sql", "SELECT DISTINCT ON (o.amount) o.amount, o.order_id FROM staging.orders o ORDER BY o.amount, o.order_id\n");
            await engine.ExecAsync("INSERT INTO staging.orders VALUES (4, 40.00), (5, 40.00), (6, NULL)");

            Ok(run.Cli("init", "--apply"), "init");
            Ok(run.Cli("render", "--write"), "render");
            Assert.Contains("row_number() OVER (PARTITION BY amount ORDER BY order_id NULLS LAST)", File.ReadAllText(Path.Combine(run.Dir, "rendered/lowered/marts.fct_first/lowered.sql")));
            Ok(run.Cli("apply", run.PlanFile(run.Cli("plan").Out)), "apply");
            Assert.Equal(new List<string> { "10|1", "20.5|2", "40|4", "∅|3" }, await engine.RowsAsync("SELECT amount, order_id FROM marts.fct_first"));

            // an ordering that leaves ties is refused before anything is planned, and says what to add
            run.Write("models/marts/fct_first.sql", "SELECT DISTINCT ON (o.amount) o.amount, o.order_id FROM staging.orders o ORDER BY o.amount\n");
            var ties = run.Cli("render", "--write");
            Assert.Contains("DDB-324", ties.Err);
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }
    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task Integer_series_become_the_engines_own_generate_series_and_join_to_tables(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            File.Delete(Path.Combine(run.Dir, "models/marts/v_orders.yml")); File.Delete(Path.Combine(run.Dir, "models/marts/v_orders.sql"));
            File.Delete(Path.Combine(run.Dir, "models/marts/fct_orders.yml")); File.Delete(Path.Combine(run.Dir, "models/marts/fct_orders.sql"));
            run.Write("models/marts/fct_slots.yml", "name: marts.fct_slots\nkind: {type: full}\ncolumns:\n  - {name: slot, type: BIGINT}\n  - {name: orders_in_slot, type: BIGINT}\n");
            // range excludes its end; the slots with no orders must still appear
            run.Write("models/marts/fct_slots.sql", "SELECT g.s AS slot, count(o.order_id) AS orders_in_slot FROM range(0, 4) AS g(s) LEFT JOIN staging.orders o ON o.order_id = g.s GROUP BY g.s\n");
            Ok(run.Cli("init", "--apply"), "init");
            Ok(run.Cli("render", "--write"), "render");
            Assert.Contains("generate_series(0, 3)", File.ReadAllText(Path.Combine(run.Dir, "rendered/lowered/marts.fct_slots/lowered.sql")));
            Ok(run.Cli("apply", run.PlanFile(run.Cli("plan").Out)), "apply");
            Assert.Equal(new List<string> { "0|0", "1|1", "2|1", "3|1" }, (await engine.RowsAsync("SELECT slot, orders_in_slot FROM marts.fct_slots")).Order().ToList());
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }
    // what DuckDB returns for the same rows and the same query (checked with DuckDB 1.5.4); the engines must agree, not just run
    private static readonly string[] ProbeExpected =
    [
        "1|0|∅|∅|∅|2.5|3|0", "2|4|12|12|12|0.28|0|0", "3|3|∅|∅|∅|1|1|0", "4|2|5|5|5|-2.5|-3|0", "5|11|∅|∅|99999999999|2.68|3|0", "6|5|6|1.56|1.555|1234.57|1235|1200",
        "7|2|-7|-7|-7|0.13|0|0", "8|4|∅|∅|∅|∅|∅|∅", "9|∅|∅|∅|∅|0|0|0", "10|3|1|100|100|-0.4|0|0", "11|4|2|0.5|0.5|1E+20|1E+20|1E+20",
    ];

    private static string Normalize(string row) => string.Join("|", row.Split('|').Select(f => f == "∅" ? f : decimal.TryParse(f, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d.ToString("0.#########", System.Globalization.CultureInfo.InvariantCulture) : f)
        .Select(f => f == "-0" ? "0" : f));

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task Target_rules_make_length_try_cast_and_round_compute_what_DuckDB_computes(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            File.Delete(Path.Combine(run.Dir, "models/marts/v_orders.yml")); File.Delete(Path.Combine(run.Dir, "models/marts/v_orders.sql"));
            File.Delete(Path.Combine(run.Dir, "models/marts/fct_orders.yml")); File.Delete(Path.Combine(run.Dir, "models/marts/fct_orders.sql"));
            run.Write("sources/staging/probe.yml", "name: staging.probe\ngrain: [id]\ncolumns:\n  - {name: id, type: BIGINT, nullable: false}\n  - {name: s, type: VARCHAR(40)}\n  - {name: t, type: VARCHAR(40)}\n  - {name: x, type: DOUBLE}\n");
            run.Write("models/marts/fct_probe.yml", "name: marts.fct_probe\nkind: {type: full}\ngrain: [id]\ncolumns:\n  - {name: id, type: BIGINT, nullable: false}\n  - {name: len, type: BIGINT}\n  - {name: i, type: INTEGER}\n  - {name: d, type: \"DECIMAL(10, 2)\"}\n  - {name: f, type: DOUBLE}\n  - {name: r2, type: DOUBLE}\n  - {name: r0, type: DOUBLE}\n  - {name: rm, type: DOUBLE}\n");
            run.Write("models/marts/fct_probe.sql", "SELECT id, length(s) AS len, TRY_CAST(t AS INTEGER) AS i, TRY_CAST(s AS DECIMAL(10, 2)) AS d, TRY_CAST(s AS DOUBLE) AS f, round(x, 2) AS r2, round(x) AS r0, round(x, -2) AS rm FROM staging.probe\n");
            var dbl = name == "postgres" ? "DOUBLE PRECISION" : "FLOAT";
            var vc = engine.ColumnType("VARCHAR(40)");
            await engine.ExecAsync($"CREATE TABLE staging.probe (id BIGINT NOT NULL, s {vc} NULL, t {vc} NULL, x {dbl} NULL)");
            await engine.ExecAsync("INSERT INTO staging.probe VALUES (1, '', '', 2.5), (2, ' 12 ', ' 12 ', 0.285), (3, 'abc', 'abc', 1.005), (4, '+5', '+5', -2.5), (5, '99999999999', '99999999999', 2.675), " +
                "(6, '1.555', '6', 1234.5678), (7, '-7', '-7', 0.125), (8, 'ab  ', 'x', NULL), (9, NULL, NULL, 0.0), (10, '100', '1', -0.4), (11, ' .5 ', '2', 1e20)");

            Ok(run.Cli("init", "--apply"), "init");
            Ok(run.Cli("render", "--write"), "render");
            var script = File.ReadAllText(Directory.GetFiles(Path.Combine(run.Dir, "rendered", name), "load.*.sql", SearchOption.AllDirectories).First(f => f.Contains("fct_probe")));
            Assert.Contains("target rules:", script);
            Ok(run.Cli("apply", run.PlanFile(run.Cli("plan").Out)), "apply");
            var rows = await engine.RowsAsync("SELECT id, len, i, d, f, r2, r0, rm FROM marts.fct_probe ORDER BY id");
            Assert.Equal(ProbeExpected.Select(Normalize).Order().ToList(), rows.Select(Normalize).Order().ToList());
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }
    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task Sums_of_integers_do_not_overflow_on_either_engine(string name)
    {
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            File.Delete(Path.Combine(run.Dir, "models/marts/v_orders.yml")); File.Delete(Path.Combine(run.Dir, "models/marts/v_orders.sql"));
            File.Delete(Path.Combine(run.Dir, "models/marts/fct_orders.yml")); File.Delete(Path.Combine(run.Dir, "models/marts/fct_orders.sql"));
            run.Write("sources/staging/big.yml", "name: staging.big\ngrain: [id]\ncolumns:\n  - {name: id, type: BIGINT, nullable: false}\n  - {name: n, type: INTEGER}\n  - {name: b, type: BIGINT}\n");
            run.Write("models/marts/fct_big.yml", "name: marts.fct_big\nkind: {type: full}\ncolumns:\n  - {name: sn, type: BIGINT}\n  - {name: sb, type: \"DECIMAL(38, 0)\"}\n");
            // DuckDB sums into a HUGEINT: three INTs near 2^31 and two BIGINTs near 2^63 are over what SQL Server's own SUM holds
            run.Write("models/marts/fct_big.sql", "SELECT CAST(sum(n) AS BIGINT) AS sn, CAST(sum(b) AS DECIMAL(38, 0)) AS sb FROM staging.big\n");
            await engine.ExecAsync("CREATE TABLE staging.big (id BIGINT NOT NULL, n INTEGER NULL, b BIGINT NULL)");
            await engine.ExecAsync("INSERT INTO staging.big VALUES (1, 2147483647, 9000000000000000000), (2, 2147483647, 9000000000000000000), (3, 2147483647, NULL)");
            Ok(run.Cli("init", "--apply"), "init");
            Ok(run.Cli("render", "--write"), "render");
            var plan = run.Cli("plan"); Ok(plan, "plan");
            Ok(run.Cli("apply", run.PlanFile(plan.Out)), "apply");
            Assert.Equal(["6442450941|18000000000000000000"], await engine.RowsAsync("SELECT sn, sb FROM marts.fct_big"));
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }
    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task A_data_bearing_engine_error_never_reaches_output_files_or_tracking_tables(string name)
    {
        // DESIGN.md 14.2: SQL Server and PostgreSQL quote the offending value in conversion errors
        const string secret = "SECRET-VALUE-4711";
        var run = await SetUp(name);
        await using var engine = run.Engine;
        try
        {
            File.Delete(Path.Combine(run.Dir, "models/marts/v_orders.yml")); File.Delete(Path.Combine(run.Dir, "models/marts/v_orders.sql"));
            File.Delete(Path.Combine(run.Dir, "models/marts/fct_orders.yml")); File.Delete(Path.Combine(run.Dir, "models/marts/fct_orders.sql"));
            run.Write("sources/staging/raw.yml", "name: staging.raw\ngrain: [id]\ncolumns:\n  - {name: id, type: BIGINT, nullable: false}\n  - {name: s, type: VARCHAR(40)}\n");
            run.Write("models/marts/fct_num.yml", "name: marts.fct_num\nkind: {type: full}\ncolumns:\n  - {name: id, type: BIGINT, nullable: false}\n  - {name: n, type: INTEGER}\n");
            run.Write("models/marts/fct_num.sql", "SELECT id, CAST(s AS INTEGER) AS n FROM staging.raw\n");
            await engine.ExecAsync($"CREATE TABLE staging.raw (id BIGINT NOT NULL, s {engine.ColumnType("VARCHAR(40)")} NULL)");
            await engine.ExecAsync($"INSERT INTO staging.raw VALUES (1, '12'), (2, '{secret}')");
            Ok(run.Cli("init", "--apply"), "init");
            Ok(run.Cli("render", "--write"), "render");
            var plan = run.Cli("plan"); Ok(plan, "plan");
            var planFile = run.PlanFile(plan.Out);

            var text = run.Cli("apply", planFile);
            var json = run.Cli("apply", planFile, "--format", "json");
            Assert.NotEqual(0, text.Exit);
            Assert.Contains("DDB-", text.Err);
            foreach (var shown in new[] { text.Out, text.Err, json.Out, json.Err }) Assert.DoesNotContain(secret, shown);

            // nothing the tool wrote locally, and nothing in the tracking tables
            foreach (var file in Directory.EnumerateFiles(run.Dir, "*", SearchOption.AllDirectories)) Assert.DoesNotContain(secret, File.ReadAllText(file));
            foreach (var table in new[] { "tracking_version", "schema_version", "ddl_log", "run_log", "operation_interval", "block_log", "migration_log", "metadata_document" })
                foreach (var row in await engine.RowsAsync($"SELECT * FROM {run.Q("dbdatabuild")}.{run.Q(table)}")) Assert.DoesNotContain(secret, row);
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }
}
