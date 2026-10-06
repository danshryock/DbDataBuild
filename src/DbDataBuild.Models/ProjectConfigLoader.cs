using DbDataBuild.Core;
using DbDataBuild.Models.Yaml;

namespace DbDataBuild.Models;

/// <summary>Loads <c>dbdatabuild.yml</c> with the strict YAML rules. Keys that are absent take the built-in default; nothing is inferred.</summary>
public static class ProjectConfigLoader
{
    private static readonly string[] TopKeys = ["defaults", "connections", "tracking_schema", "string_semantics", "policy", "hook_groups", "metadata", "lowering", "lint", "rewrites"];
    private static readonly string[] SemanticsKeys = ["case", "accent", "trailing_space", "collations"];
    private static readonly string[] ConnectionKeys = ["engine", "version"];
    private static readonly string[] CollationEngines = ["duckdb", "sqlserver", "fabric", "postgres"];

    /// <summary>Loads the project's config. A missing file yields the defaults and a DDB-109 warning; a bad file yields errors and the defaults.</summary>
    public static ProjectConfig LoadFromProject(string projectRoot, List<Diagnostic> diags)
    {
        var path = Path.Combine(projectRoot, ProductInfo.ConfigFile);
        if (!File.Exists(path))
        {
            diags.Add(new Diagnostic(DiagnosticCatalog.ConfigNotFound, new(ProductInfo.ConfigFile, 0, 0),
                $"`{ProductInfo.ConfigFile}` was not found under {projectRoot}. Built-in defaults apply."));
            return ProjectConfig.Default;
        }
        return Load(File.ReadAllText(path), ProductInfo.ConfigFile, diags) ?? ProjectConfig.Default;
    }

    public static ProjectConfig? Load(string text, string file, List<Diagnostic> diags)
    {
        var before = diags.Count;
        var root = StrictYamlReader.Read(text, file, diags);
        if (root == null) return diags.Count > before ? null : ProjectConfig.Default; // an empty file means all defaults
        var cfg = new Reader(file, diags).Read(root);
        return diags.Count > before ? null : cfg;
    }

    private sealed class Reader(string file, List<Diagnostic> diags) : YamlFieldReader(file, diags)
    {
        private readonly Dictionary<string, int> lines = [];

        public ProjectConfig? Read(YamlNode root)
        {
            if (root is not YamlMapping top)
            {
                Add(DiagnosticCatalog.InvalidValue, root, $"{ProductInfo.ConfigFile} must be a mapping of settings.");
                return null;
            }
            CheckKeys(top, TopKeys, "the configuration");
            var d = ProjectConfig.Default;

            var connections = ReadConnections(top);
            var (declared, defaults) = ReadDefaults(top, connections.Keys.ToHashSet(StringComparer.Ordinal));
            IReadOnlyList<string> targets = declared ?? d.DefaultConnections;
            var schema = ReadTrackingSchema(top) ?? d.TrackingSchema;
            var semantics = ReadSemantics(top, d.StringSemantics);
            var policy = ReadPolicy(top, d.Policy);
            return new ProjectConfig(targets, connections, schema, semantics, policy, lines, ReadHookGroups(top, connections.Keys.ToHashSet(StringComparer.Ordinal)), ReadMetadata(top), ReadLowering(top), ReadLint(top, "indexes"), ReadLint(top, "slices"), ReadRewrites(top)) { Defaults = defaults };
        }

        private bool ReadLint(YamlMapping top, string key)
        {
            if (top.Get("lint") is not { } node) return true;
            if (node is not YamlMapping m) { if (key == "indexes") Add(DiagnosticCatalog.InvalidValue, node, "`lint` must be a mapping."); return true; }
            if (key == "indexes") CheckKeys(m, ["indexes", "slices"], "`lint`");
            if (m.Get(key) is not { } v) return true;
            if (v is YamlScalar s && s.Value is "true" or "false") return s.Value == "true";
            Add(DiagnosticCatalog.InvalidValue, v, $"`lint.{key}` must be true or false (lowercase).");
            return true;
        }

        private bool ReadLowering(YamlMapping top)
        {
            if (top.Get("lowering") is not { } node) return true;
            if (node is not YamlMapping m) { Add(DiagnosticCatalog.InvalidValue, node, "`lowering` must be a mapping."); return true; }
            CheckKeys(m, ["enabled"], "`lowering`");
            if (m.Get("enabled") is not { } v) return true;
            if (v is YamlScalar s && s.Value is "true" or "false") return s.Value == "true";
            Add(DiagnosticCatalog.InvalidValue, v, "`lowering.enabled` must be true or false (lowercase).");
            return true;
        }

        private bool ReadMetadata(YamlMapping top)
        {
            if (top.Get("metadata") is not { } node) return false;
            if (node is not YamlMapping m) { Add(DiagnosticCatalog.InvalidValue, node, "`metadata` must be a mapping."); return false; }
            CheckKeys(m, ["store_on_apply"], "`metadata`");
            if (m.Get("store_on_apply") is not { } v) return false;
            if (v is YamlScalar s && s.Value is "true" or "false") return s.Value == "true";
            Add(DiagnosticCatalog.InvalidValue, v, "`metadata.store_on_apply` must be true or false (lowercase).");
            return false;
        }

        private Dictionary<string, IReadOnlyList<HookDefinition>> ReadHookGroups(YamlMapping top, IReadOnlySet<string> connections)
        {
            var result = new Dictionary<string, IReadOnlyList<HookDefinition>>(StringComparer.Ordinal);
            if (top.Get("hook_groups") is not { } node) return result;
            if (node is not YamlMapping groups) { Add(DiagnosticCatalog.InvalidValue, node, "`hook_groups` must map group names to lists of hooks."); return result; }
            foreach (var e in groups.Entries)
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(e.Key.Value, @"^[A-Za-z_][A-Za-z0-9_\-]*$")) { Add(DiagnosticCatalog.InvalidValue, e.Key, $"`{e.Key.Value}` is not a valid hook group name."); continue; }
                result[e.Key.Value] = HookReader.ReadList(e.Value, allowUse: false, $"the hook group `{e.Key.Value}`", (d, n, f) => Add(d, n, f), connections);
            }
            return result;
        }

        /// <summary>
        /// `defaults:` holds the model settings every model inherits from this file (<see cref="ModelDefinitionLoader.LayeredKeys"/>); a folder's `_dbdatabuild.yml` has the same section. Only the shape is checked here,
        /// the values are checked where they are merged into a model. The connections are read as well, for the commands that need to know where a model with no list of its own lives.
        /// </summary>
        private (List<string>? Connections, YamlMapping? Defaults) ReadDefaults(YamlMapping top, IReadOnlySet<string> connections)
        {
            if (top.Get("defaults") is not { } node) return (null, null);
            if (node is not YamlMapping map) { Add(DiagnosticCatalog.InvalidValue, node, "`defaults` must be a mapping of model settings."); return (null, null); }
            foreach (var e in map.Entries.Where(e => !ModelDefinitionLoader.LayeredKeys.Contains(e.Key.Value.TrimEnd('=', '-', '+'))))
                Add(DiagnosticCatalog.UnknownKey, e.Key, $"Unknown key `{e.Key.Value}` in `defaults`.", $"Keys: {string.Join(", ", ModelDefinitionLoader.LayeredKeys.Order(StringComparer.Ordinal))}.");
            List<string>? list = null;
            if (map.Entries.FirstOrDefault(e => e.Key.Value is "connections" or "connections=" or "connections+") is { } c)
            {
                if (c.Value is not YamlSequence seq || seq.Items.Count == 0 || seq.Items.Any(i => i is not YamlScalar { Value.Length: > 0 }))
                    Add(DiagnosticCatalog.InvalidValue, c.Value, "`connections` must be a non-empty list of connection names, for example `connections: [warehouse]`.");
                else
                {
                    list = [];
                    foreach (var t in seq.Items.Cast<YamlScalar>())
                    {
                        if (list.Contains(t.Value)) Add(DiagnosticCatalog.InvalidValue, t, $"`{t.Value}` is listed twice.");
                        else if (!connections.Contains(t.Value)) Add(DiagnosticCatalog.InvalidValue, t, $"Unknown connection `{t.Value}`.", $"One of: {string.Join(", ", connections.Order(StringComparer.Ordinal))}.");
                        list.Add(t.Value);
                    }
                }
            }
            return (list, map);
        }

        /// <summary>
        /// `connections:` maps names to `{ engine, version }`. A connection named after an engine needs no entry (and may only be declared with that engine, to set its version); any other name needs `engine`.
        /// Names become the environment variables of the logins (`DBDATABUILD_&lt;NAME&gt;_READ`), so they are letters, digits and underscores, and two names that differ only in case would share one.
        /// </summary>
        private Dictionary<string, ConnectionConfig> ReadConnections(YamlMapping top)
        {
            var result = new Dictionary<string, ConnectionConfig>(ConnectionConfig.Implicit, StringComparer.Ordinal);
            if (top.Get("connections") is not { } node) return result;
            if (node is not YamlMapping connections) { Add(DiagnosticCatalog.InvalidValue, node, "`connections` must map connection names to settings."); return result; }
            var upper = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var e in connections.Entries)
            {
                var name = e.Key.Value;
                if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z][A-Za-z0-9_]*$"))
                { Add(DiagnosticCatalog.InvalidValue, e.Key, $"`{name}` is not a valid connection name.", "Letters, digits and underscores, starting with a letter: the name is part of the login's environment variable."); continue; }
                if (upper.TryGetValue(name.ToUpperInvariant(), out var other))
                { Add(DiagnosticCatalog.DuplicateKey, e.Key, $"The connection names `{other}` and `{name}` differ only in case and would share the login variable `DBDATABUILD_{name.ToUpperInvariant()}_READ`."); continue; }
                upper[name.ToUpperInvariant()] = name;
                if (e.Value is not YamlMapping settings) { Add(DiagnosticCatalog.InvalidValue, e.Value, $"`connections.{name}` must be a mapping (for example `{{ engine: postgres }}`)."); continue; }
                CheckKeys(settings, ConnectionKeys, $"`connections.{name}`");
                var isEngineName = TargetNames.All.Contains(name);
                string? engine = isEngineName ? name : null;
                if (settings.Get("engine") is { } en)
                {
                    if (en is YamlScalar es && TargetNames.All.Contains(es.Value))
                    {
                        if (isEngineName && es.Value != name) Add(DiagnosticCatalog.InvalidValue, en, $"The connection `{name}` is named after an engine, so its engine is `{name}`, not `{es.Value}`.", "Give the connection another name.");
                        else engine = es.Value;
                    }
                    else Add(DiagnosticCatalog.InvalidValue, en, "`engine` is not one the tool knows.", $"One of: {string.Join(", ", TargetNames.All)}.");
                }
                else if (!isEngineName) Add(DiagnosticCatalog.MissingKey, e.Key, $"The connection `{name}` needs an `engine`.", $"One of: {string.Join(", ", TargetNames.All)}.");
                int? version = null;
                if (settings.Get("version") is { } v)
                {
                    if (v is YamlScalar s && int.TryParse(s.Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0) version = n;
                    else Add(DiagnosticCatalog.InvalidValue, v, "`version` must be the engine's major version as a positive integer (SQL Server 2022 is 16, 2025 is 17).");
                }
                if (engine != null) result[name] = new ConnectionConfig(name, engine, version, e.Key.Line);
            }
            return result;
        }

        private string? ReadTrackingSchema(YamlMapping top)
        {
            var s = Scalar(top, "tracking_schema", required: false, at: top);
            if (s == null) return null;
            if (!System.Text.RegularExpressions.Regex.IsMatch(s.Value, "^[A-Za-z_][A-Za-z0-9_]*$"))
            {
                Add(DiagnosticCatalog.InvalidValue, s, $"`tracking_schema` is `{s.Value}`.", "A plain identifier: letters, digits and underscores, not starting with a digit.");
                return null;
            }
            return s.Value;
        }

        private StringSemantics ReadSemantics(YamlMapping top, StringSemantics defaults)
        {
            if (top.Get("string_semantics") is not { } node) return defaults;
            if (node is not YamlMapping m) { Add(DiagnosticCatalog.InvalidValue, node, "`string_semantics` must be a mapping."); return defaults; }
            CheckKeys(m, SemanticsKeys, "`string_semantics`");

            lines["string_semantics"] = m.Line;
            var @case = Enum(m, "case", defaults.Case);
            var accent = Enum(m, "accent", defaults.Accent);
            var trailing = Enum(m, "trailing_space", defaults.TrailingSpace);
            var collations = defaults.Collations;
            if (m.Get("collations") is { } cn)
            {
                var read = ReadCollations(cn);
                if (read != null) collations = read;
            }
            return new StringSemantics(@case, accent, trailing, collations);
        }

        private T Enum<T>(YamlMapping m, string key, T fallback) where T : struct, System.Enum
        {
            if (m.Get(key) is not { } node) return fallback;
            lines["string_semantics." + key] = node.Line;
            var names = System.Enum.GetNames<T>().Select(n => n.ToLowerInvariant()).ToList();
            if (node is YamlScalar s && names.Contains(s.Value) && System.Enum.TryParse<T>(s.Value, ignoreCase: true, out var v)) return v;
            Add(DiagnosticCatalog.InvalidValue, node, $"`{key}` is {(node is YamlScalar sc ? $"`{sc.Value}`" : "not a string")}.", $"One of: {string.Join(", ", names)}.");
            return fallback;
        }

        private Dictionary<string, IReadOnlyDictionary<string, string>>? ReadCollations(YamlNode node)
        {
            if (node is not YamlMapping logical) { Add(DiagnosticCatalog.InvalidValue, node, "`collations` must map logical names to per-engine collation names."); return null; }
            var result = new Dictionary<string, IReadOnlyDictionary<string, string>>();
            lines["string_semantics.collations"] = logical.Line;
            foreach (var e in logical.Entries)
            {
                lines[$"string_semantics.collations.{e.Key.Value}"] = e.Key.Line;
                if (e.Value is not YamlMapping engines) { Add(DiagnosticCatalog.InvalidValue, e.Value, $"`collations.{e.Key.Value}` must map engines to collation names."); continue; }
                CheckKeys(engines, CollationEngines, $"`collations.{e.Key.Value}`");
                var perEngine = new Dictionary<string, string>();
                foreach (var ee in engines.Entries.Where(x => CollationEngines.Contains(x.Key.Value)))
                {
                    if (ee.Value is YamlScalar s && s.Value.Length > 0) { perEngine[ee.Key.Value] = s.Value; lines[$"string_semantics.collations.{e.Key.Value}.{ee.Key.Value}"] = ee.Key.Line; }
                    else Add(DiagnosticCatalog.InvalidValue, ee.Value, $"`collations.{e.Key.Value}.{ee.Key.Value}` must be a non-empty collation name.");
                }
                result[e.Key.Value] = perEngine;
            }
            return result;
        }

        private Dictionary<string, Severity> ReadPolicy(YamlMapping top, IReadOnlyDictionary<string, Severity> defaults)
        {
            var result = new Dictionary<string, Severity>(defaults);
            if (top.Get("policy") is not { } node) return result;
            if (node is not YamlMapping policy) { Add(DiagnosticCatalog.InvalidValue, node, "`policy` must be a mapping."); return result; }
            CheckKeys(policy, ["severity"], "`policy`");
            if (policy.Get("severity") is not { } sev) return result;
            if (sev is not YamlMapping sm) { Add(DiagnosticCatalog.InvalidValue, sev, "`policy.severity` must map finding kinds to a severity."); return result; }
            CheckKeys(sm, PolicyKeys.All, "`policy.severity`");
            foreach (var e in sm.Entries.Where(x => PolicyKeys.All.Contains(x.Key.Value)))
            {
                if (e.Value is YamlScalar s && s.Value is "error" or "warning" or "note")
                    result[e.Key.Value] = s.Value switch { "error" => Severity.Error, "warning" => Severity.Warning, _ => Severity.Note };
                else Add(DiagnosticCatalog.InvalidValue, e.Value, $"`policy.severity.{e.Key.Value}` must be error, warning or note.",
                    "`unsupported` findings are always errors and cannot be configured.");
            }
            return result;
        }
    }
}
