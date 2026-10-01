using DbDataBuild.Core;
using DbDataBuild.Models.Yaml;

namespace DbDataBuild.Models;

/// <summary>Loads and validates one model definition (.yml). All problems are reported in one pass.</summary>
public static class ModelDefinitionLoader
{
    private static readonly string[] TopKeys = ["name", "kind", "grain", "targets", "columns", "renames", "loads"];
    private static readonly string[] ColumnKeys = ["name", "type", "nullable", "collation"];
    private static readonly string[] RenameKeys = ["from", "to"];

    /// <param name="file">Path shown in diagnostics.</param>
    /// <param name="expectedName">Name implied by the path convention, or null to skip the check.</param>
    public static ModelDefinition? Load(string text, string file, string? expectedName, List<Diagnostic> diags)
    {
        var errorsBefore = diags.Count;
        var root = StrictYamlReader.Read(text, file, diags);
        if (root == null)
        {
            if (diags.Count == errorsBefore)
                diags.Add(new Diagnostic(DiagnosticCatalog.MissingKey, new(file, 1, 1), "The file is empty. Required keys: name, kind, columns."));
            return null;
        }
        var v = new Validator(file, diags);
        var def = v.Validate(root, expectedName);
        return diags.Count > errorsBefore ? null : def;
    }

    private sealed class Validator(string file, List<Diagnostic> diags)
    {
        private readonly List<YamlScalar> renameTargets = [];

        private void Add(DiagnosticDescriptor d, YamlNode at, string found, string? supported = null, string? fix = null) =>
            diags.Add(new Diagnostic(d, new(file, at.Line, at.Column), found, supported, fix));

        public ModelDefinition? Validate(YamlNode root, string? expectedName)
        {
            if (root is not YamlMapping top)
            {
                Add(DiagnosticCatalog.InvalidValue, root, "The definition must be a mapping of keys to values.");
                return null;
            }
            CheckKeys(top, TopKeys, "the definition");

            var name = Scalar(top, "name", required: true, at: top);
            if (name != null && expectedName != null && name.Value != expectedName)
                Add(DiagnosticCatalog.NameMismatch, name, $"name is `{name.Value}`, but the path implies `{expectedName}`.",
                    fix: $"Change `name:` to `{expectedName}`, or move the file.");

            var (kindType, uniqueKey, timeColumn, lookback, kindNode) = ReadKind(top);
            var grain = StringList(top, "grain", required: false);
            var targets = StringList(top, "targets", required: false);
            if (targets != null)
                foreach (var t in targets.Where(t => !TargetNames.All.Contains(t.Value)))
                    Add(DiagnosticCatalog.InvalidValue, t, $"Unknown target `{t.Value}`.", $"One of: {string.Join(", ", TargetNames.All)}.");

            var columns = ReadColumns(top);
            var renames = ReadRenames(top);

            // semantic checks
            var declared = columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            void Ref(IEnumerable<YamlScalar>? refs, string what)
            {
                foreach (var r in refs ?? [])
                    if (columns.Count > 0 && !declared.Contains(r.Value))
                        Add(DiagnosticCatalog.UnknownColumnReference, r, $"{what} refers to `{r.Value}`, which is not declared in `columns`.");
            }
            Ref(grain, "grain");
            Ref(uniqueKey, "unique_key");
            Ref(renameTargets, "rename target");
            if (timeColumn != null) Ref([timeColumn], "time_column");

            var incremental = kindType?.Value is ModelKinds.IncrementalByUniqueKey or ModelKinds.IncrementalByTimeRange;
            if (incremental && grain == null && top.Get("grain") == null)
                Add(DiagnosticCatalog.GrainMismatch, kindNode ?? top, $"Kind {kindType!.Value} requires `grain`, but none is set.");
            if (kindType?.Value == ModelKinds.IncrementalByUniqueKey && grain != null && uniqueKey != null &&
                !grain.Select(g => g.Value).ToHashSet().SetEquals(uniqueKey.Select(k => k.Value)))
                Add(DiagnosticCatalog.GrainMismatch, grain.FirstOrDefault() ?? (YamlNode)top,
                    $"grain [{string.Join(", ", grain.Select(g => g.Value))}] differs from unique_key [{string.Join(", ", uniqueKey.Select(k => k.Value))}].");

            if (name == null || kindType == null) return null;
            return new ModelDefinition(name.Value, kindType.Value,
                uniqueKey?.Select(k => k.Value).ToList() ?? [], timeColumn?.Value, lookback?.Value,
                grain?.Select(g => g.Value).ToList() ?? [], targets?.Select(t => t.Value).ToList(),
                columns, renames);
        }

        private (YamlScalar? Type, List<YamlScalar>? UniqueKey, YamlScalar? TimeColumn, YamlScalar? Lookback, YamlNode? Node) ReadKind(YamlMapping top)
        {
            var node = top.Get("kind");
            if (node == null) { Add(DiagnosticCatalog.MissingKey, top, "Required key `kind` is missing.", fix: "Add `kind:` with a `type:`."); return default; }
            if (node is not YamlMapping kind) { Add(DiagnosticCatalog.InvalidValue, node, "`kind` must be a mapping with a `type`."); return default; }

            var type = Scalar(kind, "type", required: true, at: kind);
            var valid = type != null && ModelKinds.All.Contains(type.Value);
            if (type != null && !valid)
                Add(DiagnosticCatalog.InvalidValue, type, $"Unknown kind `{type.Value}`.", $"One of: {string.Join(", ", ModelKinds.All)}.");

            var allowed = new List<string> { "type" };
            if (type?.Value == ModelKinds.IncrementalByUniqueKey) allowed.Add("unique_key");
            if (type?.Value == ModelKinds.IncrementalByTimeRange) { allowed.Add("time_column"); allowed.Add("lookback"); }
            if (valid) CheckKeys(kind, allowed, $"kind `{type!.Value}`");

            var uniqueKey = StringList(kind, "unique_key", required: false);
            var timeColumn = kind.Get("time_column") as YamlScalar;
            if (type?.Value == ModelKinds.IncrementalByUniqueKey && (uniqueKey == null || uniqueKey.Count == 0) && kind.Get("unique_key") == null)
                Add(DiagnosticCatalog.MissingUniqueKey, kind, "Kind incremental_by_unique_key requires a unique_key, but none is set.",
                    fix: "add under `kind:`  unique_key: [order_id]");
            if (type?.Value == ModelKinds.IncrementalByTimeRange && timeColumn == null)
                Add(DiagnosticCatalog.MissingTimeColumn, kind, "Kind incremental_by_time_range requires a time_column, but none is set.");
            return (type, uniqueKey, timeColumn, kind.Get("lookback") as YamlScalar, kind);
        }

        private List<ColumnDefinition> ReadColumns(YamlMapping top)
        {
            var result = new List<ColumnDefinition>();
            var node = top.Get("columns");
            if (node == null) { Add(DiagnosticCatalog.MissingKey, top, "Required key `columns` is missing. Declared columns are required for all models.", fix: "Add `columns:` listing each output column with `name:` and `type:`."); return result; }
            if (node is not YamlSequence seq || seq.Items.Count == 0) { Add(DiagnosticCatalog.InvalidValue, node, "`columns` must be a non-empty list."); return result; }

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in seq.Items)
            {
                if (item is not YamlMapping col) { Add(DiagnosticCatalog.InvalidValue, item, "Each column must be a mapping with `name` and `type`."); continue; }
                CheckKeys(col, ColumnKeys, "a column");
                var n = Scalar(col, "name", required: true, at: col);
                var t = Scalar(col, "type", required: true, at: col);
                var nullable = true;
                if (Scalar(col, "nullable", required: false, at: col) is { } nb)
                {
                    if (nb.Value is "true" or "false") nullable = nb.Value == "true";
                    else Add(DiagnosticCatalog.InvalidValue, nb, $"nullable is `{nb.Value}`.", "true or false (lowercase).");
                }
                if (n != null && !names.Add(n.Value))
                    Add(DiagnosticCatalog.DuplicateKey, n, $"Column `{n.Value}` is declared more than once.");
                if (n != null && t != null) result.Add(new ColumnDefinition(n.Value, t.Value, nullable, (col.Get("collation") as YamlScalar)?.Value));
            }
            return result;
        }

        private List<RenameDefinition> ReadRenames(YamlMapping top)
        {
            var result = new List<RenameDefinition>();
            renameTargets.Clear();
            if (top.Get("renames") is not { } node) return result;
            if (node is not YamlSequence seq) { Add(DiagnosticCatalog.InvalidValue, node, "`renames` must be a list of from/to pairs."); return result; }
            foreach (var item in seq.Items)
            {
                if (item is not YamlMapping m) { Add(DiagnosticCatalog.InvalidValue, item, "Each rename must be a mapping with `from` and `to`."); continue; }
                CheckKeys(m, RenameKeys, "a rename");
                var from = Scalar(m, "from", required: true, at: m);
                var to = Scalar(m, "to", required: true, at: m);
                if (from == null || to == null) continue;
                result.Add(new RenameDefinition(from.Value, to.Value));
                renameTargets.Add(to);
            }
            return result;
        }

        private void CheckKeys(YamlMapping map, IEnumerable<string> allowed, string where)
        {
            var allowedList = allowed.ToList();
            foreach (var e in map.Entries.Where(e => !allowedList.Contains(e.Key.Value)))
                Add(DiagnosticCatalog.UnknownKey, e.Key, $"Unknown key `{e.Key.Value}` in {where}.", $"Keys: {string.Join(", ", allowedList)}.");
        }

        private YamlScalar? Scalar(YamlMapping map, string key, bool required, YamlNode at)
        {
            var node = map.Get(key);
            if (node == null)
            {
                if (required) Add(DiagnosticCatalog.MissingKey, at, $"Required key `{key}` is missing.", fix: $"Add `{key}:`.");
                return null;
            }
            if (node is YamlScalar s && s.Value.Length > 0) return s;
            Add(DiagnosticCatalog.InvalidValue, node, $"`{key}` must be a non-empty string.");
            return null;
        }

        private List<YamlScalar>? StringList(YamlMapping map, string key, bool required)
        {
            var node = map.Get(key);
            if (node == null)
            {
                if (required) Add(DiagnosticCatalog.MissingKey, map, $"Required key `{key}` is missing.");
                return null;
            }
            if (node is not YamlSequence seq) { Add(DiagnosticCatalog.InvalidValue, node, $"`{key}` must be a list, for example `{key}: [a, b]`."); return null; }
            var list = new List<YamlScalar>();
            foreach (var item in seq.Items)
            {
                if (item is YamlScalar s && s.Value.Length > 0) list.Add(s);
                else Add(DiagnosticCatalog.InvalidValue, item, $"Entries of `{key}` must be non-empty strings.");
            }
            return list;
        }
    }
}
