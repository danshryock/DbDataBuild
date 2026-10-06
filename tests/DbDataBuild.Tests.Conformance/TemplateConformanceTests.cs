using System.Globalization;
using System.Text.Json;
using DbDataBuild.Cli;
using DbDataBuild.Execution;
using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>
/// A built-in project template on a real engine, end to end through the commands a person would type: `new`, `load-seeds` (the seeded source tables are created and filled), `render`, `init`, `plan`, `apply`.
/// Then every mart as the engine built it is compared, row by row, with the same model run on the same seeds in DuckDB (`sample`), which is the meaning the models were written with.
/// </summary>
[Trait("Group", "templates")]
public class TemplateConformanceTests
{
    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var engine in new[] { "sqlserver", "postgres" })
            foreach (var template in new[] { "starter", "retail", "chinook", "adventureworks" })
                data.Add(engine, template);
        return data;
    }

    public static TheoryData<string, string> NativeCases()
    {
        var data = new TheoryData<string, string>();
        foreach (var engine in new[] { "sqlserver", "postgres" })
            foreach (var template in new[] { "retail", "adventureworks" })
                data.Add(engine, template);
        return data;
    }

    private const string PostgresConfig = "defaults: {connections: [postgres]}\nstring_semantics:\n  case: sensitive\n  trailing_space: significant\n  collations:\n    default: { duckdb: NFC, postgres: C }\npolicy:\n  severity:\n    approximated: note\n";

    [SkippableTheory, MemberData(nameof(Cases))]
    public Task A_template_builds_on_the_engine_and_every_mart_matches_duckdb(string name, string template) => BuildAndCompare(name, template, native: false);

    /// <summary>
    /// With the optional rewrites turned off (`rewrites: { fidelity: native }`) the engine's own behavior may give other values, never other rows: the projects still build and every table has the rows DuckDB gives.
    /// Release group: it repeats the builds above with a different setting and takes about a minute.
    /// </summary>
    [SkippableTheory, MemberData(nameof(NativeCases)), Trait("Group", "release")]
    public Task A_template_built_with_the_rewrites_off_still_has_the_rows_DuckDB_gives(string name, string template) => BuildAndCompare(name, template, native: true);

    private static async Task BuildAndCompare(string name, string template, bool native)
    {
        var engine = EngineEnv.Require(name);
        await engine.StartAsync();
        await using var _ = engine;
        var dir = Path.Combine(Path.GetTempPath(), "ddb-tpl-" + Guid.NewGuid().ToString("N"));
        try
        {
            string? Env(string v) => v == LoginSettings.VariableName(name, Login.Read) || v == LoginSettings.VariableName(name, Login.Write) ? engine.ConnectionString : null;
            (int Exit, string Out, string Err) Cli(params string[] args)
            {
                var o = new StringWriter();
                var e = new StringWriter();
                var exit = CliApp.Run([args[0], .. args.Skip(1), .. (args[0] == "new" ? [] : new[] { "--project", dir })], o, e, environment: Env);
                return (exit, o.ToString(), e.ToString());
            }
            void Ok((int Exit, string Out, string Err) r, string what) => Assert.True(r.Exit == 0, $"{what} failed ({r.Exit}):\n{r.Out}\n{r.Err}");

            Ok(Cli("new", template, dir), "new");
            if (name == "postgres") File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), PostgresConfig);
            if (native) File.AppendAllText(Path.Combine(dir, "dbdatabuild.yml"), "\nrewrites:\n  fidelity: native\n");

            Ok(Cli("load-seeds", "--apply"), "load-seeds");
            Ok(Cli("render", "--write"), "render --write");
            Ok(Cli("init", "--apply"), "init");
            var plan = Cli("plan", "--accept-inferred");
            Ok(plan, "plan");
            var planFile = Path.Combine(dir, System.Text.RegularExpressions.Regex.Match(plan.Out, @"plan:\s+(\S+\.plan\.yml)").Groups[1].Value);
            Ok(Cli("apply", planFile), "apply");

            var marts = Directory.EnumerateFiles(Path.Combine(dir, "models", "marts"), "*.sql").Select(f => "marts." + Path.GetFileNameWithoutExtension(f)).OrderBy(m => m, StringComparer.Ordinal).ToList();
            Assert.NotEmpty(marts);
            foreach (var mart in marts)
            {
                var sample = Cli("sample", mart, "--limit", "1000000", "--format", "json");
                Ok(sample, $"sample {mart}");
                using var doc = JsonDocument.Parse(sample.Out);
                var table = doc.RootElement.GetProperty("data").GetProperty("tables").EnumerateArray().Single(t => t.GetProperty("name").GetString() == mart);
                if (native)
                {
                    var counted = int.Parse((await RowsAsync(engine, $"SELECT COUNT(*) FROM {mart}")).Single(), CultureInfo.InvariantCulture);
                    Assert.True(table.GetProperty("row_count").GetInt32() == counted, $"{name} (native): {mart} has {counted} row(s), DuckDB gives {table.GetProperty("row_count").GetInt32()}");
                    continue;
                }
                var expected = table.GetProperty("rows").EnumerateArray()
                    .Select(r => string.Join("|", r.EnumerateArray().Select(v => Canon(v.ValueKind == JsonValueKind.Null ? null : v.GetString()))))
                    .OrderBy(r => r, StringComparer.Ordinal).ToList();
                Assert.NotEmpty(expected);

                var actual = await RowsAsync(engine, $"SELECT * FROM {mart}");
                Assert.True(expected.SequenceEqual(actual), $"{name}: {mart} differs from DuckDB: {expected.Count} row(s) expected, {actual.Count} found.\nfirst expected not found: {expected.Except(actual).FirstOrDefault()}\nfirst found not expected: {actual.Except(expected).FirstOrDefault()}");
            }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    private static async Task<List<string>> RowsAsync(Engine engine, string sql)
    {
        using var cmd = engine.Conn.CreateCommand();
        cmd.CommandText = sql;
        using var rd = await cmd.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await rd.ReadAsync())
            rows.Add(string.Join("|", Enumerable.Range(0, rd.FieldCount).Select(i => Canon(rd.IsDBNull(i) ? null : Text(rd.GetValue(i), rd.GetDataTypeName(i))))));
        rows.Sort(StringComparer.Ordinal);
        return rows;
    }

    private static string Text(object v, string typeName) => v switch
    {
        bool b => b ? "true" : "false",
        DateTime dt when typeName.Equals("date", StringComparison.OrdinalIgnoreCase) => dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };

    /// <summary>Numbers compare as numbers (12.50 and 12.5 are the same), everything else as text; the same on both sides.</summary>
    private static string Canon(string? text) =>
        text == null ? "∅"
        : decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var m) ? Math.Round(m, 6).ToString("0.######", CultureInfo.InvariantCulture)
        : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? Math.Round(d, 6).ToString("0.######", CultureInfo.InvariantCulture)
        : text;
}
