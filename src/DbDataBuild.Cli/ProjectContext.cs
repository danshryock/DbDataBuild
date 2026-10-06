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
    public required ModelLowering Lowering { get; init; }

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
        return new ProjectContext { Root = root, Project = project, Config = config, Matrix = matrix, Linter = linter, Renderer = new LoadRenderer(matrix, linter, config), Diagnostics = diags, Lowering = new ModelLowering(project.Models, project.Descriptors, config) };
    }

    /// <summary>
    /// Renders a model's load operations. With lowering on, the query is bound by DuckDB and lowered first, the matrix lint and the transpile work on the lowered query,
    /// and the lowered artifact is one of the files; a query that cannot be lowered renders nothing and says why (DDB-324).
    /// </summary>
    public (RenderResult Result, string BodySql) RenderModel(ModelSource source, string authorSql, IReadOnlyList<string> targets)
    {
        if (!Lowering.Enabled) return (Renderer.Render(source.Definition, authorSql, source.QueryFile, targets), authorSql);
        var (lowered, error) = Lowering.Lower(source, authorSql);
        if (lowered == null) return (new RenderResult([], [], [error!]), authorSql);
        var result = Renderer.Render(source.Definition, lowered.Sql, source.QueryFile, targets, bodyFile: $"rendered/{lowered.ArtifactPath}");
        var files = result.Files.Append(new RenderedFile(lowered.ArtifactPath, lowered.ArtifactText)).OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        return (result with { Files = files }, lowered.Sql);
    }

    /// <summary>The targets a model is built for: its own `connections:`, else the project default.</summary>
    public IReadOnlyList<string> TargetsOf(ModelDefinition model) => model.Targets ?? Config.DefaultConnections;

    private DependencyGraph? graph;

    /// <summary>Which tables each model reads, from parsing the queries (names only; no DuckDB, no target).</summary>
    public DependencyGraph Graph => graph ??= ProjectGraph.Build(this);

    /// <summary>
    /// The models the arguments select, as selectors (DESIGN.md 9.9): a model name, a `.sql` or `.yml` path, or a directory under models/, with the graph operators `+model`, `model+`, `2+model`, `model+1`, `@model`, the filters
    /// `kind:`, `connection:`, `path:` and `changed:<git ref>`, a comma for an intersection, and `exclude:<selector>` to take models out again. Several arguments are a union; every model when none are given.
    /// Returns null and writes the problem when a term selects nothing.
    /// </summary>
    public List<LoadedModel>? Select(IReadOnlyList<string> args, TextWriter error)
    {
        var all = Project.Sources.OrderBy(s => s.Definition.Name, StringComparer.Ordinal).ToList();
        var chosen = new List<string>();
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var includes = args.Where(a => !a.StartsWith("exclude:", StringComparison.Ordinal)).ToList();
        if (includes.Count == 0) chosen.AddRange(all.Select(s => s.Definition.Name));
        foreach (var arg in includes)
        {
            var set = SelectTerm(arg, all, error);
            if (set == null) return null;
            foreach (var name in set) if (!chosen.Contains(name, StringComparer.OrdinalIgnoreCase)) chosen.Add(name);
        }
        foreach (var arg in args.Where(a => a.StartsWith("exclude:", StringComparison.Ordinal)))
        {
            var set = SelectTerm(arg["exclude:".Length..], all, error);
            if (set == null) return null;
            excluded.UnionWith(set);
        }
        var byName = all.ToDictionary(s => s.Definition.Name, StringComparer.OrdinalIgnoreCase);
        return chosen.Where(n => !excluded.Contains(n) && byName.ContainsKey(n)).Select(n => byName[n]).DistinctBy(s => s.Definition.Name)
            .Select(s => new LoadedModel(s, File.ReadAllText(Path.Combine(Root, s.QueryFile)))).ToList();
    }

    /// <summary>One argument: terms joined by commas are intersected, each term is an optional operator around a core.</summary>
    private HashSet<string>? SelectTerm(string arg, List<ModelSource> all, TextWriter error)
    {
        HashSet<string>? result = null;
        foreach (var part in arg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var term = ModelSelector.Parse(part);
            var core = ResolveCore(term.Core, all, error);
            if (core == null) return null;
            var set = term is { Upstream: false, Downstream: false, At: false } ? new HashSet<string>(core, StringComparer.OrdinalIgnoreCase) : ModelSelector.Expand(term, core, Graph);
            if (result == null) result = set; else result.IntersectWith(set);
        }
        return result ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private List<string>? ResolveCore(string core, List<ModelSource> all, TextWriter error)
    {
        List<string>? Found(IEnumerable<ModelSource> models, string what)
        {
            var names = models.Select(s => s.Definition.Name).ToList();
            if (names.Count == 0) { error.WriteLine($"`{core}` selects no model ({what})."); return null; }
            return names;
        }
        if (core is "all" or "*") return all.Select(s => s.Definition.Name).ToList();
        if (core.StartsWith("kind:", StringComparison.Ordinal)) { var k = core[5..]; return Found(all.Where(s => string.Equals(s.Definition.KindType, k, StringComparison.OrdinalIgnoreCase)), $"no model has the kind `{k}`"); }
        if (core.StartsWith("connection:", StringComparison.Ordinal)) { var t = core[11..]; return Found(all.Where(s => TargetsOf(s.Definition).Contains(t, StringComparer.OrdinalIgnoreCase)), $"no model is built for `{t}`"); }
        if (core.StartsWith("path:", StringComparison.Ordinal)) { var d = core[5..].Replace('\\', '/').Trim('/'); return Found(all.Where(s => s.QueryFile.StartsWith(d + "/", StringComparison.Ordinal)), $"no model is under `{d}/`"); }
        if (core.StartsWith("changed:", StringComparison.Ordinal))
        {
            var (files, problem) = GitInfo.ChangedFiles(Root, core[8..]);
            if (files == null) { error.WriteLine(problem); return null; }
            var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (files.Contains(ProductInfo.ConfigFile)) changed.UnionWith(all.Select(s => s.Definition.Name));                    // the project settings reach every model
            foreach (var s in all.Where(s => files.Contains(s.QueryFile) || files.Contains(s.DefinitionFile))) changed.Add(s.Definition.Name);
            foreach (var d in Project.Descriptors.Where(d => files.Contains(ModelSourcePath(d.Name)))) changed.UnionWith(Graph.ReadBy(d.Name));      // a changed source changes what reads it
            return changed.OrderBy(n => n, StringComparer.Ordinal).ToList();                                                          // nothing changed is a valid, empty answer
        }

        var normalized = core.Replace('\\', '/');
        var byName = all.Where(s => string.Equals(s.Definition.Name, core, StringComparison.OrdinalIgnoreCase)).ToList();
        var byPath = all.Where(s => normalized.EndsWith(s.QueryFile, StringComparison.Ordinal) || normalized.EndsWith(s.DefinitionFile, StringComparison.Ordinal) ||
                                    (Path.GetFullPath(Path.Combine(Root, s.QueryFile)) == Path.GetFullPath(core)) || (Path.GetFullPath(Path.Combine(Root, s.DefinitionFile)) == Path.GetFullPath(core))).ToList();
        var byDir = all.Where(s => normalized.TrimEnd('/') is var d && s.QueryFile.StartsWith(d + "/", StringComparison.Ordinal)).ToList();
        var found = byName.Count > 0 ? byName : byPath.Count > 0 ? byPath : byDir;
        if (found.Count == 0) { error.WriteLine($"`{core}` does not name a valid model, a model file, or a directory containing models."); return null; }
        return found.Select(s => s.Definition.Name).ToList();
    }

    private static string ModelSourcePath(string name) => $"{ProjectValidator.ModelsDir}/{name.Replace('.', '/')}.yml";
}
