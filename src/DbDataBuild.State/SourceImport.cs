using DbDataBuild.Models;

namespace DbDataBuild.State;

/// <summary>One column of a live table as the importer saw it: the native type, the logical type it maps to (if any), and how faithful that is.</summary>
public sealed record ImportedColumn(string Name, string NativeType, string? LogicalType, SourceTypeFit Fit, string Reason, bool Nullable);

/// <summary>A live table or view, described for a source descriptor. <c>File</c> is null when the object's name cannot be a path.</summary>
public sealed record ImportedObject(string Schema, string Name, ObjectKind Kind, string? File, IReadOnlyList<ImportedColumn> Columns, IReadOnlyList<string> PrimaryKey)
{
    public string QualifiedName => $"{Schema}.{Name}";
}

public enum SourceChangeKind { New, ColumnAdded, ColumnRemoved, TypeChanged, NullabilityChanged, GrainAdded }

/// <summary>One difference between a committed descriptor and the live table.</summary>
public sealed record SourceChange(SourceChangeKind Kind, string? Column, string Detail);

/// <summary>
/// Source descriptors from live tables (DESIGN.md 6.5.1). Pure: the catalog is read elsewhere. A descriptor is an export, so the live table wins for columns, types and
/// nullability, with two exceptions that are human knowledge: a committed grain is kept (a primary key only seeds a new descriptor), and a committed column whose native
/// type has no logical type is kept as written, because a person supplied the type the catalog cannot.
/// </summary>
public static class SourceImport
{
    public static ImportedObject Describe(string target, ObjectShape shape, IReadOnlyList<string>? primaryKey)
    {
        var columns = shape.Columns.Select(c =>
        {
            var t = SourceTypes.From(target, c);
            return new ImportedColumn(c.Name, NativeText(c), t.LogicalType, t.Fit, t.Reason, c.Nullable);
        }).ToList();
        return new ImportedObject(shape.Schema, shape.Name, shape.Kind, SourceDescriptorWriter.PathFor(shape.Schema, shape.Name), columns, primaryKey ?? []);
    }

    /// <summary>The descriptor for a live object, merged with the committed one (null for a new descriptor). Columns with no logical type and no committed declaration are left out.</summary>
    public static SourceDescriptor ToDescriptor(ImportedObject live, SourceDescriptor? committed)
    {
        var columns = new List<ColumnDefinition>();
        foreach (var c in live.Columns)
        {
            if (c.LogicalType != null) { columns.Add(new ColumnDefinition(c.Name, c.LogicalType, c.Nullable)); continue; }
            if (committed?.Columns.FirstOrDefault(x => string.Equals(x.Name, c.Name, StringComparison.OrdinalIgnoreCase)) is { } kept)
                columns.Add(kept with { Nullable = c.Nullable, Line = 0, CollationLine = 0 });
        }
        var kept2 = columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<string> grain = committed != null && committed.Grain.Count > 0 ? committed.Grain
            : live.PrimaryKey.Count > 0 && live.PrimaryKey.All(kept2.Contains) ? live.PrimaryKey : [];
        return new SourceDescriptor(live.QualifiedName, columns, grain);
    }

    /// <summary>What differs between a committed descriptor (null: none yet) and the one the live object produces. Empty means in sync. Column order is not compared.</summary>
    public static IReadOnlyList<SourceChange> Compare(SourceDescriptor? committed, SourceDescriptor live)
    {
        if (committed == null) return [new(SourceChangeKind.New, null, $"{live.Columns.Count} column(s)")];
        var changes = new List<SourceChange>();
        var old = committed.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var c in live.Columns)
        {
            if (!old.TryGetValue(c.Name, out var o)) { changes.Add(new(SourceChangeKind.ColumnAdded, c.Name, c.Type)); continue; }
            if (!Define.LogicalTypes.Equivalent(o.Type, c.Type)) changes.Add(new(SourceChangeKind.TypeChanged, c.Name, $"{o.Type} to {c.Type}"));
            if (o.Nullable != c.Nullable) changes.Add(new(SourceChangeKind.NullabilityChanged, c.Name, c.Nullable ? "now nullable" : "now NOT NULL"));
        }
        var now = live.Columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        changes.AddRange(committed.Columns.Where(c => !now.Contains(c.Name)).Select(c => new SourceChange(SourceChangeKind.ColumnRemoved, c.Name, c.Type)));
        if (committed.Grain.Count == 0 && live.Grain.Count > 0) changes.Add(new(SourceChangeKind.GrainAdded, null, string.Join(", ", live.Grain)));
        return changes;
    }

    private static string NativeText(ColumnShape c)
    {
        var p = c.Precision != null ? $"({c.Precision}{(c.Scale != null ? $", {c.Scale}" : "")})"
            : c.Length != null ? $"({(c.Length == -1 ? "max" : c.Length.ToString())})"
            : c.Scale != null ? $"({c.Scale})" : "";
        return c.Type + p;
    }
}
