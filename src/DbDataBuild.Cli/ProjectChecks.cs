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
            var sql = File.ReadAllText(Path.Combine(projectRoot ?? Directory.GetCurrentDirectory(), source.QueryFile));
            var targets = (source.Definition.Targets ?? config.DefaultTargets).Where(t => onlyTargets == null || onlyTargets.Contains(t)).ToList();
            // with lowering on, the matrix lint and the transpile work on the lowered query (what actually runs), and findings point at its committed artifact
            var body = sql;
            string? bodyFile = null;
            if (lowering is { Enabled: true })
            {
                var (lowered, error) = lowering.Lower(source, sql);
                if (lowered == null) { diagnostics.Add(error!); continue; }
                body = lowered.Sql;
                bodyFile = $"rendered/{lowered.ArtifactPath}";
            }
            // each target is linted on the query it will actually get: the lowered one with that target's rules applied
            foreach (var t in targets) diagnostics.AddRange(linter.Lint(DbDataBuild.Targets.Rules.TargetRules.Apply(body, t).Sql, bodyFile ?? source.QueryFile, [t], config));
            // every declared model x target x operation pair must render (in memory; nothing is written), and the scripts must pass offline validation
            diagnostics.AddRange(renderer.Render(source.Definition, body, source.QueryFile, targets, bodyFile).Diagnostics.Where(d => d.Code != DiagnosticCatalog.SqlParseFailure.Code));
            foreach (var target in targets) HookLoader.Load(source, config, target, projectRoot ?? Directory.GetCurrentDirectory(), diagnostics);   // missing or unparseable hook scripts
        }
        if (config.LintIndexes)
            foreach (var source in sources)
                diagnostics.AddRange(IndexAdvice(source, (source.Definition.Targets ?? config.DefaultTargets).Where(t => onlyTargets == null || onlyTargets.Contains(t)).ToList()));
        diagnostics.AddRange(CollationChecker.Check(config, sources));
        return diagnostics;
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
}

