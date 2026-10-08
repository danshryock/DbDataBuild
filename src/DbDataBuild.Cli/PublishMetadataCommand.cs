using DbDataBuild.Core;
using DbDataBuild.Execution;

namespace DbDataBuild.Cli;

/// <summary>
/// `dbdatabuild connection publish` (effect: tracking tables only): stores the project and model metadata documents (the ones `metadata` prints) as JSON in the target's tracking
/// schema name, so they can be queried with SQL (`metadata_current`, `metadata_columns`). Only documents that changed are written; no user data is touched.
/// </summary>
internal static class PublishMetadataCommand
{
    public static int Run(CommandSpec spec, string root, string? targetArg, string[] models, TextWriter output, TextWriter error, Func<string, string?> env)
    {
        var ctx = ProjectContext.Load(root);
        var connection = CommandTargets.Resolve(ctx.Config, targetArg, error);
        if (connection == null) return CliApp.ExitUsage;
        var target = connection.Name; var engine = connection.Engine;
        var tracking = CommandTracking.Require(ctx.Config, connection, env, needWrite: true, error, spec.Name);
        output.WriteLine($"{ProductInfo.Cli} {spec.Name}  |  effect: {spec.Effect.Describe()}  |  {spec.Marks}  |  connection: {target}  |  records on {tracking?.Target.Connection ?? "none"}: read {tracking?.Read.Describe() ?? "none"}, write {tracking?.Write?.Describe() ?? "none"}");
        if (tracking?.Write == null) return CliApp.ExitFindings;
        var read = tracking.Read; var write = tracking.Write;

        var selected = ctx.Select(models, error);
        if (selected == null) return CliApp.ExitUsage;
        var invalid = ctx.Diagnostics.Where(d => d.Severity == Severity.Error && d.Code != DiagnosticCatalog.OrphanFile.Code).ToList();
        foreach (var d in invalid) error.Diag(d);
        if (invalid.Count > 0)
        {
            output.WriteLine($"Nothing was stored: {invalid.Count} error(s) in the project (`{ProductInfo.Cli} project compile` shows them).");
            return CliApp.ExitFindings;
        }

        var (commit, _) = GitInfo.Read(root);
        var documents = MetadataPublisher.Collect(ctx, models.Length == 0 ? null : selected.Select(m => m.Source.Definition.Name).ToList(), plan: null);   // no models named: everything, sources no model reads included
        MetadataPublisher.Result result;
        try { result = Task.Run(() => MetadataPublisher.PublishAsync(documents, tracking.Scope, read, write, spec.Name, root, null, commit)).GetAwaiter().GetResult(); }
        catch (GateRefusedException ex) { error.Diag(ex.Diagnostic); return CliApp.ExitFindings; }

        output.Payload("connection", target);
        output.Payload("written", result.Written.Select(d => new { kind = d.Kind, subject = d.Subject, hash = d.Hash }).ToList());
        output.Payload("unchanged", result.Unchanged.Select(d => new { kind = d.Kind, subject = d.Subject, hash = d.Hash }).ToList());
        output.WriteLine($"Stored {result.Written.Count} document(s); {result.Unchanged.Count} already up to date. Query `{tracking.Scope.SchemaName}.metadata_current` and `{tracking.Scope.SchemaName}.metadata_columns` on `{tracking.Target.Connection}`.");
        return CliApp.ExitOk;
    }
}
