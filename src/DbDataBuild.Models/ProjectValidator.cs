using DbDataBuild.Core;
using DbDataBuild.Models.Yaml;

namespace DbDataBuild.Models;

/// <summary>A valid model definition with the project-relative paths of its two files.</summary>
public sealed record ModelSource(ModelDefinition Definition, string DefinitionFile, string QueryFile)
{
    /// <summary>The settings this model took from a project file above it (`defaults:` of the root file or of a folder's `_dbdatabuild.yml`), with the file and line each was written on. Empty when the model's own file says everything.</summary>
    public IReadOnlyList<SettingOrigin> Inherited { get; init; } = [];
}

/// <summary>One scalar of a model's effective settings that was written in another file, by dotted path (`kind.type`, `connections[1]`).</summary>
public sealed record SettingOrigin(string Path, string File, int Line, string Value);

public sealed record ProjectValidationResult(
    IReadOnlyList<ModelSource> Sources, IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<SourceDescriptor>? SourceDescriptors = null)
{
    public IReadOnlyList<SourceDescriptor> Descriptors => SourceDescriptors ?? [];
    public IReadOnlyList<ModelDefinition> Models => Sources.Select(s => s.Definition).ToList();
    public bool HasErrors => Diagnostics.Any(d => d.Severity == Severity.Error);
}

/// <summary>Offline validation of every model under <c>models/</c>. Reads files only; never writes, never connects.</summary>
public static class ProjectValidator
{
    public const string ModelsDir = "models";
    public const string SourcesDir = "sources";

    public static ProjectValidationResult Validate(string projectRoot, ProjectConfig? config = null)
    {
        var diags = new List<Diagnostic>();
        // the connections a model may name come from the project's configuration (a problem in the configuration itself is reported where the configuration is loaded)
        var connections = (config ?? ProjectConfigLoader.LoadFromProject(projectRoot, new List<Diagnostic>())).Connections.Keys.ToHashSet(StringComparer.Ordinal);
        var models = new List<ModelSource>();
        var modelsRoot = Path.Combine(projectRoot, ModelsDir);
        if (!Directory.Exists(modelsRoot))
        {
            // the sources still load: `seed` and `import-sources` need them in a project that has no models yet
            diags.Add(new Diagnostic(DiagnosticCatalog.MissingKey, new(ModelsDir, 0, 0),
                $"Directory `{ModelsDir}/` was not found under {projectRoot}.", Fix: $"Create `{ModelsDir}/` or run from the project root."));
            return new(models, diags, LoadSources(projectRoot, diags));
        }

        var files = Directory.EnumerateFiles(modelsRoot, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".yml", StringComparison.Ordinal) || f.EndsWith(".sql", StringComparison.Ordinal))
            .Where(f => Path.GetFileName(f) != ProductInfo.FolderConfigFile)         // a project file, not a model
            .Select(f => Path.GetRelativePath(projectRoot, f).Replace('\\', '/'))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        var set = files.ToHashSet(StringComparer.Ordinal);
        var folders = new FolderLayers(projectRoot, diags);
        var effectiveConfig = config ?? ProjectConfigLoader.LoadFromProject(projectRoot, new List<Diagnostic>());

        foreach (var file in files)
        {
            var stem = file[..file.LastIndexOf('.')];
            if (file.EndsWith(".sql", StringComparison.Ordinal))
            {
                if (!set.Contains(stem + ".yml"))
                    diags.Add(new Diagnostic(DiagnosticCatalog.OrphanFile, new(file, 0, 0),
                        $"`{file}` has no definition file `{stem}.yml`.",
                        Fix: $"Run `{ProductInfo.Cli} define {file}`."));
                continue;
            }
            if (!set.Contains(stem + ".sql"))
                diags.Add(new Diagnostic(DiagnosticCatalog.OrphanFile, new(file, 0, 0),
                    $"`{file}` has no query file `{stem}.sql`.", Fix: $"Add `{stem}.sql`, or remove `{file}`."));

            var expected = stem[(ModelsDir.Length + 1)..].Replace('/', '.');
            var text = File.ReadAllText(Path.Combine(projectRoot, file));
            var above = new List<YamlLayer>();
            if (effectiveConfig.Defaults != null) above.Add(new YamlLayer(ProductInfo.ConfigFile, effectiveConfig.Defaults));
            above.AddRange(folders.Above(file));
            MergedYaml? merged = null;
            var def = ModelDefinitionLoader.Load(text, file, expected, diags, connections, above, m => merged = m);
            if (def != null) models.Add(new ModelSource(def, file, stem + ".sql") { Inherited = merged == null ? [] : Inherited(merged, file) });
        }

        var descriptors = LoadSources(projectRoot, diags);
        foreach (var clash in descriptors.Select(d => d.Name).Intersect(models.Select(m => m.Definition.Name), StringComparer.OrdinalIgnoreCase))
            diags.Add(new Diagnostic(DiagnosticCatalog.DuplicateKey, new(SourcesDir, 0, 0), $"`{clash}` is defined as both a source and a model."));
        return new(models, diags, descriptors);
    }

    /// <summary>What a model's effective settings took from other files: the scalars under a layered key written in a file other than the model's own.</summary>
    private static List<SettingOrigin> Inherited(MergedYaml merged, string modelFile) =>
        merged.Origins().Where(o => o.File != modelFile && ModelDefinitionLoader.LayeredKeys.Contains(o.Path.Split('.', '[')[0]))
            .Select(o => new SettingOrigin(o.Path, o.File, o.Line, o.Value)).ToList();

    /// <summary>
    /// The project files in the folders above a model, root down: each folder's `_dbdatabuild.yml`, read once, its `defaults:` the layer. A folder file holds `defaults:` and nothing else yet;
    /// what it says wrong is reported once, where it is, not once per model beneath it.
    /// </summary>
    private sealed class FolderLayers(string projectRoot, List<Diagnostic> diags)
    {
        private readonly Dictionary<string, YamlLayer?> cache = new(StringComparer.Ordinal);

        public IEnumerable<YamlLayer> Above(string modelFile)
        {
            var parts = modelFile.Split('/');
            for (var depth = 1; depth < parts.Length; depth++)           // models/, models/a/, models/a/b/ ... (the first part is the models folder)
            {
                var file = string.Join('/', parts.Take(depth).Append(ProductInfo.FolderConfigFile));
                if (!cache.TryGetValue(file, out var layer)) cache[file] = layer = Read(file);
                if (layer != null) yield return layer;
            }
        }

        private YamlLayer? Read(string file)
        {
            var path = Path.Combine(projectRoot, file);
            if (!File.Exists(path)) return null;
            var before = diags.Count;
            var root = StrictYamlReader.Read(File.ReadAllText(path), file, diags);
            if (root == null) return null;
            if (root is not YamlMapping top) { diags.Add(new Diagnostic(DiagnosticCatalog.InvalidValue, new(file, root.Line, root.Column), $"{ProductInfo.FolderConfigFile} must be a mapping of settings.")); return null; }
            foreach (var e in top.Entries.Where(e => e.Key.Value != "defaults"))
                diags.Add(new Diagnostic(DiagnosticCatalog.UnknownKey, new(file, e.Key.Line, e.Key.Column), $"Unknown key `{e.Key.Value}` in {ProductInfo.FolderConfigFile}.", "Keys: defaults.", "Put the model settings under `defaults:`."));
            if (top.Get("defaults") is not { } node) return null;
            if (node is not YamlMapping defaults) { diags.Add(new Diagnostic(DiagnosticCatalog.InvalidValue, new(file, node.Line, node.Column), "`defaults` must be a mapping of model settings.")); return null; }
            foreach (var e in defaults.Entries.Where(e => !ModelDefinitionLoader.LayeredKeys.Contains(e.Key.Value.TrimEnd('=', '-', '+'))))
                diags.Add(new Diagnostic(DiagnosticCatalog.UnknownKey, new(file, e.Key.Line, e.Key.Column), $"Unknown key `{e.Key.Value}` in `defaults`.", $"Keys: {string.Join(", ", ModelDefinitionLoader.LayeredKeys.Order(StringComparer.Ordinal))}."));
            return diags.Count > before ? null : new YamlLayer(file, defaults);
        }
    }

    private static List<SourceDescriptor> LoadSources(string projectRoot, List<Diagnostic> diags)
    {
        var result = new List<SourceDescriptor>();
        var dir = Path.Combine(projectRoot, SourcesDir);
        if (!Directory.Exists(dir)) return result;     // sources are optional: a project may build only from models
        var files = Directory.EnumerateFiles(dir, "*.yml", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(projectRoot, f).Replace('\\', '/')).OrderBy(f => f, StringComparer.Ordinal);
        foreach (var file in files)
        {
            var expected = file[(SourcesDir.Length + 1)..^".yml".Length].Replace('/', '.');
            if (SourceDescriptorLoader.Load(File.ReadAllText(Path.Combine(projectRoot, file)), file, expected, diags) is { } d) result.Add(d);
        }
        return result;
    }
}
