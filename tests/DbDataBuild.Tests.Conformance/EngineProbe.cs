using System.Globalization;
using DbDataBuild.Lowering;
using DbDataBuild.Sql;
using DbDataBuild.Targets;
using DbDataBuild.Targets.DuckDb;
using DbDataBuild.Targets.Rules;
using DuckDB.NET.Data;

namespace DbDataBuild.Tests.Conformance;

/// <summary>
/// Runs a DuckDB-dialect query three ways over the same seeded rows: on DuckDB as written (the answer), and on an engine as the tool would send it (lowered from DuckDB's plan, rewritten by the target
/// rules, transpiled). The seed is built to hit the edges: negatives, zero, NULL, a half, non-BMP and accented text, empty and padded strings, the largest INT.
/// </summary>
public sealed class EngineProbe : IDisposable
{
    public sealed record Case(string Id, string Sql, string? Note = null);
    public sealed record Outcome(string Rows, string? Error);

    private readonly DuckDBConnection duck = new("DataSource=:memory:");
    private readonly Engine engine;

    // id, i, j, d, n, s, dt, ts
    private static readonly (int Id, string I, string J, string D, string N, string? S, string? Dt, string? Ts)[] Seed =
    [
        (1, "7", "2", "2.5", "2.50", "abc", "2024-01-15", "2024-01-15 10:30:45"),
        (2, "-7", "2", "-2.5", "-2.50", "ABC", "2024-02-29", "2024-02-29 23:59:59"),
        (3, "7", "-2", "3.5", "3.50", " abc ", "2024-03-31", "2024-03-31 00:00:00"),
        (4, "0", "0", "0.5", "0.00", "", "2023-12-31", "2023-12-31 12:00:00"),
        (5, "NULL", "3", "NULL", "NULL", null, null, null),
        (6, "5", "0", "1.005", "1.25", "😀a", "2024-06-30", "2024-06-30 08:15:30"),
        (7, "2147483647", "1", "-0.5", "-0.50", "héllo", "2024-01-31", "2024-01-31 18:45:00"),
        (8, "10", "3", "10000000000", "12345.67", "a,b,c", "2024-02-01", "2024-02-01 00:00:01"),
    ];

    public EngineProbe(Engine engine)
    {
        this.engine = engine;
        duck.Open();
        Duck("CREATE TABLE probe (id INTEGER NOT NULL, i INTEGER, j INTEGER, d DOUBLE, n DECIMAL(10, 2), s VARCHAR, dt DATE, ts TIMESTAMP)");
        Duck(Insert(sqlServer: false));
        QueryDescriber.PreparePlanConnection(duck);
    }

    public async Task CreateTableAsync()
    {
        var sqlServer = engine.Name == "sqlserver";
        await engine.ExecAsync($"CREATE TABLE probe (id INT NOT NULL, i INT, j INT, d {(sqlServer ? "FLOAT" : "DOUBLE PRECISION")}, n DECIMAL(10, 2), s {(sqlServer ? "NVARCHAR(50) COLLATE Latin1_General_100_BIN2" : "VARCHAR(50) COLLATE \"C\"")}, dt DATE, ts {(sqlServer ? "DATETIME2(6)" : "TIMESTAMP(6)")})");
        await engine.ExecAsync(Insert(sqlServer));
    }

    private static string Insert(bool sqlServer)
    {
        string Str(string? v) => v == null ? "NULL" : (sqlServer ? "N'" : "'") + v.Replace("'", "''") + "'";
        string Dt(string? v) => v == null ? "NULL" : $"CAST('{v}' AS DATE)";
        string Ts(string? v) => v == null ? "NULL" : $"CAST('{v}' AS {(sqlServer ? "DATETIME2(6)" : "TIMESTAMP")})";
        return "INSERT INTO probe VALUES " + string.Join(", ", Seed.Select(r => $"({r.Id}, {r.I}, {r.J}, {r.D}, {r.N}, {Str(r.S)}, {Dt(r.Dt)}, {Ts(r.Ts)})"));
    }

    private void Duck(string sql) { using var cmd = duck.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }

    /// <summary>DuckDB's answer, as the sorted rows of the first output column(s).</summary>
    public Outcome OnDuckDb(string sql)
    {
        try
        {
            using var cmd = duck.CreateCommand();
            cmd.CommandText = sql;
            using var r = cmd.ExecuteReader();
            return new(Rows(() => r.Read(), r.FieldCount, i => r.IsDBNull(i) ? null : r.GetValue(i), i => r.GetDataTypeName(i)), null);
        }
        catch (DuckDBException ex) { return new(string.Empty, FirstLine(ex.Message)); }
    }

    /// <summary>What the tool would send the engine for this query, or the lowering's refusal.</summary>
    public (string? Sql, string? Refused) Render(string sql, DbDataBuild.Core.RewritePolicy? rewrites = null)
    {
        try
        {
            string plan;
            using (var cmd = duck.CreateCommand()) { cmd.CommandText = QueryDescriber.PlanStatement(sql); plan = (string)cmd.ExecuteScalar()!; }
            var names = new List<string>();
            using (var cmd = duck.CreateCommand()) { cmd.CommandText = "DESCRIBE " + sql; using var r = cmd.ExecuteReader(); while (r.Read()) names.Add(r.GetString(0)); }
            var lowered = PlanLowerer.Lower(plan, names, null, rewrites).Sql;
            var ruled = TargetRules.Apply(lowered, engine.Name, rewrites).Sql;
            var (outcome, text) = Polyglot.TranspileOne(ruled, Dialects.Canonical, TargetRegistry.Get(engine.Name).Dialect);
            return outcome.Ok && text != null ? (text, null) : (null, "transpile: " + (outcome.Error ?? "failed"));
        }
        catch (LoweringException ex) { return (null, ex.Message); }
        catch (DuckDBException ex) { return (null, "duckdb: " + FirstLine(ex.Message)); }
        catch (Exception ex) { return (null, "CRASH " + ex.GetType().Name + ": " + FirstLine(ex.Message)); }   // a lowering that throws anything but a refusal is a tool bug
    }

    public async Task<Outcome> OnEngineAsync(string rendered)
    {
        try
        {
            using var cmd = engine.Conn.CreateCommand();
            cmd.CommandText = rendered;
            using var r = await cmd.ExecuteReaderAsync();
            return new(Rows(() => r.Read(), r.FieldCount, i => r.IsDBNull(i) ? null : r.GetValue(i), i => r.GetDataTypeName(i)), null);
        }
        catch (Exception ex) when (ex is System.Data.Common.DbException) { return new(string.Empty, ex.GetType().Name + ": " + FirstLine(ex.Message)); }
    }

    private static string Rows(Func<bool> read, int fields, Func<int, object?> value, Func<int, string> typeName)
    {
        var rows = new List<string>();
        while (read()) rows.Add(string.Join("|", Enumerable.Range(0, fields).Select(i => Cell(value(i), typeName(i)))));
        rows.Sort(StringComparer.Ordinal);
        return string.Join("; ", rows);
    }

    /// <summary>Numbers are compared as numbers (2.5 and 2.50 are the same answer); text is compared as text.</summary>
    private static string Cell(object? v, string typeName) => v switch
    {
        null => "∅",
        bool b => b ? "true" : "false",
        double d when double.IsNaN(d) || double.IsInfinity(d) => d.ToString(CultureInfo.InvariantCulture),
        double d => Number(Math.Round(d, 9)),
        float f => Number((decimal)Math.Round(f, 6)),
        decimal m => Number(Math.Round(m, 9)),
        DateTime dt when typeName.Equals("date", StringComparison.OrdinalIgnoreCase) => dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        sbyte or byte or short or ushort or int or uint or long or ulong => Convert.ToString(v, CultureInfo.InvariantCulture)!,
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        System.Collections.IEnumerable and not string => "[list]",
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };

    private static string Number(double d) => d == 0 ? "0" : d.ToString("0.#########", CultureInfo.InvariantCulture);     // -0 and 0 are the same number
    private static string Number(decimal d) => d == 0 ? "0" : d.ToString("0.#########", CultureInfo.InvariantCulture);

    private static string FirstLine(string message) => message.Split('\n')[0].Trim();

    public void Dispose() => duck.Dispose();
}
