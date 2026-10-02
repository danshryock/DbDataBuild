using DbDataBuild.Core;
using DbDataBuild.Models.Yaml;

namespace DbDataBuild.Models;

/// <summary>
/// A committed schema export of an upstream table that is not a model (<c>sources/&lt;schema&gt;/&lt;table&gt;.yml</c>). Models refer to
/// these by plain table name; <c>define</c> builds an empty DuckDB schema from them (DESIGN.md 6.5). Optional <c>grain</c> feeds
/// grain candidates.
/// </summary>
public sealed record SourceDescriptor(string Name, IReadOnlyList<ColumnDefinition> Columns, IReadOnlyList<string> Grain);

public static class SourceDescriptorLoader
{
    private static readonly string[] Keys = ["name", "columns", "grain"];

    public static SourceDescriptor? Load(string text, string file, string? expectedName, List<Diagnostic> diags)
    {
        var before = diags.Count;
        var root = StrictYamlReader.Read(text, file, diags);
        if (root == null)
        {
            if (diags.Count == before) diags.Add(new Diagnostic(DiagnosticCatalog.MissingKey, new(file, 1, 1), "The file is empty. Required keys: name, columns."));
            return null;
        }
        var result = new Reader(file, diags).Read(root, expectedName);
        return diags.Count > before ? null : result;
    }

    private sealed class Reader(string file, List<Diagnostic> diags) : YamlFieldReader(file, diags)
    {
        public SourceDescriptor? Read(YamlNode root, string? expectedName)
        {
            if (root is not YamlMapping top) { Add(DiagnosticCatalog.InvalidValue, root, "A source descriptor must be a mapping with `name` and `columns`."); return null; }
            CheckKeys(top, Keys, "a source descriptor");
            var name = Scalar(top, "name", required: true, at: top);
            if (name != null && expectedName != null && name.Value != expectedName)
                Add(DiagnosticCatalog.NameMismatch, name, $"name is `{name.Value}`, but the path implies `{expectedName}`.", fix: $"Change `name:` to `{expectedName}`, or move the file.");
            var grain = StringList(top, "grain", required: false, allowEmpty: false, unique: true);
            var columns = ReadColumns(top);
            var declared = columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var g in grain ?? [])
                if (columns.Count > 0 && !declared.Contains(g.Value))
                    Add(DiagnosticCatalog.UnknownColumnReference, g, $"grain refers to `{g.Value}`, which is not declared in `columns`.");
            return name == null ? null : new SourceDescriptor(name.Value, columns, grain?.Select(g => g.Value).ToList() ?? []);
        }
    }
}

/// <summary>Writes a source descriptor in canonical formatting: name, grain, columns; <c>nullable</c> only when false (the same style as definitions).</summary>
public static class SourceDescriptorWriter
{
    public static string Yaml(SourceDescriptor d)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("name: ").Append(YamlText.Scalar(d.Name)).Append('\n');
        if (d.Grain.Count > 0) sb.Append("grain: ").Append(YamlText.FlowList(d.Grain)).Append('\n');
        sb.Append("columns:\n");
        foreach (var c in d.Columns)
        {
            sb.Append("  - name: ").Append(YamlText.Scalar(c.Name)).Append('\n');
            sb.Append("    type: ").Append(YamlText.Scalar(c.Type)).Append('\n');
            if (!c.Nullable) sb.Append("    nullable: false\n");
            if (c.Collation != null) sb.Append("    collation: ").Append(YamlText.Scalar(c.Collation)).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>The project-relative path of the descriptor for a table (`staging.orders` is `sources/staging/orders.yml`), or null when the name cannot be a path (a dot, slash or backslash inside the schema or table name).</summary>
    public static string? PathFor(string schema, string table)
    {
        static bool Bad(string s) => s.Length == 0 || s.AsSpan().IndexOfAny('.', '/', '\\') >= 0 || s.Trim() != s;
        return Bad(schema) || Bad(table) ? null : $"{ProjectValidator.SourcesDir}/{schema}/{table}.yml";
    }
}
