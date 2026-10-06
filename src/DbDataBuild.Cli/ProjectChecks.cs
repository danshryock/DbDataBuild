using DbDataBuild.Core;
using DbDataBuild.Models;
using DbDataBuild.Sql.Matrix;
using DbDataBuild.Targets.Rendering;

namespace DbDataBuild.Cli;

/// <summary>
/// The offline checks of `validate`, shared with `plan` (DESIGN.md 13, layers 2 to 4): matrix lint per declared target, every declared model x target x
/// operation pair renders and passes offline validation, and the collation profile holds. Loading and per-file schema checks are done earlier by the loaders.
/// </summary>
internal static class ProjectChecks
{
    /// <param name="onlyTargets">Restrict lint and rendering to these targets (null: each model's own targets).</param>
    public static List<Diagnostic> Run(IReadOnlyList<ModelSource> sources, ProjectConfig config, IReadOnlyList<string>? onlyTargets, string? projectRoot = null, ModelLowering? lowering = null)
    {
        var matrixDiags = new List<Diagnostic>();
        var matrix = MatrixLoader.LoadEmbedded(matrixDiags);
        if (matrixDiags.Count > 0) throw new InvalidOperationException("The embedded support matrix is invalid: " + string.Join("; ", matrixDiags.Select(d => d.Found)));
        var linter = new MatrixLinter(matrix);
        var renderer = new LoadRenderer(matrix, linter, config);
        var diagnostics = new List<Diagnostic>();
        foreach (var source in sources)
        {
            foreach (var problem in source.QueryParameterProblems(projectRoot ?? Directory.GetCurrentDirectory(), config))
                diagnostics.Add(new Diagnostic(DiagnosticCatalog.InvalidValue, new(source.QueryFile, 0, 0), $"{source.Definition.Name}: {problem}."));
            if (source.QueryParameterProblems(projectRoot ?? Directory.GetCurrentDirectory(), config).Count > 0) continue;
            var sql = source.ReadQuery(projectRoot ?? Directory.GetCurrentDirectory(), config);
            var targets = (source.Definition.Targets ?? config.DefaultConnections).Where(t => onlyTargets == null || onlyTargets.Contains(t)).ToList();
            // with lowering on, the matrix lint and the transpile work on the lowered query (what actually runs), and findings point at its committed artifact
            var body = sql;
            string? bodyFile = null;
            if (lowering is { Enabled: true })
            {
                var (lowered, error) = lowering.Lower(source, sql, source.QueryParameterList(projectRoot ?? Directory.GetCurrentDirectory(), config));
                if (lowered == null) { diagnostics.Add(error!); continue; }
                body = lowered.Sql;
                bodyFile = $"rendered/{lowered.ArtifactPath}";
            }
            // each target is linted on the query it will actually get: the lowered one with that target's rules applied
            var rewrites = RewriteCatalog.For(config, source.Definition, diagnostics);
            foreach (var t in targets) diagnostics.AddRange(linter.Lint(DbDataBuild.Targets.Rules.TargetRules.Apply(body, config.EngineOf(t) ?? t, rewrites, config.TargetVersions.TryGetValue(t, out var tv) ? tv : null).Sql, bodyFile ?? source.QueryFile, [t], config));
            // every declared model x target x operation pair must render (in memory; nothing is written), and the scripts must pass offline validation
            if (config.LintSlices) diagnostics.AddRange(SliceAdvice(source, targets, body));
            diagnostics.AddRange(renderer.Render(source.Definition, body, source.QueryFile, targets, bodyFile, source.QueryParameterList(projectRoot ?? Directory.GetCurrentDirectory(), config)).Diagnostics.Where(d => d.Code != DiagnosticCatalog.SqlParseFailure.Code));
            foreach (var target in targets) HookLoader.Load(source, config, target, projectRoot ?? Directory.GetCurrentDirectory(), diagnostics);   // missing or unparseable hook scripts
        }
        if (config.LintIndexes)
            foreach (var source in sources)
                diagnostics.AddRange(IndexAdvice(source, (source.Definition.Targets ?? config.DefaultConnections).Where(t => onlyTargets == null || onlyTargets.Contains(t)).ToList()));
        diagnostics.AddRange(CollationChecker.Check(config, sources));
        return diagnostics;
    }

    /// <summary>
    /// A query runs on one connection (DDB-231): every table a model reads must exist on each connection the model is built on, as a mapped model declared there or a model (or copy) built there. The staging
    /// table of a copy is its own. Reads of a table the project does not know are reported by the lowering (DDB-218), not here.
    /// </summary>
    public static List<Diagnostic> Reachability(ProjectContext ctx, IReadOnlyList<string>? onlyTargets)
    {
        var found = new List<Diagnostic>();
        var config = ctx.Config;
        IReadOnlyList<string>? ConnectionsOf(string table)
        {
            if (ctx.Project.Sources.FirstOrDefault(s => string.Equals(s.Definition.Name, table, StringComparison.OrdinalIgnoreCase)) is { } model) return ctx.TargetsOf(model.Definition);
            if (ctx.Project.AllDescriptors.FirstOrDefault(d => string.Equals(d.Name, table, StringComparison.OrdinalIgnoreCase)) is { } mapped) return mapped.Connections ?? config.DefaultConnections;
            return null;
        }
        foreach (var source in ctx.Project.Sources.OrderBy(s => s.Definition.Name, StringComparer.Ordinal))
        {
            var targets = ctx.TargetsOf(source.Definition).Where(t => onlyTargets == null || onlyTargets.Contains(t)).ToList();
            foreach (var read in ctx.Graph.Reads(source.Definition.Name))
                if (ConnectionsOf(read) is { } where)
                    foreach (var target in targets.Where(t => !where.Contains(t, StringComparer.Ordinal)))
                        found.Add(new Diagnostic(DiagnosticCatalog.ModelReadsAnotherConnection, new(source.DefinitionFile, 0, 0),
                            $"{source.Definition.Name} is built on `{target}` and reads `{read}`, which is on {string.Join(", ", where.Select(w => $"`{w}`"))}, not on `{target}`.",
                            Fix: $"Copy `{read}` to `{target}` (a model of `kind: {{type: copy, from: {read}}}` on `{target}`) and read the copy, or build {source.Definition.Name} on {where[0]}."));
        }
        return found;
    }

    /// <summary>Index lint (DDB-223, DDB-224): advice only, with the exact index to declare. A model silences single codes with `lint_ignore`.</summary>
    internal static IEnumerable<Diagnostic> IndexAdvice(ModelSource source, IReadOnlyList<string> targets)
    {
        var def = source.Definition;
        foreach (var a in IndexAdvisor.For(def, targets).Where(a => !def.LintIgnore.Contains(a.Code)))
        {
            var columns = string.Join(", ", a.Columns);
            var what = a.Reason switch
            {
                IndexReason.MergeKey => $"loads by key ({columns})",
                IndexReason.TimeColumn => $"loads by the time column ({columns})",
                IndexReason.Watermark => $"reads its watermark column ({columns})",
                _ => $"deletes by the range column ({columns})",
            };
            var found = a.Existing != null
                ? $"{def.Name} {what}; index `{a.Existing}` leads with it but is not declared unique, so the engine will not reject a duplicate key ({string.Join(", ", a.Targets)})."
                : $"{def.Name} {what}, but no declared index leads with it, so each load scans the table ({string.Join(", ", a.Targets)}).";
            var fix = a.Existing != null
                ? $"Add `unique: true` to index `{a.Existing}` if you want the engine to enforce the key; leaving it is allowed."
                : $"Add under `indexes:` in {source.DefinitionFile}:  {Models.IndexAdvisor.Yaml(a)}";
            yield return new Diagnostic(a.Severity == Severity.Warning ? DiagnosticCatalog.MergeKeyNotIndexed : DiagnosticCatalog.LoadColumnNotIndexed, new(source.DefinitionFile, 0, 0), found, Fix: fix);
        }
    }

    /// <summary>
    /// Slice lint (DDB-225): for each load that selects its slice from the finished query, whether the filter can be applied below the query's aggregates and windows. Advice only;
    /// the query is never rewritten. A model silences it with `lint_ignore`.
    /// </summary>
    internal static IEnumerable<Diagnostic> SliceAdvice(ModelSource source, IReadOnlyList<string> targets, string body)
    {
        var def = source.Definition;
        if (def.LintIgnore.Contains(DiagnosticCatalog.LoadSliceNotPushable.Code)) yield break;
        var done = new HashSet<(string Operation, string Column)>();
        foreach (var target in targets)
            foreach (var op in LoadPlan.For(def, target))
            {
                var (column, what) = op.Strategy switch
                {
                    LoadStrategies.WatermarkAppend when op.Watermark?.Column is { } w => (w, "loads rows at or after its watermark"),
                    LoadStrategies.DeleteInsertByRange => (op.Column ?? def.TimeColumn, "reloads a range"),
                    _ => (null, ""),
                };
                if (column == null || !done.Add((op.Name, column))) continue;
                var blockers = DbDataBuild.Sql.Analysis.SliceAnalyzer.Analyze(body, column);
                if (blockers.Count == 0) continue;
                yield return new Diagnostic(DiagnosticCatalog.LoadSliceNotPushable, new(source.DefinitionFile, 0, 0),
                    $"{def.Name} / {op.Name} {what} by `{column}`, which is applied to the finished query, but {string.Join("; and ", blockers.Select(b => b.Detail))}. " +
                    "Every load therefore does the work for the whole history and keeps only the slice.",
                    Fix: "Slice by a grouping or partition column, or use a strategy that does not slice the finished result (for example filter the source inside the query yourself and use full_replace or a key-based load). Silence this with `lint_ignore: [DDB-225]` if the cost is acceptable.");
            }
    }
}

