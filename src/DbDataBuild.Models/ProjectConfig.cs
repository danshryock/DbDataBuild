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

/// <summary>Project configuration (<c>dbdatabuild.yml</c>). Offline settings only: credentials never live here (DESIGN.md 9.2).</summary>
public sealed record ProjectConfig(
    IReadOnlyList<string> DefaultTargets,
    IReadOnlyDictionary<string, int> TargetVersions,
    string TrackingSchema,
    StringSemantics StringSemantics,
    IReadOnlyDictionary<string, Severity> Policy,
    IReadOnlyDictionary<string, int>? SourceLines = null,
    IReadOnlyDictionary<string, IReadOnlyList<HookDefinition>>? DeclaredHookGroups = null,
    bool StoreMetadataOnApply = false,
    bool LoweringEnabled = true,
    bool LintIndexes = true,
    bool LintSlices = true)
{
    /// <summary>Named, ordered sets of hooks that models reference with `use:` (`hook_groups:` in dbdatabuild.yml).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<HookDefinition>> HookGroups => DeclaredHookGroups ?? new Dictionary<string, IReadOnlyList<HookDefinition>>();

    /// <summary>1-based lines in dbdatabuild.yml of settings that were present, keyed by dotted path (for diagnostics). Empty for defaults.</summary>
    public IReadOnlyDictionary<string, int> Lines { get; } = SourceLines ?? new Dictionary<string, int>();

    /// <summary>Built-in defaults, used when no file exists. Printed in every command header so they are never hidden.</summary>
    public static readonly ProjectConfig Default = new(
        [TargetNames.SqlServer],
        new Dictionary<string, int>(),
        ProductInfo.TrackingSchema,
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
        $"default targets: {string.Join(", ", DefaultTargets)}; string semantics: {StringSemantics.Describe()}; " +
        $"target versions: {(TargetVersions.Count == 0 ? "not set" : string.Join(", ", TargetVersions.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => $"{v.Key} {v.Value}")))}";
}
