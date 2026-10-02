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
        new("metadata", EffectClass.OfflineOnly, "Print everything the tool knows about the project and its models (use --format json)", true),
        new("define", EffectClass.RepoFilesOnly, "Generate or update model definition files", true),
        new("check", EffectClass.TargetReadOnly, "Preflight findings: drift, blocks, what a plan would do", true),
        new("plan", EffectClass.TargetReadOnly, "Guided planning (writes plan files locally)", true),
        new("report", EffectClass.TargetReadOnly, "Applied plans, DDL and load history, recorded shapes, and what needs attention", true),
        new("apply", EffectClass.TargetWrites, "Execute exactly the plan's recorded statements", true),
        new("run", EffectClass.TargetDataWrites, "Plan + apply for routine loads only (refuses anything else)", true),
        new("ack", EffectClass.TrackingTablesOnly, "Record a human decision (drift, definition change)", true),
        new("init", EffectClass.TrackingTablesOnly, "Create tracking schema and tables (prints the script; --apply runs it)", true),
    ];
}
