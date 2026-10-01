using System.Text.RegularExpressions;
using DuckDB.NET.Data;

namespace DbDataBuild.Targets.DuckDb;

public sealed record DuckColumn(string Name, string Type, bool Nullable);

public sealed record DuckTable(string Schema, string Name, IReadOnlyList<DuckColumn> Columns);

public sealed record DescribedColumn(string Name, string DuckDbType);

/// <summary>Either the output columns of a query, or why DuckDB could not describe it.</summary>
public sealed record DescribeResult(IReadOnlyList<DescribedColumn>? Columns, string? Error)
{
    public bool Ok => Columns != null;
}

/// <summary>
/// Asks DuckDB what a model query returns, offline (DESIGN.md 6.5): an empty in-memory schema is built from the upstream declared
/// columns, then the query is described, never run. External access is switched off first, so a query cannot read files or the network
/// while it is being bound. This is the offline engine, not a connection to any target.
/// </summary>
public static partial class QueryDescriber
{
    // Declared types come from repo files. Only plain type names are passed to DuckDB: DECIMAL(14, 2), VARCHAR(20), BIGINT[], TIMESTAMP WITH TIME ZONE.
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*( [A-Za-z][A-Za-z0-9_]*)*(\(\s*[0-9]+\s*(,\s*[0-9]+\s*)?\))?(\[[0-9]*\])*$")]
    private static partial Regex TypePattern();

    public static bool IsPlainType(string type) => TypePattern().IsMatch(type.Trim());

    public static DescribeResult Describe(IReadOnlyList<DuckTable> upstream, string sql)
    {
        foreach (var column in upstream.SelectMany(t => t.Columns).Where(c => !IsPlainType(c.Type)))
            return new(null, $"The declared type `{column.Type}` of column `{column.Name}` is not a plain SQL type name.");

        using var connection = new DuckDBConnection("DataSource=:memory:");
        connection.Open();
        try
        {
            // Lock the engine down before anything repo-controlled reaches it. These settings cannot be changed back.
            Run(connection, "SET autoinstall_known_extensions = false");
            Run(connection, "SET autoload_known_extensions = false");
            Run(connection, "SET enable_external_access = false");

            foreach (var table in upstream)
            {
                Run(connection, $"CREATE SCHEMA IF NOT EXISTS {Quote(table.Schema)}");
                var columns = string.Join(", ", table.Columns.Select(c => $"{Quote(c.Name)} {c.Type.Trim()}{(c.Nullable ? "" : " NOT NULL")}"));
                Run(connection, $"CREATE TABLE {Quote(table.Schema)}.{Quote(table.Name)} ({columns})");
            }

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DESCRIBE " + sql.Trim().TrimEnd(';').TrimEnd();
            using var reader = cmd.ExecuteReader();
            var result = new List<DescribedColumn>();
            while (reader.Read()) result.Add(new DescribedColumn(reader.GetString(0), reader.GetString(1)));
            return new(result, null);
        }
        catch (DuckDBException ex)
        {
            return new(null, FirstLine(ex.Message));
        }
    }

    private static void Run(DuckDBConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    private static string FirstLine(string message)
    {
        var line = message.Split('\n', 2)[0].Trim();
        return line.Length > 300 ? line[..300] : line;
    }
}
