using DbDataBuild.Core;
using DbDataBuild.Models.Yaml;

namespace DbDataBuild.Models;

/// <summary>Loads <c>dbdatabuild.yml</c> with the strict YAML rules. Keys that are absent take the built-in default; nothing is inferred.</summary>
public static class ProjectConfigLoader
{
    private static readonly string[] TopKeys = ["default_targets", "targets", "tracking_schema", "string_semantics", "policy"];
    private static readonly string[] SemanticsKeys = ["case", "accent", "trailing_space", "collations"];
    private static readonly string[] TargetKeys = ["version"];
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

            var targets = ReadDefaultTargets(top) ?? d.DefaultTargets;
            var versions = ReadTargetVersions(top);
            var schema = ReadTrackingSchema(top) ?? d.TrackingSchema;
            var semantics = ReadSemantics(top, d.StringSemantics);
            var policy = ReadPolicy(top, d.Policy);
            return new ProjectConfig(targets, versions, schema, semantics, policy, lines);
        }

        private List<string>? ReadDefaultTargets(YamlMapping top)
        {
            var list = StringList(top, "default_targets", required: false, allowEmpty: false, unique: true);
            if (list == null) return null;
            foreach (var t in list.Where(t => !TargetNames.All.Contains(t.Value)))
                Add(DiagnosticCatalog.InvalidValue, t, $"Unknown target `{t.Value}`.", $"One of: {string.Join(", ", TargetNames.All)}.");
            return list.Select(t => t.Value).ToList();
        }

        private Dictionary<string, int> ReadTargetVersions(YamlMapping top)
        {
            var result = new Dictionary<string, int>();
            if (top.Get("targets") is not { } node) return result;
            if (node is not YamlMapping targets) { Add(DiagnosticCatalog.InvalidValue, node, "`targets` must map target names to settings."); return result; }
            CheckKeys(targets, TargetNames.All, "`targets`");
            foreach (var e in targets.Entries.Where(e => TargetNames.All.Contains(e.Key.Value)))
            {
                if (e.Value is not YamlMapping settings) { Add(DiagnosticCatalog.InvalidValue, e.Value, $"`targets.{e.Key.Value}` must be a mapping (for example `{{ version: 16 }}`)."); continue; }
                CheckKeys(settings, TargetKeys, $"`targets.{e.Key.Value}`");
                if (settings.Get("version") is { } v)
                {
                    if (v is YamlScalar s && int.TryParse(s.Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0) result[e.Key.Value] = n;
                    else Add(DiagnosticCatalog.InvalidValue, v, "`version` must be the engine's major version as a positive integer (SQL Server 2022 is 16, 2025 is 17).");
                }
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
