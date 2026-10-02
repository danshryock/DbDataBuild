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
        new("agent-kit", EffectClass.RepoFilesOnly, "Install the skill and JSON Schemas an AI coding agent needs to work in a project (lists them unless --write)", true),
        new("tui", EffectClass.OfflineOnly, "Interactive terminal interface: choose, plan and run operations (each action it runs declares its own effect)", true),
        new("sample", EffectClass.OfflineOnly, "Run models on generated or supplied sample data, offline", true),
        new("metadata", EffectClass.OfflineOnly, "Print everything the tool knows about the project and its models (use --format json)", true),
        new("define", EffectClass.RepoFilesOnly, "Generate or update model definition files", true),
        new("import-sources", EffectClass.TargetReadOnly, "Export tables and views from the target as source descriptors (sources/), so models over them bind offline (writes files only with --write)", true),
        new("check", EffectClass.TargetReadOnly, "Preflight findings: drift, blocks, what a plan would do", true),
        new("plan", EffectClass.TargetReadOnly, "Guided planning (writes plan files locally)", true),
        new("publish-metadata", EffectClass.TrackingTablesOnly, "Store the project and model metadata as JSON in the target for introspection", true),
        new("report", EffectClass.TargetReadOnly, "Applied plans, DDL and load history, recorded shapes, and what needs attention", true),
        new("apply", EffectClass.TargetWrites, "Execute exactly the plan's recorded statements", true),
        new("run", EffectClass.TargetDataWrites, "Plan + apply for routine loads only (refuses anything else)", true),
        new("ack", EffectClass.TrackingTablesOnly, "Record a human decision (drift, definition change)", true),
        new("init", EffectClass.TrackingTablesOnly, "Create tracking schema and tables (prints the script; --apply runs it)", true),
    ];
}
