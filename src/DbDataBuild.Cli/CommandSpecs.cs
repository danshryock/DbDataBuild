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
        new("mcp", EffectClass.OfflineOnly, "Model Context Protocol server on standard input and output: the commands as tools for an AI agent, the agent kit as resources (each tool it offers declares its own effect; commands that change a target are offered only with --allow-writes)", true),
        new("web", EffectClass.OfflineOnly, "Read-only web interface on this machine's loopback address: project health, lineage, models and their rendered scripts, tests, the support matrix (it runs only commands that read the project)", true),
        new("new", EffectClass.RepoFilesOnly, "List the project templates built in, or create a ready-to-run project from one", true),
        new("seed", EffectClass.RepoFilesOnly, "Run the seeds (DuckDB queries that generate the source data) into a DuckDB file", true),
        new("load-seeds", EffectClass.TargetWrites, "Create the seeded source tables on a target and fill them from the seeds (prints what it would do unless --apply)", true),
        new("sample", EffectClass.OfflineOnly, "Run models on generated or supplied sample data, offline", true),
        new("metadata", EffectClass.OfflineOnly, "Print everything the tool knows about the project and its models (use --format json)", true),
        new("define", EffectClass.RepoFilesOnly, "Generate or update model definition files", true),
        new("diff", EffectClass.TargetReadOnly, "Compare the data of two tables of one target: schemas, row counts and a key-based row diff done inside the engine (values are read only with --show-values)", true),
        new("graph", EffectClass.OfflineOnly, "Show the dependency graph (which table each model reads), column lineage, or a diagram of it; selectors pick the part to show", true),
        new("import-sources", EffectClass.TargetReadOnly, "Export tables and views from the target as source descriptors (sources/), so models over them bind offline (writes files only with --write)", true),
        new("test", EffectClass.OfflineOnly, "Run the project's tests: metadata rules (DuckDB SQL over the metadata views) in tests/metadata/ and model tests (given rows, expected rows) in tests/models/", true),
        new("check", EffectClass.TargetReadOnly, "Preflight findings: drift, blocks, what a plan would do", true),
        new("plan", EffectClass.TargetReadOnly, "Guided planning (writes plan files locally)", true),
        new("review", EffectClass.OfflineOnly, "Read plan files (offline, nothing applied): list a project's plans, or show one with its steps, risk, exact statements, report and what applying it would need to be allowed", true),
        new("publish-metadata", EffectClass.TrackingTablesOnly, "Store the project and model metadata as JSON in the target for introspection", true),
        new("report", EffectClass.TargetReadOnly, "Applied plans, DDL and load history, recorded shapes, and what needs attention", true),
        new("apply", EffectClass.TargetWrites, "Execute exactly the plan's recorded statements", true),
        new("run", EffectClass.TargetDataWrites, "Plan + apply for routine loads only (refuses anything else)", true),
        new("ack", EffectClass.TrackingTablesOnly, "Record a human decision (drift, definition change)", true),
        new("init", EffectClass.TrackingTablesOnly, "Create tracking schema and tables (prints the script; --apply runs it)", true),
    ];
}
