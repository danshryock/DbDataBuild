using System.Data;
using DbDataBuild.Core;
using DbDataBuild.State;

namespace DbDataBuild.Execution;

public enum TrackingState { Missing, Ready, UnknownLayout }

public sealed record TrackingStatus(TrackingState State, int? Version)
{
    /// <summary>The DDB-505 diagnostic for a state that is not ready, or null.</summary>
    public Diagnostic? AsDiagnostic(string schema) => State switch
    {
        TrackingState.Ready => null,
        TrackingState.Missing => new Diagnostic(DiagnosticCatalog.TrackingNotInitialized, new($"schema:{schema}", 0, 0), $"The tracking tables do not exist in schema `{schema}` on the target."),
        _ => new Diagnostic(DiagnosticCatalog.TrackingNotInitialized, new($"schema:{schema}", 0, 0),
            $"The tracking tables in schema `{schema}` are at layout version {(Version?.ToString() ?? "unreadable")}; this tool knows version {TrackingSchema.Version}."),
    };
}

/// <summary>
/// Strictly increasing UTC timestamps at the tracking tables' millisecond precision, so two records for one object written in the same millisecond
/// still order (the newest `schema_version` row is found by timestamp).
/// </summary>
internal static class TrackingClock
{
    private static long last;
    /// <summary>The value is UTC wall-clock time with Kind Unspecified: the columns are zone-less (`datetime2`, `timestamp`) and Npgsql refuses UTC-kind values for them.</summary>
    public static DateTime NextUtc()
    {
        var now = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        long observed, next;
        do { observed = Interlocked.Read(ref last); next = Math.Max(now, observed + 1); } while (Interlocked.CompareExchange(ref last, next, observed) != observed);
        return new DateTime(next * TimeSpan.TicksPerMillisecond, DateTimeKind.Unspecified);
    }
}

/// <summary>Reads and writes the tracking tables. Reads use the read session, writes go through the gate as tracking statements.</summary>
public static class TrackingStore
{
    private static string Quote(string target, string id) => TrackingDdl.For(target).Quote(id);

    /// <summary>Runs the init script through the gate. Idempotent: a second run changes nothing.</summary>
    public static async Task InitAsync(MutationGate gate, string target, string schema, CancellationToken ct = default)
    {
        foreach (var s in TrackingDdl.For(target).InitScript(schema, ProductInfo.Version))
            await gate.ExecuteAsync(GateStatement.Tracking(s.Id, s.Text), ct);
    }

    public static async Task<TrackingStatus> StatusAsync(ReadSession read, string target, string schema, CancellationToken ct = default)
    {
        var exists = target == "postgres"
            ? "SELECT 1 FROM information_schema.tables WHERE table_schema = @schema AND table_name = 'tracking_version'"
            : "SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = @schema AND t.name = 'tracking_version'";
        var p = new[] { new GateParameter("schema", DbType.String, schema) };
        if ((await read.QueryAsync(exists, p, ct)).Count == 0) return new(TrackingState.Missing, null);

        var q = Quote(target, schema);
        var rows = await read.QueryAsync($"SELECT {Quote(target, "version")} FROM {q}.{Quote(target, "tracking_version")}", null, ct);
        var versions = rows.Select(r => Convert.ToInt32(r[0])).ToList();
        if (versions.Count == 0) return new(TrackingState.UnknownLayout, null);
        var newest = versions.Max();
        return new(newest == TrackingSchema.Version ? TrackingState.Ready : TrackingState.UnknownLayout, newest);
    }

    /// <summary>The shape hash of the newest `schema_version` row for each object, or no entry when the tool has never recorded it.</summary>
    public static async Task<IReadOnlyDictionary<string, string>> LatestShapeHashesAsync(ReadSession read, string target, string schema, CancellationToken ct = default)
    {
        string C(string n) => Quote(target, n);
        var rows = await read.QueryAsync(
            $"SELECT sv.{C("object_name")}, sv.{C("shape_hash")} FROM {C(schema)}.{C("schema_version")} sv " +
            $"WHERE sv.{C("first_seen_utc")} = (SELECT MAX(x.{C("first_seen_utc")}) FROM {C(schema)}.{C("schema_version")} x WHERE x.{C("object_name")} = sv.{C("object_name")})", null, ct);
        return rows.ToDictionary(r => (string)r[0]!, r => ((string)r[1]!).Trim());
    }

    /// <summary>Records a shape as the newest known state of an object. `source` is `tool` or `out_of_band` (DESIGN.md 12).</summary>
    public static Task RecordSchemaVersionAsync(MutationGate gate, string target, string schema, string stepId, string objectName, string shapeHash, string? physicalHash,
        string source, string? planId, string? gitCommit, CancellationToken ct = default)
    {
        string C(string n) => Quote(target, n);
        var text = $"INSERT INTO {C(schema)}.{C("schema_version")} ({C("object_name")}, {C("shape_hash")}, {C("physical_hash")}, {C("first_seen_utc")}, {C("source")}, {C("plan_id")}, {C("git_commit")}) " +
                   "VALUES (@object_name, @shape_hash, @physical_hash, @first_seen_utc, @source, @plan_id, @git_commit)";
        return gate.ExecuteAsync(GateStatement.Tracking(stepId, text,
        [
            new("object_name", DbType.String, objectName), new("shape_hash", DbType.AnsiStringFixedLength, shapeHash), new("physical_hash", DbType.AnsiStringFixedLength, physicalHash),
            new("first_seen_utc", DbType.DateTime2, TrackingClock.NextUtc()), new("source", DbType.AnsiString, source), new("plan_id", DbType.AnsiString, planId), new("git_commit", DbType.AnsiString, gitCommit),
        ]), ct);
    }
}
