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
        QueryDescriber.PreparePlanConnection(c);        // the same plan shape as the production path
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

    private static List<string> NamesOf(DuckDBConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DESCRIBE " + sql;
        using var r = cmd.ExecuteReader();
        var names = new List<string>();
        while (r.Read()) names.Add(r.GetString(0));
        return names;
    }

    private static List<string> ResultNames(DuckDBConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        return Enumerable.Range(0, r.FieldCount).Select(r.GetName).ToList();
    }

    private static string Lower(DuckDBConnection c, string sql) => PlanLowerer.Lower(PlanOf(c, sql), NamesOf(c, sql)).Sql;

    // the declared grains of the seeded tables: t is identified by id, u by (a, b)
    private static IReadOnlyList<string> Grain(string table) => table switch { "t" => ["id"], "u" => ["a", "b"], _ => [] };
    private static string LowerWithGrain(DuckDBConnection c, string sql) => PlanLowerer.Lower(PlanOf(c, sql), NamesOf(c, sql), Grain).Sql;

    private static List<(string Id, string Sql)> Corpus()
    {
        var diags = new List<Diagnostic>();
        var seq = (YamlSequence)StrictYamlReader.Read(File.ReadAllText(Path.Combine(RepoRoot(), "spike", "constructs.yml")), "constructs.yml", diags)!;
        return seq.Items.Cast<YamlMapping>().Select(m => (((YamlScalar)m.Get("id")!).Value, ((YamlScalar)m.Get("sql")!).Value)).ToList();
    }

    // what has no lowering yet: UNNEST, USING SAMPLE, nested constructors
    private static readonly string[] NotLowered = ["list_literal", "struct_literal", "unnest", "select_distinct_on", "sample_clause"];

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
            // output names are kept; a name DuckDB repeats (a join of two tables with a column b) gets a suffix, because a table cannot have two columns of one name
            var expectedNames = new List<string>();
            foreach (var n in ResultNames(c, sql)) { var name = n; var k = 1; while (expectedNames.Contains(name)) name = $"{n}_{++k}"; expectedNames.Add(name); }
            if (!expectedNames.SequenceEqual(ResultNames(c, lowered))) { different.Add($"{id}: column names {string.Join(",", expectedNames)} vs {string.Join(",", ResultNames(c, lowered))}"); continue; }
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

    [Theory]
    [InlineData("SELECT x FROM generate_series(1, 10) AS g(x)")]
    [InlineData("SELECT x FROM generate_series(0, 9, 3) AS g(x)")]
    [InlineData("SELECT x FROM generate_series(10, 1, -4) AS g(x)")]
    [InlineData("SELECT x FROM generate_series(5, 1) AS g(x)")]
    [InlineData("SELECT x FROM range(5) AS g(x)")]
    [InlineData("SELECT x FROM range(2, 12, 5) AS g(x)")]
    [InlineData("SELECT x FROM range(10, 0, -5) AS g(x)")]
    [InlineData("SELECT x FROM range(0) AS g(x)")]
    [InlineData("SELECT x * 2 AS y FROM generate_series(1, 4) AS g(x) WHERE x > 1")]
    [InlineData("SELECT g.x, t.id FROM generate_series(1, 3) AS g(x) JOIN t ON t.a = g.x")]
    [InlineData("SELECT * FROM generate_series(1, 3)")]
    public void Integer_series_lower_to_generate_series_and_return_the_same_rows(string source)
    {
        using var c = Open();
        var lowered = Lower(c, source);
        Assert.Contains("generate_series(", lowered);
        Assert.Equal(Rows(c, source, false), Rows(c, lowered, false));
        Assert.Equal(ResultNames(c, source), ResultNames(c, lowered));
    }

    [Fact]
    public void Range_ends_are_made_inclusive_and_the_column_keeps_DuckDBs_bigint_type()
    {
        using var c = Open();
        Assert.Equal("SELECT CAST(value AS BIGINT) AS x\nFROM generate_series(0, 4) AS series(value)", Lower(c, "SELECT x FROM range(5) AS g(x)"));
        Assert.Contains("generate_series(2, 11, 5)", Lower(c, "SELECT x FROM range(2, 12, 5) AS g(x)"));
    }

    [Theory]
    [InlineData("SELECT d FROM generate_series(DATE '2024-01-01', DATE '2024-01-05', INTERVAL 1 DAY) AS g(d)")]
    [InlineData("SELECT x FROM generate_series(1, 3) AS g(x) WHERE x > (SELECT min(a) FROM t)", false)]
    public void Series_that_have_no_equal_on_the_engines_are_refused(string source, bool refused = true)
    {
        using var c = Open();
        if (refused) Assert.Throws<LoweringException>(() => Lower(c, source));
        else Assert.Contains("generate_series", Lower(c, source));
    }

    [Fact]
    public void The_authors_output_names_survive_even_when_the_plan_has_no_projection_to_carry_them()
    {
        using var c = Open();
        const string source = "SELECT CASE WHEN a > 2 THEN 'big' ELSE 'small' END AS size, COUNT(*) AS n, AVG(a) AS mean FROM t GROUP BY ALL";
        var lowered = Lower(c, source);
        Assert.Equal(["size", "n", "mean"], ResultNames(c, lowered));
        Assert.Equal(ResultNames(c, source), ResultNames(c, lowered));
        // an unaliased expression keeps the name DuckDB gives it, which is explicit in the lowered text
        Assert.Contains("AS \"count_star()\"", Lower(c, "SELECT a, COUNT(*) FROM t GROUP BY ALL"));
    }

    [Fact]
    public void A_join_qualifies_columns_only_where_a_block_has_several_sources_and_keeps_the_author_of_each_name()
    {
        using var c = Open();
        var sql = Lower(c, "SELECT t.id, u.b AS ub, sum(t.x) AS sx FROM t JOIN u ON t.a = u.a WHERE t.b > 1 GROUP BY t.id, u.b HAVING sum(t.x) > 1 ORDER BY sx DESC LIMIT 3");
        Assert.Equal("SELECT t.id, u.b AS ub, sum(CAST(t.x AS BIGINT)) AS sx\nFROM t\nJOIN u\n  ON (t.a = u.a)\nWHERE (t.b > 1)\nGROUP BY t.id, u.b\nHAVING (sum(CAST(t.x AS BIGINT)) > 1)\nORDER BY sum(CAST(t.x AS BIGINT)) DESC NULLS LAST\nLIMIT 3", sql);
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

    [Theory]
    [InlineData("SELECT DISTINCT ON (a) a, b, x FROM t ORDER BY a, b DESC, id")]
    [InlineData("SELECT DISTINCT ON (a) id, b FROM t ORDER BY a, id")]
    [InlineData("SELECT DISTINCT ON (a) id, b FROM t ORDER BY a DESC, id DESC")]
    [InlineData("SELECT DISTINCT ON (a, b) id, s FROM t ORDER BY a, b, id")]
    [InlineData("SELECT DISTINCT ON (t.a) t.id, u.b FROM t JOIN u ON t.a = u.a ORDER BY t.a, t.id, u.a, u.b")]
    [InlineData("SELECT DISTINCT ON (a) id, b FROM t WHERE x > 1 ORDER BY a, b NULLS FIRST, id")]
    public void DISTINCT_ON_with_an_ordering_that_decides_the_row_lowers_to_row_number_and_keeps_the_same_rows(string source)
    {
        using var c = Open();
        var lowered = LowerWithGrain(c, source);
        Assert.Contains("row_number() OVER (PARTITION BY", lowered);
        Assert.Contains("= 1", lowered);
        Assert.DoesNotContain("DISTINCT ON", lowered);
        Assert.Equal(Rows(c, source, false), Rows(c, lowered, false));
    }

    [Fact]
    public void Hidden_trailing_columns_DuckDB_adds_for_its_own_use_are_not_part_of_the_lowered_output()
    {
        using var c = Open();
        const string source = "SELECT DISTINCT ON (a) a, id FROM t ORDER BY a, id";
        var lowered = LowerWithGrain(c, source);
        Assert.Equal(ResultNames(c, source), ResultNames(c, lowered));
        Assert.Equal(Rows(c, source, false), Rows(c, lowered, false));
    }

    [Fact]
    public void DISTINCT_ON_reads_as_a_partitioned_row_number_filtered_to_one()
    {
        using var c = Open();
        Assert.Equal("SELECT a, b\nFROM (\n  SELECT a, b, id, row_number() OVER (PARTITION BY a ORDER BY b DESC NULLS LAST, id NULLS LAST) AS rn\n  FROM t\n) AS s1\nWHERE (rn = 1)\nORDER BY a NULLS LAST, b DESC NULLS LAST, id NULLS LAST",
            LowerWithGrain(c, "SELECT DISTINCT ON (a) a, b FROM t ORDER BY a, b DESC, id"));
    }

    [Theory]
    [InlineData("SELECT DISTINCT ON (a) a, b FROM t", "ORDER BY")]
    [InlineData("SELECT DISTINCT ON (a) a, b FROM t ORDER BY a", "only names the ON columns")]
    [InlineData("SELECT DISTINCT ON (a) a, b FROM t ORDER BY a, b", "`id`")]
    [InlineData("SELECT DISTINCT ON (t.a) t.id, u.b FROM t JOIN u ON t.a = u.a ORDER BY t.a, t.id", "`u`")]
    [InlineData("SELECT DISTINCT ON (a) a, b FROM (SELECT a, b, id FROM t WHERE x > 0 LIMIT 5) q ORDER BY a, id", "derived table")]
    public void DISTINCT_ON_that_could_pick_among_ties_is_refused_and_says_what_to_add(string source, string mention)
    {
        using var c = Open();
        var ex = Assert.Throws<LoweringException>(() => LowerWithGrain(c, source));
        Assert.Contains(mention, ex.Message);
        Assert.Contains("DISTINCT ON", ex.Message);
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

    // subqueries: every one lowers to a query with the same rows (DuckDB's binder had decorrelated them; the lowerer writes them back as subqueries)
    public static TheoryData<string> Subqueries => new()
    {
        "SELECT id FROM t WHERE EXISTS (SELECT 1 FROM u WHERE u.a = t.a)",
        "SELECT id FROM t WHERE NOT EXISTS (SELECT 1 FROM u WHERE u.a = t.a)",
        "SELECT id FROM t WHERE EXISTS (SELECT 1 FROM u WHERE u.a = t.a AND u.b > t.b)",
        "SELECT id FROM t WHERE EXISTS (SELECT 1 FROM u WHERE u.a = t.a OR u.b = t.b)",
        "SELECT id FROM t WHERE EXISTS (SELECT 1 FROM u WHERE u.b > t.a * 10)",
        "SELECT id FROM t WHERE EXISTS (SELECT 1 FROM u)",
        "SELECT id FROM t WHERE NOT EXISTS (SELECT 1 FROM u WHERE u.a > 100)",
        "SELECT id FROM t WHERE a IN (SELECT a FROM u)",
        "SELECT id FROM t WHERE a NOT IN (SELECT a FROM u)",
        "SELECT id FROM t WHERE a NOT IN (SELECT a FROM u WHERE a IS NOT NULL)",
        "SELECT id FROM t WHERE a IN (SELECT u.a FROM u WHERE u.b > t.b)",
        "SELECT id FROM t WHERE a NOT IN (SELECT u.a FROM u WHERE u.b > t.b)",
        "SELECT id FROM t WHERE a IN (SELECT a FROM u WHERE b > 150)",
        "SELECT id, EXISTS (SELECT 1 FROM u WHERE u.a = t.a) AS has_match FROM t",
        "SELECT id, a IN (SELECT a FROM u) AS in_u FROM t",
        "SELECT id, CASE WHEN EXISTS (SELECT 1 FROM u WHERE u.a = t.a) THEN 'y' ELSE 'n' END AS m FROM t",
        "SELECT id, (SELECT max(u.b) FROM u WHERE u.a = t.a) AS m FROM t",
        "SELECT id, (SELECT count(*) FROM u WHERE u.a = t.a) AS c FROM t",
        "SELECT id, (SELECT sum(u.b) FROM u WHERE u.a = t.a) AS s FROM t",
        "SELECT id, (SELECT u.b FROM u WHERE u.a = t.a AND u.b > 250) AS only_one FROM t",
        "SELECT id, (SELECT max(b) FROM u) AS m FROM t",
        "SELECT id FROM t WHERE b > (SELECT avg(u.b) / 100 FROM u WHERE u.a = t.a)",
        "SELECT id FROM t WHERE a > (SELECT min(a) FROM u)",
        "SELECT id FROM t WHERE (SELECT count(*) FROM u WHERE u.a = t.a) = 0",
        "SELECT id FROM t WHERE (SELECT u.b FROM u WHERE u.a = t.a ORDER BY u.b DESC LIMIT 1) > 100",
        "SELECT id FROM t WHERE EXISTS (SELECT 1 FROM u WHERE u.a = t.a AND EXISTS (SELECT 1 FROM u u2 WHERE u2.b = u.b AND u2.a = t.a))",
        "SELECT id FROM t WHERE EXISTS (SELECT 1 FROM u WHERE u.a = t.a AND u.b IN (SELECT b FROM u u3 WHERE u3.a = t.a))",
        "SELECT id FROM t WHERE EXISTS (SELECT 1 FROM (SELECT a, b FROM u WHERE b > 100) v WHERE v.a = t.a)",
        "SELECT id FROM t WHERE EXISTS (SELECT 1 FROM u WHERE u.a = t.a GROUP BY u.a HAVING count(*) > 0)",
        "SELECT a, count(*) AS n FROM t GROUP BY a HAVING EXISTS (SELECT 1 FROM u WHERE u.a = t.a)",
        "SELECT t.id, x.b FROM t, LATERAL (SELECT u.b FROM u WHERE u.a = t.a) x",
        "SELECT t.id, x.b FROM t LEFT JOIN LATERAL (SELECT u.b FROM u WHERE u.a = t.a) x ON TRUE",
        "SELECT t.id, x.m FROM t, LATERAL (SELECT max(u.b) AS m FROM u WHERE u.a = t.a) x",
        "SELECT t.id FROM t JOIN u ON t.a = u.a WHERE EXISTS (SELECT 1 FROM u u2 WHERE u2.b > u.b AND u2.a = t.a)",
        "SELECT id FROM t WHERE id IN (SELECT id FROM t t2 WHERE t2.a = t.a AND t2.id <> t.id)",
        "WITH big AS (SELECT a FROM u WHERE b > 100) SELECT id FROM t WHERE EXISTS (SELECT 1 FROM big WHERE big.a = t.a)",
        "SELECT id FROM t WHERE s IN (SELECT s FROM t t2 WHERE t2.d > t.d)",
        "SELECT t.id FROM t JOIN u ON t.a = u.a ORDER BY (SELECT count(*) FROM u u2 WHERE u2.a = t.a), t.id",
        "SELECT t.id FROM t JOIN u ON t.a = u.a AND EXISTS (SELECT 1 FROM u u2 WHERE u2.b > u.b)",
        "SELECT t.id, u.b FROM t JOIN u ON t.a = u.a WHERE u.b > (SELECT avg(u2.b) FROM u u2 WHERE u2.a = t.a)",
        "SELECT a, (SELECT count(*) FROM u WHERE u.a = x.a) AS c FROM (SELECT DISTINCT a FROM t) x",
        "SELECT id FROM t WHERE a + 1 IN (SELECT a FROM u WHERE u.b > t.b)",
        "SELECT sum((SELECT count(*) FROM u WHERE u.a = t.a)) AS s FROM t",
        "SELECT id FROM t WHERE EXISTS (SELECT DISTINCT u.b FROM u WHERE u.a = t.a)",
        "SELECT id, (SELECT u.b FROM u WHERE u.a = t.a ORDER BY u.b LIMIT 1) AS first_b FROM t",
        "SELECT id FROM t WHERE (SELECT count(*) FROM u WHERE u.a = t.a) > (SELECT count(*) FROM u WHERE u.b = t.b)",
        "SELECT id, (SELECT max(b) FROM u WHERE u.a = t.a) + (SELECT min(b) FROM u WHERE u.a = t.a) AS span FROM t",
        "SELECT id FROM t WHERE EXISTS (SELECT 1 FROM u WHERE u.a = t.a AND u.b > (SELECT avg(b) FROM u u2 WHERE u2.a = t.a))",
        "SELECT id FROM t WHERE NOT EXISTS (SELECT 1 FROM u)",
        "SELECT id FROM t WHERE a = (SELECT max(a) FROM u)",
        "SELECT id, row_number() OVER (ORDER BY (SELECT count(*) FROM u WHERE u.a = t.a), id) AS r FROM t",
        "SELECT id FROM t WHERE (SELECT count(*) FROM u WHERE u.a = t.a) BETWEEN 1 AND 2",
        "SELECT id FROM (SELECT id, a FROM t ORDER BY id LIMIT 5) q WHERE EXISTS (SELECT 1 FROM u WHERE u.a = q.a)",
    };

    [Theory, MemberData(nameof(Subqueries))]
    public void Subqueries_lower_to_subqueries_with_the_same_rows_as_the_original(string source)
    {
        using var c = Open();
        var lowered = Lower(c, source);
        Assert.Equal(Rows(c, source, false), Rows(c, lowered, false));
        Assert.Contains("SELECT", lowered);
    }

    [Fact]
    public void Correlated_subqueries_read_like_the_original_with_the_outer_column_qualified()
    {
        using var c = Open();
        Assert.Equal("SELECT id\nFROM t\nWHERE EXISTS (\n  SELECT 1\n  FROM u\n  WHERE (u.a = t.a)\n)", Lower(c, "SELECT id FROM t WHERE EXISTS (SELECT 1 FROM u WHERE u.a = t.a)"));
        Assert.Equal("SELECT id\nFROM t\nWHERE (NOT EXISTS (\n  SELECT 1\n  FROM u\n  WHERE (u.a = t.a)\n))", Lower(c, "SELECT id FROM t WHERE NOT EXISTS (SELECT 1 FROM u WHERE u.a = t.a)"));
        Assert.Equal("SELECT id\nFROM t\nWHERE (a IN (\n  SELECT a\n  FROM u\n))", Lower(c, "SELECT id FROM t WHERE a IN (SELECT a FROM u)"));
        var scalar = Lower(c, "SELECT id, (SELECT max(u.b) FROM u WHERE u.a = t.a) AS m FROM t");
        Assert.Contains("max(u.b)", scalar);
        Assert.Contains("(u.a = t.a)", scalar);
        Assert.DoesNotContain("GROUP BY", scalar);          // the group on the correlated value is gone
    }

    [Fact]
    public void An_unqualified_name_inside_a_subquery_never_stands_for_an_outer_column()
    {
        using var c = Open();
        // t and u both have columns a and b: inside the subquery every column is written with its source
        var sql = Lower(c, "SELECT id FROM t WHERE EXISTS (SELECT 1 FROM u WHERE u.a = t.a AND u.b = t.b)");
        Assert.DoesNotContain("WHERE (a =", sql);
        Assert.Contains("u.a = t.a", sql);
        Assert.Contains("u.b = t.b", sql);
    }

    [Theory]
    [InlineData("SELECT * FROM unnest([1, 2])", "table function")]
    [InlineData("SELECT a FROM t USING SAMPLE 3 ROWS", "SAMPLE")]
    [InlineData("SELECT DISTINCT ON (a) a, b FROM t ORDER BY a, b", "DISTINCT ON")]
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
        Assert.Equal("binder", ex.Kind);
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
