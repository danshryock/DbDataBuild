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
    public static List<Diagnostic> Run(IReadOnlyList<ModelSource> sources, ProjectConfig config, IReadOnlyList<string>? onlyTargets, string? projectRoot = null)
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
            diagnostics.AddRange(linter.Lint(sql, source.QueryFile, targets, config));
            // every declared model x target x operation pair must render (in memory; nothing is written), and the scripts must pass offline validation
            diagnostics.AddRange(renderer.Render(source.Definition, sql, source.QueryFile, targets).Diagnostics.Where(d => d.Code != DiagnosticCatalog.SqlParseFailure.Code));
        }
        diagnostics.AddRange(CollationChecker.Check(config, sources));
        return diagnostics;
    }
}
