using System.Data;
using DbDataBuild.State;

namespace DbDataBuild.Execution;

/// <summary>The effect class of one statement. A command permits some of these and the gate refuses the rest (DESIGN.md 9.3).</summary>
[Flags]
public enum StatementKind { Tracking = 1, Data = 2, Ddl = 4 }

/// <summary>A value bound through the driver. Statement text never contains values.</summary>
public sealed record GateParameter(string Name, DbType Type, object? Value);

/// <summary>
/// A statement the gate may execute. There is no public constructor: a statement comes from a plan step (<see cref="FromPlanStep"/>) or from a
/// tracking-table writer inside this assembly (<see cref="Tracking"/>), so arbitrary text cannot be executed by accident.
/// </summary>
public sealed class GateStatement
{
    public string StepId { get; }
    public StatementKind Kind { get; }
    public string Text { get; }
    public IReadOnlyList<GateParameter> Parameters { get; }
    public string Hash { get; }

    private GateStatement(string stepId, StatementKind kind, string text, IReadOnlyList<GateParameter> parameters)
    {
        StepId = stepId; Kind = kind; Text = text; Parameters = parameters; Hash = Hashing.ScriptHash(text);
    }

    /// <summary>A step of a plan. Plan steps are data loads or DDL; tracking writes never come from a plan.</summary>
    public static GateStatement FromPlanStep(string stepId, StatementKind kind, string text, IReadOnlyList<GateParameter>? parameters = null)
    {
        if (kind == StatementKind.Tracking || !Enum.IsDefined(kind)) throw new ArgumentException("A plan step is a data or DDL statement.", nameof(kind));
        return new(stepId, kind, text, parameters ?? []);
    }

    internal static GateStatement Tracking(string stepId, string text, IReadOnlyList<GateParameter>? parameters = null) =>
        new(stepId, StatementKind.Tracking, text, parameters ?? []);
}

/// <summary>What the statement log records about one execution. Error detail is a type name and a number, never driver text, which can contain data.</summary>
public sealed record StatementLogEntry(
    Guid RunId, int Ordinal, DateTime Utc, string Command, string Phase, string StepId, string Kind, string StatementHash, string? Text,
    IReadOnlyList<(string Name, string Value)>? Parameters, string? Outcome);

public interface IStatementLog
{
    /// <summary>Must be durable when it returns: the gate calls it before the statement runs, and does not run the statement if it throws.</summary>
    void Append(StatementLogEntry entry);
}
