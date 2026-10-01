using DbDataBuild.Core;
using DbDataBuild.Models.Yaml;

namespace DbDataBuild.Models;

/// <summary>Shared field-reading helpers for definition and configuration loaders. Problems become diagnostics at the node's position.</summary>
internal abstract class YamlFieldReader(string file, List<Diagnostic> diags)
{
    private static readonly string[] ColumnKeys = ["name", "type", "nullable", "collation"];

    protected string File => file;
    protected List<Diagnostic> Diagnostics => diags;

    protected void Add(DiagnosticDescriptor d, YamlNode at, string found, string? supported = null, string? fix = null) =>
        diags.Add(new Diagnostic(d, new(file, at.Line, at.Column), found, supported, fix));

    protected void CheckKeys(YamlMapping map, IEnumerable<string> allowed, string where)
    {
        var allowedList = allowed.ToList();
        foreach (var e in map.Entries.Where(e => !allowedList.Contains(e.Key.Value)))
            Add(DiagnosticCatalog.UnknownKey, e.Key, $"Unknown key `{e.Key.Value}` in {where}.", $"Keys: {string.Join(", ", allowedList)}.");
    }

    protected YamlScalar? Scalar(YamlMapping map, string key, bool required, YamlNode at)
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

    /// <summary>Reads a list of non-empty strings. An empty list is reported as invalid unless <paramref name="allowEmpty"/>.</summary>
    protected List<YamlScalar>? StringList(YamlMapping map, string key, bool required, bool allowEmpty = true, bool unique = false)
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
        if (!allowEmpty && seq.Items.Count == 0) Add(DiagnosticCatalog.InvalidValue, seq, $"`{key}` must not be an empty list.");
        if (unique)
            foreach (var dup in list.GroupBy(x => x.Value).Where(g => g.Count() > 1).SelectMany(g => g.Skip(1)))
                Add(DiagnosticCatalog.InvalidValue, dup, $"`{dup.Value}` is listed more than once in `{key}`.");
        return list;
    }

    /// <summary>Reads a required, non-empty `columns:` list (name, type, nullable, collation), reporting every problem.</summary>
        protected List<ColumnDefinition> ReadColumns(YamlMapping top)
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
                if (n != null && t != null) result.Add(new ColumnDefinition(n.Value, t.Value, nullable, (col.Get("collation") as YamlScalar)?.Value, n.Line, (col.Get("collation") as YamlScalar)?.Line ?? 0));
            }
            return result;
        }
}
