using System.Text.Json;
using DbDataBuild.Core;
using DbDataBuild.Lowering;
using DbDataBuild.Models.Yaml;
using DbDataBuild.Targets.DuckDb;
using DuckDB.NET.Data;
using static DbDataBuild.Tests.Unit.PolyglotBindingTests;

namespace DbDataBuild.Tests.Unit;

/// <summary>The plan lowerer (docs/research/duckdb-plan-lowering), differentially tested against DuckDB itself on the spike corpus and the seeded edge-case data.</summary>
public class PlanLowererTests
{
    private static DuckDBConnection Open()
    {
        var c = new DuckDBConnection("DataSource=:memory:");
        c.Open();
        Exec(c, File.ReadAllText(Path.Combine(RepoRoot(), "spike", "seed.duckdb.sql")));
        return c;
    }

    private static void Exec(DuckDBConnection c, string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }

    private static string PlanOf(DuckDBConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = QueryDescriber.PlanStatement(sql);
        return (string)cmd.ExecuteScalar()!;
    }

    private static List<string> Rows(DuckDBConnection c, string sql, bool ordered)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = ordered ? sql : $"SELECT * FROM ({sql}) ORDER BY ALL";
        using var r = cmd.ExecuteReader();
        var rows = new List<string>();
        while (r.Read()) rows.Add(string.Join("|", Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? "∅" : Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture))));
        return rows;
    }

    private static string Lower(DuckDBConnection c, string sql) => PlanLowerer.Lower(PlanOf(c, sql)).Sql;

    private static List<(string Id, string Sql)> Corpus()
    {
        var diags = new List<Diagnostic>();
        var seq = (YamlSequence)StrictYamlReader.Read(File.ReadAllText(Path.Combine(RepoRoot(), "spike", "constructs.yml")), "constructs.yml", diags)!;
        return seq.Items.Cast<YamlMapping>().Select(m => (((YamlScalar)m.Get("id")!).Value, ((YamlScalar)m.Get("sql")!).Value)).ToList();
    }

    // what has no lowering yet: correlated subqueries, UNNEST, USING SAMPLE, DISTINCT ON, nested constructors
    private static readonly string[] NotLowered = ["list_literal", "struct_literal", "unnest", "select_distinct_on", "sample_clause", "lateral_join", "exists_subquery", "in_subquery"];

    [Fact]
    public void Every_corpus_construct_lowers_to_an_equal_query_or_is_refused_with_a_reason()
    {
        using var c = Open();
        var refused = new List<string>();
        var different = new List<string>();
        var equal = 0;
        foreach (var (id, sql) in Corpus().Where(x => x.Id != "current_ts"))
        {
            string lowered;
            try { lowered = Lower(c, sql); }
            catch (LoweringException ex) { Assert.False(string.IsNullOrWhiteSpace(ex.Message)); refused.Add(id); continue; }
            List<string>? a = null, b = null; string? ea = null, eb = null;
            try { a = Rows(c, sql, false); } catch (DuckDBException ex) { ea = ex.Message; }
            try { b = Rows(c, lowered, false); } catch (DuckDBException ex) { eb = ex.Message; }
            if (ea != null || eb != null) { if (ea == null || eb == null) different.Add($"{id}: one side failed ({ea ?? eb})"); else equal++; continue; }
            if (a!.SequenceEqual(b!)) equal++; else different.Add($"{id}: {string.Join(",", a!.Take(3))} vs {string.Join(",", b!.Take(3))}\n  {lowered}");
        }
        Assert.Empty(different);
        Assert.Equal(NotLowered.Order(), refused.Order());
        Assert.True(equal >= 83, $"only {equal} constructs lowered equal");
    }

    [Theory]
    [InlineData("SELECT a / b AS q FROM t", "SELECT (CAST(a AS DOUBLE) / CAST(b AS DOUBLE)) AS q\nFROM t")]
    [InlineData("SELECT AVG(a) AS m FROM t", "SELECT avg(CAST(a AS DOUBLE)) AS m\nFROM t")]
    [InlineData("SELECT DATE_TRUNC('month', d) AS m FROM t", "SELECT CAST(date_trunc('month', d) AS TIMESTAMP) AS m\nFROM t")]
    [InlineData("SELECT d + INTERVAL 3 DAY AS x FROM t", "SELECT CAST((d + INTERVAL 3 DAY) AS TIMESTAMP) AS x\nFROM t")]
    [InlineData("SELECT a, COUNT(*) AS n FROM t GROUP BY ALL", "SELECT a, count(*) AS n\nFROM t\nGROUP BY a")]
    [InlineData("SELECT a FROM t WHERE s LIKE 'a%' AND a IN (1, 2)", "SELECT a\nFROM t\nWHERE (s LIKE 'a%') AND (a IN (1, 2))")]
    [InlineData("SELECT id, row_number() OVER (PARTITION BY a ORDER BY id) AS rn FROM t", "SELECT id, row_number() OVER (PARTITION BY a ORDER BY id NULLS LAST) AS rn\nFROM t")]
    [InlineData("SELECT a FROM t UNION ALL SELECT a FROM u", "SELECT a\nFROM t\nUNION ALL\nSELECT a\nFROM u")]
    [InlineData("SELECT * FROM u", "SELECT a, b\nFROM u")]
    public void Typical_queries_lower_to_the_expected_text(string source, string expected)
    {
        using var c = Open();
        Assert.Equal(expected, Lower(c, source));
    }

    [Fact]
    public void A_join_qualifies_columns_only_where_a_block_has_several_sources_and_keeps_the_author_of_each_name()
    {
        using var c = Open();
        var sql = Lower(c, "SELECT t.id, u.b AS ub, sum(t.x) AS sx FROM t JOIN u ON t.a = u.a WHERE t.b > 1 GROUP BY t.id, u.b HAVING sum(t.x) > 1 ORDER BY sx DESC LIMIT 3");
        Assert.Equal("SELECT t.id, u.b AS ub, sum(t.x) AS sx\nFROM t\nJOIN u\n  ON (t.a = u.a)\nWHERE (t.b > 1)\nGROUP BY t.id, u.b\nHAVING (sum(t.x) > 1)\nORDER BY sum(t.x) DESC NULLS LAST\nLIMIT 3", sql);
        Assert.Equal(Rows(c, "SELECT t.id, u.b AS ub, sum(t.x) AS sx FROM t JOIN u ON t.a = u.a WHERE t.b > 1 GROUP BY t.id, u.b HAVING sum(t.x) > 1 ORDER BY sx DESC, t.id LIMIT 3", true).Count, Rows(c, sql, true).Count);
    }

    [Fact]
    public void Null_ordering_is_always_written_so_engines_cannot_disagree_about_row_order()
    {
        using var c = Open();
        var asc = Lower(c, "SELECT a FROM t ORDER BY a");
        var desc = Lower(c, "SELECT a FROM t ORDER BY a DESC");
        var first = Lower(c, "SELECT a FROM t ORDER BY a NULLS FIRST");
        Assert.EndsWith("ORDER BY a NULLS LAST", asc);
        Assert.EndsWith("ORDER BY a DESC NULLS LAST", desc);
        Assert.EndsWith("ORDER BY a NULLS FIRST", first);
        // and the order a query returns rows in is the same after lowering, row by row
        foreach (var (src, low) in new[] { ("SELECT id, a FROM t ORDER BY a, id", Lower(c, "SELECT id, a FROM t ORDER BY a, id")), ("SELECT id, a FROM t ORDER BY a DESC, id LIMIT 4", Lower(c, "SELECT id, a FROM t ORDER BY a DESC, id LIMIT 4")) })
            Assert.Equal(Rows(c, src, true), Rows(c, low, true));
    }

    [Theory]
    [InlineData("SELECT id, sum(x) OVER (ORDER BY a ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS s FROM t ORDER BY id")]
    [InlineData("SELECT id, sum(x) OVER (ORDER BY a RANGE BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS s FROM t ORDER BY id")]
    [InlineData("SELECT id, sum(x) OVER (ORDER BY a) AS s FROM t ORDER BY id")]
    [InlineData("SELECT id, sum(x) OVER (ORDER BY a ROWS BETWEEN 1 PRECEDING AND 1 FOLLOWING) AS s FROM t ORDER BY id")]
    [InlineData("SELECT id, sum(x) OVER (PARTITION BY a) AS s FROM t ORDER BY id")]
    [InlineData("SELECT id, lag(x, 2, 0) OVER (ORDER BY id) AS p FROM t ORDER BY id")]
    public void Window_frames_keep_their_meaning_row_by_row_even_when_rows_tie(string source)
    {
        using var c = Open();
        Assert.Equal(Rows(c, source, true), Rows(c, Lower(c, source), true));
    }

    [Fact]
    public void ROWS_and_RANGE_frames_are_not_confused()
    {
        using var c = Open();
        Assert.Contains("ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW", Lower(c, "SELECT sum(x) OVER (ORDER BY a ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS s FROM t"));
        Assert.DoesNotContain("ROWS", Lower(c, "SELECT sum(x) OVER (ORDER BY a) AS s FROM t"));      // the default frame is RANGE and is not written
    }

    [Fact]
    public void Literals_the_binder_turned_into_casts_come_back_as_literals()
    {
        using var c = Open();
        Assert.Equal("SELECT TRUE AS x", Lower(c, "SELECT TRUE AS x"));
        Assert.Equal("SELECT DATE '2024-01-15' AS x", Lower(c, "SELECT DATE '2024-01-15' AS x"));
        Assert.Contains("INTERVAL 3 DAY", Lower(c, "SELECT d + INTERVAL 3 DAY FROM t"));
        Assert.Equal("SELECT a\nFROM t\nWHERE (s = 'it''s')", Lower(c, "SELECT a FROM t WHERE s = 'it''s'"));
    }

    [Fact]
    public void A_cte_is_kept_as_a_cte_in_dependency_order()
    {
        using var c = Open();
        var sql = Lower(c, "WITH c1 AS MATERIALIZED (SELECT a, b FROM t WHERE a > 1), c2 AS MATERIALIZED (SELECT a FROM c1 WHERE b > 1) SELECT a FROM c2 UNION ALL SELECT a FROM c2");
        Assert.True(sql.IndexOf("c1 AS", StringComparison.Ordinal) < sql.IndexOf("c2 AS", StringComparison.Ordinal), sql);
        Assert.Equal(Rows(c, "WITH c1 AS MATERIALIZED (SELECT a, b FROM t WHERE a > 1), c2 AS MATERIALIZED (SELECT a FROM c1 WHERE b > 1) SELECT a FROM c2 UNION ALL SELECT a FROM c2", false), Rows(c, sql, false));
    }

    [Fact]
    public void Output_columns_and_their_resolved_types_come_from_the_plan()
    {
        using var c = Open();
        var q = PlanLowerer.Lower(PlanOf(c, "SELECT a / b AS ratio, avg(a) OVER () AS m, d, CAST(s AS VARCHAR(20)) AS name FROM t"));
        Assert.Equal([("ratio", "DOUBLE"), ("m", "DOUBLE"), ("d", "DATE"), ("name", "VARCHAR")], q.Columns.Select(x => (x.Name, x.DuckDbType)));
        Assert.Contains("avg-double", q.Rules);
        var sums = PlanLowerer.Lower(PlanOf(c, "SELECT sum(a) AS s FROM t"));
        Assert.Equal("HUGEINT", sums.Columns[0].DuckDbType);         // the type an engine's SUM over integers does not have
    }

    [Theory]
    [InlineData("SELECT * FROM unnest([1, 2])", "table function")]
    [InlineData("SELECT a FROM t USING SAMPLE 3 ROWS", "SAMPLE")]
    [InlineData("SELECT DISTINCT ON (a) a, b FROM t", "DISTINCT ON")]
    [InlineData("SELECT a FROM t WHERE EXISTS (SELECT 1 FROM u WHERE u.a = t.a)", "DELIM_JOIN")]
    [InlineData("SELECT [1, 2] AS l", "list_value")]
    [InlineData("SELECT a FROM t LIMIT 10 PERCENT", "percentage")]
    public void What_has_no_lowering_is_refused_by_name_never_guessed(string source, string mention)
    {
        using var c = Open();
        var ex = Assert.Throws<LoweringException>(() => Lower(c, source));
        Assert.Contains(mention, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_query_DuckDB_cannot_bind_is_reported_as_such()
    {
        using var c = Open();
        var json = "{\"error\":true,\"error_type\":\"binder\",\"error_message\":\"Table with name nope does not exist!\"}";
        var ex = Assert.Throws<LoweringException>(() => PlanLowerer.Lower(json));
        Assert.Contains("Table with name nope does not exist", ex.Message);
    }

    [Fact]
    public void The_production_path_builds_the_plan_against_an_empty_upstream_schema()
    {
        var upstream = new[] { new DuckTable("staging", "orders", [new DuckColumn("order_id", "BIGINT", false), new DuckColumn("amount", "DECIMAL(14, 2)", true)]) };
        var (json, error) = QueryDescriber.SerializePlan(upstream, "SELECT o.order_id, o.amount / 3 AS third FROM staging.orders o WHERE o.amount > 0");
        Assert.Null(error);
        var q = PlanLowerer.Lower(json!);
        Assert.Equal("SELECT order_id, (CAST(amount AS DOUBLE) / 3) AS third\nFROM staging.orders\nWHERE (amount > 0)", q.Sql);
        Assert.Equal([("order_id", "BIGINT"), ("third", "DOUBLE")], q.Columns.Select(x => (x.Name, x.DuckDbType)));
        Assert.StartsWith("v", QueryDescriber.DuckDbVersion());
        // DuckDB reports a query it cannot bind inside the JSON, and the lowerer turns that into a refusal that carries its message
        var (badJson, _) = QueryDescriber.SerializePlan(upstream, "SELECT nope FROM staging.orders");
        Assert.Contains("nope", Assert.Throws<LoweringException>(() => PlanLowerer.Lower(badJson!)).Message);
        // the text is one string literal: a second statement in it is not run, and it cannot end the literal
        var (hostileJson, _) = QueryDescriber.SerializePlan(upstream, "SELECT 1; DROP TABLE staging.orders");
        Assert.Throws<LoweringException>(() => PlanLowerer.Lower(hostileJson!));
        var (quoted, _) = QueryDescriber.SerializePlan(upstream, "SELECT 'x'' || (SELECT 1)' AS s");
        Assert.Contains("x'' || (SELECT 1)", PlanLowerer.Lower(quoted!).Sql);
    }
}
