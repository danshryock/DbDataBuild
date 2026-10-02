namespace DbDataBuild.Models;

public static class ModelKinds
{
    public const string View = "view";
    public const string Full = "full";
    public const string IncrementalByUniqueKey = "incremental_by_unique_key";
    public const string IncrementalByTimeRange = "incremental_by_time_range";
    public static readonly IReadOnlyList<string> All = [View, Full, IncrementalByUniqueKey, IncrementalByTimeRange];
}

public static class TargetNames
{
    public const string SqlServer = "sqlserver";
    public const string Fabric = "fabric";
    public const string Postgres = "postgres";
    public static readonly IReadOnlyList<string> All = [SqlServer, Fabric, Postgres];
}

public sealed record ColumnDefinition(string Name, string Type, bool Nullable = true, string? Collation = null, int Line = 0, int CollationLine = 0);

public sealed record RenameDefinition(string From, string To);

public sealed record ModelDefinition(
    string Name,
    string KindType,
    IReadOnlyList<string> UniqueKey,
    string? TimeColumn,
    string? Lookback,
    IReadOnlyList<string> Grain,
    IReadOnlyList<string>? Targets,   // null: project default applies
    IReadOnlyList<ColumnDefinition> Columns,
    IReadOnlyList<RenameDefinition> Renames,
    IReadOnlyList<LoadOperation>? DeclaredLoads = null,
    IReadOnlyList<IndexDefinition>? DeclaredIndexes = null,
    IReadOnlyList<HookDefinition>? DeclaredHooks = null)
{
    public IReadOnlyList<IndexDefinition> Indexes => DeclaredIndexes ?? [];

    /// <summary>Hook entries in the order written (the order they run in); groups are expanded by <see cref="HookReader.Resolve"/>.</summary>
    public IReadOnlyList<HookDefinition> Hooks => DeclaredHooks ?? [];

    /// <summary>Declared load operations (the `loads:` block). Empty when the kind supplies its single default (see <see cref="LoadPlan"/>).</summary>
    public IReadOnlyList<LoadOperation> Loads => DeclaredLoads ?? [];
}
