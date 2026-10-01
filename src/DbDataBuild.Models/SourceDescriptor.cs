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
