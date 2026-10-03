using System.Text;
using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>
/// Where the engines disagree with DuckDB over scalar expressions and aggregates, run the way the tool would send them. A case whose engine answer equals DuckDB's is a regression test; a case in
/// <see cref="Known"/> is a difference that is understood and written down (docs/research/engine-differences); anything else fails the run. Set DDB_PROBE_OUT to a file to get the full report.
/// </summary>
public class EngineDifferenceProbes
{
    private static EngineProbe.Case C(string id, string expr, string? note = null) => new(id, $"SELECT id, {expr} AS v FROM probe", note);
    private static EngineProbe.Case A(string id, string agg, string? note = null) => new(id, $"SELECT {agg} AS v FROM probe", note);

    public static readonly EngineProbe.Case[] Cases =
    [
        // integer and decimal arithmetic
        C("int_div", "i / j"), C("int_floor_div", "i // j"), C("mod", "i % j"), C("mod_neg", "(-7) % 3"), C("mod_neg2", "7 % (-3)"), C("mod_by_zero", "i % j"),
        C("mul", "i * j"), C("add_overflow", "i + j"), C("neg", "-i"), C("abs", "abs(i)"), C("sign", "sign(i)"), C("power", "power(i, 2)"), C("sqrt", "sqrt(abs(d))"),
        C("dec_div", "n / j"), C("dec_mul", "n * n"), C("dec_add", "n + d"), C("div_double", "d / j"), C("div_zero_double", "d / 0"), C("int_div_zero", "i / 0"),
        C("ceil", "ceil(d)"), C("floor", "floor(d)"), C("ceil_dec", "ceil(n)"), C("floor_dec", "floor(n)"), C("round1", "round(n, 1)"), C("round0", "round(n)"), C("round_d", "round(d)"), C("round_d1", "round(d, 1)"),
        C("round_half_dec", "round(CAST(2.5 AS DECIMAL(5, 1)))"), C("round_neg", "round(n, -1)"), C("trunc", "trunc(d)"), C("ln", "ln(abs(d) + 1)"), C("exp", "exp(i / 10.0)"), C("log10", "log10(abs(d) + 1)"),
        C("greatest", "greatest(i, j)"), C("least", "least(i, j)"), C("greatest3", "greatest(i, j, 3)"),
        // casts
        C("cast_double_int", "CAST(d AS INTEGER)"), C("cast_dec_int", "CAST(n AS INTEGER)"), C("cast_double_bigint", "CAST(d AS BIGINT)"), C("cast_int_varchar", "CAST(i AS VARCHAR)"),
        C("cast_double_varchar", "CAST(d AS VARCHAR)"), C("cast_dec_varchar", "CAST(n AS VARCHAR)"), C("cast_date_varchar", "CAST(dt AS VARCHAR)"), C("cast_ts_varchar", "CAST(ts AS VARCHAR)"),
        C("cast_int_double", "CAST(i AS DOUBLE)"), C("cast_int_dec", "CAST(i AS DECIMAL(10, 2))"), C("cast_double_dec", "CAST(d AS DECIMAL(10, 2))"), C("cast_dec_dec", "CAST(n AS DECIMAL(10, 0))"),
        C("cast_ts_date", "CAST(ts AS DATE)"), C("cast_date_ts", "CAST(dt AS TIMESTAMP)"), C("cast_int_bool", "CAST(i AS BOOLEAN)"), C("cast_str_bool", "CAST(s = 'abc' AS INTEGER)"),
        C("try_cast_str_int", "TRY_CAST(s AS INTEGER)"), C("try_cast_str_double", "TRY_CAST(s AS DOUBLE)"), C("try_cast_str_date", "TRY_CAST(s AS DATE)"), C("cast_str_int_literal", "CAST('12' AS INTEGER) + i"),
        C("cast_str_spaces_int", "CAST(' 12 ' AS INTEGER)"), C("cast_str_decimal_literal_int", "CAST('1.5' AS INTEGER)"), C("cast_varchar_len", "CAST(s AS VARCHAR(3))"),
        // text
        C("length", "length(s)"), C("upper", "upper(s)"), C("lower", "lower(s)"), C("substr2", "substr(s, 2, 2)"), C("substr0", "substr(s, 0, 2)"), C("substr_neg", "substr(s, -2)"), C("substr_neg_len", "substr(s, 2, -1)"),
        C("substr_from", "substr(s, 2)"), C("left", "left(s, 2)"), C("right", "right(s, 2)"), C("left_neg", "left(s, -1)"), C("concat_op", "s || 'x'"), C("concat_fn", "concat(s, 'x')"), C("concat_int", "concat(s, i)"),
        C("concat_ws", "concat_ws('-', s, 'x')"), C("trim", "trim(s)"), C("ltrim", "ltrim(s)"), C("rtrim", "rtrim(s)"), C("replace", "replace(s, 'a', 'b')"), C("replace_empty", "replace(s, '', 'x')"),
        C("strpos", "strpos(s, 'b')"), C("position", "position('b' IN s)"), C("reverse", "reverse(s)"), C("lpad", "lpad(s, 5, '*')"), C("rpad", "rpad(s, 5, '*')"), C("lpad_short", "lpad(s, 2, '*')"),
        C("repeat", "repeat(s, 2)"), C("like", "s LIKE 'a%'"), C("like_under", "s LIKE '_bc'"), C("ilike", "s ILIKE 'a%'"), C("not_like", "s NOT LIKE 'a%'"), C("starts_with", "starts_with(s, 'a')"),
        C("contains", "contains(s, 'b')"), C("eq", "s = 'abc'"), C("eq_trailing", "s = 'abc '"), C("lt", "s < 'b'"), C("ascii", "ascii(s)"), C("chr", "chr(65)"), C("split_part", "split_part(s, ',', 2)"),
        C("initcap_like", "upper(left(s, 1)) || lower(substr(s, 2))"), C("length_nonbmp", "length('😀a')"), C("upper_sharp_s", "upper('ß')"), C("lower_dotted_i", "lower('İ')"),
        C("min_str_cmp", "s > 'ABC'"),
        // nulls and logic
        C("coalesce", "coalesce(s, 'z')"), C("nullif", "nullif(i, 0)"), C("distinct_from", "i IS DISTINCT FROM j"), C("not_distinct", "i IS NOT DISTINCT FROM j"), C("case", "CASE WHEN i > j THEN 1 WHEN i = j THEN 0 ELSE -1 END"),
        C("between", "i BETWEEN 1 AND 7"), C("in_null", "i IN (7, NULL)"), C("not_in_null", "i NOT IN (7, NULL)"), C("and_null", "(i > 1) AND (j > 1)"), C("or_null", "(i > 1) OR (j > 1)"),
        C("bool_to_int", "CAST((i > 1) AS INTEGER)"), C("if_fn", "if(i > 1, 'y', 'n')"), C("ifnull", "ifnull(i, -1)"),
        C("empty_literal", "coalesce(s, '')"), C("ne_empty", "s <> ''"), C("nullif_empty", "nullif(s, '')"), C("eq_empty", "s = ''"), C("in_empty", "s IN ('', 'abc')"), C("concat_empty", "s || ''"),
        C("cast_double_int2", "CAST(d / 10000 AS INTEGER)"), C("cast_dec_smallint", "CAST(n AS SMALLINT)"), C("round_n_bigint", "CAST(round(n) AS BIGINT)"),
        // dates
        C("date_add", "dt + INTERVAL 1 DAY"), C("date_add_month", "dt + INTERVAL 1 MONTH"), C("date_sub", "dt - INTERVAL 1 MONTH"), C("date_diff_day", "date_diff('day', dt, DATE '2024-03-01')"),
        C("date_diff_month", "date_diff('month', dt, DATE '2024-03-01')"), C("date_diff_year", "date_diff('year', dt, DATE '2025-01-01')"), C("date_part_year", "date_part('year', dt)"), C("extract_month", "extract(month FROM dt)"),
        C("year", "year(dt)"), C("month", "month(dt)"), C("day", "day(dt)"), C("dayofweek", "dayofweek(dt)"), C("dayofyear", "dayofyear(dt)"), C("week", "week(dt)"), C("quarter", "quarter(dt)"),
        C("date_trunc_month", "date_trunc('month', dt)"), C("date_trunc_year", "date_trunc('year', dt)"), C("date_trunc_day_ts", "date_trunc('day', ts)"), C("date_trunc_hour", "date_trunc('hour', ts)"),
        C("last_day", "last_day(dt)"), C("date_sub_dates", "dt - DATE '2024-01-01'"), C("ts_add", "ts + INTERVAL 90 MINUTE"), C("ts_diff", "date_diff('second', ts, TIMESTAMP '2024-03-01 00:00:00')"),
        C("hour", "hour(ts)"), C("minute", "minute(ts)"), C("second", "second(ts)"), C("year_ts", "year(ts)"), C("dp_quarter", "date_part('quarter', dt)"), C("dp_dow", "date_part('dow', dt)"),
        C("dp_doy", "date_part('doy', dt)"), C("dp_week", "date_part('week', dt)"), C("dp_isodow", "date_part('isodow', dt)"), C("dp_epoch", "date_part('epoch', ts)"), C("dp_hour", "date_part('hour', ts)"), C("make_date", "make_date(2024, 2, 29)"), C("dt_lt", "dt < DATE '2024-02-01'"), C("dt_eq_ts", "dt = CAST(ts AS DATE)"),
        // aggregates over the whole table
        A("sum_int", "sum(i)"), A("sum_dec", "sum(n)"), A("sum_double", "sum(d)"), A("avg_int", "avg(i)"), A("avg_dec", "avg(n)"), A("min_str", "min(s)"), A("max_str", "max(s)"), A("count_all", "count(*)"),
        A("count_col", "count(s)"), A("count_distinct", "count(DISTINCT s)"), A("stddev", "stddev(d)"), A("stddev_pop", "stddev_pop(d)"), A("var_samp", "var_samp(d)"), A("var_pop", "var_pop(d)"),
        A("string_agg", "string_agg(s, ',' ORDER BY id)"), A("string_agg_sep", "string_agg(s, ' | ' ORDER BY id DESC)"), A("string_agg_filter", "string_agg(s, '-' ORDER BY id) FILTER (WHERE id > 1)"),
        A("quantile", "quantile_cont(d, 0.9)"), A("min_date", "min(dt)"), A("max_ts", "max(ts)"), A("bool_and", "bool_and(i > 0)"), A("bool_or", "bool_or(i > 5)"), A("median", "median(d)"),
        A("sum_empty", "sum(i) FILTER (WHERE id > 100)"), A("count_filter", "count(*) FILTER (WHERE i > 0)"), A("min_int_null", "min(i)"),
        A("sum_bigint_overflow", "sum(CAST(i AS BIGINT)) * 1000000000"), A("avg_double", "avg(d)"), A("corr", "corr(i, j)"), A("approx_free_mode", "mode(i)"),
    ];

    // Differences that are understood, as "<engine>:<case id>" to why. Each is a row of the support matrix, a diagnostic the linter already raises (DDB-305, the function is not covered),
    // or a note in docs/research/engine-differences/README.md. A case here must still differ: if it starts to agree, the test says so and the entry should go.
    private static readonly string DivisionByZero = "op.div: DuckDB 1.5 gives Infinity and NaN for a division by zero; the engines (rewritten to NULLIF) give NULL";
    private static readonly string Supplementary = "fn.length and docs: SQL Server counts and cuts UTF-16 code units, so a character outside the BMP (an emoji) is two";
    private static readonly string Undefined = "not covered by the support matrix (DDB-305 at validate); the engine has no such function or a different one";

    public static readonly IReadOnlyDictionary<string, string> Known = new Dictionary<string, string>
    {
        ["sqlserver:int_div"] = DivisionByZero, ["sqlserver:dec_div"] = DivisionByZero, ["sqlserver:div_double"] = DivisionByZero, ["sqlserver:div_zero_double"] = DivisionByZero, ["sqlserver:int_div_zero"] = DivisionByZero,
        ["postgres:int_div"] = DivisionByZero, ["postgres:dec_div"] = DivisionByZero, ["postgres:div_double"] = DivisionByZero, ["postgres:div_zero_double"] = DivisionByZero, ["postgres:int_div_zero"] = DivisionByZero,
        ["sqlserver:exp"] = "exp() of a very large number raises an overflow error on the engine; DuckDB gives Infinity",
        ["postgres:exp"] = "exp() of a very large number raises an overflow error on the engine; DuckDB gives Infinity",
        ["sqlserver:cast_double_varchar"] = "the text form of a double differs (1e+010, 10000000000, 10000000000.0): cast to DECIMAL first (docs)",
        ["postgres:cast_double_varchar"] = "the text form of a double differs (1e+010, 10000000000, 10000000000.0): cast to DECIMAL first (docs)",
        ["sqlserver:cast_ts_varchar"] = "CAST(datetime2 AS VARCHAR) always shows six fractional digits; DuckDB shows none when they are zero (docs)",
        ["sqlserver:cast_str_decimal_literal_int"] = "DuckDB rounds the text '1.5' to 2; the engines refuse it (an error, not a wrong answer)",
        ["postgres:cast_str_decimal_literal_int"] = "DuckDB rounds the text '1.5' to 2; the engines refuse it (an error, not a wrong answer)",
        ["postgres:try_cast_str_date"] = "fn.try_cast: PostgreSQL has no try-cast; a bad date raises (the rule covers numbers)",
        ["sqlserver:length"] = Supplementary, ["sqlserver:length_nonbmp"] = Supplementary, ["sqlserver:substr2"] = Supplementary, ["sqlserver:substr0"] = Supplementary, ["sqlserver:substr_from"] = Supplementary,
        ["sqlserver:left"] = Supplementary, ["sqlserver:right"] = Supplementary, ["sqlserver:reverse"] = Supplementary, ["sqlserver:ascii"] = "ASCII() is the first byte, DuckDB's ascii() is the code point (and '' is NULL, not 0); UNICODE() is the equivalent",
        ["sqlserver:upper_sharp_s"] = "str.case_mapping: SQL Server leaves 'ß' alone, DuckDB makes it 'ẞ'",
        ["postgres:upper"] = "str.case_mapping: under the C collation PostgreSQL's upper() changes ASCII only",
        ["sqlserver:eq_trailing"] = "str.eq: SQL Server ignores trailing spaces in comparisons; the string profile (trailing_space) says so",
        ["postgres:substr_neg"] = "fn.substring_negative: a negative start does not count from the end on PostgreSQL",
        ["sqlserver:substr_neg"] = "fn.substring_negative: a negative start does not count from the end on SQL Server",
        ["sqlserver:substr_neg_len"] = "a negative length is an error on the engines; DuckDB returns an empty string",
        ["postgres:substr_neg_len"] = "a negative length is an error on the engines; DuckDB returns an empty string",
        ["sqlserver:left_neg"] = "a negative count is an error on SQL Server; DuckDB drops that many characters from the end",
        ["postgres:date_diff_month"] = "fn.date_diff: PostgreSQL counts whole elapsed months, DuckDB counts month boundaries crossed",
        ["postgres:date_diff_year"] = "fn.date_diff: PostgreSQL counts whole elapsed years, DuckDB counts year boundaries crossed",
        ["sqlserver:dp_week"] = "fn.date_part_calendar: DATEPART(WEEK) is not the ISO week",
        ["sqlserver:dp_epoch"] = "fn.date_part_calendar: no epoch part",
        ["postgres:dp_epoch"] = "fn.date_part_calendar: not translated",
        ["postgres:string_agg"] = "fn.string_agg: polyglot writes LISTAGG for PostgreSQL", ["postgres:string_agg_sep"] = "fn.string_agg: polyglot writes LISTAGG for PostgreSQL", ["postgres:string_agg_filter"] = "fn.string_agg: polyglot writes LISTAGG for PostgreSQL",
        ["sqlserver:lpad"] = "fn.pad", ["sqlserver:rpad"] = "fn.pad", ["sqlserver:lpad_short"] = "fn.pad",
        ["sqlserver:contains"] = "fn.contains", ["postgres:contains"] = "fn.contains",
        ["sqlserver:corr"] = "fn.corr", ["sqlserver:approx_free_mode"] = "fn.mode", ["postgres:approx_free_mode"] = "fn.mode",
        ["sqlserver:split_part"] = Undefined, ["postgres:split_part"] = Undefined, ["sqlserver:week"] = Undefined, ["postgres:week"] = Undefined, ["postgres:last_day"] = Undefined,
    };

    [SkippableTheory]
    [InlineData("sqlserver")]
    [InlineData("postgres")]
    public async Task Scalar_expressions_and_aggregates_give_DuckDBs_answer_or_a_documented_difference(string name)
    {
        var engine = EngineEnv.Require(name);
        await engine.StartAsync();
        await using var _ = engine;
        using var probe = new EngineProbe(engine);
        await probe.CreateTableAsync();

        var report = new StringBuilder();
        var unexpected = new List<string>();
        var stale = new List<string>();
        foreach (var c in Cases)
        {
            var expected = probe.OnDuckDb(c.Sql);
            var (rendered, refused) = probe.Render(c.Sql);
            string status, detail = "";
            if (refused != null) { status = "refused"; detail = refused; }
            else
            {
                var actual = await probe.OnEngineAsync(rendered!);
                if (expected.Error != null) { status = actual.Error != null ? "both-error" : "duckdb-errors"; detail = $"duckdb: {expected.Error}; engine: {actual.Error ?? actual.Rows}"; }
                else if (actual.Error != null) { status = "engine-error"; detail = actual.Error; }
                else if (actual.Rows == expected.Rows) status = "same";
                else { status = "DIFFERENT"; detail = $"duckdb [{expected.Rows}]\n        engine [{actual.Rows}]\n        sql    {rendered!.Replace("\n", " ")}"; }
            }
            report.AppendLine($"{status,-13} {c.Id,-24} {c.Sql}\n{(detail.Length > 0 ? "        " + detail + "\n" : "")}");
            var bad = status is "DIFFERENT" or "engine-error" or "duckdb-errors" || (status == "refused" && detail.StartsWith("CRASH", StringComparison.Ordinal));
            var key = $"{name}:{c.Id}";
            if (bad && !Known.ContainsKey(key)) unexpected.Add($"{c.Id}: {status}");
            if (!bad && Known.ContainsKey(key) && status is "same") stale.Add(c.Id);
        }
        if (Environment.GetEnvironmentVariable("DDB_PROBE_OUT") is { Length: > 0 } path) File.AppendAllText($"{path}.{name}.txt", report.ToString());
        Assert.True(unexpected.Count == 0, $"{name}: {unexpected.Count} undocumented difference(s): {string.Join(", ", unexpected)}");
        Assert.True(stale.Count == 0, $"{name}: documented differences that are gone: {string.Join(", ", stale)}");
    }
}
