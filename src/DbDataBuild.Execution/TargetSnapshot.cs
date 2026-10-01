using System.Data;
using System.Globalization;
using DbDataBuild.State;

namespace DbDataBuild.Execution;

/// <summary>Everything planning reads from a target: live shapes of the managed schemas and the tool's own records about them.</summary>
/// <param name="LastViewStatementHashes">Newest successful DDL statement hash per object (`ddl_log`); for a view this is the statement that created or altered it.</param>
/// <param name="LastLoadDefinitionHashes">Definition hash of the newest successful load per model (`run_log`).</param>
/// <param name="Acknowledged">`code|object|detail` of every block a person acknowledged (`block_log`).</param>
public sealed record TargetSnapshot(
    IReadOnlyDictionary<string, ObjectShape> Live,
    IReadOnlySet<string> Schemas,
    IReadOnlyDictionary<string, string> RecordedShapeHashes,
    IReadOnlyDictionary<string, string> LastViewStatementHashes,
    IReadOnlyDictionary<string, string> LastLoadDefinitionHashes,
    IReadOnlySet<string> Acknowledged);

/// <summary>The result of running a resolver: one value, or the reason it was not usable.</summary>
public sealed record ResolverValue(string? Value, string? Error);

public static class TargetSnapshotReader
{
    public static async Task<TargetSnapshot> ReadAsync(ReadSession read, string target, string trackingSchema, IEnumerable<string> managedSchemas, CancellationToken ct = default)
    {
        string C(string n) => TrackingDdl.For(target).Quote(n);
        var t = (string table) => $"{C(trackingSchema)}.{C(table)}";

        var schemaRows = await read.QueryAsync(target == "postgres" ? "SELECT schema_name FROM information_schema.schemata" : "SELECT name FROM sys.schemas", null, ct);
        var schemas = schemaRows.Select(r => (string)r[0]!).ToHashSet(StringComparer.Ordinal);

        var live = new Dictionary<string, ObjectShape>(StringComparer.Ordinal);
        foreach (var schema in managedSchemas.Distinct(StringComparer.Ordinal).Where(schemas.Contains))
            foreach (var (name, shape) in await CatalogReader.ReadSchemaAsync(read, target, schema, ct)) live[name] = shape;

        var recorded = await TrackingStore.LatestShapeHashesAsync(read, target, trackingSchema, ct);

        var ddl = await read.QueryAsync(
            $"SELECT d.{C("object_name")}, d.{C("statement_hash")} FROM {t("ddl_log")} d WHERE d.{C("status")} = 'ok' " +
            $"AND d.{C("executed_utc")} = (SELECT MAX(x.{C("executed_utc")}) FROM {t("ddl_log")} x WHERE x.{C("object_name")} = d.{C("object_name")} AND x.{C("status")} = 'ok')", null, ct);
        var views = ddl.ToDictionary(r => (string)r[0]!, r => ((string)r[1]!).Trim(), StringComparer.Ordinal);

        var runs = await read.QueryAsync(
            $"SELECT r.{C("model")}, r.{C("definition_hash")} FROM {t("run_log")} r WHERE r.{C("status")} = 'ok' AND r.{C("definition_hash")} IS NOT NULL " +
            $"AND r.{C("started_utc")} = (SELECT MAX(x.{C("started_utc")}) FROM {t("run_log")} x WHERE x.{C("model")} = r.{C("model")} AND x.{C("status")} = 'ok' AND x.{C("definition_hash")} IS NOT NULL)", null, ct);
        var loads = runs.ToDictionary(r => (string)r[0]!, r => ((string)r[1]!).Trim(), StringComparer.Ordinal);

        var acks = await read.QueryAsync($"SELECT b.{C("code")}, b.{C("model")}, b.{C("detail")} FROM {t("block_log")} b WHERE b.{C("ack_utc")} IS NOT NULL", null, ct);
        var acknowledged = acks.Select(r => $"{r[0]}|{r[1]}|{r[2]}").ToHashSet(StringComparer.Ordinal);

        return new TargetSnapshot(live, schemas, recorded, views, loads, acknowledged);
    }

    /// <summary>Runs a committed resolver on the read login. Exactly one row and one column is required; anything else is reported, never guessed at.</summary>
    public static async Task<ResolverValue> RunResolverAsync(ReadSession read, string resolverText, string parameterType, CancellationToken ct = default)
    {
        IReadOnlyList<IReadOnlyList<object?>> rows;
        try { rows = await read.QueryAsync(resolverText, null, ct); }
        catch (GateRefusedException) { throw; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(null, $"failed to run ({ex.GetType().Name})");   // type only: driver messages can quote data
        }
        if (rows.Count != 1) return new(null, $"returned {rows.Count} rows, not exactly one");
        if (rows[0].Count != 1) return new(null, $"returned {rows[0].Count} columns, not exactly one");
        var v = rows[0][0];
        return v switch
        {
            null => new(null, null),
            DateTime dt when parameterType.Trim().Equals("DATE", StringComparison.OrdinalIgnoreCase) => new(dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), null),
            DateTime dt => new(dt.ToString("yyyy-MM-dd HH:mm:ss.FFFFFF", CultureInfo.InvariantCulture), null),
            DateOnly d => new(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), null),
            DateTimeOffset dto => new(dto.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.FFFFFF", CultureInfo.InvariantCulture), null),
            sbyte or byte or short or ushort or int or uint or long or ulong => new(Convert.ToInt64(v, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture), null),
            _ => new(Convert.ToString(v, CultureInfo.InvariantCulture), null),
        };
    }
}
