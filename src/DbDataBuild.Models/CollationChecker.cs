using System.Text.RegularExpressions;
using DbDataBuild.Core;

namespace DbDataBuild.Models;

/// <summary>What a collation name says about string comparison. A null dimension means the name does not tell, which is never assumed to match.</summary>
public sealed record CollationTraits(CaseSensitivity? Case, AccentSensitivity? Accent, TrailingSpace? Trailing)
{
    public static readonly CollationTraits Unknown = new(null, null, null);
}

/// <summary>
/// Reads collation names offline (DESIGN.md 7.4). Rules per engine are data-like pure functions, enumerated by tests.
/// Behavior that no name can settle (Fabric trailing spaces, unrecognized names) stays unknown and is reported, not assumed.
/// </summary>
public static class CollationTraitsParser
{
    public const string DuckDb = "duckdb";

    public static CollationTraits Parse(string engine, string name) => engine switch
    {
        TargetNames.SqlServer => ParseSqlServerStyle(name, trailing: TrailingSpace.Ignored),   // ANSI padding: trailing spaces ignored in =, whatever the collation
        TargetNames.Fabric => ParseSqlServerStyle(name, trailing: null),                       // [VERIFY] DESIGN.md 7.4: Fabric trailing-space semantics
        TargetNames.Postgres => ParsePostgres(name),
        DuckDb => ParseDuckDb(name),
        _ => CollationTraits.Unknown,
    };

    // Windows and SQL collations: ..._CI_AS_KS_WS_SC_UTF8, SQL_Latin1_General_CP1_CI_AS, Latin1_General_BIN2
    private static CollationTraits ParseSqlServerStyle(string name, TrailingSpace? trailing)
    {
        var tokens = name.ToUpperInvariant().Split('_');
        var binary = tokens.Any(t => t is "BIN" or "BIN2");
        CaseSensitivity? @case = binary ? CaseSensitivity.Sensitive
            : tokens.Contains("CI") ? CaseSensitivity.Insensitive
            : tokens.Contains("CS") ? CaseSensitivity.Sensitive : null;
        AccentSensitivity? accent = binary ? AccentSensitivity.Sensitive
            : tokens.Contains("AI") ? AccentSensitivity.Insensitive
            : tokens.Contains("AS") ? AccentSensitivity.Sensitive : null;
        return new CollationTraits(@case, accent, trailing);
    }

    // Verified on DuckDB 1.5.4: NOCASE ignores case only, NOACCENT ignores accents only, they chain with '.', NFC and locale
    // collations stay case- and accent-sensitive, and trailing spaces are significant under every collation.
    private static CollationTraits ParseDuckDb(string name)
    {
        var @case = CaseSensitivity.Sensitive;
        var accent = AccentSensitivity.Sensitive;
        foreach (var part in name.Trim().ToUpperInvariant().Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == "NOCASE") @case = CaseSensitivity.Insensitive;
            else if (part == "NOACCENT") accent = AccentSensitivity.Insensitive;
            else if (part == "NFC" || Regex.IsMatch(part, "^[A-Z]{2,3}([_-][A-Z]{2})?$")) { /* sensitive, unchanged */ }
            else return new CollationTraits(null, null, TrailingSpace.Significant);
        }
        return new CollationTraits(@case, accent, TrailingSpace.Significant);
    }

    // libc and C locales are deterministic and case/accent-sensitive; ICU nondeterministic collations are recognized by their
    // strength key (-u-ks-level1 ignores case and accents, level2 ignores case). varchar `=` keeps trailing spaces in all of them.
    private static CollationTraits ParsePostgres(string name)
    {
        var n = name.Trim().ToLowerInvariant();
        if (n is "c" or "posix" or "ucs_basic" or "default" || Regex.IsMatch(n, @"^[a-z]{2,3}_[a-z]{2}(\.utf-?8)?$") ||
            Regex.IsMatch(n, @"^[a-z]{2,3}(-[a-z]{2})?-x-icu$"))
            return new CollationTraits(CaseSensitivity.Sensitive, AccentSensitivity.Sensitive, TrailingSpace.Significant);
        if (n.Contains("-u-ks-level1")) return new CollationTraits(CaseSensitivity.Insensitive, AccentSensitivity.Insensitive, TrailingSpace.Significant);
        if (n.Contains("-u-ks-level2")) return new CollationTraits(CaseSensitivity.Insensitive, AccentSensitivity.Sensitive, TrailingSpace.Significant);
        return new CollationTraits(null, null, TrailingSpace.Significant);
    }
}

/// <summary>Offline check that the configured collations can satisfy the project's string profile (reported before any connection exists).</summary>
public static class CollationChecker
{
    public const string DefaultLogicalName = "default";

    public static IReadOnlyList<Diagnostic> Check(ProjectConfig config, IReadOnlyList<ModelSource> sources)
    {
        var diags = new List<Diagnostic>();
        var cfgFile = ProductInfo.ConfigFile;
        int Line(string key) => config.Lines.TryGetValue(key, out var l) ? l : 0;

        // Engines in play: every model's targets (or the project defaults) plus DuckDB, which runs the offline emulation.
        // The project's default targets count only when a model relies on them, or when there are no models yet.
        var engines = sources.SelectMany(s => s.Definition.Targets ?? config.DefaultTargets).Concat(sources.Count == 0 ? config.DefaultTargets : [])
            .Distinct().OrderBy(e => e, StringComparer.Ordinal).Append(CollationTraitsParser.DuckDb).ToList();
        var collations = config.StringSemantics.Collations;

        if (!collations.TryGetValue(DefaultLogicalName, out var defaults))
        {
            diags.Add(new Diagnostic(DiagnosticCatalog.CollationNotConfigured, new(cfgFile, Line("string_semantics.collations"), 1),
                $"`string_semantics.collations` has no `{DefaultLogicalName}` entry, which is the project default collation.",
                Fix: $"Add `{DefaultLogicalName}:` with an entry per engine ({string.Join(", ", engines)})."));
            return diags;
        }

        foreach (var engine in engines)
        {
            if (!defaults.TryGetValue(engine, out var name))
            {
                diags.Add(new Diagnostic(DiagnosticCatalog.CollationNotConfigured, new(cfgFile, Line($"string_semantics.collations.{DefaultLogicalName}"), 1),
                    $"No default collation is configured for `{engine}`, which this project uses.",
                    Fix: $"Add `{engine}: <collation>` under `string_semantics.collations.{DefaultLogicalName}`."));
                continue;
            }
            diags.AddRange(CheckProfile(config, engine, name, new SourceLocation(cfgFile, Line($"string_semantics.collations.{DefaultLogicalName}.{engine}"), 1)));
        }

        foreach (var source in sources)
        {
            var modelEngines = (source.Definition.Targets ?? config.DefaultTargets).Append(CollationTraitsParser.DuckDb).Distinct().ToList();
            foreach (var column in source.Definition.Columns.Where(c => c.Collation != null))
            {
                var loc = new SourceLocation(source.DefinitionFile, column.CollationLine, 1);
                if (!collations.TryGetValue(column.Collation!, out var perEngine))
                {
                    diags.Add(new Diagnostic(DiagnosticCatalog.CollationNotConfigured, loc,
                        $"Column `{column.Name}` uses collation `{column.Collation}`, which is not defined under `string_semantics.collations`.",
                        Fix: $"Define `{column.Collation}` under `string_semantics.collations` in {cfgFile}, or use `{DefaultLogicalName}`."));
                    continue;
                }
                foreach (var engine in modelEngines.Where(e => !perEngine.ContainsKey(e)))
                    diags.Add(new Diagnostic(DiagnosticCatalog.CollationNotConfigured, loc,
                        $"Column `{column.Name}` uses collation `{column.Collation}`, which has no entry for `{engine}`.",
                        Fix: $"Add `{engine}: <collation>` under `string_semantics.collations.{column.Collation}`."));
            }
        }
        return diags;
    }

    private static IEnumerable<Diagnostic> CheckProfile(ProjectConfig config, string engine, string name, SourceLocation loc)
    {
        var profile = config.StringSemantics;
        var traits = CollationTraitsParser.Parse(engine, name);
        var failures = new List<string>();
        var unknown = new List<string>();

        Compare("case", profile.Case.ToString().ToLowerInvariant(), traits.Case?.ToString().ToLowerInvariant(), profile.Case == traits.Case);
        Compare("accent", profile.Accent.ToString().ToLowerInvariant(), traits.Accent?.ToString().ToLowerInvariant(), profile.Accent == traits.Accent);
        // DuckDB cannot ignore trailing spaces with any collation; the profile is met there by the offline rtrim() rewrite (DESIGN.md 7.4).
        if (engine != CollationTraitsParser.DuckDb)
            Compare("trailing_space", profile.TrailingSpace.ToString().ToLowerInvariant(), traits.Trailing?.ToString().ToLowerInvariant(), profile.TrailingSpace == traits.Trailing);

        void Compare(string dimension, string required, string? actual, bool ok)
        {
            if (actual == null) unknown.Add($"{dimension} (profile requires {required})");
            else if (!ok) failures.Add($"{dimension} is {actual} but the profile requires {required}");
        }

        if (failures.Count > 0)
            yield return new Diagnostic(DiagnosticCatalog.CollationCannotSatisfyProfile, loc,
                $"Collation `{name}` for {engine} cannot satisfy the string profile ({profile.Describe()}): {string.Join("; ", failures)}.",
                Fix: $"Change `string_semantics.collations.default.{engine}` to a collation with the required behavior, or change `string_semantics`.");
        if (unknown.Count > 0)
            yield return new Diagnostic(DiagnosticCatalog.CollationNotVerifiable, loc,
                $"Cannot tell from `{name}` how {engine} compares strings for: {string.Join("; ", unknown)}.");
    }
}
