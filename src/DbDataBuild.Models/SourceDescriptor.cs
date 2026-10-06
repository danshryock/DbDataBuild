using DbDataBuild.Core;
using DbDataBuild.Models.Yaml;

namespace DbDataBuild.Models;

/// <summary>
/// A **mapped** model: the declaration of a table that exists and that the tool does not build (<c>kind: {type: mapped}</c>, a <c>models/&lt;schema&gt;/&lt;table&gt;.yml</c> with no query). Models refer to
/// it by plain table name; <c>define</c> builds an empty DuckDB schema from it (DESIGN.md 6.5). Optional <c>grain</c> feeds
/// grain candidates. <c>indexes</c> and <c>foreign_keys</c> are what the table has, as exported by `import`; the tool reads them (metadata, advice) and never creates them.
/// </summary>
/// <param name="DeclaredConnections">The connections the table exists on (after the project files above it were merged in); null when nothing says, which is the project's default connections.</param>
public sealed record SourceDescriptor(string Name, IReadOnlyList<ColumnDefinition> Columns, IReadOnlyList<string> Grain,
    IReadOnlyList<IndexDefinition>? DeclaredIndexes = null, IReadOnlyList<SourceForeignKey>? DeclaredForeignKeys = null, IReadOnlyList<string>? DeclaredConnections = null, bool Generated = false)
{
    /// <summary>True for a table the tool declares itself, not a file of the project: the staging table a copy is read from (`CopyModels`). It is bound by queries like any mapped model and is left out of what is listed, imported and checked.</summary>
    public bool IsGenerated => Generated;

    public IReadOnlyList<string>? Connections => DeclaredConnections;
    public IReadOnlyList<IndexDefinition> Indexes => DeclaredIndexes ?? [];
    public IReadOnlyList<SourceForeignKey> ForeignKeys => DeclaredForeignKeys ?? [];
}

/// <summary>A foreign key of a source table: its columns, the table it points at (`schema.table`) and that table's columns, in the same order.</summary>
public sealed record SourceForeignKey(string Name, IReadOnlyList<string> Columns, string Table, IReadOnlyList<string> ReferencedColumns, int Line = 0);

public static class SourceDescriptorLoader
{
    private static readonly string[] Keys = ["name", "kind", "connections", "columns", "grain", "indexes", "foreign_keys"];

    /// <summary>The kind of a model that maps an existing table: no query, nothing built, everything declared.</summary>
    public const string MappedKind = "mapped";

    /// <summary>Reads a mapped model from its own file alone (the layers above it, if any, are merged by <see cref="LoadMerged"/>).</summary>
    public static SourceDescriptor? Load(string text, string file, string? expectedName, List<Diagnostic> diags, IReadOnlySet<string>? connections = null)
    {
        var before = diags.Count;
        var root = StrictYamlReader.Read(text, file, diags);
        if (root == null)
        {
            if (diags.Count == before) diags.Add(new Diagnostic(DiagnosticCatalog.MissingKey, new(file, 1, 1), "The file is empty. Required keys: name, kind, columns."));
            return null;
        }
        var known = connections ?? TargetNames.All.ToHashSet(StringComparer.Ordinal);
        if (root is not YamlMapping own) { new Reader(file, diags, known).Read(root, expectedName); return null; }
        var merged = YamlMerge.Merge([new YamlLayer(file, own)], ModelDefinitionLoader.LayeredKeys, diags);
        return diags.Count > before ? null : LoadMerged(merged, file, expectedName, diags, known);
    }

    /// <summary>Reads a mapped model that was merged from the project files above it and its own file (`kind` and `connections` may come from a folder).</summary>
    public static SourceDescriptor? LoadMerged(MergedYaml merged, string file, string? expectedName, List<Diagnostic> diags, IReadOnlySet<string>? connections = null)
    {
        var before = diags.Count;
        var result = new Reader(file, diags, connections ?? TargetNames.All.ToHashSet(StringComparer.Ordinal)) { NodeFiles = merged.FileOf }.Read(merged.Root, expectedName);
        return diags.Count > before ? null : result;
    }

    private sealed class Reader(string file, List<Diagnostic> diags, IReadOnlySet<string> knownConnections) : YamlFieldReader(file, diags)
    {
        public SourceDescriptor? Read(YamlNode root, string? expectedName)
        {
            if (root is not YamlMapping top) { Add(DiagnosticCatalog.InvalidValue, root, "A mapped model must be a mapping with `name`, `kind` and `columns`."); return null; }
            CheckKeys(top, Keys, "a mapped model");
            if (top.Get("kind") is not YamlMapping kind || kind.Get("type") is not YamlScalar { Value: MappedKind } || kind.Entries.Count != 1)
                Add(DiagnosticCatalog.InvalidValue, top.Get("kind") ?? top, "A mapped model's kind is `{type: mapped}` and nothing else.");
            var connections = StringList(top, "connections", required: false, allowEmpty: false, unique: true);
            foreach (var c in (connections ?? []).Where(c => !knownConnections.Contains(c.Value)))
                Add(DiagnosticCatalog.InvalidValue, c, $"Unknown connection `{c.Value}`.", $"One of: {string.Join(", ", knownConnections.Order(StringComparer.Ordinal))}.");
            var name = Scalar(top, "name", required: true, at: top);
            if (name != null && expectedName != null && name.Value != expectedName)
                Add(DiagnosticCatalog.NameMismatch, name, $"name is `{name.Value}`, but the path implies `{expectedName}`.", fix: $"Change `name:` to `{expectedName}`, or move the file.");
            var grain = StringList(top, "grain", required: false, allowEmpty: false, unique: true);
            var columns = ReadColumns(top);
            var declared = columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var g in grain ?? [])
                if (columns.Count > 0 && !declared.Contains(g.Value))
                    Add(DiagnosticCatalog.UnknownColumnReference, g, $"grain refers to `{g.Value}`, which is not declared in `columns`.");
            var indexes = ReadIndexes(top, columns);
            var foreignKeys = ReadForeignKeys(top, columns);
            return name == null ? null : new SourceDescriptor(name.Value, columns, grain?.Select(g => g.Value).ToList() ?? [], indexes, foreignKeys, connections?.Select(c => c.Value).ToList());
        }

        private List<IndexDefinition> ReadIndexes(YamlMapping top, List<ColumnDefinition> columns)
        {
            var result = new List<IndexDefinition>();
            if (top.Get("indexes") is not { } node) return result;
            if (node is not YamlSequence seq) { Add(DiagnosticCatalog.InvalidValue, node, "`indexes` must be a list."); return result; }
            var declared = columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in seq.Items)
            {
                if (item is not YamlMapping m) { Add(DiagnosticCatalog.InvalidValue, item, "Each index must be a mapping with `name` and `columns`."); continue; }
                CheckKeys(m, ["name", "columns", "unique", "include"], "an index of a source");
                var name = Scalar(m, "name", required: true, at: m);
                var cols = StringList(m, "columns", required: true, allowEmpty: false, unique: true);
                var include = StringList(m, "include", required: false, allowEmpty: false, unique: true);
                var unique = false;
                if (Scalar(m, "unique", required: false, at: m) is { } u)
                {
                    if (u.Value is "true" or "false") unique = u.Value == "true";
                    else Add(DiagnosticCatalog.InvalidValue, u, $"unique is `{u.Value}`.", "true or false (lowercase).");
                }
                // the name is whatever the table's index is called: it is not a name this tool creates, so it is not held to an identifier pattern
                if (name != null && !names.Add(name.Value)) Add(DiagnosticCatalog.DuplicateKey, name, $"The index name `{name.Value}` is used more than once.");
                foreach (var c in (cols ?? []).Concat(include ?? []).Where(c => columns.Count > 0 && !declared.Contains(c.Value)))
                    Add(DiagnosticCatalog.UnknownColumnReference, c, $"Index `{name?.Value}` refers to `{c.Value}`, which is not declared in `columns`.");
                if (name != null && cols != null) result.Add(new IndexDefinition(name.Value, cols.Select(c => c.Value).ToList(), unique, include?.Select(c => c.Value).ToList() ?? [], null, m.Line));
            }
            return result;
        }

        private List<SourceForeignKey> ReadForeignKeys(YamlMapping top, List<ColumnDefinition> columns)
        {
            var result = new List<SourceForeignKey>();
            if (top.Get("foreign_keys") is not { } node) return result;
            if (node is not YamlSequence seq) { Add(DiagnosticCatalog.InvalidValue, node, "`foreign_keys` must be a list."); return result; }
            var declared = columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in seq.Items)
            {
                if (item is not YamlMapping m) { Add(DiagnosticCatalog.InvalidValue, item, "Each foreign key must be a mapping with `name`, `columns` and `references`."); continue; }
                CheckKeys(m, ["name", "columns", "references"], "a foreign key");
                var name = Scalar(m, "name", required: true, at: m);
                var cols = StringList(m, "columns", required: true, allowEmpty: false, unique: true);
                string? table = null;
                List<YamlScalar>? refCols = null;
                if (m.Get("references") is not { } r) Add(DiagnosticCatalog.MissingKey, m, "Required key `references` is missing.", fix: "Add `references: {table: schema.table, columns: [...]}`.");
                else if (r is not YamlMapping rm) Add(DiagnosticCatalog.InvalidValue, r, "`references` must be a mapping with `table` and `columns`.");
                else
                {
                    CheckKeys(rm, ["table", "columns"], "`references`");
                    table = Scalar(rm, "table", required: true, at: rm)?.Value;
                    refCols = StringList(rm, "columns", required: true, allowEmpty: false, unique: true);
                }
                if (name != null && !names.Add(name.Value)) Add(DiagnosticCatalog.DuplicateKey, name, $"The foreign key name `{name.Value}` is used more than once.");
                foreach (var c in (cols ?? []).Where(c => columns.Count > 0 && !declared.Contains(c.Value)))
                    Add(DiagnosticCatalog.UnknownColumnReference, c, $"Foreign key `{name?.Value}` refers to `{c.Value}`, which is not declared in `columns`.");
                if (cols != null && refCols != null && cols.Count != refCols.Count)
                    Add(DiagnosticCatalog.InvalidValue, m, $"Foreign key `{name?.Value}` has {cols.Count} column(s) and references {refCols.Count}.");
                if (name != null && cols != null && table != null && refCols != null)
                    result.Add(new SourceForeignKey(name.Value, cols.Select(c => c.Value).ToList(), table, refCols.Select(c => c.Value).ToList(), m.Line));
            }
            return result;
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
        sb.Append("kind:\n  type: mapped\n");
        if (d.Connections is { Count: > 0 }) sb.Append("connections=: ").Append(YamlText.FlowList(d.Connections)).Append('\n');
        if (d.Grain.Count > 0) sb.Append("grain: ").Append(YamlText.FlowList(d.Grain)).Append('\n');
        sb.Append("columns:\n");
        foreach (var c in d.Columns)
        {
            sb.Append("  - name: ").Append(YamlText.Scalar(c.Name)).Append('\n');
            sb.Append("    type: ").Append(YamlText.Scalar(c.Type)).Append('\n');
            if (!c.Nullable) sb.Append("    nullable: false\n");
            if (c.Collation != null) sb.Append("    collation: ").Append(YamlText.Scalar(c.Collation)).Append('\n');
        }
        if (d.Indexes.Count > 0)
        {
            sb.Append("indexes:\n");
            foreach (var i in d.Indexes)
            {
                sb.Append("  - {name: ").Append(YamlText.FlowScalar(i.Name)).Append(", columns: ").Append(YamlText.FlowList(i.Columns));
                if (i.Unique) sb.Append(", unique: true");
                if (i.Include.Count > 0) sb.Append(", include: ").Append(YamlText.FlowList(i.Include));
                sb.Append("}\n");
            }
        }
        if (d.ForeignKeys.Count > 0)
        {
            sb.Append("foreign_keys:\n");
            foreach (var f in d.ForeignKeys)
                sb.Append("  - {name: ").Append(YamlText.FlowScalar(f.Name)).Append(", columns: ").Append(YamlText.FlowList(f.Columns))
                  .Append(", references: {table: ").Append(YamlText.FlowScalar(f.Table)).Append(", columns: ").Append(YamlText.FlowList(f.ReferencedColumns)).Append("}}\n");
        }
        return sb.ToString();
    }

    /// <summary>The project-relative path of the descriptor for a table (`staging.orders` is `models/staging/orders.yml`), or null when the name cannot be a path (a dot, slash or backslash inside the schema or table name).</summary>
    public static string? PathFor(string schema, string table)
    {
        static bool Bad(string s) => s.Length == 0 || s.AsSpan().IndexOfAny('.', '/', '\\') >= 0 || s.Trim() != s;
        return Bad(schema) || Bad(table) ? null : $"{ProjectValidator.ModelsDir}/{schema}/{table}.yml";
    }
}
