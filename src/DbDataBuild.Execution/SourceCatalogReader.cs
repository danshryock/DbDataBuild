using System.Data;
using System.Globalization;

namespace DbDataBuild.Execution;

/// <summary>What `import-sources` reads beyond the shapes <see cref="CatalogReader"/> gives: which schemas exist, and each table's primary key (it seeds a new descriptor's grain). Read session only.</summary>
public static class SourceCatalogReader
{
    // sys and INFORMATION_SCHEMA are the engine's; db_* are the schemas of fixed database roles; guest is empty. pg_* are PostgreSQL's catalogs and temp schemas.
    private const string SqlServerSchemas = "SELECT name FROM sys.schemas WHERE name NOT IN ('sys', 'INFORMATION_SCHEMA', 'guest') AND name NOT LIKE 'db[_]%' ORDER BY name";
    private const string PostgresSchemas = "SELECT schema_name FROM information_schema.schemata WHERE schema_name <> 'information_schema' AND schema_name NOT LIKE 'pg\\_%' ORDER BY schema_name";

    private const string SqlServerPrimaryKeys = @"
SELECT o.name, col.name, ic.key_ordinal
FROM sys.indexes i
JOIN sys.objects o ON o.object_id = i.object_id
JOIN sys.schemas s ON s.schema_id = o.schema_id
JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
JOIN sys.columns col ON col.object_id = ic.object_id AND col.column_id = ic.column_id
WHERE i.is_primary_key = 1 AND ic.is_included_column = 0 AND o.type = 'U' AND s.name = @schema
ORDER BY o.name, ic.key_ordinal";

    private const string PostgresPrimaryKeys = @"
SELECT t.relname, a.attname, k.ord
FROM pg_index i
JOIN pg_class t ON t.oid = i.indrelid
JOIN pg_namespace n ON n.oid = t.relnamespace
CROSS JOIN LATERAL unnest(i.indkey::int2[]) WITH ORDINALITY AS k(attnum, ord)
JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = k.attnum
WHERE i.indisprimary AND k.ord <= i.indnkeyatts AND n.nspname = @schema
ORDER BY t.relname, k.ord";

    public static async Task<IReadOnlyList<string>> SchemasAsync(ReadSession read, string target, CancellationToken ct = default)
    {
        var rows = await read.QueryAsync(target == "postgres" ? PostgresSchemas : SqlServerSchemas, null, ct);
        return rows.Select(r => (string)r[0]!).ToList();
    }

    /// <summary>Primary key columns, in key order, per table name of the schema.</summary>
    public static async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> PrimaryKeysAsync(ReadSession read, string target, string schema, CancellationToken ct = default)
    {
        var rows = await read.QueryAsync(target == "postgres" ? PostgresPrimaryKeys : SqlServerPrimaryKeys, [new GateParameter("schema", DbType.String, schema)], ct);
        return rows.GroupBy(r => (string)r[0]!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.OrderBy(r => Convert.ToInt32(r[2], CultureInfo.InvariantCulture)).Select(r => (string)r[1]!).ToList(), StringComparer.Ordinal);
    }
}
