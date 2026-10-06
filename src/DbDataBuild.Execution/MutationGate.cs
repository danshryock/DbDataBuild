using System.Data;
using System.Data.Common;
using System.Globalization;
using DbDataBuild.Core;

namespace DbDataBuild.Execution;

public sealed class GateRefusedException(Diagnostic diagnostic) : Exception(diagnostic.Found)
{
    public Diagnostic Diagnostic { get; } = diagnostic;
}

/// <summary>The write side of a connection. The one place a statement is handed to a driver (see the invariant test).</summary>
internal interface IWriteExecutor : IAsyncDisposable
{
    Task<long> ExecuteAsync(string text, IReadOnlyList<GateParameter> parameters, CancellationToken ct);

    /// <summary>Tries to take a session-level application lock without waiting. The lock lives as long as this connection (or until <see cref="UnlockAsync"/>).</summary>
    Task<bool> TryLockAsync(string resource, CancellationToken ct);
    Task UnlockAsync(string resource, CancellationToken ct);

    /// <summary>After a failed statement: leaves no transaction open (PostgreSQL refuses further statements in an aborted transaction).</summary>
    Task RecoverAsync(CancellationToken ct);
}

internal sealed class AdoWriteExecutor(DbConnection connection, bool postgres) : IWriteExecutor
{
    public async Task<bool> TryLockAsync(string resource, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = postgres
            ? "SELECT pg_try_advisory_lock(hashtextextended(@res, 0))"
            : "DECLARE @r int; EXEC @r = sp_getapplock @Resource = @res, @LockMode = N'Exclusive', @LockOwner = N'Session', @LockTimeout = 0; SELECT @r;";
        var p = cmd.CreateParameter();
        p.ParameterName = "@res";
        p.DbType = DbType.String;
        p.Value = resource;
        cmd.Parameters.Add(p);
        var result = await cmd.ExecuteScalarAsync(ct);
        return postgres ? Convert.ToBoolean(result, CultureInfo.InvariantCulture) : Convert.ToInt32(result, CultureInfo.InvariantCulture) >= 0;
    }

    public async Task UnlockAsync(string resource, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = postgres ? "SELECT pg_advisory_unlock(hashtextextended(@res, 0))" : "EXEC sp_releaseapplock @Resource = @res, @LockOwner = N'Session';";
        var p = cmd.CreateParameter();
        p.ParameterName = "@res";
        p.DbType = DbType.String;
        p.Value = resource;
        cmd.Parameters.Add(p);
        await cmd.ExecuteScalarAsync(ct);
    }

    public async Task RecoverAsync(CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = postgres ? "ROLLBACK;" : "IF @@TRANCOUNT > 0 ROLLBACK;";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> ExecuteAsync(string text, IReadOnlyList<GateParameter> parameters, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = text;
        foreach (var p in parameters)
        {
            var dp = cmd.CreateParameter();
            dp.ParameterName = p.Name.StartsWith('@') ? p.Name : "@" + p.Name;
            dp.DbType = p.Type;
            dp.Value = p.Value ?? DBNull.Value;
            cmd.Parameters.Add(dp);
        }
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public ValueTask DisposeAsync() => connection.DisposeAsync();
}

/// <summary>
/// The single route to a write-capable connection (DESIGN.md 9.3). It accepts only <see cref="GateStatement"/>s, checks the statement's effect class against
/// what the running command permits, records the statement in the statement log before executing it, and records the outcome after.
/// If the record cannot be written, the statement is not executed. In dry-run mode the same checks and the same log entries happen and nothing is executed.
/// </summary>
public sealed class MutationGate : IAsyncDisposable
{
    private readonly IWriteExecutor? executor;
    private readonly IStatementLog log;
    private readonly string command;
    private readonly StatementKind permitted;
    private int ordinal;

    public Guid RunId { get; }
    public bool DryRun { get; }

    private MutationGate(IWriteExecutor? executor, IStatementLog log, string command, StatementKind permitted, bool dryRun, Guid runId)
    {
        this.executor = executor; this.log = log; this.command = command; this.permitted = permitted; DryRun = dryRun; RunId = runId;
    }

    /// <summary>Opens the write login and wraps it. The returned gate is the only handle on the connection.</summary>
    public static async Task<MutationGate> OpenAsync(LoginSettings write, string command, StatementKind permitted, IStatementLog log, Guid runId, CancellationToken ct = default)
    {
        if (write.Login != Login.Write) throw new ArgumentException("The mutation gate needs the write login.", nameof(write));
        return new MutationGate(new AdoWriteExecutor(await write.OpenAsync(ct), write.Engine == "postgres"), log, command, permitted, dryRun: false, runId);
    }

    /// <summary>A gate that runs every check and writes every log entry but never touches a database.</summary>
    public static MutationGate DryRunGate(string command, StatementKind permitted, IStatementLog log, Guid runId) => new(null, log, command, permitted, dryRun: true, runId);

    internal static MutationGate ForTesting(IWriteExecutor executor, string command, StatementKind permitted, IStatementLog log, bool dryRun = false) =>
        new(executor, log, command, permitted, dryRun, Guid.NewGuid());

    /// <summary>Returns rows affected as the driver reports it (-1 for statements that do not report one, 0 in dry-run mode).</summary>
    public async Task<long> ExecuteAsync(GateStatement statement, CancellationToken ct = default)
    {
        if ((permitted & statement.Kind) == 0)
            throw new GateRefusedException(new Diagnostic(DiagnosticCatalog.GateRefused, new($"step:{statement.StepId}", 0, 0),
                $"`{command}` permits {Describe(permitted)} statements; step `{statement.StepId}` is a {statement.Kind.ToString().ToLowerInvariant()} statement. Nothing was executed."));

        var n = Interlocked.Increment(ref ordinal);
        Record(n, DryRun ? "dry-run" : "begin", statement, includeText: true, outcome: null);
        if (DryRun) return 0;

        try
        {
            var rows = await executor!.ExecuteAsync(statement.Text, statement.Parameters, ct);
            Record(n, "end", statement, includeText: false, outcome: $"ok rows={rows.ToString(CultureInfo.InvariantCulture)}");
            return rows;
        }
        catch (Exception ex) when (ex is not GateRefusedException)
        {
            // type and driver error number only: driver messages can quote data (DESIGN.md 14.2)
            var number = ex is DbException { ErrorCode: var code } && code != 0 ? $" code={code}" : "";
            TryRecord(n, "end", statement, $"failed {ex.GetType().Name}{number}");
            await RecoverAsync(statement.StepId, ct);
            throw;
        }
    }

    /// <summary>Best-effort rollback of whatever the failed statement left open, so the tracking writes that follow a failure can run. Logged like any other statement.</summary>
    private async Task RecoverAsync(string stepId, CancellationToken ct)
    {
        try
        {
            var n = Interlocked.Increment(ref ordinal);
            var rollback = GateStatement.Tracking(stepId + ":rollback", "ROLLBACK (leave no transaction open)");
            Record(n, "recover", rollback, includeText: true, outcome: null);
            await executor!.RecoverAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* the connection is unusable; the next statement will fail and say so */ }
    }

    /// <summary>
    /// Takes the application lock that makes `apply` mutually exclusive on one target (`sp_getapplock` on SQL Server, an advisory lock on PostgreSQL).
    /// Returns false when another apply holds it. The attempt and the outcome are in the statement log.
    /// </summary>
    public async Task<bool> TryAcquireApplicationLockAsync(string resource, CancellationToken ct = default)
    {
        var n = Interlocked.Increment(ref ordinal);
        var s = GateStatement.Tracking("lock", $"acquire application lock {resource}");
        Record(n, DryRun ? "dry-run" : "begin", s, includeText: true, outcome: null);
        if (DryRun) return true;
        var granted = await executor!.TryLockAsync(resource, ct);
        Record(n, "end", s, includeText: false, outcome: granted ? "granted" : "held by another process");
        return granted;
    }

    public async Task ReleaseApplicationLockAsync(string resource, CancellationToken ct = default)
    {
        if (DryRun) return;
        var n = Interlocked.Increment(ref ordinal);
        var s = GateStatement.Tracking("unlock", $"release application lock {resource}");
        Record(n, "begin", s, includeText: true, outcome: null);
        await executor!.UnlockAsync(resource, ct);
        Record(n, "end", s, includeText: false, outcome: "released");
    }

    private void Record(int n, string phase, GateStatement s, bool includeText, string? outcome)
    {
        try
        {
            log.Append(new StatementLogEntry(RunId, n, DateTime.UtcNow, command, phase, s.StepId, s.Kind.ToString(), s.Hash, includeText ? s.Text : null,
                includeText ? (s.BulkValues ? [("(bulk values)", $"{s.Parameters.Count.ToString(CultureInfo.InvariantCulture)} values, not logged")] : s.Parameters.Select(p => (p.Name, Convert.ToString(p.Value, CultureInfo.InvariantCulture) ?? "NULL")).ToList()) : null, outcome));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new GateRefusedException(new Diagnostic(DiagnosticCatalog.StatementLogUnavailable, new($"step:{s.StepId}", 0, 0),
                $"The statement log could not be written ({ex.GetType().Name}), so step `{s.StepId}` was not executed."));
        }
    }

    // after a failed statement the original error matters more than a failure to log it
    private void TryRecord(int n, string phase, GateStatement s, string outcome)
    {
        try { Record(n, phase, s, false, outcome); } catch (GateRefusedException) { }
    }

    private static string Describe(StatementKind k) => string.Join("/", Enum.GetValues<StatementKind>().Where(v => k.HasFlag(v)).Select(v => v.ToString().ToLowerInvariant()));

    public ValueTask DisposeAsync() => executor?.DisposeAsync() ?? ValueTask.CompletedTask;
}
