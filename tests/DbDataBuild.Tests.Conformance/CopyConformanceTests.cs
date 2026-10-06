using System.Text.RegularExpressions;
using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Execution;
using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>
/// A copy between two real engines, both ways, through the command surface: the rows of a table on one connection arrive in a table on the other with the values and the NULLs intact. The two
/// connections here are the two engines (`sqlserver`, `postgres`), each with its own login.
/// </summary>
[Trait("Group", "apply")]
public partial class CopyConformanceTests
{
    public static TheoryData<string, string> Directions => new() { { "postgres", "sqlserver" }, { "sqlserver", "postgres" } };

    private const string Columns =
        "columns:\n" +
        "  - {name: id, type: BIGINT, nullable: false}\n" +
        "  - {name: small, type: SMALLINT}\n" +
        "  - {name: n, type: INTEGER}\n" +
        "  - {name: ratio, type: DOUBLE}\n" +
        "  - {name: flag, type: BOOLEAN}\n" +
        "  - {name: born, type: DATE}\n" +
        "  - {name: seen, type: TIMESTAMP}\n" +
        "  - {name: price, type: \"DECIMAL(18, 3)\"}\n" +
        "  - {name: big, type: \"DECIMAL(28, 6)\"}\n" +
        "  - {name: code, type: \"VARCHAR(40)\"}\n" +
        "  - {name: notes, type: VARCHAR}\n";

    private sealed class Pair(Engine origin, Engine destination, string dir)
    {
        public Engine Origin => origin;
        public Engine Destination => destination;
        public string Dir => dir;

        public Func<string, string?> Env => v =>
            v == LoginSettings.VariableName(origin.Name, Login.Read) ? origin.ConnectionString
            : v == LoginSettings.VariableName(destination.Name, Login.Read) || v == LoginSettings.VariableName(destination.Name, Login.Write) ? destination.ConnectionString
            : null;

        public (int Exit, string Out, string Err) Cli(params string[] args)
        {
            var o = new StringWriter(); var e = new StringWriter();
            var exit = CliApp.Run([args[0], "--project", dir, .. args.Skip(1)], o, e, environment: Env);
            return (exit, o.ToString(), e.ToString());
        }

        public void Write(string rel, string text)
        {
            var path = Path.Combine(dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
    }

    private static string Sensitive(string name) => name == "postgres" ? "string_semantics:\n  case: sensitive\n  trailing_space: significant\n  collations:\n    default: { duckdb: NFC, postgres: C }\n" : "";

    private static async Task<Pair> SetUp(string origin, string destination)
    {
        var from = EngineEnv.Require(origin);
        var to = EngineEnv.Require(destination);
        await from.StartAsync();
        await to.StartAsync();
        var pair = new Pair(from, to, Path.Combine(Path.GetTempPath(), "ddb-copy-" + Guid.NewGuid().ToString("N")));
        pair.Write("dbdatabuild.yml", $"defaults: {{connections: [{destination}]}}\ntracking: {{ connection: {destination} }}\n" + Sensitive(destination));
        pair.Write("models/src/items.yml", $"name: src.items\nkind: {{type: mapped}}\nconnections=: [{origin}]\ngrain: [id]\n{Columns}");
        pair.Write("models/dst/items.yml", "name: dst.items\nkind: {type: copy, from: src.items}\nindexes:\n  - {name: ix_items_code, columns: [code]}\n");
        await from.ExecAsync(origin == "postgres" ? "CREATE SCHEMA src" : "EXEC('CREATE SCHEMA src')");
        var q = from.QuoteIdent;
        await from.ExecAsync($"CREATE TABLE src.items ({q("id")} BIGINT NOT NULL, {q("small")} SMALLINT, {q("n")} INTEGER, {q("ratio")} {(origin == "postgres" ? "DOUBLE PRECISION" : "FLOAT(53)")}, {q("flag")} {(origin == "postgres" ? "BOOLEAN" : "BIT")}, " +
                             $"{q("born")} DATE, {q("seen")} {from.ColumnType("TIMESTAMP")}, {q("price")} {from.ColumnType("DECIMAL(18,3)")}, {q("big")} {from.ColumnType("DECIMAL(28,6)")}, {q("code")} {from.ColumnType("VARCHAR(40)")}, {q("notes")} {(origin == "postgres" ? "TEXT" : "NVARCHAR(MAX)")})");
        await from.ExecAsync("INSERT INTO src.items VALUES " +
            $"(1, 7, 100000, 3.5, {(origin == "postgres" ? "TRUE" : "1")}, '2024-02-29', '2024-02-29 13:14:15.123456', 12345.678, 12345678901234567890.123456, N'café ☕', N'long text'), " +
            $"(2, -32768, -2147483648, -0.25, {(origin == "postgres" ? "FALSE" : "0")}, '0001-01-01', '1999-12-31 23:59:59.999999', -0.001, -0.000001, N'', N''), " +
            "(3, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL), " +
            $"(4, 32767, 2147483647, 1e100, {(origin == "postgres" ? "TRUE" : "1")}, '9999-12-31', '2000-01-01 00:00:00', 0, 0, N'it''s \"quoted\"', N'line1{(origin == "postgres" ? "\n" : "' + CHAR(10) + N'")}line2')".Replace("1e100", origin == "postgres" ? "1e100" : "1e100"));
        return pair;
    }

    private static void Ok((int Exit, string Out, string Err) r, string what) => Assert.True(r.Exit == 0, $"{what} failed ({r.Exit}):\n{r.Out}\n{r.Err}");

    /// <summary>Every row as the same text on both engines: each value converted in SQL to a string that does not depend on the engine's own formatting.</summary>
    private static async Task<List<string>> ComparableAsync(Engine e, string table)
    {
        var q = e.QuoteIdent;
        string Text(string col, string type) => (e.Name, type) switch
        {
            ("postgres", "BOOLEAN") => $"CASE WHEN {q(col)} IS NULL THEN NULL WHEN {q(col)} THEN '1' ELSE '0' END",
            ("postgres", "TIMESTAMP") => $"to_char({q(col)}, 'YYYY-MM-DD HH24:MI:SS.US')",
            ("postgres", "DATE") => $"to_char({q(col)}, 'YYYY-MM-DD')",
            ("sqlserver", "BOOLEAN") => $"CAST({q(col)} AS VARCHAR(1))",
            ("sqlserver", "TIMESTAMP") => $"CONVERT(VARCHAR(30), {q(col)}, 121)",
            ("sqlserver", "DATE") => $"CONVERT(VARCHAR(10), {q(col)}, 23)",
            ("sqlserver", _) => $"CAST({q(col)} AS NVARCHAR(100))",
            _ => $"CAST({q(col)} AS VARCHAR(100))",
        };
        string[] cols = ["id", "small", "n", "ratio", "flag", "born", "seen", "price", "big", "code", "notes"];
        string[] types = ["BIGINT", "SMALLINT", "INTEGER", "DOUBLE", "BOOLEAN", "DATE", "TIMESTAMP", "DECIMAL", "DECIMAL", "VARCHAR", "VARCHAR"];
        var parts = cols.Select((c, i) => $"COALESCE({Text(c, types[i])}, '<null>')");
        var concat = e.Name == "postgres" ? string.Join(" || '|' || ", parts) : string.Join(" + '|' + ", parts);
        return await e.RowsAsync($"SELECT {concat} FROM {table}");
    }

    [SkippableTheory, MemberData(nameof(Directions))]
    public async Task The_rows_of_a_table_on_one_engine_arrive_intact_on_the_other(string origin, string destination)
    {
        var pair = await SetUp(origin, destination);
        await using var fromEngine = pair.Origin;
        await using var toEngine = pair.Destination;
        try
        {
            Ok(pair.Cli("init", "--connection", destination, "--apply"), "init");
            Ok(pair.Cli("render", "--write"), "render --write");
            Ok(pair.Cli("check", "--connection", destination), "check");
            var plan = pair.Cli("plan", "--connection", destination);
            Ok(plan, "plan");
            var planFile = Path.Combine(pair.Dir, Regex.Match(plan.Out, @"plan:\s+(\S+\.plan\.yml)").Groups[1].Value);
            var text = File.ReadAllText(planFile);
            Assert.Contains("type: transfer", text);
            Assert.Contains($"origin: \"{origin}\"", text);

            var dry = pair.Cli("apply", planFile, "--dry-run");
            Ok(dry, "apply --dry-run");
            Assert.Empty(await toEngine.RowsAsync($"SELECT 1 FROM information_schema.tables WHERE table_schema = 'dst'"));        // nothing was created

            Ok(pair.Cli("apply", planFile), "apply");
            var expected = await ComparableAsync(fromEngine, "src.items");
            var copied = await ComparableAsync(toEngine, "dst.items");
            Assert.Equal(string.Join("\n", expected), string.Join("\n", copied));
            Assert.Equal(4, copied.Count);
            Assert.Empty(await toEngine.RowsAsync($"SELECT 1 FROM information_schema.tables WHERE table_name LIKE 'stg_dst%'"));    // the staging table is gone

            // the next plan has nothing to change in the structure and loads again: a copy is a full replace
            var again = pair.Cli("plan", "--connection", destination);
            Ok(again, "second plan");
            var second = File.ReadAllText(Path.Combine(pair.Dir, Regex.Match(again.Out, @"plan:\s+(\S+\.plan\.yml)").Groups[1].Value));
            Assert.DoesNotContain("create table", second);
            await fromEngine.ExecAsync("INSERT INTO src.items (id) VALUES (5)");
            Ok(pair.Cli("apply", Path.Combine(pair.Dir, Regex.Match(again.Out, @"plan:\s+(\S+\.plan\.yml)").Groups[1].Value)), "second apply");
            Assert.Equal(5, (await ComparableAsync(toEngine, "dst.items")).Count);
        }
        finally { try { Directory.Delete(pair.Dir, true); } catch (IOException) { } }
    }

    [SkippableFact]
    public async Task A_missing_origin_login_is_refused_before_anything_runs_and_a_value_that_cannot_be_held_stops_the_copy()
    {
        var pair = await SetUp("postgres", "sqlserver");
        await using var fromEngine = pair.Origin;
        await using var toEngine = pair.Destination;
        try
        {
            Ok(pair.Cli("init", "--connection", "sqlserver", "--apply"), "init");
            Ok(pair.Cli("render", "--write"), "render --write");
            var plan = pair.Cli("plan", "--connection", "sqlserver");
            Ok(plan, "plan");
            var planFile = Path.Combine(pair.Dir, Regex.Match(plan.Out, @"plan:\s+(\S+\.plan\.yml)").Groups[1].Value);

            // no login for the origin: nothing is executed
            var o = new StringWriter(); var e = new StringWriter();
            var exit = CliApp.Run(["apply", planFile, "--project", pair.Dir], o, e, environment: v => v == LoginSettings.VariableName("sqlserver", Login.Read) || v == LoginSettings.VariableName("sqlserver", Login.Write) ? toEngine.ConnectionString : null);
            Assert.NotEqual(0, exit);
            Assert.Contains("DBDATABUILD_POSTGRES_READ", e.ToString() + o.ToString());
            Assert.Empty(await toEngine.RowsAsync("SELECT 1 FROM information_schema.tables WHERE table_schema = 'dst'"));

            // a decimal wider than the declared type cannot be held: the transfer stops, the destination table is not loaded
            await fromEngine.ExecAsync("DELETE FROM src.items; INSERT INTO src.items (id, big) VALUES (1, 1)");
            await fromEngine.ExecAsync("ALTER TABLE src.items ALTER COLUMN big TYPE NUMERIC(38, 6)");
            await fromEngine.ExecAsync("UPDATE src.items SET big = 99999999999999999999999999999999.123456");
            var failed = pair.Cli("apply", planFile);
            Assert.NotEqual(0, failed.Exit);
            Assert.Empty(await toEngine.RowsAsync("SELECT 1 FROM dst.items"));
        }
        finally { try { Directory.Delete(pair.Dir, true); } catch (IOException) { } }
    }

    [SkippableTheory, MemberData(nameof(Directions))]
    public async Task A_table_of_a_few_hundred_thousand_rows_moves_in_bulk(string origin, string destination)
    {
        var pair = await SetUp(origin, destination);
        await using var fromEngine = pair.Origin;
        await using var toEngine = pair.Destination;
        try
        {
            const int Rows = 300_000;
            await fromEngine.ExecAsync("DELETE FROM src.items");
            await fromEngine.ExecAsync(origin == "postgres"
                ? $"INSERT INTO src.items (id, n, price, code, notes, seen) SELECT g, g % 1000, g / 7.0, 'code-' || g, repeat('x', 100), TIMESTAMP '2024-01-01' + g * INTERVAL '1 second' FROM generate_series(1, {Rows}) g"
                : $"INSERT INTO src.items (id, n, price, code, notes, seen) SELECT TOP ({Rows}) n, n % 1000, n / 7.0, 'code-' + CAST(n AS NVARCHAR(20)), REPLICATE(N'x', 100), DATEADD(SECOND, n, '2024-01-01') FROM (SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n FROM sys.all_objects a CROSS JOIN sys.all_objects b) t");
            Ok(pair.Cli("init", "--connection", destination, "--apply"), "init");
            Ok(pair.Cli("render", "--write"), "render --write");
            var plan = pair.Cli("plan", "--connection", destination);
            Ok(plan, "plan");
            var planFile = Path.Combine(pair.Dir, Regex.Match(plan.Out, @"plan:\s+(\S+\.plan\.yml)").Groups[1].Value);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Ok(pair.Cli("apply", planFile), "apply");
            clock.Stop();
            Assert.Equal(Rows.ToString(), (await toEngine.RowsAsync("SELECT COUNT(*) FROM dst.items")).Single());
            Assert.Equal((await fromEngine.RowsAsync("SELECT SUM(n) FROM src.items")).Single(), (await toEngine.RowsAsync("SELECT SUM(n) FROM dst.items")).Single());
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(90), $"{Rows} rows took {clock.Elapsed.TotalSeconds:0.0}s");
            Console.WriteLine($"copy {origin} -> {destination}: {Rows} rows in {clock.Elapsed.TotalSeconds:0.0}s");
        }
        finally { try { Directory.Delete(pair.Dir, true); } catch (IOException) { } }
    }

    [SkippableFact]
    public async Task One_application_on_several_systems_lands_in_one_table_one_origin_at_a_time()
    {
        var store17 = EngineEnv.Require("postgres");
        var store18 = EngineEnv.Require("postgres");
        var warehouse = EngineEnv.Require("sqlserver");
        await store17.StartAsync(); await store18.StartAsync(); await warehouse.StartAsync();
        await using var _17 = store17; await using var _18 = store18; await using var _wh = warehouse;
        var dir = Path.Combine(Path.GetTempPath(), "ddb-fanin-" + Guid.NewGuid().ToString("N"));
        string? Env(string v) =>
            v == "DBDATABUILD_STORE_17_READ" ? store17.ConnectionString : v == "DBDATABUILD_STORE_18_READ" ? store18.ConnectionString
            : v is "DBDATABUILD_SQLSERVER_READ" or "DBDATABUILD_SQLSERVER_WRITE" ? warehouse.ConnectionString : null;
        (int Exit, string Out, string Err) Cli(params string[] args)
        {
            var o = new StringWriter(); var e = new StringWriter();
            var exit = CliApp.Run([args[0], "--project", dir, .. args.Skip(1)], o, e, environment: Env);
            return (exit, o.ToString(), e.ToString());
        }
        void Write(string rel, string text) { var path = Path.Combine(dir, rel); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }
        string PlanOf(string output) => Path.Combine(dir, Regex.Match(output, @"plan:\s+(\S+\.plan\.yml)").Groups[1].Value);
        try
        {
            foreach (var (store, rows) in new[] { (store17, "(1, 10.00), (2, 20.00)"), (store18, "(1, 11.00), (3, 33.00)") })
            {
                await store.ExecAsync("CREATE SCHEMA pos");
                await store.ExecAsync("CREATE TABLE pos.orders (order_id BIGINT NOT NULL, total NUMERIC(10,2))");
                await store.ExecAsync($"INSERT INTO pos.orders VALUES {rows}");
            }
            Write("dbdatabuild.yml", "connections:\n  store_17: { engine: postgres, parameters: { store_id: \"017\" } }\n  store_18: { engine: postgres, parameters: { store_id: \"018\" } }\ndefaults:\n  connections: [sqlserver]\ntracking: { connection: sqlserver }\n");
            Write("models/pos/orders.yml", "name: pos.orders\nkind: {type: mapped}\nconnections=: [store_17, store_18]\ngrain: [order_id]\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: total, type: \"DECIMAL(10, 2)\"}\n");
            Write("models/warehouse/orders.yml", "name: warehouse.orders\nkind:\n  type: copy\n  from: pos.orders\n  slice: {column: store_id, value: \"${origin.store_id}\", type: \"VARCHAR(10)\"}\n");

            Ok(Cli("init", "--connection", "sqlserver", "--apply"), "init");
            Ok(Cli("render", "--write"), "render --write");
            var plan = Cli("plan", "--connection", "sqlserver");
            Ok(plan, "plan");
            var text = File.ReadAllText(PlanOf(plan.Out));
            Assert.Equal(2, Regex.Matches(text, "type: transfer").Count);                                         // one transfer per origin
            Ok(Cli("apply", PlanOf(plan.Out)), "apply");
            Assert.Equal(["1|017|10.00", "1|018|11.00", "2|017|20.00", "3|018|33.00"], await warehouse.RowsAsync("SELECT CAST(order_id AS VARCHAR(10)) + '|' + store_id + '|' + CAST(total AS VARCHAR(20)) FROM warehouse.orders"));

            // a row that belongs to no origin of this copy is not touched by a run, and each origin's run replaces only its own rows
            await warehouse.ExecAsync("INSERT INTO warehouse.orders (order_id, total, store_id) VALUES (9, 90.00, '999')");
            await store17.ExecAsync("DELETE FROM pos.orders WHERE order_id = 1; INSERT INTO pos.orders VALUES (4, 40.00)");
            var again = Cli("plan", "--connection", "sqlserver");
            Ok(again, "second plan");
            Ok(Cli("apply", PlanOf(again.Out)), "second apply");
            Assert.Equal(["1|018|11.00", "2|017|20.00", "3|018|33.00", "4|017|40.00", "9|999|90.00"], await warehouse.RowsAsync("SELECT CAST(order_id AS VARCHAR(10)) + '|' + store_id + '|' + CAST(total AS VARCHAR(20)) FROM warehouse.orders"));

            // version skew: store 18 lost a column. The plan names the origin and stops, and says nothing else about the others
            await store18.ExecAsync("ALTER TABLE pos.orders DROP COLUMN total");
            var skew = Cli("plan", "--connection", "sqlserver");
            Assert.NotEqual(0, skew.Exit);
            Assert.Contains("DDB-230", skew.Err);
            Assert.Contains("`pos.orders` on `store_18` differs from its declaration: column `total` is gone", skew.Err);
            Assert.DoesNotContain("store_17` differs", skew.Err);

            // with on_mismatch: skip the other origin still loads, and the skipped one's rows stay as they were
            Write("models/warehouse/orders.yml", "name: warehouse.orders\nkind:\n  type: copy\n  from: pos.orders\n  on_mismatch: skip\n  slice: {column: store_id, value: \"${origin.store_id}\", type: \"VARCHAR(10)\"}\n");
            Ok(Cli("render", "--write"), "render --write");
            await store17.ExecAsync("INSERT INTO pos.orders VALUES (5, 50.00)");
            var skipped = Cli("plan", "--connection", "sqlserver");
            Ok(skipped, "plan with a skipped origin");
            Assert.Contains("left out of the plan", skipped.Err);
            Assert.Single(Regex.Matches(File.ReadAllText(PlanOf(skipped.Out)), "type: transfer"));
            Ok(Cli("apply", PlanOf(skipped.Out)), "apply with a skipped origin");
            Assert.Equal(["1|018|11.00", "2|017|20.00", "3|018|33.00", "4|017|40.00", "5|017|50.00", "9|999|90.00"], await warehouse.RowsAsync("SELECT CAST(order_id AS VARCHAR(10)) + '|' + store_id + '|' + CAST(total AS VARCHAR(20)) FROM warehouse.orders"));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
