using DbDataBuild.Core;
using DbDataBuild.Execution;
using DbDataBuild.Planning;
using DbDataBuild.Sql.Analysis;
using DbDataBuild.State;
using DbDataBuild.Targets.Ddl;

namespace DbDataBuild.Cli;

/// <summary>
/// `dbdatabuild ack drift &lt;object&gt;` and `ack definition &lt;model&gt;` (DESIGN.md 9.1, 11). Effect class: tracking tables only. A person's decision is recorded with the exact hash it
/// is about, so it clears exactly that block and a later, different change blocks again. It changes no user data.
/// </summary>
internal static class AckCommand
{
    public static int Run(CommandSpec spec, string root, string kind, string name, string? reason, string? targetArg, TextWriter output, TextWriter error, Func<string, string?> env)
    {
        var ctx = ProjectContext.Load(root);
        var target = CommandTargets.Resolve(ctx.Config, targetArg, error);
        if (target == null) return CliApp.ExitUsage;
        if (kind is not ("drift" or "definition" or "history")) { error.WriteLine($"Unknown acknowledgement `{kind}`. One of: drift, definition, history."); return CliApp.ExitUsage; }
        if (string.IsNullOrWhiteSpace(reason)) { error.WriteLine("--reason is required: an acknowledgement is recorded with who made it and why."); return CliApp.ExitUsage; }

        var (read, readMissing) = LoginSettings.FromEnvironment(target, Login.Read, env);
        var (write, writeMissing) = LoginSettings.FromEnvironment(target, Login.Write, env);
        output.WriteLine($"{ProductInfo.Cli} {spec.Name}  |  effect: {spec.Effect.Describe()}  |  target: {target}  |  login: read {read?.Describe() ?? "none"}, write {write?.Describe() ?? "none"}");
        foreach (var m in new[] { readMissing, writeMissing }.OfType<Diagnostic>()) error.Write(DiagnosticFormatter.Format(m));
        if (read == null || write == null) return CliApp.ExitFindings;

        string code, detail;
        var schema = ctx.Config.TrackingSchema;
        var (objSchema, _) = DdlGenerator.Split(kind == "history" ? name[..Math.Max(name.LastIndexOf('.'), 0)] : name);
        return Task.Run(async () =>
        {
            await using var reader = await ReadSession.OpenAsync(read);
            var status = await TrackingStore.StatusAsync(reader, target, schema);
            if (status.AsDiagnostic(schema) is { } notReady) { error.Write(DiagnosticFormatter.Format(notReady)); return CliApp.ExitFindings; }

            if (kind == "history")
            {
                // `model.column`: accept the recorded history as it is, without changing data
                var (entries, _) = await HistoryReader.ReadAsync(reader, target, schema);
                var open = entries.Where(e => $"{e.Model}.{e.Column}" == name && e.AckKey != null && e.Acknowledgement == null).ToList();
                if (open.Count == 0)
                {
                    error.WriteLine(entries.Any(e => $"{e.Model}.{e.Column}" == name && e.Acknowledgement != null)
                        ? $"`{name}` is already acknowledged. Nothing was recorded."
                        : $"`{name}` has no unacknowledged history inconsistency: there is nothing to acknowledge.");
                    return entries.Any(e => $"{e.Model}.{e.Column}" == name && e.Acknowledgement != null) ? CliApp.ExitOk : CliApp.ExitFindings;
                }
                var historyRun = Guid.NewGuid();
                using var historyLog = new FileStatementLog(Path.Combine(root, InitCommand.StatementLogDir), spec.Name, historyRun);
                await using var historyGate = await MutationGate.OpenAsync(write, spec.Name, StatementKind.Tracking, historyLog, historyRun);
                foreach (var e in open)
                {
                    var parts = e.AckKey!.Split('|');
                    await AuditLog.AcknowledgeAsync(historyGate, target, schema, "ack", parts[1], parts[0], parts[2], write.User ?? Environment.UserName, reason!);
                }
                output.WriteLine($"Recorded: {open.Count} acknowledgement(s) for {name} by {write.User ?? Environment.UserName}. The report still shows the history, marked as accepted, and no longer lists it as needing attention.");
                return CliApp.ExitOk;
            }

            if (kind == "drift")
            {
                var live = (await CatalogReader.ReadSchemaAsync(reader, target, objSchema)).GetValueOrDefault(name);
                var recorded = (await TrackingStore.LatestShapeHashesAsync(reader, target, schema)).GetValueOrDefault(name);
                var state = Drift.Classify(live, recorded);
                if (state != ObjectState.OutOfBand)
                {
                    error.WriteLine($"`{name}` is {(state == ObjectState.InSync ? "in sync with the last recorded shape" : state == ObjectState.Missing ? "missing on the target" : "not tracked")}: there is no change to acknowledge.");
                    return CliApp.ExitFindings;
                }
                (code, detail) = (DiagnosticCatalog.ObjectChangedOutsideTool.Code, live!.ShapeHash);
            }
            else
            {
                var model = ctx.Project.Sources.FirstOrDefault(s => s.Definition.Name == name);
                if (model == null) { error.WriteLine($"`{name}` is not a model of this project."); return CliApp.ExitUsage; }
                var hash = AstHasher.Hash(File.ReadAllText(Path.Combine(root, model.QueryFile))).Hash ?? "";
                var last = (await TargetSnapshotReader.ReadAsync(reader, target, schema, [objSchema])).LastLoadDefinitionHashes.GetValueOrDefault(name);
                if (last == null || last == hash)
                {
                    error.WriteLine($"`{name}` has {(last == null ? "no recorded load" : "an unchanged query since its last load")}: there is no change to acknowledge.");
                    return CliApp.ExitFindings;
                }
                (code, detail) = (DiagnosticCatalog.LoadDefinitionChanged.Code, hash);
            }

            var snapshot = await TargetSnapshotReader.ReadAsync(reader, target, schema, [objSchema]);
            if (snapshot.Acknowledged.Contains(Acknowledgements.Key(code, name, detail)))
            {
                output.WriteLine($"{code} on {name} for hash {detail[..Math.Min(12, detail.Length)]} is already acknowledged. Nothing was recorded.");
                return CliApp.ExitOk;
            }

            var runId = Guid.NewGuid();
            using var log = new FileStatementLog(Path.Combine(root, InitCommand.StatementLogDir), spec.Name, runId);
            await using var gate = await MutationGate.OpenAsync(write, spec.Name, StatementKind.Tracking, log, runId);
            await AuditLog.AcknowledgeAsync(gate, target, schema, "ack", name, code, detail, write.User ?? Environment.UserName, reason!);
            output.WriteLine($"Recorded: {code} on {name} for hash {detail[..Math.Min(12, detail.Length)]} acknowledged by {write.User ?? Environment.UserName}. The next plan will accept exactly this change.");
            return CliApp.ExitOk;
        }).GetAwaiter().GetResult();
    }
}
