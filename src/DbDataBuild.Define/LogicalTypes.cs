using System.Text.RegularExpressions;
using DbDataBuild.Core.Questions;

namespace DbDataBuild.Define;

/// <summary>A proposed logical type, or none (with the reason a person has to decide).</summary>
public sealed record TypeProposal(string? LogicalType, ProposalCertainty? Certainty, string Reason)
{
    public bool HasProposal => LogicalType != null;
}

/// <summary>
/// DuckDB result types to the logical types written in model definitions (DESIGN.md 6.5), and when two spellings are the same type.
/// The mapping is an enumerated table. High certainty means a clean, target-neutral mapping that <c>--accept-inferred</c> may take;
/// Normal means the type exists but the mapping is lossy for some target, so a person accepts it explicitly; no proposal means ask.
/// </summary>
public static partial class LogicalTypes
{
    public static readonly IReadOnlyDictionary<string, ProposalCertainty> ExactMapping = new Dictionary<string, ProposalCertainty>(StringComparer.Ordinal)
    {
        ["BIGINT"] = ProposalCertainty.High,
        ["INTEGER"] = ProposalCertainty.High,
        ["SMALLINT"] = ProposalCertainty.High,
        ["DOUBLE"] = ProposalCertainty.High,
        ["BOOLEAN"] = ProposalCertainty.High,
        ["DATE"] = ProposalCertainty.High,
        ["TIMESTAMP"] = ProposalCertainty.High,
        ["TIME"] = ProposalCertainty.High,
        // lossy for at least one target: T-SQL TINYINT is unsigned, FLOAT is 8 bytes there, unsigned integers do not exist, and so on
        ["TINYINT"] = ProposalCertainty.Normal,
        ["FLOAT"] = ProposalCertainty.Normal,
        ["UTINYINT"] = ProposalCertainty.Normal,
        ["USMALLINT"] = ProposalCertainty.Normal,
        ["UINTEGER"] = ProposalCertainty.Normal,
        ["UBIGINT"] = ProposalCertainty.Normal,
        ["TIMESTAMP WITH TIME ZONE"] = ProposalCertainty.Normal,
        ["BLOB"] = ProposalCertainty.Normal,
        ["UUID"] = ProposalCertainty.Normal,
    };

    [GeneratedRegex(@"^DECIMAL\((\d+),\s*(\d+)\)$")]
    private static partial Regex DecimalPattern();

    [GeneratedRegex(@"^(?:TEXT|VARCHAR)\((\d+)\)$", RegexOptions.IgnoreCase)]
    private static partial Regex SizedTextPattern();

    /// <summary>The proposal for a type DuckDB reported, with no lineage information.</summary>
    public static TypeProposal FromDuckDb(string duckType)
    {
        var t = duckType.Trim().ToUpperInvariant();
        if (DecimalPattern().Match(t) is { Success: true } d)
            return new($"DECIMAL({d.Groups[1].Value}, {d.Groups[2].Value})", ProposalCertainty.High, $"DuckDB resolves {t}");
        if (ExactMapping.TryGetValue(t, out var certainty))
            return new(t, certainty, certainty == ProposalCertainty.High ? $"DuckDB resolves {t}" : $"DuckDB resolves {t}, which is lossy for some targets");
        if (t == "VARCHAR")
            return new(null, null, "DuckDB reports VARCHAR without a length and the targets need one");
        return new(null, null, $"DuckDB reports {t}, which has no direct logical type");
    }

    /// <summary>A written cast to a sized text type (polyglot reports it as TEXT(n)) is an explicit length, so it can be proposed with high certainty.</summary>
    public static string? SizedVarchar(string? castTypeOrHint) =>
        castTypeOrHint != null && SizedTextPattern().Match(castTypeOrHint.Trim()) is { Success: true } m ? $"VARCHAR({m.Groups[1].Value})" : null;

    /// <summary>Canonical spelling: upper case, single spaces, and ", " inside parentheses (DECIMAL(14, 2)).</summary>
    public static string Normalize(string type)
    {
        var t = Regex.Replace(type.Trim().ToUpperInvariant(), @"\s+", " ");
        t = Regex.Replace(t, @"\s*\(\s*", "(");
        t = Regex.Replace(t, @"\s*,\s*", ", ");
        t = Regex.Replace(t, @"\s*\)", ")");
        return t;
    }

    private static readonly Dictionary<string, string> Synonyms = new(StringComparer.Ordinal)
    {
        ["INT"] = "INTEGER", ["INT4"] = "INTEGER", ["SIGNED"] = "INTEGER",
        ["INT8"] = "BIGINT", ["LONG"] = "BIGINT",
        ["INT2"] = "SMALLINT", ["SHORT"] = "SMALLINT",
        ["BOOL"] = "BOOLEAN", ["LOGICAL"] = "BOOLEAN",
        ["REAL"] = "FLOAT", ["FLOAT4"] = "FLOAT",
        ["FLOAT8"] = "DOUBLE",
        ["DATETIME"] = "TIMESTAMP",
        ["TIMESTAMPTZ"] = "TIMESTAMP WITH TIME ZONE",
        ["STRING"] = "VARCHAR", ["TEXT"] = "VARCHAR", ["CHAR"] = "VARCHAR", ["BPCHAR"] = "VARCHAR",
    };

    [GeneratedRegex(@"^([A-Z][A-Z0-9 ]*?)(?:\((\d+)(?:, (\d+))?\))?$")]
    private static partial Regex ShapePattern();

    private static (string Base, string? P, string? S)? Shape(string type)
    {
        var n = Normalize(type);
        if (ShapePattern().Match(n) is not { Success: true } m) return null;
        var baseName = m.Groups[1].Value.Trim();
        baseName = Synonyms.TryGetValue(baseName, out var s) ? s : baseName;
        if (baseName == "NUMERIC") baseName = "DECIMAL";
        return (baseName, m.Groups[2].Success ? m.Groups[2].Value : null, m.Groups[3].Success ? m.Groups[3].Value : null);
    }

    /// <summary>Canonical spelling with synonyms resolved: INT is INTEGER, TEXT(20) is VARCHAR(20), NUMERIC(14,2) is DECIMAL(14, 2). Unrecognized text is only normalized.</summary>
    public static string Canonical(string type)
    {
        if (Shape(type) is not { } s) return Normalize(type);
        return s.P == null ? s.Base : s.S == null ? $"{s.Base}({s.P})" : $"{s.Base}({s.P}, {s.S})";
    }

    /// <summary>
    /// Whether a declared type and a resolved type are the same type. Synonyms match (INT/INTEGER), a bare DECIMAL is DuckDB's DECIMAL(18, 3),
    /// and a resolved VARCHAR with no length (all DuckDB can say) matches any declared VARCHAR(n).
    /// </summary>
    public static bool Equivalent(string declared, string resolved)
    {
        if (Shape(declared) is not { } d || Shape(resolved) is not { } r) return Normalize(declared) == Normalize(resolved);
        if (d.Base == "DECIMAL") d = d.P == null ? ("DECIMAL", "18", "3") : (d.Base, d.P, d.S ?? "0");
        if (r.Base == "DECIMAL") r = r.P == null ? ("DECIMAL", "18", "3") : (r.Base, r.P, r.S ?? "0");
        if (d.Base != r.Base) return false;
        if (d.Base == "VARCHAR") return r.P == null || d.P == r.P;
        return d.P == r.P && d.S == r.S;
    }
}
