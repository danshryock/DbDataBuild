namespace DbDataBuild.State;

public enum ObjectKind { Table, View }

/// <summary>An object as the live catalog reports it. Columns are in ordinal order; the hashes sort them (see <see cref="Hashing"/>).</summary>
public sealed record ObjectShape(string Schema, string Name, ObjectKind Kind, IReadOnlyList<ColumnShape> Columns, IReadOnlyList<PhysicalItem> Physical)
{
    public string QualifiedName => $"{Schema}.{Name}";
    public string ShapeHash => Hashing.ShapeHash(Columns);
    public string PhysicalHash => Hashing.PhysicalHash(Physical);
}

/// <summary>What the target holds versus what the tool last recorded for an object (the object-level rows of DESIGN.md section 11).</summary>
public enum ObjectState
{
    /// <summary>The object does not exist on the target.</summary>
    Missing,
    /// <summary>It exists and the tool has never recorded it: ask whether to adopt it.</summary>
    Untracked,
    /// <summary>Live shape equals the last recorded shape.</summary>
    InSync,
    /// <summary>Live shape differs from the last recorded shape: someone changed it outside the tool. Blocks until acknowledged or re-planned.</summary>
    OutOfBand,
}

public static class Drift
{
    /// <param name="live">The live object, or null when absent.</param>
    /// <param name="recordedShapeHash">The shape hash of the newest `schema_version` row for the object, or null when there is none.</param>
    public static ObjectState Classify(ObjectShape? live, string? recordedShapeHash) =>
        live == null ? ObjectState.Missing
        : recordedShapeHash == null ? ObjectState.Untracked
        : live.ShapeHash == recordedShapeHash ? ObjectState.InSync
        : ObjectState.OutOfBand;
}

/// <summary>The engine-neutral text of an index's definition: uniqueness, key columns in order (`desc` when descending) and included columns. Used to compare a declared index with a live one.</summary>
public static class IndexText
{
    public static string Canonical(bool unique, IEnumerable<string> keys, IEnumerable<string> include) =>
        $"unique={(unique ? 1 : 0)};keys={string.Join(",", keys)};include={string.Join(",", include)}";
}
