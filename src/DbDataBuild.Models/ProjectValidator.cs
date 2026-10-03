using DbDataBuild.Core;

namespace DbDataBuild.Models;

/// <summary>A valid model definition with the project-relative paths of its two files.</summary>
public sealed record ModelSource(ModelDefinition Definition, string DefinitionFile, string QueryFile);

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

    public static ProjectValidationResult Validate(string projectRoot)
    {
        var diags = new List<Diagnostic>();
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
            .Select(f => Path.GetRelativePath(projectRoot, f).Replace('\\', '/'))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        var set = files.ToHashSet(StringComparer.Ordinal);

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
            var def = ModelDefinitionLoader.Load(File.ReadAllText(Path.Combine(projectRoot, file)), file, expected, diags);
            if (def != null) models.Add(new ModelSource(def, file, stem + ".sql"));
        }

        var descriptors = LoadSources(projectRoot, diags);
        foreach (var clash in descriptors.Select(d => d.Name).Intersect(models.Select(m => m.Definition.Name), StringComparer.OrdinalIgnoreCase))
            diags.Add(new Diagnostic(DiagnosticCatalog.DuplicateKey, new(SourcesDir, 0, 0), $"`{clash}` is defined as both a source and a model."));
        return new(models, diags, descriptors);
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
