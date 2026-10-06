using DbDataBuild.Core;
using DbDataBuild.Models.Yaml;

namespace DbDataBuild.Models;

/// <summary>A valid model definition with the project-relative paths of its two files.</summary>
public sealed record ModelSource(ModelDefinition Definition, string DefinitionFile, string QueryFile)
{
    /// <summary>The query of a model that has none on disk: a copy reads its generated staging table (DuckDB dialect). Null for a model with a `.sql` file.</summary>
    public string? GeneratedQuery { get; init; }

    /// <summary>The project parameters this model sees: the root file's, overridden by the folder files above it (nearest wins).</summary>
    public IReadOnlyDictionary<string, ParameterValue> ProjectParameters { get; init; } = new Dictionary<string, ParameterValue>();

    /// <summary>Connection parameters the folder files above the model override, by connection name.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, ParameterValue>> ConnectionParameterOverrides { get; init; } = new Dictionary<string, IReadOnlyDictionary<string, ParameterValue>>();

    /// <summary>Every parameter value the model sees when built on <paramref name="connection"/>, keyed `project.x`, `connection.x`, `model.x`.</summary>
    public IReadOnlyDictionary<string, ParameterValue> ParametersFor(ProjectConfig config, string connection)
    {
        var own = config.Connections.TryGetValue(connection, out var c) ? c.Parameters : new Dictionary<string, ParameterValue>();
        var merged = new Dictionary<string, ParameterValue>(own, StringComparer.Ordinal);
        if (ConnectionParameterOverrides.TryGetValue(connection, out var over)) foreach (var (k, v) in over) merged[k] = v;
        return ParameterReferences.Effective(ProjectParameters, merged, Definition.Parameters);
    }

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
        // the parameters each file sees from the project files above it: the root's, overridden by each folder's, and the connection parameters the folders override
        var parameterFiles = new Dictionary<string, (IReadOnlyDictionary<string, ParameterValue> Project, IReadOnlyDictionary<string, IReadOnlyDictionary<string, ParameterValue>> Connections)>(StringComparer.Ordinal);
        ModelSource WithParameters(ModelSource s) => parameterFiles.TryGetValue(s.DefinitionFile, out var p) ? s with { ProjectParameters = p.Project, ConnectionParameterOverrides = p.Connections } : s;

        foreach (var file in files.Where(f => f.EndsWith(".yml", StringComparison.Ordinal)))
        {
            var stem = file[..^".yml".Length];
            var expected = stem[(ModelsDir.Length + 1)..].Replace('/', '.');
            var text = File.ReadAllText(Path.Combine(projectRoot, file));
            var projectParameters = new Dictionary<string, ParameterValue>(effectiveConfig.Parameters, StringComparer.Ordinal);
            var connectionOverrides = new Dictionary<string, Dictionary<string, ParameterValue>>(StringComparer.Ordinal);
            foreach (var folder in folders.ParametersAbove(file))
            {
                foreach (var (k, v) in folder.Project) projectParameters[k] = v;
                foreach (var (c, values) in folder.Connections)
                {
                    if (!connectionOverrides.TryGetValue(c, out var target)) connectionOverrides[c] = target = new Dictionary<string, ParameterValue>(StringComparer.Ordinal);
                    foreach (var (k, v) in values) target[k] = v;
                }
            }
            parameterFiles[file] = (projectParameters, connectionOverrides.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<string, ParameterValue>)kv.Value, StringComparer.Ordinal));
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
                    else models.Add(WithParameters(new ModelSource(def, file, stem + ".sql") { Inherited = Inherited(merged, file) }));
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

        ResolveCopies(copies, models, descriptors, effectiveConfig, diags, WithParameters);
        return new(models, diags, descriptors);
    }

    /// <summary>
    /// A copy has the columns (and the grain, unless it names its own) of the model it copies, found among the project's models, mapped models and other copies. The origin must be on one connection and
    /// the copy on others: a copy moves rows between connections. Each resolved copy gets its generated query and the staging table it reads declared as a generated mapped table.
    /// </summary>
    private static void ResolveCopies(List<(ModelDefinition Definition, string File)> copies, List<ModelSource> models, List<SourceDescriptor> descriptors, ProjectConfig config, List<Diagnostic> diags, Func<ModelSource, ModelSource> withParameters)
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
            var origins = origin.Value.Connections;
            var destinations = def.Targets ?? config.DefaultConnections;
            string? problem = null;
            if (origins.Count > 1 && def.Slice == null)
                problem = $"`{def.From}` is on {string.Join(", ", origins)}, so the rows of each must be told apart: a copy from several connections needs `slice` (the column that says which rows are an origin's, and its value).";
            else if (origins.Count > 1 && !def.Slice!.Value.Contains("${origin.", StringComparison.Ordinal))
                problem = $"The slice's value `{def.Slice.Value}` is the same for every origin of `{def.From}`; it has to differ, for example `${{origin.store_id}}`.";
            foreach (var destination in destinations)
                if (problem == null && origins.Contains(destination, StringComparer.Ordinal))
                    problem = $"`{def.Name}` is on `{destination}`, a connection of `{def.From}`, which it copies: a copy moves rows between connections.";
            if (problem == null && def.Slice != null)
            {
                var seen = withParameters(new ModelSource(def, c.File, c.File));
                foreach (System.Text.RegularExpressions.Match r in ParameterReferences.Pattern.Matches(def.Slice.Value))
                {
                    var (scope, pname) = (r.Groups[1].Value, r.Groups[2].Value);
                    foreach (var named in scope == "origin" ? origins : scope == "connection" ? destinations : ["-"])
                    {
                        var key = scope is "origin" ? $"connection.{pname}" : $"{scope}.{pname}";
                        var on = scope is "origin" or "connection" ? named : destinations[0];
                        if (problem == null && !seen.ParametersFor(config, on).ContainsKey(key))
                            problem = scope is "origin" or "connection"
                                ? $"The slice uses `{r.Value}`, but the connection `{named}` has no parameter `{pname}` (`connections.{named}.parameters`)."
                                : $"The slice uses `{r.Value}`, but there is no such {scope} parameter (`parameters:` in {(scope == "model" ? "the model's file" : "a project file")}).";
                    }
                }
            }
            if (problem != null)
            {
                diags.Add(new Diagnostic(DiagnosticCatalog.InvalidValue, At(), problem, Fix: "A copy reads from one connection, or from several with a `slice`, and is built on others."));
                failed.Add(def.Name);
                return null;
            }

            var columns = origin.Value.Columns.Select(x => x with { Line = 0, CollationLine = 0 }).ToList();
            var grain = def.Grain.Count > 0 ? def.Grain : origin.Value.Grain;
            var sliceAdded = false;
            if (def.Slice != null)
            {
                if (!columns.Any(x => string.Equals(x.Name, def.Slice.Column, StringComparison.OrdinalIgnoreCase)))
                {
                    if (def.Slice.Type == null)
                    {
                        diags.Add(new Diagnostic(DiagnosticCatalog.InvalidValue, At(), $"`{def.From}` has no column `{def.Slice.Column}`, so the copy adds it, and the slice needs a `type` for it, for example `type: VARCHAR(20)`."));
                        failed.Add(def.Name);
                        return null;
                    }
                    columns.Add(new ColumnDefinition(def.Slice.Column, LogicalNormalize(def.Slice.Type), false));
                    sliceAdded = true;
                }
                if (grain.Count > 0 && !grain.Contains(def.Slice.Column, StringComparer.OrdinalIgnoreCase)) grain = [.. grain, def.Slice.Column];       // the rows of different origins may share a key: the origin's value is part of it
            }
            foreach (var named in def.UniqueKey.Select(k => ("unique_key", k)).Concat(def.Watermark == null ? [] : [("watermark.column", def.Watermark.Column)]))
                if (!columns.Any(x => string.Equals(x.Name, named.Item2, StringComparison.OrdinalIgnoreCase)))
                {
                    diags.Add(new Diagnostic(DiagnosticCatalog.UnknownColumnReference, At(), $"{named.Item1} refers to `{named.Item2}`, which `{def.From}` does not have."));
                    failed.Add(def.Name);
                    return null;
                }
            var resolved = def with { Columns = columns, Grain = grain, SliceColumnAdded = sliceAdded };
            done[def.Name] = resolved;
            return resolved;
        }

        foreach (var c in copies)
        {
            var resolved = Resolve(c, [c.Definition.Name]);
            if (resolved == null) continue;
            models.Add(withParameters(new ModelSource(resolved, c.File, c.File) { GeneratedQuery = CopyModels.Query(config, resolved) }));
            descriptors.Add(CopyModels.StagingDescriptor(config, resolved, resolved.Columns));
        }
    }

    private static string LogicalNormalize(string type) => System.Text.RegularExpressions.Regex.Replace(type.Trim().ToUpperInvariant(), @"\s*,\s*", ", ");

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
        private readonly Dictionary<string, FolderParameters?> parameterCache = new(StringComparer.Ordinal);

        /// <summary>What one folder's project file says about parameters: the project's (`parameters:`) and a connection's (`connections.<name>.parameters:`), which override the root file's for what is beneath it.</summary>
        public sealed record FolderParameters(IReadOnlyDictionary<string, ParameterValue> Project, IReadOnlyDictionary<string, IReadOnlyDictionary<string, ParameterValue>> Connections);

        public IEnumerable<YamlLayer> Above(string modelFile)
        {
            foreach (var file in FilesAbove(modelFile))
            {
                if (!cache.TryGetValue(file, out var layer)) { Load(file); layer = cache[file]; }
                if (layer != null) yield return layer;
            }
        }

        public IEnumerable<FolderParameters> ParametersAbove(string modelFile)
        {
            foreach (var file in FilesAbove(modelFile))
            {
                if (!cache.ContainsKey(file)) Load(file);
                if (parameterCache.GetValueOrDefault(file) is { } p) yield return p;
            }
        }

        private static IEnumerable<string> FilesAbove(string modelFile)
        {
            var parts = modelFile.Split('/');
            for (var depth = 1; depth < parts.Length; depth++)           // models/, models/a/, models/a/b/ ... (the first part is the models folder)
                yield return string.Join('/', parts.Take(depth).Append(ProductInfo.FolderConfigFile));
        }

        private void Load(string file)
        {
            cache[file] = null; parameterCache[file] = null;
            var path = Path.Combine(projectRoot, file);
            if (!File.Exists(path)) return;
            var before = diags.Count;
            var root = StrictYamlReader.Read(File.ReadAllText(path), file, diags);
            if (root == null) return;
            if (root is not YamlMapping top) { diags.Add(new Diagnostic(DiagnosticCatalog.InvalidValue, new(file, root.Line, root.Column), $"{ProductInfo.FolderConfigFile} must be a mapping of settings.")); return; }
            foreach (var e in top.Entries.Where(e => e.Key.Value is not ("defaults" or "parameters" or "connections")))
                diags.Add(new Diagnostic(DiagnosticCatalog.UnknownKey, new(file, e.Key.Line, e.Key.Column), $"Unknown key `{e.Key.Value}` in {ProductInfo.FolderConfigFile}.", "Keys: defaults, parameters, connections.", "Put the model settings under `defaults:`."));

            void Report(DiagnosticDescriptor d, YamlNode n, string found) => diags.Add(new Diagnostic(d, new(file, n.Line, n.Column), found));
            var project = top.Get("parameters") is { } pn ? ParameterReferences.Read(pn, "`parameters`", Report) : null;
            var byConnection = new Dictionary<string, IReadOnlyDictionary<string, ParameterValue>>(StringComparer.Ordinal);
            if (top.Get("connections") is { } cn)
            {
                if (cn is not YamlMapping cm) Report(DiagnosticCatalog.InvalidValue, cn, "`connections` maps connection names to their `parameters`.");
                else foreach (var e in cm.Entries)
                {
                    if (e.Value is not YamlMapping settings) { Report(DiagnosticCatalog.InvalidValue, e.Value, $"`connections.{e.Key.Value}` is a mapping with `parameters`."); continue; }
                    foreach (var k in settings.Entries.Where(k => k.Key.Value != "parameters"))
                        Report(DiagnosticCatalog.UnknownKey, k.Key, $"Unknown key `{k.Key.Value}` in `connections.{e.Key.Value}`: a folder's project file can only override a connection's `parameters` (the connections themselves are declared once, in {ProductInfo.ConfigFile}).");
                    if (settings.Get("parameters") is { } pm && ParameterReferences.Read(pm, $"`connections.{e.Key.Value}.parameters`", Report) is { } values) byConnection[e.Key.Value] = values;
                }
            }
            if (project != null || byConnection.Count > 0) parameterCache[file] = new FolderParameters(project ?? new Dictionary<string, ParameterValue>(), byConnection);

            if (top.Get("defaults") is not { } node) return;
            if (node is not YamlMapping defaults) { diags.Add(new Diagnostic(DiagnosticCatalog.InvalidValue, new(file, node.Line, node.Column), "`defaults` must be a mapping of model settings.")); return; }
            foreach (var e in defaults.Entries.Where(e => !ModelDefinitionLoader.LayeredKeys.Contains(e.Key.Value.TrimEnd('=', '-', '+'))))
                diags.Add(new Diagnostic(DiagnosticCatalog.UnknownKey, new(file, e.Key.Line, e.Key.Column), $"Unknown key `{e.Key.Value}` in `defaults`.", $"Keys: {string.Join(", ", ModelDefinitionLoader.LayeredKeys.Order(StringComparer.Ordinal))}."));
            cache[file] = diags.Count > before ? null : new YamlLayer(file, defaults);
        }
    }
}
