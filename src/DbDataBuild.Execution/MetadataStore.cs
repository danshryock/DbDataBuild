using System.Data;
using DbDataBuild.State;

namespace DbDataBuild.Execution;

/// <summary>
/// Metadata documents in the target (`metadata_document`, DESIGN.md 12): JSON text with its hash, append-only. A document is written only when it differs from the latest one
/// for the same kind and subject, so republishing an unchanged project writes nothing.
/// </summary>
public static class MetadataStore
{
    private static string C(string target, string n) => TrackingDdl.For(target).Quote(n);

    /// <summary>The hash of the newest document per `kind|subject`.</summary>
    public static async Task<IReadOnlyDictionary<string, string>> LatestHashesAsync(ReadSession read, TrackingScope scope, CancellationToken ct = default)
    {
        string c(string n) => C(scope.Engine, n);
        var rows = await read.QueryAsync($"SELECT m.{c("kind")}, m.{c("subject")}, m.{c("document_hash")} FROM {c(scope.SchemaName)}.{c("metadata_current")} m WHERE m.{c("connection")} = @connection",
            [new GateParameter("connection", DbType.String, scope.Connection)], ct);
        return rows.ToDictionary(r => $"{((string)r[0]!).Trim()}|{r[1]}", r => ((string)r[2]!).Trim(), StringComparer.Ordinal);
    }

    /// <summary>The text of the newest document of a kind and subject, or null when there is none.</summary>
    public static async Task<string?> LatestDocumentAsync(ReadSession read, TrackingScope scope, string kind, string subject, CancellationToken ct = default)
    {
        string c(string n) => C(scope.Engine, n);
        var rows = await read.QueryAsync($"SELECT m.{c("document")} FROM {c(scope.SchemaName)}.{c("metadata_current")} m WHERE m.{c("connection")} = @connection AND m.{c("kind")} = @kind AND m.{c("subject")} = @subject",
            [new GateParameter("connection", DbType.String, scope.Connection), new GateParameter("kind", DbType.String, kind), new GateParameter("subject", DbType.String, subject)], ct);
        return rows.Count == 0 ? null : rows[0][0]?.ToString();
    }

    public static string Key(string kind, string subject) => $"{kind}|{subject}";

    /// <summary>Stores one document. The JSON is bound as a parameter (cast to jsonb on PostgreSQL), never interpolated.</summary>
    public static Task StoreAsync(MutationGate gate, TrackingScope scope, string stepId, string kind, string subject, string json, string hash, string toolVersion, string? planId, string? gitCommit, CancellationToken ct = default)
    {
        string c(string n) => C(scope.Engine, n);
        var document = scope.Engine == "postgres" ? "CAST(@document AS jsonb)" : "@document";
        var text = $"INSERT INTO {c(scope.SchemaName)}.{c("metadata_document")} ({c("connection")}, {c("kind")}, {c("subject")}, {c("document")}, {c("document_hash")}, {c("recorded_utc")}, {c("tool_version")}, {c("plan_id")}, {c("git_commit")}) " +
                   $"VALUES (@connection, @kind, @subject, {document}, @document_hash, @recorded_utc, @tool_version, @plan_id, @git_commit)";
        return gate.ExecuteAsync(GateStatement.Tracking(stepId, text,
        [
            new("connection", DbType.String, scope.Connection), new("kind", DbType.AnsiString, kind), new("subject", DbType.String, subject), new("document", DbType.String, json), new("document_hash", DbType.AnsiStringFixedLength, hash),
            new("recorded_utc", DbType.DateTime2, TrackingClock.NextUtc()), new("tool_version", DbType.AnsiString, toolVersion), new("plan_id", DbType.AnsiString, planId), new("git_commit", DbType.AnsiString, gitCommit),
        ]), ct);
    }
}
