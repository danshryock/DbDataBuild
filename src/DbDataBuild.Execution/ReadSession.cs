using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;
using DbDataBuild.Core;

namespace DbDataBuild.Execution;

/// <summary>The read side: catalog and tracking-table queries on the read login. No method here can execute a data-changing statement.</summary>
public sealed class ReadSession : IAsyncDisposable
{
    private readonly DbConnection connection;
    private ReadSession(DbConnection connection, string engine) { this.connection = connection; Engine = engine; }

    /// <summary>The engine of the connection this session reads (sqlserver, fabric, postgres): what the catalog and tracking queries are written for.</summary>
    public string Engine { get; }

    public static async Task<ReadSession> OpenAsync(LoginSettings read, CancellationToken ct = default)
    {
        if (read.Login != Login.Read) throw new ArgumentException("A read session needs the read login.", nameof(read));
        return new ReadSession(await read.OpenAsync(ct), read.Engine);
    }

    internal static ReadSession ForTesting(DbConnection connection, string engine = "sqlserver") => new(connection, engine);

    public async Task<IReadOnlyList<IReadOnlyList<object?>>> QueryAsync(string sql, IReadOnlyList<GateParameter>? parameters = null, CancellationToken ct = default)
    {
        if (ReadGuard.Check(sql) is { } refused) throw new GateRefusedException(refused);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var p in parameters ?? [])
        {
            var dp = cmd.CreateParameter();
            dp.ParameterName = p.Name.StartsWith('@') ? p.Name : "@" + p.Name;
            dp.DbType = p.Type;
            dp.Value = p.Value ?? DBNull.Value;
            cmd.Parameters.Add(dp);
        }
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<IReadOnlyList<object?>>();
        while (await rd.ReadAsync(ct))
            rows.Add(Enumerable.Range(0, rd.FieldCount).Select(i => rd.IsDBNull(i) ? null : rd.GetValue(i)).ToList());
        return rows;
    }

    /// <summary>
    /// Opens a streaming read: one SELECT, the rows handed over as they arrive instead of being collected (a copy reads whole tables). Guarded like every read. The stream owns the command and the reader
    /// and must be disposed.
    /// </summary>
    public async Task<RowStream> OpenStreamAsync(string sql, IReadOnlyList<GateParameter>? parameters = null, CancellationToken ct = default)
    {
        if (ReadGuard.Check(sql) is { } refused) throw new GateRefusedException(refused);
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 0;
        foreach (var p in parameters ?? [])
        {
            var dp = cmd.CreateParameter();
            dp.ParameterName = p.Name.StartsWith('@') ? p.Name : "@" + p.Name;
            dp.DbType = p.Type;
            dp.Value = p.Value ?? DBNull.Value;
            cmd.Parameters.Add(dp);
        }
        try { return new RowStream(cmd, await cmd.ExecuteReaderAsync(System.Data.CommandBehavior.SequentialAccess, ct)); }
        catch { await cmd.DisposeAsync(); throw; }
    }

    public ValueTask DisposeAsync() => connection.DisposeAsync();
}

/// <summary>The rows of one streaming read. <see cref="Names"/> are the columns the query returned, in order.</summary>
public sealed class RowStream : IAsyncDisposable
{
    private readonly DbCommand command;
    private readonly DbDataReader reader;
    internal RowStream(DbCommand command, DbDataReader reader) { this.command = command; this.reader = reader; }

    public IReadOnlyList<string> Names => Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();

    /// <summary>Each row as an array of the driver's own values (null for SQL NULL). A value the driver cannot represent is a <see cref="TransferException"/> naming the column.</summary>
    public async IAsyncEnumerable<object?[]> ReadAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var width = reader.FieldCount;
        while (await reader.ReadAsync(ct))
        {
            var row = new object?[width];
            for (var i = 0; i < width; i++)
            {
                try { row[i] = await reader.IsDBNullAsync(i, ct) ? null : reader.GetValue(i); }
                catch (OverflowException) { throw new TransferException($"column `{reader.GetName(i)}`: a value does not fit what the driver can read (a decimal of more than 28 digits)."); }
            }
            yield return row;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await reader.DisposeAsync();
        await command.DisposeAsync();
    }
}
