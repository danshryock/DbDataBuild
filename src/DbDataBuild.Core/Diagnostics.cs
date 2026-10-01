using System.Text;

namespace DbDataBuild.Core;

public enum Severity { Error, Warning, Note }

/// <summary>Static metadata for one diagnostic code. The catalog of these is data; docs are generated from it.</summary>
public sealed record DiagnosticDescriptor(
    string Code,
    Severity DefaultSeverity,
    string Title,
    string Supported,
    string Fix,
    string Explanation);

public readonly record struct SourceLocation(string File, int Line, int Column)
{
    public override string ToString() => Line > 0 ? $"{File}:{Line}" : File;
}

/// <summary>One reported problem. <see cref="Found"/> says what was found; supported/fix come from the descriptor unless overridden.</summary>
public sealed record Diagnostic(
    DiagnosticDescriptor Descriptor,
    SourceLocation Location,
    string Found,
    string? Supported = null,
    string? Fix = null,
    Severity? SeverityOverride = null)
{
    public string Code => Descriptor.Code;

    /// <summary>The descriptor's default, unless project policy (dbdatabuild.yml) raised or lowered it.</summary>
    public Severity Severity => SeverityOverride ?? Descriptor.DefaultSeverity;
}

public static class DiagnosticCatalog
{
    private static DiagnosticDescriptor E(string code, string title, string supported, string fix, string explanation) =>
        new(ProductInfo.DiagnosticPrefix + code, Severity.Error, title, supported, fix, explanation);
    private static DiagnosticDescriptor W(string code, string title, string supported, string fix, string explanation) =>
        new(ProductInfo.DiagnosticPrefix + code, Severity.Warning, title, supported, fix, explanation);
    private static DiagnosticDescriptor N(string code, string title, string supported, string fix, string explanation) =>
        new(ProductInfo.DiagnosticPrefix + code, Severity.Note, title, supported, fix, explanation);

    // 1xx: config / model definition
    public static readonly DiagnosticDescriptor YamlSyntax = E("101", "YAML syntax error",
        "Well-formed YAML in the strict subset (see DESIGN.md section 6.3).",
        "Correct the syntax at the reported position.",
        "The file could not be parsed as YAML, so nothing in it could be validated.");
    public static readonly DiagnosticDescriptor DuplicateKey = E("102", "Duplicate key",
        "Each key at most once per mapping.",
        "Remove or merge the duplicate key.",
        "Duplicate keys are ambiguous (parsers disagree on which wins), so they are always errors.");
    public static readonly DiagnosticDescriptor UnsupportedYamlFeature = E("103", "Unsupported YAML feature",
        "Plain mappings, sequences, and scalars. No anchors, aliases, merge keys, or custom tags.",
        "Write the value out in full.",
        "The loader accepts a strict YAML subset so definitions mean the same thing in every editor and parser.");
    public static readonly DiagnosticDescriptor UnknownKey = E("104", "Unknown key",
        "Only the documented keys for this section.",
        "Remove the key, or correct its spelling.",
        "Unknown keys are errors, never ignored, so a typo cannot silently drop a setting.");
    public static readonly DiagnosticDescriptor MissingKey = E("105", "Missing required key",
        "All required keys for this section.",
        "Add the missing key.",
        "A required key is absent from the definition.");
    public static readonly DiagnosticDescriptor InvalidValue = E("106", "Invalid value",
        "A value of the documented shape and enumeration.",
        "Use one of the supported values.",
        "All scalars are read as strings and validated against the documented shape. Nothing is coerced by YAML type rules.");
    public static readonly DiagnosticDescriptor NameMismatch = E("107", "Model name does not match path",
        "name equals the path under models/ with '/' replaced by '.' (models/marts/fct_orders.yml is marts.fct_orders).",
        "Change `name:` or move the file.",
        "The explicit name and the path convention must agree.");
    public static readonly DiagnosticDescriptor OrphanFile = E("108", "Orphan model file",
        "Every .sql has a .yml with the same base name, and vice versa.",
        "Run `dbdatabuild define <path>` to create the missing definition, or remove the orphan.",
        "A model is a pair of files. One without the other cannot be built.");

    public static readonly DiagnosticDescriptor ConfigNotFound = W("109", "Project configuration file not found",
        $"A {ProductInfo.ConfigFile} at the project root.",
        $"Create {ProductInfo.ConfigFile} (see schemas/config.schema.json), or accept the built-in defaults printed in the command header.",
        "Without a configuration file the built-in defaults apply. They are printed in every command header so they are never hidden.");

    // 2xx: model semantics
    public static readonly DiagnosticDescriptor MissingUniqueKey = E("214", "Missing unique_key",
        "unique_key: [<column>, ...]",
        "Add `unique_key:` under `kind:`.",
        "Kind incremental_by_unique_key upserts by key and cannot run without one.");
    public static readonly DiagnosticDescriptor MissingTimeColumn = E("215", "Missing time_column",
        "time_column: <column>",
        "Add `time_column:` under `kind:`.",
        "Kind incremental_by_time_range derives its range from MAX(time_column) in the target.");
    public static readonly DiagnosticDescriptor GrainMismatch = E("216", "Grain missing or inconsistent",
        "grain is required for incremental kinds, and must equal unique_key for incremental_by_unique_key.",
        "Set `grain:` to the columns that uniquely identify a row.",
        "Grain documents row identity and is checked against the declared key.");
    public static readonly DiagnosticDescriptor UnknownColumnReference = E("217", "Reference to undeclared column",
        "Columns named in grain, unique_key, time_column, and renames must be declared in `columns`.",
        "Declare the column, or correct the reference.",
        "Declared columns are the model's output schema. Everything else must refer to it.");

    // 3xx: matrix / portability
    public static readonly DiagnosticDescriptor ConstructUnsupported = E("301", "Construct unsupported on a declared target",
        "Constructs whose matrix status for every declared target is native, translated, emulated, approximated or unverified.",
        "Rewrite the model without the construct, or remove the target from `targets:`.",
        "The support matrix marks this construct `unsupported` for a target the model declares, so the build would fail or give wrong results there.");
    public static readonly DiagnosticDescriptor ConstructApproximated = W("302", "Construct approximated on a declared target",
        "A rewrite with a documented semantic difference (see the note).",
        "Check that the documented difference is acceptable for this model, or avoid the construct.",
        "The construct is rewritten for the target, but the result can differ from DuckDB. The matrix note says how.");
    public static readonly DiagnosticDescriptor ConstructEmulated = N("303", "Construct emulated on a declared target",
        "A multi-statement or helper rewrite (see the note).",
        "No action needed unless the note describes a concern for this model.",
        "The target has no native equivalent; the rewrite emulates it. Check atomicity and performance notes.");
    public static readonly DiagnosticDescriptor ConstructUnverified = W("304", "Construct unverified on a declared target",
        "A construct with a passing conformance test for the target.",
        "Add a conformance case and a matrix test reference, or avoid the construct.",
        "A rewrite exists, but no conformance test has passed for it on this target.");
    public static readonly DiagnosticDescriptor ConstructNotCovered = W("305", "Construct not covered by the support matrix",
        "Nodes, functions and clauses listed in matrix/covered.yml or matrix/constructs.yml.",
        "Add a conformance case for the construct and extend the matrix, or avoid it.",
        "The linter never assumes an unknown construct is safe. It was not exercised by any passing conformance case.");
    public static readonly DiagnosticDescriptor SqlParseFailure = E("306", "Model query could not be parsed",
        "A single SELECT in DuckDB dialect.",
        "Correct the SQL so that it runs in DuckDB.",
        "The query body could not be parsed as DuckDB SQL. There is no best-effort fallback.");
    public static readonly DiagnosticDescriptor NotASingleSelect = E("307", "Model query is not a single SELECT",
        "Exactly one SELECT (or set operation) statement.",
        "Keep one query in the .sql file, with no other statements.",
        "A model body is a single query that load strategies can wrap.");
    public static readonly DiagnosticDescriptor ConstructNeedsVersion = W("308", "Construct needs a minimum target version",
        "Target versions at or above the matrix min_version.",
        "Confirm the target version, or avoid the construct.",
        "The construct is supported only from a certain engine version. Target versions are not configured yet, so this cannot be checked offline.");

    // 9xx: internal
    public static readonly DiagnosticDescriptor InternalError = new(ProductInfo.DiagnosticPrefix + "900",
        Severity.Error, "Internal error (tool bug)",
        "n/a", "Report this with the command line used.",
        "An unhandled exception occurred. This is a bug in the tool, not in your configuration.");

    public static readonly IReadOnlyList<DiagnosticDescriptor> All =
    [
        YamlSyntax, DuplicateKey, UnsupportedYamlFeature, UnknownKey, MissingKey, InvalidValue, NameMismatch, OrphanFile, ConfigNotFound,
        MissingUniqueKey, MissingTimeColumn, GrainMismatch, UnknownColumnReference,
        ConstructUnsupported, ConstructApproximated, ConstructEmulated, ConstructUnverified, ConstructNotCovered,
        SqlParseFailure, NotASingleSelect, ConstructNeedsVersion,
        InternalError,
    ];

    public static DiagnosticDescriptor? Find(string code) =>
        All.FirstOrDefault(d => string.Equals(d.Code, code, StringComparison.OrdinalIgnoreCase));
}

public static class DiagnosticFormatter
{
    public static string Format(Diagnostic d)
    {
        var sb = new StringBuilder();
        sb.Append(d.Severity switch { Severity.Error => "error", Severity.Warning => "warning", _ => "note" }).Append(' ').Append(d.Code).Append("  ").AppendLine(d.Location.ToString());
        sb.Append("  ").AppendLine(d.Found);
        sb.Append("  Supported: ").AppendLine(d.Supported ?? d.Descriptor.Supported);
        sb.Append("  Fix: ").AppendLine(d.Fix ?? d.Descriptor.Fix);
        sb.Append("  Docs: ").Append(ProductInfo.Cli).Append(" explain ").AppendLine(d.Code);
        return sb.ToString();
    }

    public static string Explain(DiagnosticDescriptor d) =>
        $"{d.Code}  {d.Title}\n\n{d.Explanation}\n\nSupported: {d.Supported}\nFix: {d.Fix}\n";
}
