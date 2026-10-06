using DbDataBuild.Core;
using DbDataBuild.Models.Yaml;

namespace DbDataBuild.Models;

/// <summary>A valid model definition with the project-relative paths of its two files.</summary>
public sealed record ModelSource(ModelDefinition Definition, string DefinitionFile, string QueryFile)
{
    /// <summary>The query of a model that has none on disk: a copy reads its generated staging table (DuckDB dialect). Null for a model with a `.sql` file.</summary>
    public string? GeneratedQuery { get; init; }

    /// <summary>The model's query in DuckDB dialect: its `.sql` file, or the generated one of a copy.</summary>
    public string ReadQuery(string projectRoot) => GeneratedQuery ?? File.ReadAllText(Path.Combine(projectRoot, QueryFile));

    /// <summary>The settings this model took from a project file above it (`defaults:` of the root file or of a folder's `_dbdatabuild.yml`), with the file and line each was written on. Empty when the model's own file says everything.</summary>
    public IReadOnlyList<SettingOrigin> Inherited { get; init; } = [];
}

/// <summary>One scalar of a model's effective settings that was written in another file, by dotted path (`kind.type`, `connections[1]`).</summary>
public sealed record SettingOrigin(string Path, string File, int Line, string Value);

public sealed record ProjectValidationResult(
    IReadOnlyList<ModelSource> Sources, IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<SourceDescriptor>? SourceDescriptors = null)
{
    /// <summary>The mapped models of the project, as files.</summary>
    public IReadOnlyList<SourceDescriptor> Descriptors => (SourceDescriptors ?? []).Where(d => !d.IsGenerated).ToList();

    /// <summary>Everything a query binds against: the mapped models and the staging tables that copies read from.</summary>
    public IReadOnlyList<SourceDescriptor> AllDescriptors => SourceDescriptors ?? [];
    public IReadOnlyList<ModelDefinition> Models => Sources.Select(s => s.Definition).ToList();
    public bool HasErrors => Diagnostics.Any(d => d.Severity == Severity.Error);
}

/// <summary>Offline validation of every model under <c>models/</c>. Reads files only; never writes, never connects.</summary>
public static class ProjectValidator
{
    public const string ModelsDir = "models";

    /// <summary>The folder `sources/` held the descriptors of tables the tool does not build, before they became **mapped** models in `models/`.</summary>
    public const string RetiredSourcesDir = "sources";

    public static ProjectValidationResult Validate(string projectRoot, ProjectConfig? config = null)
    {
        var diags = new List<Diagnostic>();
        var effectiveConfig = config ?? ProjectConfigLoader.LoadFromProject(projectRoot, new List<Diagnostic>());
        // the connections a model may name come from the project's configuration (a problem in the configuration itself is reported where the configuration is loaded)
        var connections = effectiveConfig.Connections.Keys.ToHashSet(StringComparer.Ordinal);
        var models = new List<ModelSource>();
        var descriptors = new List<SourceDescriptor>();
        if (Directory.Exists(Path.Combine(projectRoot, RetiredSourcesDir)))
            diags.Add(new Diagnostic(DiagnosticCatalog.InvalidValue, new(RetiredSourcesDir, 0, 0),
                $"`{RetiredSourcesDir}/` is no longer read: the tables the tool does not build are mapped models now.",
                Fix: $"Move each `{RetiredSourcesDir}/<schema>/<table>.yml` to `{ModelsDir}/<schema>/<table>.yml` and add `kind: {{type: mapped}}` (or set it once in a folder's `{ProductInfo.FolderConfigFile}`)."));
        var modelsRoot = Path.Combine(projectRoot, ModelsDir);
        if (!Directory.Exists(modelsRoot))
        {
            diags.Add(new Diagnostic(DiagnosticCatalog.MissingKey, new(ModelsDir, 0, 0),
                $"Directory `{ModelsDir}/` was not found under {projectRoot}.", Fix: $"Create `{ModelsDir}/` or run from the project root."));
            return new(models, diags, descriptors);
        }

        var files = Directory.EnumerateFiles(modelsRoot, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".yml", StringComparison.Ordinal) || f.EndsWith(".sql", StringComparison.Ordinal))
            .Where(f => Path.GetFileName(f) != ProductInfo.FolderConfigFile)         // a project file, not a model
            .Select(f => Path.GetRelativePath(projectRoot, f).Replace('\\', '/'))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        var set = files.ToHashSet(StringComparer.Ordinal);
        var folders = new FolderLayers(projectRoot, diags);
        var mappedStems = new HashSet<string>(StringComparer.Ordinal);      // files with no query: mapped models and copies
        var copies = new List<(ModelDefinition Definition, string File)>();

        foreach (var file in files.Where(f => f.EndsWith(".yml", StringComparison.Ordinal)))
        {
            var stem = file[..^".yml".Length];
            var expected = stem[(ModelsDir.Length + 1)..].Replace('/', '.');
            var text = File.ReadAllText(Path.Combine(projectRoot, file));
            var above = new List<YamlLayer>();
            if (effectiveConfig.Defaults != null) above.Add(new YamlLayer(ProductInfo.ConfigFile, effectiveConfig.Defaults));
            above.AddRange(folders.Above(file));

            // the kind decides what the file is, and the kind may come from a folder: so the file is merged first
            var before = diags.Count;
            var root = StrictYamlReader.Read(text, file, diags);
            if (root is YamlMapping own)
            {
                if (diags.Count > before) continue;
                var merged = YamlMerge.Merge([.. above, new YamlLayer(file, own)], ModelDefinitionLoader.LayeredKeys, diags);
                if (diags.Count > before) continue;
                if (((merged.Root.Get("kind") as YamlMapping)?.Get("type") as YamlScalar)?.Value == SourceDescriptorLoader.MappedKind)
                {
                    mappedStems.Add(stem);
                    if (SourceDescriptorLoader.LoadMerged(merged, file, expected, diags, connections) is { } mapped) descriptors.Add(mapped);
                    continue;
                }
                var isCopy = ((merged.Root.Get("kind") as YamlMapping)?.Get("type") as YamlScalar)?.Value == ModelKinds.Copy;
                if (isCopy) mappedStems.Add(stem);
                else if (!set.Contains(stem + ".sql"))
                    diags.Add(new Diagnostic(DiagnosticCatalog.OrphanFile, new(file, 0, 0), $"`{file}` has no query file `{stem}.sql`.", Fix: $"Add `{stem}.sql`, or remove `{file}`."));
                if (ModelDefinitionLoader.LoadMerged(merged, file, expected, diags, connections) is { } def)
                {
                    if (isCopy) copies.Add((def, file));
                    else models.Add(new ModelSource(def, file, stem + ".sql") { Inherited = Inherited(merged, file) });
                }
                continue;
            }
            // empty, damaged or not a mapping: the loader says what is wrong with it, once
            diags.RemoveRange(before, diags.Count - before);
            if (!set.Contains(stem + ".sql"))
                diags.Add(new Diagnostic(DiagnosticCatalog.OrphanFile, new(file, 0, 0), $"`{file}` has no query file `{stem}.sql`.", Fix: $"Add `{stem}.sql`, or remove `{file}`."));
            ModelDefinitionLoader.Load(text, file, expected, diags, connections);
        }

        foreach (var file in files.Where(f => f.EndsWith(".sql", StringComparison.Ordinal)))
        {
            var stem = file[..^".sql".Length];
            if (mappedStems.Contains(stem))
                diags.Add(new Diagnostic(DiagnosticCatalog.OrphanFile, new(file, 0, 0), $"`{stem}.yml` has no query of its own (a mapped model is not built, a copy reads its origin), so `{file}` has no use.", Fix: $"Remove `{file}`, or give `{stem}.yml` a kind that has a query."));
            else if (!set.Contains(stem + ".yml"))
                diags.Add(new Diagnostic(DiagnosticCatalog.OrphanFile, new(file, 0, 0), $"`{file}` has no definition file `{stem}.yml`.", Fix: $"Run `{ProductInfo.Cli} define {file}`."));
        }

        ResolveCopies(copies, models, descriptors, effectiveConfig, diags);
        return new(models, diags, descriptors);
    }

    /// <summary>
    /// A copy has the columns (and the grain, unless it names its own) of the model it copies, found among the project's models, mapped models and other copies. The origin must be on one connection and
    /// the copy on others: a copy moves rows between connections. Each resolved copy gets its generated query and the staging table it reads declared as a generated mapped table.
    /// </summary>
    private static void ResolveCopies(List<(ModelDefinition Definition, string File)> copies, List<ModelSource> models, List<SourceDescriptor> descriptors, ProjectConfig config, List<Diagnostic> diags)
    {
        var pending = copies.ToDictionary(c => c.Definition.Name, c => c, StringComparer.OrdinalIgnoreCase);
        var done = new Dictionary<string, ModelDefinition>(StringComparer.OrdinalIgnoreCase);
        var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // the columns, grain and connections of what a copy copies; null when it cannot be found (reported by the caller)
        (IReadOnlyList<ColumnDefinition> Columns, IReadOnlyList<string> Grain, IReadOnlyList<string> Connections)? Origin(string name, List<string> stack)
        {
            if (models.FirstOrDefault(m => string.Equals(m.Definition.Name, name, StringComparison.OrdinalIgnoreCase)) is { } m)
                return (m.Definition.Columns, m.Definition.Grain, m.Definition.Targets ?? config.DefaultConnections);
            if (descriptors.FirstOrDefault(d => !d.IsGenerated && string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)) is { } d)
                return (d.Columns, d.Grain, d.Connections ?? config.DefaultConnections);
            if (pending.TryGetValue(name, out var c))
            {
                if (done.TryGetValue(name, out var resolved)) return (resolved.Columns, resolved.Grain, resolved.Targets ?? config.DefaultConnections);
                if (stack.Contains(name, StringComparer.OrdinalIgnoreCase) || failed.Contains(name)) return null;
                stack.Add(name);
                return Resolve(c, stack) is { } r ? (r.Columns, r.Grain, r.Targets ?? config.DefaultConnections) : null;
            }
            return null;
        }

        ModelDefinition? Resolve((ModelDefinition Definition, string File) c, List<string> stack)
        {
            var def = c.Definition;
            if (done.TryGetValue(def.Name, out var already)) return already;
            SourceLocation At() => new(c.File, def.FromLine, 1);
            var origin = Origin(def.From!, stack);
            if (origin == null)
            {
                diags.Add(new Diagnostic(DiagnosticCatalog.InvalidValue, At(), $"`{def.Name}` copies `{def.From}`, which is not a model, a mapped model or a copy of this project{(stack.Contains(def.From!, StringComparer.OrdinalIgnoreCase) ? " (it copies itself, through the others)" : "")}.",
                    Fix: "Name the model to copy as `kind: {type: copy, from: schema.table}`."));
                failed.Add(def.Name);
                return null;
            }
            if (origin.Value.Connections.Count != 1)
            {
                diags.Add(new Diagnostic(DiagnosticCatalog.InvalidValue, At(), $"`{def.From}` is on {string.Join(", ", origin.Value.Connections)}; a copy reads from exactly one connection (copying from several is not built yet).",
                    Fix: $"Give `{def.From}` a single connection."));
                failed.Add(def.Name);
                return null;
            }
            var originConnection = origin.Value.Connections[0];
            foreach (var destination in def.Targets ?? config.DefaultConnections)
                if (string.Equals(destination, originConnection, StringComparison.Ordinal))
                {
                    diags.Add(new Diagnostic(DiagnosticCatalog.InvalidValue, At(), $"`{def.Name}` is on `{destination}`, the connection of `{def.From}`, which it copies: a copy moves rows between connections.",
                        Fix: "Build a model for a table on the same connection, or give the copy another connection."));
                    failed.Add(def.Name);
                    return null;
                }
            var resolved = def with { Columns = origin.Value.Columns.Select(x => x with { Line = 0, CollationLine = 0 }).ToList(), Grain = def.Grain.Count > 0 ? def.Grain : origin.Value.Grain };
            done[def.Name] = resolved;
            return resolved;
        }

        foreach (var c in copies)
        {
            var resolved = Resolve(c, [c.Definition.Name]);
            if (resolved == null) continue;
            models.Add(new ModelSource(resolved, c.File, c.File) { GeneratedQuery = CopyModels.Query(config, resolved) });
            descriptors.Add(CopyModels.StagingDescriptor(config, resolved, resolved.Columns));
        }
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
}
