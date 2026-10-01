using System.Text;

namespace DbDataBuild.State;

/// <summary>One statement of the init script. The id is stable, so the statement log and a reviewer can refer to it.</summary>
public sealed record InitStatement(string Id, string Description, string Text);

/// <summary>Creates the tracking schema and tables for one engine from the logical definition. Scripts are idempotent: running one twice changes nothing.</summary>
public interface ITrackingDdl
{
    string Target { get; }
    /// <summary>True when the output has not been run on that engine (no Fabric engine has been available).</summary>
    bool Unverified { get; }
    string Quote(string identifier);
    IReadOnlyList<InitStatement> InitScript(string schema, string toolVersion);
}

public static class TrackingDdl
{
    public static ITrackingDdl For(string target) => target switch
    {
        "sqlserver" => new TSqlTrackingDdl("sqlserver", unverified: false),
        "fabric" => new TSqlTrackingDdl("fabric", unverified: true),
        "postgres" => new PostgresTrackingDdl(),
        _ => throw new ArgumentException($"Unknown target `{target}`.", nameof(target)),
    };

    /// <summary>The reviewable text of an init script: each statement under a comment naming its id.</summary>
    public static string Render(IReadOnlyList<InitStatement> script)
    {
        var sb = new StringBuilder();
        foreach (var s in script) sb.Append("-- ").Append(s.Id).Append(": ").AppendLine(s.Description).AppendLine(s.Text.TrimEnd()).AppendLine();
        return sb.ToString();
    }

    internal static string Literal(string s) => "'" + s.Replace("'", "''") + "'";
}

internal sealed class TSqlTrackingDdl(string target, bool unverified) : ITrackingDdl
{
    public string Target => target;
    public bool Unverified => unverified;
    public string Quote(string id) => "[" + id.Replace("]", "]]") + "]";

    // Fabric has no NVARCHAR, and its keys must be declared NONCLUSTERED ... NOT ENFORCED (Microsoft's T-SQL surface area page, 2026-09-30).
    private string Native(TrackingType t) => t switch
    {
        TrackingType.Name => target == "fabric" ? "varchar(512)" : "nvarchar(512)",
        TrackingType.Short => target == "fabric" ? "varchar(128)" : "varchar(128)",
        TrackingType.Hash => "char(64)",
        TrackingType.Long => target == "fabric" ? "varchar(max)" : "nvarchar(max)",
        TrackingType.BigInt => "bigint",
        TrackingType.Int => "int",
        TrackingType.Guid => "uniqueidentifier",
        TrackingType.TimestampUtc => target == "fabric" ? "datetime2(6)" : "datetime2(3)",
        _ => throw new ArgumentOutOfRangeException(nameof(t)),
    };

    public IReadOnlyList<InitStatement> InitScript(string schema, string toolVersion)
    {
        var s = Quote(schema);
        var list = new List<InitStatement>
        {
            new("init-00", $"schema {schema}", $"IF SCHEMA_ID({TrackingDdl.Literal(schema)}) IS NULL EXEC({TrackingDdl.Literal("CREATE SCHEMA " + s)});"),
        };
        var n = 1;
        foreach (var t in TrackingSchema.Tables)
        {
            var cols = string.Join(",\n", t.Columns.Select(c => $"  {Quote(c.Name)} {Native(c.Type)} {(c.Nullable ? "NULL" : "NOT NULL")}"));
            var key = string.Join(", ", t.PrimaryKey.Select(Quote));
            var pk = target == "fabric" ? $"PRIMARY KEY NONCLUSTERED ({key}) NOT ENFORCED" : $"PRIMARY KEY ({key})";
            var obj = $"{s}.{Quote(t.Name)}";
            list.Add(new($"init-{n++:00}", $"table {t.Name}: {t.Purpose}",
                $"IF OBJECT_ID({TrackingDdl.Literal(schema + "." + t.Name)}, N'U') IS NULL\nCREATE TABLE {obj} (\n{cols},\n  CONSTRAINT {Quote("pk_" + t.Name)} {pk}\n);"));
        }
        var v = $"{s}.{Quote("tracking_version")}";
        list.Add(new($"init-{n}", "record the layout version",
            $"IF NOT EXISTS (SELECT 1 FROM {v} WHERE [version] = {TrackingSchema.Version})\nINSERT INTO {v} ([version], [tool_version], [applied_utc]) VALUES ({TrackingSchema.Version}, {TrackingDdl.Literal(toolVersion)}, SYSUTCDATETIME());"));
        return list;
    }
}

internal sealed class PostgresTrackingDdl : ITrackingDdl
{
    public string Target => "postgres";
    public bool Unverified => false;
    public string Quote(string id) => "\"" + id.Replace("\"", "\"\"") + "\"";

    private static string Native(TrackingType t) => t switch
    {
        TrackingType.Name => "varchar(512)",
        TrackingType.Short => "varchar(128)",
        TrackingType.Hash => "char(64)",
        TrackingType.Long => "text",
        TrackingType.BigInt => "bigint",
        TrackingType.Int => "integer",
        TrackingType.Guid => "uuid",
        TrackingType.TimestampUtc => "timestamp(3)",
        _ => throw new ArgumentOutOfRangeException(nameof(t)),
    };

    public IReadOnlyList<InitStatement> InitScript(string schema, string toolVersion)
    {
        var s = Quote(schema);
        var list = new List<InitStatement> { new("init-00", $"schema {schema}", $"CREATE SCHEMA IF NOT EXISTS {s};") };
        var n = 1;
        foreach (var t in TrackingSchema.Tables)
        {
            var cols = string.Join(",\n", t.Columns.Select(c => $"  {Quote(c.Name)} {Native(c.Type)} {(c.Nullable ? "NULL" : "NOT NULL")}"));
            var key = string.Join(", ", t.PrimaryKey.Select(Quote));
            list.Add(new($"init-{n++:00}", $"table {t.Name}: {t.Purpose}",
                $"CREATE TABLE IF NOT EXISTS {s}.{Quote(t.Name)} (\n{cols},\n  CONSTRAINT {Quote("pk_" + t.Name)} PRIMARY KEY ({key})\n);"));
        }
        var v = $"{s}.{Quote("tracking_version")}";
        list.Add(new($"init-{n}", "record the layout version",
            $"INSERT INTO {v} (\"version\", \"tool_version\", \"applied_utc\")\nSELECT {TrackingSchema.Version}, {TrackingDdl.Literal(toolVersion)}, (now() AT TIME ZONE 'utc')\nWHERE NOT EXISTS (SELECT 1 FROM {v} WHERE \"version\" = {TrackingSchema.Version});"));
        return list;
    }
}
