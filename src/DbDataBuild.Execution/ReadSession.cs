using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;
using DbDataBuild.Core;

namespace DbDataBuild.Execution;

/// <summary>
/// A second layer in front of the read login: refuses anything that is not a single SELECT or WITH ... SELECT, and refuses data-changing keywords
/// anywhere outside quotes and comments. It can refuse a harmless query (a column literally named `update`, unquoted); the tool's own catalog queries
/// avoid that. The read login's permissions are the real enforcement.
/// </summary>
public static partial class ReadGuard
{
    private static readonly Regex Forbidden = ForbiddenPattern();

    [GeneratedRegex(@"\$[A-Za-z_0-9]*\$")]
    private static partial Regex DollarQuote();

    [GeneratedRegex(@"\b(insert|update|delete|merge|drop|alter|create|truncate|exec|execute|grant|revoke|into|call|copy|vacuum|set)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ForbiddenPattern();

    public static Diagnostic? Check(string sql)
    {
        // dollar-quoting would let a string hide a quote character from the scanner, and the tool's own queries never use it
        if (DollarQuote().IsMatch(sql)) return new Diagnostic(DiagnosticCatalog.ReadStatementRefused, new("read-statement", 0, 0), "A read-only statement was refused: it uses dollar-quoting. Nothing was executed.");
        var bare = Strip(sql).Trim().TrimEnd(';').Trim();
        string? reason = null;
        if (bare.Length == 0) reason = "the statement is empty";
        else if (!Regex.IsMatch(bare, @"^(select|with)\b", RegexOptions.IgnoreCase)) reason = "it does not start with SELECT or WITH";
        else if (bare.Contains(';')) reason = "it contains more than one statement";
        else if (Forbidden.Match(bare) is { Success: true } m) reason = $"it contains the keyword `{m.Value.ToUpperInvariant()}`";
        return reason == null ? null : new Diagnostic(DiagnosticCatalog.ReadStatementRefused, new("read-statement", 0, 0), $"A read-only statement was refused: {reason}. Nothing was executed.");
    }

    /// <summary>Removes comments and the contents of string literals and quoted identifiers, so keywords inside them do not count.</summary>
    internal static string Strip(string sql)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i];
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-') { while (i < sql.Length && sql[i] != '\n') i++; sb.Append(' '); }
            else if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*') { var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal); i = end < 0 ? sql.Length : end + 1; sb.Append(' '); }
            else if (c is '\'' or '"' or '[')
            {
                var close = c == '[' ? ']' : c;
                i++;
                while (i < sql.Length) { if (sql[i] == close) { if (i + 1 < sql.Length && sql[i + 1] == close) { i += 2; continue; } break; } i++; }
                sb.Append(" _ ");
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }
}

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
