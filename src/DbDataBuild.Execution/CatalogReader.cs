using System.Data;
using System.Globalization;
using DbDataBuild.State;

namespace DbDataBuild.Execution;

/// <summary>Reads the live shape of the objects in a schema, through the read session (DESIGN.md 12.2: catalog hashing on every target).</summary>
public static class CatalogReader
{
    // Column order matters to nothing here except ordinals; the hashes sort. Types are lower case without parameters.
    private const string SqlServerColumns = @"
SELECT o.name, o.type, c.column_id, c.name, t.name, c.max_length, c.precision, c.scale, c.is_nullable, c.collation_name, cc.definition
FROM sys.objects o
JOIN sys.schemas s ON s.schema_id = o.schema_id
JOIN sys.columns c ON c.object_id = o.object_id
JOIN sys.types t ON t.user_type_id = c.user_type_id
LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
WHERE o.type IN ('U', 'V') AND s.name = @schema
ORDER BY o.name, c.column_id";

    private const string SqlServerIndexes = @"
SELECT o.name, i.name, i.type_desc, i.is_unique,
  (SELECT STRING_AGG(CONCAT(col.name, CASE WHEN ic.is_descending_key = 1 THEN ' desc' ELSE ' asc' END, CASE WHEN ic.is_included_column = 1 THEN ' include' ELSE '' END), ', ') WITHIN GROUP (ORDER BY ic.is_included_column, ic.key_ordinal, ic.index_column_id)
   FROM sys.index_columns ic JOIN sys.columns col ON col.object_id = ic.object_id AND col.column_id = ic.column_id WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id),
  (SELECT MIN(p.data_compression_desc) FROM sys.partitions p WHERE p.object_id = i.object_id AND p.index_id = i.index_id),
  (SELECT COUNT(*) FROM sys.partitions p WHERE p.object_id = i.object_id AND p.index_id = i.index_id)
FROM sys.indexes i
JOIN sys.objects o ON o.object_id = i.object_id
JOIN sys.schemas s ON s.schema_id = o.schema_id
WHERE o.type = 'U' AND i.type > 0 AND s.name = @schema
ORDER BY o.name, i.name";

    private const string PostgresColumns = @"
SELECT t.table_name, t.table_type, c.ordinal_position, c.column_name, c.data_type, c.character_maximum_length, c.numeric_precision, c.numeric_scale,
       c.datetime_precision, c.is_nullable, c.collation_name, c.is_generated, c.generation_expression
FROM information_schema.tables t
JOIN information_schema.columns c ON c.table_schema = t.table_schema AND c.table_name = t.table_name
WHERE t.table_schema = @schema AND t.table_type IN ('BASE TABLE', 'VIEW')
ORDER BY t.table_name, c.ordinal_position";

    private const string PostgresIndexes = "SELECT tablename, indexname, indexdef FROM pg_indexes WHERE schemaname = @schema ORDER BY tablename, indexname";

    public static async Task<IReadOnlyDictionary<string, ObjectShape>> ReadSchemaAsync(ReadSession read, string target, string schema, CancellationToken ct = default)
    {
        var p = new[] { new GateParameter("schema", DbType.String, schema) };
        var postgres = target == "postgres";
        var columnRows = await read.QueryAsync(postgres ? PostgresColumns : SqlServerColumns, p, ct);
        var indexRows = await read.QueryAsync(postgres ? PostgresIndexes : SqlServerIndexes, p, ct);

        var objects = new Dictionary<string, (ObjectKind Kind, List<ColumnShape> Columns, List<PhysicalItem> Physical)>(StringComparer.Ordinal);
        foreach (var r in columnRows)
        {
            var name = (string)r[0]!;
            if (!objects.TryGetValue(name, out var o)) objects[name] = o = (KindOf(postgres, r[1]!), [], []);
            o.Columns.Add(postgres ? PostgresColumn(r) : SqlServerColumn(r));
        }
        foreach (var r in indexRows)
        {
            var table = (string)r[0]!;
            if (!objects.TryGetValue(table, out var o)) continue;
            if (postgres) o.Physical.Add(new("index", (string)r[1]!, (string)r[2]!));
            else
            {
                o.Physical.Add(new("index", (string)r[1]!, $"{r[2]}{((Convert.ToInt32(r[3], CultureInfo.InvariantCulture) == 1) ? " unique" : "")} ({r[4]})"));
                o.Physical.Add(new("compression", $"{(string)r[1]!}", $"{r[5]} partitions={r[6]}"));
            }
        }
        return objects.ToDictionary(kv => $"{schema}.{kv.Key}", kv => new ObjectShape(schema, kv.Key, kv.Value.Kind, kv.Value.Columns, kv.Value.Physical));
    }

    private static ObjectKind KindOf(bool postgres, object kind) =>
        (postgres ? (string)kind == "VIEW" : ((string)kind).Trim() == "V") ? ObjectKind.View : ObjectKind.Table;

    private static int? Int(object? v) => v == null ? null : Convert.ToInt32(v, CultureInfo.InvariantCulture);

    private static ColumnShape SqlServerColumn(IReadOnlyList<object?> r)
    {
        var type = ((string)r[4]!).ToLowerInvariant();
        var bytes = Int(r[5])!.Value;
        int? length = type switch
        {
            "char" or "varchar" or "binary" or "varbinary" => bytes,
            "nchar" or "nvarchar" => bytes == -1 ? -1 : bytes / 2,
            _ => null,
        };
        int? precision = type is "decimal" or "numeric" ? Int(r[6]) : null;
        int? scale = type is "decimal" or "numeric" or "datetime2" or "time" or "datetimeoffset" ? Int(r[7]) : null;
        return new((string)r[3]!, type, length, precision, scale, Convert.ToBoolean(r[8], CultureInfo.InvariantCulture), r[9] as string, r[10] as string);
    }

    private static ColumnShape PostgresColumn(IReadOnlyList<object?> r)
    {
        var type = ((string)r[4]!).ToLowerInvariant();
        var temporal = type.StartsWith("timestamp") || type.StartsWith("time");
        int? precision = type == "numeric" ? Int(r[6]) : null;
        int? scale = type == "numeric" ? Int(r[7]) : temporal ? Int(r[8]) : null;
        var generated = (string?)r[11] == "ALWAYS" ? r[12] as string : null;
        return new((string)r[3]!, type, Int(r[5]), precision, scale, (string?)r[9] == "YES", r[10] as string, generated);
    }
}
