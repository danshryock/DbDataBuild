using DbDataBuild.Core;

namespace DbDataBuild.Models;

public enum CaseSensitivity { Sensitive, Insensitive }
public enum AccentSensitivity { Sensitive, Insensitive }
public enum TrailingSpace { Significant, Ignored }

/// <summary>String comparison profile (DESIGN.md 7.4). Logical collation name -> engine -> engine collation name.</summary>
public sealed record StringSemantics(
    CaseSensitivity Case,
    AccentSensitivity Accent,
    TrailingSpace TrailingSpace,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Collations)
{
    public string Describe() =>
        $"case={Case.ToString().ToLowerInvariant()}, accent={Accent.ToString().ToLowerInvariant()}, trailing_space={TrailingSpace.ToString().ToLowerInvariant()}";
}

/// <summary>Policy keys: the linter findings whose severity a project may change.</summary>
public static class PolicyKeys
{
    public const string Approximated = "approximated";
    public const string Emulated = "emulated";
    public const string Unverified = "unverified";
    public const string NotCovered = "not_covered";
    public static readonly IReadOnlyList<string> All = [Approximated, Emulated, Unverified, NotCovered];
}

/// <summary>
/// A named database endpoint: an engine (the SQL dialect: sqlserver, fabric, postgres) and an optional version of it. Its logins come from the environment (`DBDATABUILD_&lt;NAME&gt;_READ` and `_WRITE`),
/// never from the configuration. A connection named after an engine (`sqlserver`) exists without being declared; declaring it sets its version.
/// </summary>
/// <param name="Version">The T-SQL level (or major version) the tool generates for: SQL Server 2022 and 2025 at compatibility level 160 are 16, 2025 at 170 is 17.</param>
public sealed record ConnectionConfig(string Name, string Engine, int? Version = null, int Line = 0, IReadOnlyDictionary<string, ParameterValue>? DeclaredParameters = null, ConnectionTracking? Tracking = null)
{
    /// <summary>The values this connection carries (`parameters:`): what differs between connections of one application, such as a store id. Referenced as `${connection.name}`, or `${origin.name}` by a copy that reads from it.</summary>
    public IReadOnlyDictionary<string, ParameterValue> Parameters => DeclaredParameters ?? new Dictionary<string, ParameterValue>();

    /// <summary>The names of connections the tool knows without a declaration: one per engine, named after it.</summary>
    public static IReadOnlyDictionary<string, ConnectionConfig> Implicit { get; } =
        TargetNames.All.ToDictionary(e => e, e => new ConnectionConfig(e, e), StringComparer.Ordinal);
}

/// <summary>
/// The project's `tracking:` section: the connection that keeps the records of what the tool built (null: none is configured, which is "nothing is tracked", with a warning) and the schema they live in there.
/// <see cref="Disabled"/> is the explicit choice `tracking: none`: not tracked, and no warning.
/// </summary>
public sealed record TrackingConfig(string? Connection, string Schema, bool Disabled = false)
{
    public static TrackingConfig Default { get; } = new(null, ProductInfo.TrackingSchema);
}

/// <summary>A connection's own `tracking:`: `none` (an explicit opt-out), or another tracking connection and, optionally, schema.</summary>
public sealed record ConnectionTracking(bool None, string? Connection, string? Schema);

/// <summary>Where the records about one data connection are kept, resolved: the tracking connection, its engine, and the schema there.</summary>
public sealed record TrackingTarget(string Connection, string Engine, string Schema);

/// <summary>What a connection's tracking resolved to. <see cref="Target"/> is null when nothing is tracked; <see cref="Explicit"/> says that was a choice (`tracking: none`), so no warning is due.</summary>
public sealed record TrackingResolution(TrackingTarget? Target, bool Explicit);

/// <summary>Project configuration (<c>dbdatabuild.yml</c>). Offline settings only: credentials never live here (DESIGN.md 9.2).</summary>
public sealed record ProjectConfig(
    IReadOnlyList<string> DefaultConnections,
    IReadOnlyDictionary<string, ConnectionConfig> Connections,
    TrackingConfig Tracking,
    StringSemantics StringSemantics,
    IReadOnlyDictionary<string, Severity> Policy,
    IReadOnlyDictionary<string, int>? SourceLines = null,
    IReadOnlyDictionary<string, IReadOnlyList<HookDefinition>>? DeclaredHookGroups = null,
    bool StoreMetadataOnApply = false,
    bool LoweringEnabled = true,
    bool LintIndexes = true,
    bool LintSlices = true,
    RewriteSettings? Rewrites = null)
{
    /// <summary>Named, ordered sets of hooks that models reference with `use:` (`hook_groups:` in dbdatabuild.yml).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<HookDefinition>> HookGroups => DeclaredHookGroups ?? new Dictionary<string, IReadOnlyList<HookDefinition>>();

    /// <summary>1-based lines in dbdatabuild.yml of settings that were present, keyed by dotted path (for diagnostics). Empty for defaults.</summary>
    public IReadOnlyDictionary<string, int> Lines { get; } = SourceLines ?? new Dictionary<string, int>();

    /// <summary>The `defaults:` section of dbdatabuild.yml, the first layer of every model's settings (null when there is none). Its nodes are positions in dbdatabuild.yml.</summary>
    public Yaml.YamlMapping? Defaults { get; init; }

    /// <summary>The project's own `parameters:` (the root file's; the folder files above a model override them for it). Referenced as `${project.name}`.</summary>
    public IReadOnlyDictionary<string, ParameterValue> Parameters { get; init; } = new Dictionary<string, ParameterValue>();

    /// <summary>The schema of the tracking tables (the project's; a connection may name another connection but keeps this schema unless it says its own).</summary>
    public string TrackingSchema => Tracking.Schema;

    /// <summary>
    /// Where the records about <paramref name="connection"/> are kept: its own `tracking:`, else the project's. Nothing is tracked unless a project says where (`tracking: { connection: audit }`); `tracking: none`
    /// is the explicit opt-out, and the only difference between the two is that the first earns a warning.
    /// </summary>
    public TrackingResolution TrackingOf(string connection)
    {
        Connections.TryGetValue(connection, out var c);
        if (c?.Tracking is { } own)
        {
            if (own.None) return new(null, true);
            var name = own.Connection ?? Tracking.Connection;
            return name != null && Connections.TryGetValue(name, out var t) ? new(new TrackingTarget(name, t.Engine, own.Schema ?? Tracking.Schema), true) : new(null, false);
        }
        if (Tracking.Disabled) return new(null, true);
        return Tracking.Connection is { } p && Connections.TryGetValue(p, out var pc) ? new(new TrackingTarget(p, pc.Engine, Tracking.Schema), true) : new(null, false);
    }

    /// <summary>The engine of a connection, or null when the project has no such connection.</summary>
    public string? EngineOf(string connection) => Connections.TryGetValue(connection, out var c) ? c.Engine : null;

    /// <summary>The version a connection is configured with, per connection (two servers of one engine can differ).</summary>
    public IReadOnlyDictionary<string, int> TargetVersions => Connections.Where(c => c.Value.Version != null).ToDictionary(c => c.Key, c => c.Value.Version!.Value, StringComparer.Ordinal);

    /// <summary>Built-in defaults, used when no file exists. Printed in every command header so they are never hidden.</summary>
    public static readonly ProjectConfig Default = new(
        [TargetNames.SqlServer],
        ConnectionConfig.Implicit,
        TrackingConfig.Default,
        new StringSemantics(CaseSensitivity.Insensitive, AccentSensitivity.Sensitive, TrailingSpace.Ignored,
            new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["default"] = new Dictionary<string, string>
                {
                    ["duckdb"] = "NOCASE",
                    ["sqlserver"] = "Latin1_General_100_CI_AS",
                    ["fabric"] = "Latin1_General_100_CI_AS_KS_WS_SC_UTF8", // [VERIFY] DESIGN.md 7.4
                },
            }),
        new Dictionary<string, Severity>
        {
            [PolicyKeys.Approximated] = Severity.Warning,
            [PolicyKeys.Emulated] = Severity.Note,
            [PolicyKeys.Unverified] = Severity.Warning,
            [PolicyKeys.NotCovered] = Severity.Warning,
        });

    public string Describe() =>
        $"default connections: {string.Join(", ", DefaultConnections)}; string semantics: {StringSemantics.Describe()}; " +
        $"target versions: {(TargetVersions.Count == 0 ? "not set" : string.Join(", ", TargetVersions.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => $"{v.Key} {v.Value}")))}" +
        (Connections.Any(c => !ConnectionConfig.Implicit.ContainsKey(c.Key) || c.Value.Engine != c.Key)
            ? $"; connections: {string.Join(", ", Connections.Where(c => !ConnectionConfig.Implicit.ContainsKey(c.Key) || c.Value.Engine != c.Key).OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => $"{c.Key} ({c.Value.Engine})"))}" : "");
}
