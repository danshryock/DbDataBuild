using DbDataBuild.Core;
using DbDataBuild.Models;
using DbDataBuild.Sql.Matrix;
using DbDataBuild.Targets.Rendering;

namespace DbDataBuild.Cli;

internal sealed record LoadedModel(ModelSource Source, string Sql);

/// <summary>Everything an offline command needs from a project: models, sources, config, the matrix and a renderer.</summary>
internal sealed class ProjectContext
{
    public required string Root { get; init; }
    public required ProjectValidationResult Project { get; init; }
    public required ProjectConfig Config { get; init; }
    public required SupportMatrix Matrix { get; init; }
    public required MatrixLinter Linter { get; init; }
    public required LoadRenderer Renderer { get; init; }
    public required List<Diagnostic> Diagnostics { get; init; }

    public static ProjectContext Load(string root)
    {
        var diags = new List<Diagnostic>();
        var project = ProjectValidator.Validate(root);
        diags.AddRange(project.Diagnostics);
        var config = ProjectConfigLoader.LoadFromProject(root, diags);

        var matrixDiags = new List<Diagnostic>();
        var matrix = MatrixLoader.LoadEmbedded(matrixDiags);
        if (matrixDiags.Count > 0) throw new InvalidOperationException("The embedded support matrix is invalid: " + string.Join("; ", matrixDiags.Select(d => d.Found)));
        var linter = new MatrixLinter(matrix);
        return new ProjectContext { Root = root, Project = project, Config = config, Matrix = matrix, Linter = linter, Renderer = new LoadRenderer(matrix, linter, config), Diagnostics = diags };
    }

    /// <summary>The targets a model is built for: its own `targets:`, else the project default.</summary>
    public IReadOnlyList<string> TargetsOf(ModelDefinition model) => model.Targets ?? Config.DefaultTargets;

    /// <summary>
    /// The models the arguments select (model names such as marts.fct_orders, or .sql/.yml paths under models/); every model when none are given.
    /// Returns null and writes the problem when an argument selects nothing.
    /// </summary>
    public List<LoadedModel>? Select(IReadOnlyList<string> args, TextWriter error)
    {
        var all = Project.Sources.OrderBy(s => s.Definition.Name, StringComparer.Ordinal).ToList();
        var chosen = new List<ModelSource>();
        if (args.Count == 0) chosen.AddRange(all);
        foreach (var arg in args)
        {
            var normalized = arg.Replace('\\', '/');
            var byName = all.Where(s => string.Equals(s.Definition.Name, arg, StringComparison.OrdinalIgnoreCase)).ToList();
            var byPath = all.Where(s => normalized.EndsWith(s.QueryFile, StringComparison.Ordinal) || normalized.EndsWith(s.DefinitionFile, StringComparison.Ordinal) ||
                                        (Path.GetFullPath(Path.Combine(Root, s.QueryFile)) == Path.GetFullPath(arg)) || (Path.GetFullPath(Path.Combine(Root, s.DefinitionFile)) == Path.GetFullPath(arg))).ToList();
            var byDir = all.Where(s => normalized.TrimEnd('/') is var d && s.QueryFile.StartsWith(d + "/", StringComparison.Ordinal)).ToList();
            var found = byName.Count > 0 ? byName : byPath.Count > 0 ? byPath : byDir;
            if (found.Count == 0) { error.WriteLine($"`{arg}` does not name a valid model, a model file, or a directory containing models."); return null; }
            chosen.AddRange(found);
        }
        return chosen.DistinctBy(s => s.Definition.Name).Select(s => new LoadedModel(s, File.ReadAllText(Path.Combine(Root, s.QueryFile)))).ToList();
    }
}
