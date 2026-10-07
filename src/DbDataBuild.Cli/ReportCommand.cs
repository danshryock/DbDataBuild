using System.Globalization;
using DbDataBuild.Core;
using DbDataBuild.Execution;
using DbDataBuild.Models;
using DbDataBuild.Planning;
using DbDataBuild.State;
using DbDataBuild.Targets.Ddl;

namespace DbDataBuild.Cli;

/// <summary>
/// `dbdatabuild report` (DESIGN.md 9.1, 12.3). Effect class: target read-only. Shows what the tracking tables say happened: applied plans, DDL, loads, each object's
/// recorded shapes, anything that started and never finished, and objects whose live shape no longer matches the last recorded one. The per-column history
/// consistency report of DESIGN.md 12.3 is part of it (HistoryReader).
/// </summary>
internal static class ReportCommand
{
    public static int Run(CommandSpec spec, string root, string? targetArg, int last, TextWriter output, TextWriter error, Func<string, string?> env)
    {
        var config = ProjectConfigLoader.LoadFromProject(root, new List<Diagnostic>());
        var connection = CommandTargets.Resolve(config, targetArg, error);
        if (connection == null) return CliApp.ExitUsage;
        var target = connection.Name; var engine = connection.Engine;
        if (last < 1) { error.WriteLine("--last must be at least 1."); return CliApp.ExitUsage; }
        var (login, missing) = LoginSettings.FromEnvironment(connection.Name, connection.Engine, Login.Read, env);
        var tracking = CommandTracking.Require(config, connection, env, needWrite: false, error, spec.Name);
        output.WriteLine($"{ProductInfo.Cli} {spec.Name}  |  effect: {spec.Effect.Describe()}  |  connection: {target}  |  login: {login?.Describe() ?? "none"}; records on {tracking?.Target.Connection ?? "none"}");
        if (missing != null) { error.Diag(missing); return CliApp.ExitFindings; }
        if (tracking == null) return CliApp.ExitFindings;

        var scope = tracking.Scope;
        var schema = scope.SchemaName;
        var ddl = TrackingDdl.For(scope.Engine);
        string C(string n) => ddl.Quote(n);
        string T(string t) => $"{C(schema)}.{C(t)}";
        string Top(string cols, string from, string order) => scope.Engine == "postgres" ? $"SELECT {cols} FROM {from} WHERE {C("connection")} = @connection ORDER BY {order} LIMIT {last}" : $"SELECT TOP ({last}) {cols} FROM {from} WHERE {C("connection")} = @connection ORDER BY {order}";
        var byConnection = new[] { new GateParameter("connection", System.Data.DbType.String, scope.Connection) };

        return Task.Run(async () =>
        {
            await using var read = await ReadSession.OpenAsync(login!);
            await using var ownTrackingReader = tracking.Read.Connection == login!.Connection ? null : await ReadSession.OpenAsync(tracking.Read);
            var trackRead = ownTrackingReader ?? read;
            var status = await TrackingStore.StatusAsync(trackRead, scope.Engine, schema);
            if (status.AsDiagnostic(schema) is { } notReady) { error.Diag(notReady); return CliApp.ExitFindings; }

            string Cell(object? v) => v switch { null => "", DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), string s => s.Trim(), _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "" };
            string Short(object? v) => Cell(v) is { Length: > 12 } s ? s[..12] : Cell(v);
            void Table(string key, string title, string[] header, IEnumerable<string[]> rows)
            {
                var list = rows.ToList();
                output.Payload(key, list.Select(r => header.Zip(r).ToDictionary(p => System.Text.RegularExpressions.Regex.Replace(p.First.ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_'), p => p.Second)).ToList());
                output.WriteLine();
                output.WriteLine($"{title} ({list.Count})");
                if (list.Count == 0) { output.WriteLine("  none"); return; }
                var widths = Enumerable.Range(0, header.Length).Select(i => Math.Max(header[i].Length, list.Max(r => r[i].Length))).ToArray();
                output.WriteLine("  " + string.Join("  ", header.Select((h, i) => h.PadRight(widths[i]))).TrimEnd());
                foreach (var r in list) output.WriteLine("  " + string.Join("  ", r.Select((c, i) => c.PadRight(widths[i]))).TrimEnd());
            }

            var migrations = await trackRead.QueryAsync(Top($"{C("applied_utc")}, {C("plan_id")}, {C("status")}, {C("applied_by")}, {C("git_commit")}", T("migration_log"), $"{C("applied_utc")} DESC"), byConnection);
            Table("applied_plans", "Applied plans (newest first)", ["when (UTC)", "plan", "status", "by", "commit"], migrations.Select(r => new[] { Cell(r[0]), Cell(r[1]), Cell(r[2]), Cell(r[3]), Short(r[4]) }));

            var ddlRows = await trackRead.QueryAsync(Top($"{C("executed_utc")}, {C("object_name")}, {C("status")}, {C("plan_id")}, {C("statement_hash")}", T("ddl_log"), $"{C("executed_utc")} DESC"), byConnection);
            Table("ddl", "DDL (newest first)", ["when (UTC)", "object", "status", "plan", "statement"], ddlRows.Select(r => new[] { Cell(r[0]), Cell(r[1]), Cell(r[2]), Cell(r[3]), Short(r[4]) }));

            var runs = await trackRead.QueryAsync(Top($"{C("started_utc")}, {C("model")}, {C("operation")}, {C("status")}, {C("rows_affected")}, {C("plan_id")}", T("run_log"), $"{C("started_utc")} DESC"), byConnection);
            Table("loads", "Loads (newest first)", ["started (UTC)", "model", "operation", "status", "rows", "plan"], runs.Select(r => new[] { Cell(r[0]), Cell(r[1]), Cell(r[2]), Cell(r[3]), Cell(r[4]), Cell(r[5]) }));

            // each origin of each copy: when it last copied well, and how the latest attempt ended (a run is recorded as `from <origin>`; older records have no origin)
            var transfers = await trackRead.QueryAsync($"SELECT {C("model")}, {C("load_name")}, {C("status")}, {C("started_utc")}, {C("rows_affected")} FROM {T("run_log")} WHERE {C("connection")} = @connection AND {C("operation")} = 'transfer' ORDER BY {C("started_utc")} DESC", byConnection);
            var origins = transfers.GroupBy(r => (Model: Cell(r[0]), Origin: Cell(r[1]).StartsWith("from ", StringComparison.Ordinal) ? Cell(r[1])[5..] : ""))
                .OrderBy(g => g.Key.Model, StringComparer.Ordinal).ThenBy(g => g.Key.Origin, StringComparer.Ordinal)
                .Select(g => (g.Key.Model, g.Key.Origin, Latest: g.First(), Good: g.FirstOrDefault(r => Cell(r[2]) == "ok"))).ToList();
            Table("origins", "Copy origins (last good run, and the latest attempt)", ["model", "origin", "last good (UTC)", "rows", "latest attempt"],
                origins.Select(o => new[] { o.Model, o.Origin, o.Good == null ? "never" : Cell(o.Good[3]), o.Good == null ? "" : Cell(o.Good[4]), $"{Cell(o.Latest[3])} {Cell(o.Latest[2])}" }));

            var versions = await trackRead.QueryAsync($"SELECT {C("object_name")}, COUNT(*), MAX({C("first_seen_utc")}) FROM {T("schema_version")} WHERE {C("connection")} = @connection GROUP BY {C("object_name")} ORDER BY {C("object_name")}", byConnection);
            var recorded = await TrackingStore.LatestShapeHashesAsync(trackRead, scope);
            var schemas = recorded.Keys.Select(k => DdlGenerator.Split(k).SchemaName).Distinct(StringComparer.Ordinal).ToList();
            var live = new Dictionary<string, ObjectShape>();
            foreach (var s in schemas) foreach (var (k, v) in await CatalogReader.ReadObjectsAsync(read, engine, s)) live[k] = v;
            var drifted = new List<string>();
            var projectModels = ProjectContext.Load(root).Project.Sources.Select(s => s.Definition.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var accepted = (await TargetSnapshotReader.ReadAsync(read, trackRead, scope, engine, schemas)).Acknowledged;        // drift an operator has accepted (`ack drift`) is shown, but is not something that needs attention
            bool Accepted(string name) => live.TryGetValue(name, out var l) && accepted.Contains(Acknowledgements.Key(DiagnosticCatalog.ObjectChangedOutsideTool.Code, name, l.ShapeHash));
            Table("objects", "Objects the tool has recorded", ["object", "shapes recorded", "last recorded (UTC)", "now"], versions.Select(r =>
            {
                var name = Cell(r[0]);
                var state = Drift.Classify(live.GetValueOrDefault(name), recorded.GetValueOrDefault(name));
                if (state == ObjectState.OutOfBand && !Accepted(name)) drifted.Add(name);
                // an object the tool built whose model has since left the project stays on the target (the tool never drops one): it is in sync with what was recorded, and nothing builds it any more
                var inProject = projectModels.Contains(name);
                return new[] { name, Cell(r[1]), Cell(r[2]), state switch { ObjectState.InSync => inProject ? "in sync" : "in sync (no model in the project; the tool never drops it)", ObjectState.Missing => "MISSING on the target", ObjectState.OutOfBand => Accepted(name) ? "changed outside the tool (accepted)" : "CHANGED OUTSIDE THE TOOL", _ => "not tracked" } };
            }));

            // ---- column history (DESIGN.md 12.3), from the answers embedded in the applied plans ----
            var (history, unreadable) = await HistoryReader.ReadAsync(trackRead, scope);
            output.WriteLine();
            output.Payload("column_history", history.Select(h => new { model = h.Model, column = h.Column, decision = h.Disposition, text = h.Text, needs_attention = h.NeedsAttention, acknowledgement = h.Acknowledgement == null ? null : new { by = h.Acknowledgement.By, reason = h.Acknowledgement.Reason, utc = h.Acknowledgement.Utc } }).ToList());
            output.WriteLine($"Column history ({history.Count})");
            if (history.Count == 0) output.WriteLine("  none: no applied plan added a column");
            foreach (var h in history) output.WriteLine($"  - {h.Text}");

            var open = new List<string>();
            foreach (var h in history.Where(h => h.NeedsAttention)) open.Add($"{h.Model}.{h.Column}: a backfill was requested and none is recorded (`{ProductInfo.Cli} plan --backfill {h.Model}=<operation>`, or `{ProductInfo.Cli} ack history {h.Model}.{h.Column} --reason ...` to accept it)");
            foreach (var id in unreadable) open.Add($"the plan text recorded for {id} cannot be read back (edited or damaged); its decisions are not in this report");
            foreach (var r in await trackRead.QueryAsync($"SELECT {C("plan_id")}, MAX({C("applied_utc")}) FROM {T("migration_log")} WHERE {C("connection")} = @connection GROUP BY {C("plan_id")} HAVING SUM(CASE WHEN {C("status")} = 'completed' THEN 1 ELSE 0 END) = 0", byConnection))
                open.Add($"plan {Cell(r[0])} never completed (last record {Cell(r[1])} UTC): resume it with `{ProductInfo.Cli} apply --resume`, or plan again");
            // a step that did not finish ok is left out when the same step of the same plan was run again, and finished, later (a resume after a failure or a lost connection)
            foreach (var r in await trackRead.QueryAsync($"SELECT d.{C("object_name")}, d.{C("plan_id")} FROM {T("ddl_log")} d WHERE d.{C("connection")} = @connection AND d.{C("status")} <> 'ok' AND NOT EXISTS (SELECT 1 FROM {T("ddl_log")} later WHERE later.{C("connection")} = d.{C("connection")} AND later.{C("plan_id")} = d.{C("plan_id")} AND later.{C("object_name")} = d.{C("object_name")} AND later.{C("statement_hash")} = d.{C("statement_hash")} AND later.{C("status")} = 'ok' AND later.{C("executed_utc")} >= d.{C("executed_utc")})", byConnection))
                open.Add($"DDL on {Cell(r[0])} in plan {Cell(r[1])} did not finish ok");
            foreach (var r in await trackRead.QueryAsync($"SELECT r.{C("model")}, r.{C("plan_id")} FROM {T("run_log")} r WHERE r.{C("connection")} = @connection AND r.{C("status")} <> 'ok' AND NOT EXISTS (SELECT 1 FROM {T("run_log")} later WHERE later.{C("connection")} = r.{C("connection")} AND later.{C("plan_id")} = r.{C("plan_id")} AND later.{C("step_id")} = r.{C("step_id")} AND later.{C("status")} = 'ok' AND later.{C("started_utc")} >= r.{C("started_utc")})", byConnection))
                open.Add($"load of {Cell(r[0])} in plan {Cell(r[1])} did not finish ok");
            foreach (var o in origins.Where(o => Cell(o.Latest[2]) != "ok"))
                open.Add($"{o.Model}: {(o.Origin.Length > 0 ? $"the origin `{o.Origin}`" : "an origin")} {(o.Good == null ? "has never copied well" : $"last copied well at {Cell(o.Good[3])} UTC")}, and its latest attempt ended `{Cell(o.Latest[2])}` (`{ProductInfo.Cli} plan` and `apply`, or `apply --resume`)");
            foreach (var d in drifted) open.Add($"{d} changed outside the tool (`{ProductInfo.Cli} ack drift {d} --reason ...`, or restore it)");
            output.WriteLine();
            output.Payload("needs_attention", open);
            output.Payload("connection", target);
            output.WriteLine($"Needs attention ({open.Count})");
            foreach (var o in open) output.WriteLine("  - " + o);
            if (open.Count == 0) output.WriteLine("  nothing");
            return open.Count == 0 ? CliApp.ExitOk : CliApp.ExitFindings;
        }).GetAwaiter().GetResult();
    }
}
