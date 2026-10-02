using DbDataBuild.Core;
using DbDataBuild.Execution;

namespace DbDataBuild.Cli;

/// <summary>
/// `dbdatabuild publish-metadata` (effect: tracking tables only): stores the project and model metadata documents (the ones `metadata` prints) as JSON in the target's tracking
/// schema, so they can be queried with SQL (`metadata_current`, `metadata_columns`). Only documents that changed are written; no user data is touched.
/// </summary>
internal static class PublishMetadataCommand
{
    public static int Run(CommandSpec spec, string root, string? targetArg, string[] models, TextWriter output, TextWriter error, Func<string, string?> env)
    {
        var ctx = ProjectContext.Load(root);
        var target = CommandTargets.Resolve(ctx.Config, targetArg, error);
        if (target == null) return CliApp.ExitUsage;
        var (read, readMissing) = LoginSettings.FromEnvironment(target, Login.Read, env);
        var (write, writeMissing) = LoginSettings.FromEnvironment(target, Login.Write, env);
        output.WriteLine($"{ProductInfo.Cli} {spec.Name}  |  effect: {spec.Effect.Describe()}  |  target: {target}  |  login: read {read?.Describe() ?? "none"}, write {write?.Describe() ?? "none"}");
        foreach (var m in new[] { readMissing, writeMissing }.OfType<Diagnostic>()) error.Diag(m);
        if (read == null || write == null) return CliApp.ExitFindings;

        var selected = ctx.Select(models, error);
        if (selected == null) return CliApp.ExitUsage;
        var invalid = ctx.Diagnostics.Where(d => d.Severity == Severity.Error && d.Code != DiagnosticCatalog.OrphanFile.Code).ToList();
        foreach (var d in invalid) error.Diag(d);
        if (invalid.Count > 0)
        {
            output.WriteLine($"Nothing was stored: {invalid.Count} error(s) in the project (`{ProductInfo.Cli} validate` shows them).");
            return CliApp.ExitFindings;
        }

        var (commit, _) = GitInfo.Read(root);
        var documents = MetadataPublisher.Collect(ctx, models.Length == 0 ? null : selected.Select(m => m.Source.Definition.Name).ToList(), plan: null);   // no models named: everything, sources no model reads included
        MetadataPublisher.Result result;
        try { result = Task.Run(() => MetadataPublisher.PublishAsync(documents, target, ctx.Config.TrackingSchema, read, write, spec.Name, root, null, commit)).GetAwaiter().GetResult(); }
        catch (GateRefusedException ex) { error.Diag(ex.Diagnostic); return CliApp.ExitFindings; }

        output.Payload("target", target);
        output.Payload("written", result.Written.Select(d => new { kind = d.Kind, subject = d.Subject, hash = d.Hash }).ToList());
        output.Payload("unchanged", result.Unchanged.Select(d => new { kind = d.Kind, subject = d.Subject, hash = d.Hash }).ToList());
        output.WriteLine($"Stored {result.Written.Count} document(s); {result.Unchanged.Count} already up to date. Query `{ctx.Config.TrackingSchema}.metadata_current` and `{ctx.Config.TrackingSchema}.metadata_columns`.");
        return CliApp.ExitOk;
    }
}
