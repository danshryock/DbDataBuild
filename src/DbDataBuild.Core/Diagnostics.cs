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

    public static readonly DiagnosticDescriptor UpstreamNotFound = E("218", "Upstream table not found",
        "Every table a model queries is another model or a source descriptor, named schema.table.",
        "Add sources/<schema>/<table>.yml for it, or define the model that produces it.",
        "A model's declared schema is checked against an empty DuckDB schema built from its upstream tables' declared columns, so each one must be declared somewhere.");
    public static readonly DiagnosticDescriptor QueryNotDescribable = E("219", "DuckDB cannot describe the model query",
        "A query DuckDB can bind against the declared upstream columns.",
        "Fix the query, or the declared columns of the upstream tables it uses.",
        "`define` asks DuckDB to describe the query (it is never run) to learn its output columns and types.");
    public static readonly DiagnosticDescriptor OutputColumnUnusable = E("220", "Output column cannot be declared",
        "Every output column has a name (an alias for expressions) that no other column shares.",
        "Add an alias to the expression, or rename the duplicate.",
        "Declared columns are matched to the query's output by name, so each name must be present and unique.");

    public static readonly DiagnosticDescriptor ModelCycle = E("221", "Models depend on each other in a cycle",
        "A model graph without cycles.",
        "Break the cycle: one of the models must not query another in the cycle.",
        "Models are defined and built in dependency order, which needs an acyclic graph.");

    public static readonly DiagnosticDescriptor ResolverResultInvalid = E("222", "Resolver returned an unusable result",
        "A resolver that returns exactly one row and one column, of the parameter's type.",
        "Fix the committed resolver query in the model's `loads:` block, then run `render --write`.",
        "A resolver supplies a load parameter from the target (DESIGN.md 6.6). Any other shape of result stops that load: the tool never guesses a value.");

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
    public static readonly DiagnosticDescriptor PairUnsupported = E("317", "Load operation has no supported rendering",
        "Every declared model x target x operation pair renders: its strategy and every construct in the query are supported on the target.",
        "Change the strategy or the query, or remove the target from the model or from the operation's `targets:`.",
        "There are no silent gaps: a pair that cannot be rendered is reported by name, never skipped.");
    public static readonly DiagnosticDescriptor RenderedScriptInvalid = E("318", "Rendered script failed offline validation",
        "A rendered script that the target's parser accepts (ScriptDOM for T-SQL, the polyglot parser for PostgreSQL).",
        "Report this: the renderer produced SQL the target's parser rejects, which is a bug in the tool or in a construct the matrix should mark unsupported.",
        "Every rendered file is parsed offline as part of validation.");
    public static readonly DiagnosticDescriptor PlaceholderUndeclared = E("319", "Rendered script has an undeclared placeholder",
        "Only the operation's declared value parameters (@name) appear as placeholders.",
        "Remove the placeholder from the query. Identifiers can never be parameters.",
        "Parameters are bound through the driver and never interpolated, so the executed text is exactly the committed text. Any other placeholder is rejected.");
    public static readonly DiagnosticDescriptor KeyColumnNullable = W("320", "Key column is nullable",
        "Key columns declared `nullable: false`.",
        "Declare the key columns NOT NULL.",
        "A row whose key is NULL never matches in a key comparison, so it would be inserted again on every load.");
    public static readonly DiagnosticDescriptor ConstructNeedsVersion = W("308", "Construct needs a minimum target version",
        "Target versions at or above the matrix min_version.",
        "Confirm the target version, or avoid the construct.",
        "The construct is supported only from a certain engine version. Target versions are not configured yet, so this cannot be checked offline.");

    public static readonly DiagnosticDescriptor CollationCannotSatisfyProfile = E("310", "Collation cannot satisfy the string comparison profile",
        "For each engine in use, a collation whose case, accent and trailing-space behavior match string_semantics.",
        "Choose a collation with the required behavior, or change string_semantics.",
        "The configured collation is known not to behave as the project's profile requires, so string comparisons would silently differ from the canonical DuckDB run.");
    public static readonly DiagnosticDescriptor CollationNotVerifiable = W("311", "Collation behavior cannot be verified offline",
        "A collation name the checker understands, or behavior confirmed against the live catalog.",
        "Verify the collation's behavior manually or against the target, or use a collation the checker recognizes.",
        "The checker could not tell from the name whether the collation matches the profile. It is not assumed to.");
    public static readonly DiagnosticDescriptor CollationNotConfigured = E("312", "Collation not configured",
        "string_semantics.collations.default with an entry for every engine in use, and every `collation:` on a column defined there.",
        "Add the missing entry under string_semantics.collations.",
        "Generated DDL always states collations explicitly, so each engine in use needs a collation name for every logical collation a model uses.");

    public static readonly DiagnosticDescriptor TypeNotMappable = E("321", "Column type has no native mapping on a target",
        "A logical type from the mapping table (BIGINT, INTEGER, SMALLINT, TINYINT, DOUBLE, FLOAT, BOOLEAN, DATE, TIMESTAMP, TIME, TIMESTAMP WITH TIME ZONE, DECIMAL(p, s), VARCHAR(n), UUID, BLOB).",
        "Declare a supported type for the column (a cast in the query, then `define`), or remove the target from the model.",
        "DDL needs an exact native type for every declared column. Types without a faithful equivalent (unsigned integers, HUGEINT, structs, lists, a VARCHAR without a length) are refused rather than guessed.");

    // 4xx: planning / questions
    public static readonly DiagnosticDescriptor AnswerForUnknownQuestion = W("410", "Answer for a question that was not asked",
        "Answers whose id matches a question asked in this run.",
        "Remove the answer, or ignore this warning: answer files are reusable across runs and may carry extra answers.",
        "Question ids are deterministic, so an answers file can be reused. An answer with no matching question in this run is ignored.");
    public static readonly DiagnosticDescriptor AnswerChoiceInvalid = E("411", "Answer is not one of the question's options",
        "`choice:` equal to one of the options listed for the question.",
        "Use one of the listed option keys.",
        "An answer must pick an explicit option. Nothing is guessed or defaulted.");
    public static readonly DiagnosticDescriptor AnswerValueMismatch = E("412", "Answer value does not fit the chosen option",
        "`value:` present for options that take one, and absent for options that do not.",
        "Add or remove `value:` to match the option.",
        "Some options carry a value (for example the new column name for a rename). Others must not have one.");
    public static readonly DiagnosticDescriptor NoProposalToAccept = E("413", "Nothing to accept for this question",
        "`accept: inferred` only for questions that carry an inferred proposal.",
        "Answer with an explicit `choice:` instead.",
        "Only questions with an inferred proposal can be answered by accepting it.");
    public static readonly DiagnosticDescriptor QuestionUnanswered = E("414", "Question is unanswered",
        "An answer for every question, interactively or in the answers file.",
        "Add one of the listed answers to the answers file, or run interactively.",
        "In non-interactive mode every open question is listed at once with the YAML to add. No question is answered by default.");

    public static readonly DiagnosticDescriptor DefinitionOutOfSync = E("420", "Definition is out of sync with its query",
        "Declared columns that match the columns the query returns (name, type, nullability).",
        $"Run `{ProductInfo.Cli} define <model>` to update the definition.",
        "A model's declared columns are the output schema that drives DDL and hashing. Planning never proceeds from a stale declared schema.");
    public static readonly DiagnosticDescriptor RenderedFileOutOfDate = E("424", "Committed rendered file is out of date",
        "Files under rendered/ identical to a fresh render of the current models.",
        $"Run `{ProductInfo.Cli} render --write` and commit the result.",
        "The rendered load operations are committed and executed exactly as written, so they must match what the current models render to. Planning is blocked for a model whose files differ.");
    public static readonly DiagnosticDescriptor DefinitionFileChanged = E("421", "Definition file changed while define was running",
        "The definition file unchanged between being read and being written.",
        $"Run `{ProductInfo.Cli} define` again.",
        "`define` checks the file's hash before writing and refuses if anything else touched it, so concurrent edits are never overwritten.");
    public static readonly DiagnosticDescriptor DefinitionNotEditable = E("422", "Definition cannot be edited automatically",
        "A `columns:` list written in block style.",
        "Rewrite the part named in the message in block style, or edit the definition by hand.",
        "`define` edits definitions by minimal text splices located with the YAML parser's positions, which needs block style for the lists it changes.");

    public static readonly DiagnosticDescriptor ObjectChangedOutsideTool = E("430", "Object changed outside the tool",
        "A live shape equal to the last shape the tool recorded for the object.",
        $"Review the change, then run `{ProductInfo.Cli} ack drift <object>` to accept the live shape as the new baseline, or restore the object.",
        "The target's catalog hash for this object differs from the last recorded one (DESIGN.md 12.2). Planning for the model and everything downstream of it is blocked until a person decides.");
    public static readonly DiagnosticDescriptor LoadDefinitionChanged = E("431", "Incremental model changed since its last load",
        "An incremental model whose query is unchanged since the last load, or a change that a person has acknowledged.",
        $"Review the difference, then run `{ProductInfo.Cli} ack definition <model>`, or request a backfill.",
        "Rows already loaded were produced by the old query. Loading more rows with a changed query would mix two definitions in one table, so planning is blocked until a person decides (DESIGN.md 11).");
    public static readonly DiagnosticDescriptor AdoptionDeclined = E("432", "Existing object was not adopted",
        "An answer of `adopt` for objects that exist on the target but have no tool record.",
        "Answer the adoption question with `adopt`, or rename the model, or remove the existing object.",
        "The tool does not take over an object it did not create without an explicit answer. Choosing `stop` stops planning for the model.");
    public static readonly DiagnosticDescriptor UpstreamBlocked = W("433", "Model skipped because an upstream model is blocked",
        "Every upstream model planned or unchanged.",
        "Resolve the block on the upstream model named in the message.",
        "A model is not planned while something it reads from is blocked or skipped (DESIGN.md 11).");
    public static readonly DiagnosticDescriptor ModelUnplannable = E("434", "Model cannot be planned",
        "A model whose declared columns map to native types and whose query renders for the target.",
        "Fix the problem the message names.",
        "Planning for this model stopped; the message says why.");

    public static readonly DiagnosticDescriptor PlanFileInvalid = E("435", "Plan file is invalid or was edited",
        "A plan file exactly as `plan` wrote it: every key known, its content hash matching.",
        $"Generate a new plan with `{ProductInfo.Cli} plan`. A plan is never edited by hand.",
        "A plan records the exact statements `apply` will run, and a content hash over all of it (DESIGN.md 10.3). A hand-edited or damaged plan is refused rather than applied.");

    // 5xx: state and safety (logins, the mutation gate, tracking tables)
    public static readonly DiagnosticDescriptor LoginNotConfigured = E("501", "Login not configured",
        "A connection string in the environment variable named in the message, for the login this command needs.",
        "Set the variable in the runtime environment (never in the repo). Read-only commands use the read login, mutating commands the write login.",
        "Credentials come from the runtime environment only (DESIGN.md 9.2). There is no fallback from one login to the other: a read-only command never silently uses the write login.");
    public static readonly DiagnosticDescriptor GateRefused = E("502", "Statement refused by the mutation gate",
        "Only statements from a plan step or a tracking-table writer, of an effect class the running command permits.",
        "This is a bug in the tool if it was not caused by an edited plan. Report it with the command line used.",
        "Every write goes through one gate that checks the statement's origin and effect class against the command that is running (DESIGN.md 9.3). Nothing was executed.");
    public static readonly DiagnosticDescriptor StatementLogUnavailable = E("503", "Statement log could not be written",
        "A writable statement log directory.",
        "Make the log directory writable, or set it elsewhere in the project configuration.",
        "A statement is recorded before it runs. If the record cannot be written the statement is not executed.");
    public static readonly DiagnosticDescriptor ReadStatementRefused = E("504", "Read statement refused",
        "A single SELECT (or WITH ... SELECT) statement with no data-changing keywords.",
        "This is a bug in the tool. Report it with the command line used.",
        "Read-only commands run catalog and tracking-table queries through a guard that refuses anything that could change data. The read login's permissions are the real enforcement; the guard is a second layer. Nothing was executed.");
    public static readonly DiagnosticDescriptor TrackingNotInitialized = E("505", "Tracking tables are missing or have an unknown layout",
        $"The tracking schema created by `{ProductInfo.Cli} init`, at a layout version this tool knows.",
        $"Run `{ProductInfo.Cli} init` (review its script first), or use a newer tool if the layout is newer.",
        "Planning and applying need the tracking tables to compare hashes and to record what was done.");

    // 9xx: internal
    public static readonly DiagnosticDescriptor InternalError = new(ProductInfo.DiagnosticPrefix + "900",
        Severity.Error, "Internal error (tool bug)",
        "n/a", "Report this with the command line used.",
        "An unhandled exception occurred. This is a bug in the tool, not in your configuration.");

    public static readonly IReadOnlyList<DiagnosticDescriptor> All =
    [
        YamlSyntax, DuplicateKey, UnsupportedYamlFeature, UnknownKey, MissingKey, InvalidValue, NameMismatch, OrphanFile, ConfigNotFound,
        MissingUniqueKey, MissingTimeColumn, GrainMismatch, UnknownColumnReference, UpstreamNotFound, QueryNotDescribable, OutputColumnUnusable, ModelCycle, ResolverResultInvalid,
        ConstructUnsupported, ConstructApproximated, ConstructEmulated, ConstructUnverified, ConstructNotCovered,
        SqlParseFailure, NotASingleSelect, ConstructNeedsVersion, PairUnsupported, RenderedScriptInvalid, PlaceholderUndeclared, KeyColumnNullable, TypeNotMappable,
        CollationCannotSatisfyProfile, CollationNotVerifiable, CollationNotConfigured,
        AnswerForUnknownQuestion, AnswerChoiceInvalid, AnswerValueMismatch, NoProposalToAccept, QuestionUnanswered,
        DefinitionOutOfSync, RenderedFileOutOfDate, ObjectChangedOutsideTool, LoadDefinitionChanged, AdoptionDeclined, UpstreamBlocked, ModelUnplannable, PlanFileInvalid, DefinitionFileChanged, DefinitionNotEditable,
        LoginNotConfigured, GateRefused, StatementLogUnavailable, ReadStatementRefused, TrackingNotInitialized,
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
        sb.Append("  ").AppendLine(Indent(d.Found));
        sb.Append("  Supported: ").AppendLine(Indent(d.Supported ?? d.Descriptor.Supported));
        sb.Append("  Fix: ").AppendLine(Indent(d.Fix ?? d.Descriptor.Fix));
        sb.Append("  Docs: ").Append(ProductInfo.Cli).Append(" explain ").AppendLine(d.Code);
        return sb.ToString();
    }

    /// <summary>Continuation lines of multi-line fields are indented under the field (single-line text is unchanged).</summary>
    private static string Indent(string text) => text.Replace("\r\n", "\n").Replace("\n", "\n    ");

    public static string Explain(DiagnosticDescriptor d) =>
        $"{d.Code}  {d.Title}\n\n{d.Explanation}\n\nSupported: {d.Supported}\nFix: {d.Fix}\n";
}
