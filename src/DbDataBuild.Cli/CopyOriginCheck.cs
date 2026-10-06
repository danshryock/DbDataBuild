using DbDataBuild.Core;
using DbDataBuild.Execution;
using DbDataBuild.Models;
using DbDataBuild.Planning;
using DbDataBuild.State;

namespace DbDataBuild.Cli;

/// <summary>
/// Plan-time check of a copy's origins: each origin's live table against the mapped model that declares it (version skew between the systems of one application). An origin whose table lost a column or changed
/// a type the copy reads is named, with what differs; the copy's `on_mismatch` decides whether that stops the plan (`fail`, the default) or leaves that origin out of it (`skip`), never silently. An origin whose
/// login is not in the environment is not checked, and the plan says so. Only mapped origins are checked: a model's shape is the business of the plan that builds it.
/// </summary>
internal static class CopyOriginCheck
{
    public sealed record Result(IReadOnlyList<Diagnostic> Findings, IReadOnlySet<(string Model, string Origin)> Skipped);

    public static Result Run(ProjectContext ctx, IReadOnlyList<ModelDefinition> copies, string destination, Func<string, string?> env)
    {
        var findings = new List<Diagnostic>();
        var skipped = new HashSet<(string, string)>();
        foreach (var copy in copies)
        {
            var declared = ctx.Project.Descriptors.FirstOrDefault(d => string.Equals(d.Name, copy.From, StringComparison.OrdinalIgnoreCase));
            if (declared == null) continue;
            foreach (var origin in ctx.OriginsOf(copy, destination))
            {
                var (login, missing) = LoginSettings.FromEnvironment(origin.Connection, origin.Engine, Login.Read, env);
                if (login == null)
                {
                    findings.Add(new Diagnostic(DiagnosticCatalog.CopyOriginDiffers, new(copy.From ?? copy.Name, 0, 0), $"{copy.Name}: the origin `{origin.Connection}` was not checked against `{copy.From}`: {missing?.Found}") with { SeverityOverride = Severity.Note });
                    continue;
                }
                var differences = Task.Run(() => Differences(login, origin, declared)).GetAwaiter().GetResult();
                if (differences.Count == 0) continue;
                var text = $"{copy.Name}: `{copy.From}` on `{origin.Connection}` differs from its declaration: {string.Join("; ", differences)}.";
                if (copy.OnMismatch == CopySlice.Skip)
                {
                    skipped.Add((copy.Name, origin.Connection));
                    findings.Add(new Diagnostic(DiagnosticCatalog.CopyOriginDiffers, new(copy.From ?? copy.Name, 0, 0), text + " The origin is left out of the plan (`on_mismatch: skip`).") with { SeverityOverride = Severity.Warning });
                }
                else findings.Add(new Diagnostic(DiagnosticCatalog.CopyOriginDiffers, new(copy.From ?? copy.Name, 0, 0), text));
            }
        }
        return new Result(findings, skipped);
    }

    private static async Task<List<string>> Differences(LoginSettings login, CopyOrigin origin, SourceDescriptor declared)
    {
        await using var read = await ReadSession.OpenAsync(login);
        var (schema, name) = (origin.Table[..origin.Table.IndexOf('.')], origin.Table[(origin.Table.IndexOf('.') + 1)..]);
        var shapes = await CatalogReader.ReadSchemaAsync(read, origin.Engine, schema);
        if (!shapes.TryGetValue(origin.Table, out var shape)) return [$"the table does not exist there"];
        var live = SourceImport.Describe(origin.Engine, shape, null);
        var now = SourceImport.ToDescriptor(live, declared);
        // what a copy reads: a column that is gone, a type that changed, a column that can now be NULL where the declaration says it cannot; an added column is not read, so it is no difference
        return SourceImport.Compare(declared, now)
            .Where(c => c.Kind is SourceChangeKind.ColumnRemoved or SourceChangeKind.TypeChanged || c is { Kind: SourceChangeKind.NullabilityChanged, Detail: "now nullable" })
            .Select(c => c.Kind switch
            {
                SourceChangeKind.ColumnRemoved => $"column `{c.Column}` is gone",
                SourceChangeKind.TypeChanged => $"column `{c.Column}` changed type ({c.Detail})",
                _ => $"column `{c.Column}` can now be NULL",
            }).ToList();
    }
}
