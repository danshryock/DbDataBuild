using System.Text.Json;
using DuckDB.NET.Data;

namespace DbDataBuild.Targets.DuckDb;

/// <summary>
/// The tables a query reads, according to DuckDB's own parser (`json_serialize_sql`), which knows every syntax DuckDB does (PIVOT and UNPIVOT, table functions, nested subqueries) where the
/// offline SQL parser knows a subset. The query is parsed, never bound or run, and the engine is locked down first like every other use of it.
/// </summary>
public static class DuckParseTree
{
    public sealed record Table(string? Schema, string Name);

    public static (IReadOnlyList<Table>? Tables, string? Error) BaseTables(string sql)
    {
        try
        {
            using var connection = new DuckDBConnection("DataSource=:memory:");
            connection.Open();
            foreach (var setting in new[] { "SET autoinstall_known_extensions = false", "SET autoload_known_extensions = false", "SET enable_external_access = false" })
            {
                using var lockDown = connection.CreateCommand();
                lockDown.CommandText = setting;
                lockDown.ExecuteNonQuery();
            }
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT json_serialize_sql(CAST($sql AS VARCHAR))";
            var p = cmd.CreateParameter();
            p.ParameterName = "sql";
            p.Value = sql;
            cmd.Parameters.Add(p);
            var json = (string?)cmd.ExecuteScalar();
            if (json == null) return (null, "DuckDB returned no parse tree");
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.True)
                return (null, doc.RootElement.TryGetProperty("error_message", out var m) ? m.GetString() : "DuckDB could not parse the query");
            var ctes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var tables = new List<Table>();
            Walk(doc.RootElement, ctes, tables);
            return (tables.Where(t => t.Schema != null || !ctes.Contains(t.Name)).DistinctBy(t => (t.Schema ?? "").ToLowerInvariant() + "." + t.Name.ToLowerInvariant()).ToList(), null);
        }
        catch (DuckDBException ex) { return (null, ex.Message.Split('\n')[0]); }
    }

    private static void Walk(JsonElement e, HashSet<string> ctes, List<Table> tables)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                if (e.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "BASE_TABLE" && e.TryGetProperty("table_name", out var name) && name.ValueKind == JsonValueKind.String)
                {
                    var schema = e.TryGetProperty("schema_name", out var s) && s.ValueKind == JsonValueKind.String && s.GetString() is { Length: > 0 } sn ? sn : null;
                    tables.Add(new Table(schema, name.GetString()!));
                }
                if (e.TryGetProperty("cte_map", out var map) && map.ValueKind == JsonValueKind.Object && map.TryGetProperty("map", out var entries) && entries.ValueKind == JsonValueKind.Array)
                    foreach (var entry in entries.EnumerateArray())
                        if (entry.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.String) ctes.Add(key.GetString()!);
                foreach (var property in e.EnumerateObject()) Walk(property.Value, ctes, tables);
                break;
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray()) Walk(item, ctes, tables);
                break;
        }
    }
}
