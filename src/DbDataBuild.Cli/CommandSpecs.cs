using DbDataBuild.Core;

namespace DbDataBuild.Cli;

/// <summary>Effect classes (DESIGN.md section 9.1). Printed in --help and in each run header.</summary>
public enum EffectClass
{
    OfflineOnly,
    RepoFilesOnly,
    TargetReadOnly,
    TargetWrites,
    TargetDataWrites,
    TrackingTablesOnly,
}

public static class EffectClasses
{
    public static string Describe(this EffectClass e) => e switch
    {
        EffectClass.OfflineOnly => "Offline only",
        EffectClass.RepoFilesOnly => "Repo files only (no target connection)",
        EffectClass.TargetReadOnly => "Target read-only",
        EffectClass.TargetWrites => "Target writes (DDL and/or data, as the plan states)",
        EffectClass.TargetDataWrites => "Target writes (data only)",
        EffectClass.TrackingTablesOnly => "Tracking tables only",
        _ => throw new ArgumentOutOfRangeException(nameof(e)),
    };
}

public sealed record CommandSpec(string Name, EffectClass Effect, string Purpose, bool Implemented);

/// <summary>The full command surface as data. A test asserts every command declares an effect class.</summary>
public static class CommandSpecs
{
    public static readonly IReadOnlyList<CommandSpec> All =
    [
        new("validate", EffectClass.OfflineOnly, "Validate config and models (offline)", true),
        new("render", EffectClass.RepoFilesOnly, "Render load operations and resolvers per target", true),
        new("loads", EffectClass.OfflineOnly, "Print the model x target x operation pairing table", true),
        new("matrix", EffectClass.OfflineOnly, "Print the support matrix and portability report", true),
        new("explain", EffectClass.OfflineOnly, "Long-form explanation of a diagnostic code", true),
        new("define", EffectClass.RepoFilesOnly, "Generate or update model definition files", true),
        new("check", EffectClass.TargetReadOnly, "Preflight findings: drift, blocks, history inputs", false),
        new("plan", EffectClass.TargetReadOnly, "Guided planning (writes plan files locally)", false),
        new("report", EffectClass.TargetReadOnly, "History consistency, drift, run and DDL history", false),
        new("apply", EffectClass.TargetWrites, "Execute exactly the plan's recorded statements", false),
        new("run", EffectClass.TargetDataWrites, "Plan + apply for routine loads only", false),
        new("ack", EffectClass.TrackingTablesOnly, "Record a human decision (drift, history)", false),
        new("init", EffectClass.TrackingTablesOnly, "Create tracking schema and tables (prints the script; --apply runs it)", true),
    ];
}
