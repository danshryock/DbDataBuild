using System.Globalization;
using DbDataBuild.Core;
using DbDataBuild.Execution;
using DbDataBuild.Models;
using DbDataBuild.State;
using DbDataBuild.Targets.Ddl;

namespace DbDataBuild.Cli;

/// <summary>
/// `dbdatabuild report` (DESIGN.md 9.1, 12.3). Effect class: target read-only. Shows what the tracking tables say happened: applied plans, DDL, loads, each object's
/// recorded shapes, anything that started and never finished, and objects whose live shape no longer matches the last recorded one. The per-column history
/// consistency report of DESIGN.md 12.3 is not built yet.
/// </summary>
internal static class ReportCommand
{
    public static int Run(CommandSpec spec, string root, string? targetArg, int last, TextWriter output, TextWriter error, Func<string, string?> env)
    {
        var config = ProjectConfigLoader.LoadFromProject(root, new List<Diagnostic>());
        var target = CommandTargets.Resolve(config, targetArg, error);
        if (target == null) return CliApp.ExitUsage;
        if (last < 1) { error.WriteLine("--last must be at least 1."); return CliApp.ExitUsage; }
        var (login, missing) = LoginSettings.FromEnvironment(target, Login.Read, env);
        output.WriteLine($"{ProductInfo.Cli} {spec.Name}  |  effect: {spec.Effect.Describe()}  |  target: {target}  |  login: {login?.Describe() ?? "none"}");
        if (missing != null) { error.Write(DiagnosticFormatter.Format(missing)); return CliApp.ExitFindings; }

        var schema = config.TrackingSchema;
        var ddl = TrackingDdl.For(target);
        string C(string n) => ddl.Quote(n);
        string T(string t) => $"{C(schema)}.{C(t)}";
        string Top(string cols, string from, string order) => target == "postgres" ? $"SELECT {cols} FROM {from} ORDER BY {order} LIMIT {last}" : $"SELECT TOP ({last}) {cols} FROM {from} ORDER BY {order}";

        return Task.Run(async () =>
        {
            await using var read = await ReadSession.OpenAsync(login!);
            var status = await TrackingStore.StatusAsync(read, target, schema);
            if (status.AsDiagnostic(schema) is { } notReady) { error.Write(DiagnosticFormatter.Format(notReady)); return CliApp.ExitFindings; }

            string Cell(object? v) => v switch { null => "", DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), string s => s.Trim(), _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "" };
            string Short(object? v) => Cell(v) is { Length: > 12 } s ? s[..12] : Cell(v);
            void Table(string title, string[] header, IEnumerable<string[]> rows)
            {
                var list = rows.ToList();
                output.WriteLine();
                output.WriteLine($"{title} ({list.Count})");
                if (list.Count == 0) { output.WriteLine("  none"); return; }
                var widths = Enumerable.Range(0, header.Length).Select(i => Math.Max(header[i].Length, list.Max(r => r[i].Length))).ToArray();
                output.WriteLine("  " + string.Join("  ", header.Select((h, i) => h.PadRight(widths[i]))).TrimEnd());
                foreach (var r in list) output.WriteLine("  " + string.Join("  ", r.Select((c, i) => c.PadRight(widths[i]))).TrimEnd());
            }

            var migrations = await read.QueryAsync(Top($"{C("applied_utc")}, {C("plan_id")}, {C("status")}, {C("applied_by")}, {C("git_commit")}", T("migration_log"), $"{C("applied_utc")} DESC"));
            Table("Applied plans (newest first)", ["when (UTC)", "plan", "status", "by", "commit"], migrations.Select(r => new[] { Cell(r[0]), Cell(r[1]), Cell(r[2]), Cell(r[3]), Short(r[4]) }));

            var ddlRows = await read.QueryAsync(Top($"{C("executed_utc")}, {C("object_name")}, {C("status")}, {C("plan_id")}, {C("statement_hash")}", T("ddl_log"), $"{C("executed_utc")} DESC"));
            Table("DDL (newest first)", ["when (UTC)", "object", "status", "plan", "statement"], ddlRows.Select(r => new[] { Cell(r[0]), Cell(r[1]), Cell(r[2]), Cell(r[3]), Short(r[4]) }));

            var runs = await read.QueryAsync(Top($"{C("started_utc")}, {C("model")}, {C("operation")}, {C("status")}, {C("rows_affected")}, {C("plan_id")}", T("run_log"), $"{C("started_utc")} DESC"));
            Table("Loads (newest first)", ["started (UTC)", "model", "operation", "status", "rows", "plan"], runs.Select(r => new[] { Cell(r[0]), Cell(r[1]), Cell(r[2]), Cell(r[3]), Cell(r[4]), Cell(r[5]) }));

            var versions = await read.QueryAsync($"SELECT {C("object_name")}, COUNT(*), MAX({C("first_seen_utc")}) FROM {T("schema_version")} GROUP BY {C("object_name")} ORDER BY {C("object_name")}");
            var recorded = await TrackingStore.LatestShapeHashesAsync(read, target, schema);
            var schemas = recorded.Keys.Select(k => DdlGenerator.Split(k).Schema).Distinct(StringComparer.Ordinal).ToList();
            var live = new Dictionary<string, ObjectShape>();
            foreach (var s in schemas) foreach (var (k, v) in await CatalogReader.ReadSchemaAsync(read, target, s)) live[k] = v;
            var drifted = new List<string>();
            Table("Objects the tool has recorded", ["object", "shapes recorded", "last recorded (UTC)", "now"], versions.Select(r =>
            {
                var name = Cell(r[0]);
                var state = Drift.Classify(live.GetValueOrDefault(name), recorded.GetValueOrDefault(name));
                if (state == ObjectState.OutOfBand) drifted.Add(name);
                return new[] { name, Cell(r[1]), Cell(r[2]), state switch { ObjectState.InSync => "in sync", ObjectState.Missing => "MISSING on the target", ObjectState.OutOfBand => "CHANGED OUTSIDE THE TOOL", _ => "not tracked" } };
            }));

            // ---- column history (DESIGN.md 12.3), from the answers embedded in the applied plans ----
            var planRows = await read.QueryAsync($"SELECT m.{C("plan_id")}, m.{C("applied_by")}, m.{C("applied_utc")}, m.{C("plan_text")} FROM {T("migration_log")} m WHERE m.{C("status")} = 'completed' ORDER BY m.{C("applied_utc")}");
            var applied = new List<DbDataBuild.Planning.AppliedPlan>();
            var unreadable = new List<string>();
            foreach (var r in planRows)
            {
                var planDiags = new List<Diagnostic>();
                if (Planning.PlanDocument.Parse((string)r[3]!, "migration_log", planDiags) is { } p) applied.Add(new(Cell(r[0]), Cell(r[1]), (DateTime)r[2]!, p));
                else unreadable.Add(Cell(r[0]));
            }
            var shapeRows = await read.QueryAsync($"SELECT s.{C("object_name")}, s.{C("shape_hash")}, s.{C("first_seen_utc")}, s.{C("source")}, s.{C("plan_id")} FROM {T("schema_version")} s");
            var intervalRows = await read.QueryAsync($"SELECT DISTINCT i.{C("model")}, i.{C("range_start")}, i.{C("range_end")}, i.{C("operation")}, r.{C("started_utc")} FROM {T("operation_interval")} i JOIN {T("run_log")} r ON r.{C("run_id")} = i.{C("run_id")} AND r.{C("model")} = i.{C("model")}");
            var history = Planning.ColumnHistory.Build(applied,
                shapeRows.Select(r => new Planning.RecordedShape(Cell(r[0]), Cell(r[1]), (DateTime)r[2]!, Cell(r[3]), r[4] == null ? null : Cell(r[4]))).ToList(),
                intervalRows.Select(r => new Planning.RecordedInterval(Cell(r[0]), r[1] == null ? null : Cell(r[1]), r[2] == null ? null : Cell(r[2]), Cell(r[3]), (DateTime)r[4]!)).ToList());
            output.WriteLine();
            output.WriteLine($"Column history ({history.Count})");
            if (history.Count == 0) output.WriteLine("  none: no applied plan added a column");
            foreach (var h in history) output.WriteLine($"  - {h.Text}");

            var open = new List<string>();
            foreach (var h in history.Where(h => h.NeedsAttention)) open.Add($"{h.Model}.{h.Column}: a backfill was requested and none is recorded (`{ProductInfo.Cli} plan --backfill {h.Model}=<operation>`)");
            foreach (var id in unreadable) open.Add($"the plan text recorded for {id} cannot be read back (edited or damaged); its decisions are not in this report");
            foreach (var r in await read.QueryAsync($"SELECT {C("plan_id")}, MAX({C("applied_utc")}) FROM {T("migration_log")} GROUP BY {C("plan_id")} HAVING SUM(CASE WHEN {C("status")} = 'completed' THEN 1 ELSE 0 END) = 0"))
                open.Add($"plan {Cell(r[0])} never completed (last record {Cell(r[1])} UTC): resume it with `{ProductInfo.Cli} apply --resume`, or plan again");
            foreach (var r in await read.QueryAsync($"SELECT {C("object_name")}, {C("plan_id")} FROM {T("ddl_log")} WHERE {C("status")} <> 'ok'"))
                open.Add($"DDL on {Cell(r[0])} in plan {Cell(r[1])} did not finish ok");
            foreach (var r in await read.QueryAsync($"SELECT {C("model")}, {C("plan_id")} FROM {T("run_log")} WHERE {C("status")} <> 'ok'"))
                open.Add($"load of {Cell(r[0])} in plan {Cell(r[1])} did not finish ok");
            foreach (var d in drifted) open.Add($"{d} changed outside the tool (`{ProductInfo.Cli} ack drift {d} --reason ...`, or restore it)");
            output.WriteLine();
            output.WriteLine($"Needs attention ({open.Count})");
            foreach (var o in open) output.WriteLine("  - " + o);
            if (open.Count == 0) output.WriteLine("  nothing");
            return open.Count == 0 ? CliApp.ExitOk : CliApp.ExitFindings;
        }).GetAwaiter().GetResult();
    }
}
