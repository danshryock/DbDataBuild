using System.Text.RegularExpressions;
using DbDataBuild.Cli;
using DbDataBuild.Execution;
using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>Macros on the real engines: one macro, a live table and its snapshot, and for each the SQL the engine runs is the one written for that table.</summary>
[Trait("Group", "apply")]
public class MacroConformanceTests
{
    public static TheoryData<string> Engines => new() { "sqlserver", "postgres" };

    private const string Macros = """
        CREATE MACRO snapshot_at(tbl, col) AS TABLE
          SELECT * EXCLUDE (__none), CASE WHEN col IS NULL THEN current_date ELSE COLUMNS(lambda c: c = coalesce(col, '__none')) END AS as_of_date
          FROM (SELECT *, NULL::DATE AS __none FROM query_table(tbl));

        CREATE MACRO status_totals(tbl, col) AS TABLE
          SELECT as_of_date, status, sum(amount) AS total FROM snapshot_at(tbl, col) GROUP BY as_of_date, status;
        """;

    private static string Config(string name) =>
        $"defaults: {{connections: [{name}]}}\ntracking: {{ connection: {name} }}\n" +
        (name == "postgres" ? "string_semantics:\n  case: sensitive\n  trailing_space: significant\n  collations:\n    default: { duckdb: NFC, postgres: C }\n" : "");

    private const string Cols = "grain: [as_of_date, status]\ncolumns:\n  - {name: as_of_date, type: DATE, nullable: false}\n  - {name: status, type: \"VARCHAR(20)\", nullable: false}\n  - {name: total, type: \"DECIMAL(38,2)\", nullable: false}\n";

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task One_macro_gives_each_table_its_own_query_and_the_engine_returns_the_rows_DuckDB_would(string name)
    {
        var engine = EngineEnv.Require(name);
        await engine.StartAsync();
        await using var _ = engine;
        var dir = Path.Combine(Path.GetTempPath(), "ddb-macro-" + Guid.NewGuid().ToString("N"));
        string? Env(string v) => v == LoginSettings.VariableName(name, Login.Read) || v == LoginSettings.VariableName(name, Login.Write) ? engine.ConnectionString : null;
        (int Exit, string Out, string Err) Cli(params string[] args)
        {
            var o = new StringWriter(); var e = new StringWriter();
            var exit = CliApp.Run([args[0], "--project", dir, .. args.Skip(1)], o, e, environment: Env);
            return (exit, o.ToString(), e.ToString());
        }
        void Write(string rel, string text) { var p = Path.Combine(dir, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, text); }
        void Ok((int Exit, string Out, string Err) r, string what) => Assert.True(r.Exit == 0, $"{what} failed:\n{r.Out}\n{r.Err}");
        string PlanOf(string output) => Path.Combine(dir, Regex.Match(output, @"plan:\s+(\S+\.plan\.yml)").Groups[1].Value);
        try
        {
            await engine.ExecAsync(name == "postgres" ? "CREATE SCHEMA IF NOT EXISTS src" : "IF SCHEMA_ID('src') IS NULL EXEC('CREATE SCHEMA src')");
            await engine.ExecAsync("DROP TABLE IF EXISTS src.orders");
            await engine.ExecAsync("DROP TABLE IF EXISTS src.orders_snap");
            await engine.ExecAsync("CREATE TABLE src.orders (order_id bigint NOT NULL, amount decimal(18,2) NOT NULL, status varchar(20) NOT NULL)");
            await engine.ExecAsync("CREATE TABLE src.orders_snap (order_id bigint NOT NULL, amount decimal(18,2) NOT NULL, status varchar(20) NOT NULL, snap_date date NOT NULL)");
            await engine.ExecAsync("INSERT INTO src.orders VALUES (1, 10.50, 'open'), (2, 20.25, 'open'), (3, 7.00, 'shipped')");
            await engine.ExecAsync("INSERT INTO src.orders_snap VALUES (1, 10.00, 'open', '2026-01-01'), (2, 5.00, 'open', '2026-01-01'), (1, 12.00, 'shipped', '2026-02-01'), (2, 6.00, 'open', '2026-02-01')");

            Write("dbdatabuild.yml", Config(name));
            Write("macros/snapshots.sql", Macros);
            Write("models/src/orders.yml", "name: src.orders\nkind: {type: mapped}\ngrain: [order_id]\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: amount, type: \"DECIMAL(18,2)\", nullable: false}\n  - {name: status, type: \"VARCHAR(20)\", nullable: false}\n");
            Write("models/src/orders_snap.yml", "name: src.orders_snap\nkind: {type: mapped}\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: amount, type: \"DECIMAL(18,2)\", nullable: false}\n  - {name: status, type: \"VARCHAR(20)\", nullable: false}\n  - {name: snap_date, type: DATE, nullable: false}\n");
            Write("models/marts/live_totals.yml", "name: marts.live_totals\nkind: {type: full}\n" + Cols);
            Write("models/marts/live_totals.sql", "SELECT * FROM status_totals('src.orders', NULL)\n");
            Write("models/marts/snap_totals.yml", "name: marts.snap_totals\nkind: {type: full}\n" + Cols);
            Write("models/marts/snap_totals.sql", "SELECT * FROM status_totals('src.orders_snap', 'snap_date')\n");

            Ok(Cli("init", "--connection", name, "--apply"), "init");
            Ok(Cli("render", "--write"), "render");
            // what each engine is given is the query for the table: no choice left in the text
            var snapScript = File.ReadAllText(Path.Combine(dir, "rendered", name, "marts.snap_totals", "load.default.sql"));
            var liveScript = File.ReadAllText(Path.Combine(dir, "rendered", name, "marts.live_totals", "load.default.sql"));
            Assert.Contains("orders_snap", snapScript); Assert.DoesNotContain("CASE", snapScript); Assert.DoesNotContain("UNION", snapScript);
            Assert.DoesNotContain("orders_snap", liveScript); Assert.DoesNotContain("snap_date", liveScript); Assert.DoesNotContain("UNION", liveScript);
            var plan = Cli("plan", "--connection", name);
            Ok(plan, "plan");
            Ok(Cli("apply", PlanOf(plan.Out)), "apply");

            // the snapshot: the rows DuckDB gives for the same query (the sums per snapshot date and status)
            Assert.Equal(["2026-01-01|open|15.00", "2026-02-01|open|6.00", "2026-02-01|shipped|12.00"],
                await engine.RowsAsync("SELECT CONCAT(CONVERT(VARCHAR(10), as_of_date, 23), '|', status, '|', CAST(total AS VARCHAR(20))) FROM marts.snap_totals ORDER BY 1".Replace("CONVERT(VARCHAR(10), as_of_date, 23)", name == "postgres" ? "to_char(as_of_date, 'YYYY-MM-DD')" : "CONVERT(VARCHAR(10), as_of_date, 23)")));
            // the live table: as of today, by the engine's own clock
            var today = (await engine.RowsAsync(name == "postgres" ? "SELECT to_char(CURRENT_DATE, 'YYYY-MM-DD')" : "SELECT CONVERT(VARCHAR(10), CAST(GETDATE() AS DATE), 23)")).Single();
            Assert.Equal([$"{today}|open|30.75", $"{today}|shipped|7.00"],
                await engine.RowsAsync("SELECT CONCAT(to_char(as_of_date, 'YYYY-MM-DD'), '|', status, '|', CAST(total AS VARCHAR(20))) FROM marts.live_totals ORDER BY 1".Replace("to_char(as_of_date, 'YYYY-MM-DD')", name == "postgres" ? "to_char(as_of_date, 'YYYY-MM-DD')" : "CONVERT(VARCHAR(10), as_of_date, 23)")));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
