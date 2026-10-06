namespace DbDataBuild.Models;

public static class ModelKinds
{
    public const string View = "view";
    public const string Full = "full";
    public const string IncrementalByUniqueKey = "incremental_by_unique_key";
    public const string IncrementalByTimeRange = "incremental_by_time_range";

    /// <summary>A table filled by copying the rows of another model that lives on another connection: no query, the columns are the origin's, always persisted.</summary>
    public const string Copy = "copy";
    public static readonly IReadOnlyList<string> All = [View, Full, IncrementalByUniqueKey, IncrementalByTimeRange, Copy];
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
    IReadOnlyList<HookDefinition>? DeclaredHooks = null,
    IReadOnlyList<string>? DeclaredLintIgnore = null,
    RewriteSettings? Rewrites = null,
    string? From = null,
    int FromLine = 0)
{
    /// <summary>True for a model that copies another one (<see cref="ModelKinds.Copy"/>): `From` is the model it copies.</summary>
    public bool IsCopy => KindType == ModelKinds.Copy;

    /// <summary>Diagnostic codes of advisory lints (DDB-223, DDB-224) the operator has silenced for this model (`lint_ignore:`).</summary>
    public IReadOnlyList<string> LintIgnore => DeclaredLintIgnore ?? [];

    public IReadOnlyList<IndexDefinition> Indexes => DeclaredIndexes ?? [];

    /// <summary>Hook entries in the order written (the order they run in); groups are expanded by <see cref="HookReader.Resolve"/>.</summary>
    public IReadOnlyList<HookDefinition> Hooks => DeclaredHooks ?? [];

    /// <summary>Declared load operations (the `loads:` block). Empty when the kind supplies its single default (see <see cref="LoadPlan"/>).</summary>
    public IReadOnlyList<LoadOperation> Loads => DeclaredLoads ?? [];
}
