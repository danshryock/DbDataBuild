using DbDataBuild.Core.Questions;
using DbDataBuild.State;

namespace DbDataBuild.Planning;

/// <summary>`track` is a tracking-table write (recording an adopted or acknowledged shape); the other types are those of DESIGN.md 10.2.</summary>
public enum StepType { Ddl, Load, Backfill, Hook, Track }

public enum RiskClass { Safe, Risky, Destructive }

/// <param name="Source">runtime (a person supplied it) or resolver (read from the target).</param>
/// <param name="Value">Invariant-culture text of the value in the parameter's logical type, or null for SQL NULL.</param>
public sealed record PlanParameter(string Name, string Type, string Source, string? Value);

/// <summary>One thing apply will do. <see cref="Text"/> is the full statement or script, executed exactly as written.</summary>
/// <param name="Reasons">The reason chain: a decision-table row, a model change, an answer.</param>
/// <param name="HashAfter">For DDL on tables: the shape hash the object must have afterwards. Null when it cannot be predicted (views).</param>
/// <param name="ResolverText">The committed resolver of a load, so apply can re-run it and refuse a stale value.</param>
/// <param name="ResolverResult">What the resolver returned at plan time (null for SQL NULL). Compared again at apply.</param>
/// <param name="FileHash">Hash of the committed rendered file a load step runs.</param>
/// <param name="DefinitionHash">For a load: the hash of the model query it was planned from, recorded in `run_log` so a later plan can see the query changed.</param>
public sealed record PlanStep(
    string Id, StepType Type, string Object, string Description, string Text, RiskClass Risk, IReadOnlyList<string> Reasons,
    string? HashAfter, IReadOnlyList<PlanParameter> Parameters, string? ResolverText = null, string? ResolverResult = null, bool HasResolver = false,
    string? FileHash = null, string? Operation = null, string? ShapeSource = null, string? DefinitionHash = null);

/// <summary>What the plan assumed about an object, verified against the live target at apply (a mismatch is a stale plan).</summary>
public sealed record ObjectBase(string Object, ObjectState State, string? LiveShapeHash, string? RecordedShapeHash);

public sealed record Plan(
    string Id, string Target, string? GitCommit, bool GitDirty, string ToolVersion,
    IReadOnlyList<ObjectBase> Bases, IReadOnlyList<ResolvedAnswer> Answers, IReadOnlyList<PlanStep> Steps, IReadOnlyList<string> Noticed);
