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
}
