using DbDataBuild.Lowering;
using DbDataBuild.Sql;
using DbDataBuild.Targets.Rules;

namespace DbDataBuild.Tests.Unit;

/// <summary>The step between the lowered query and the transpile (DESIGN.md 7.6.1). Behavior on real engines is in the conformance suite.</summary>
public class TargetRulesTests
{
    private static string Transpiled(string sql, string target) => Polyglot.TranspileOne(TargetRules.Apply(sql, target).Sql, Dialects.Canonical, target == "sqlserver" ? "tsql" : "postgres").Sql!;

    [Fact]
    public void A_query_no_rule_applies_to_is_returned_byte_for_byte()
    {
        const string sql = "SELECT  a,\n  b\nFROM t   -- kept\nWHERE a > 1";
        foreach (var target in new[] { "sqlserver", "postgres", "fabric", "duckdb" })
        {
            var r = TargetRules.Apply(sql, target);
            Assert.Equal(sql, r.Sql);
            Assert.Empty(r.Rules);
        }
    }

    [Fact]
    public void Length_counts_trailing_spaces_on_SQL_Server_only()
    {
        var r = TargetRules.Apply("SELECT length(s) AS n FROM t", "sqlserver");
        Assert.Equal([TargetRules.LengthKeepsTrailingSpaces], r.Rules);
        Assert.Equal("SELECT LEN(s + 'x') - 1 AS n FROM t", Transpiled("SELECT length(s) AS n FROM t", "sqlserver"));
        Assert.Empty(TargetRules.Apply("SELECT length(s) AS n FROM t", "postgres").Rules);
        Assert.Equal([TargetRules.LengthKeepsTrailingSpaces], TargetRules.Apply("SELECT length(s) AS n FROM t", "fabric").Rules);
    }

    [Theory]
    [InlineData("SELECT round(CAST(x AS DOUBLE), 2) AS r FROM t", "SIGN(x * 100.0)")]
    [InlineData("SELECT round(CAST(x AS DOUBLE)) AS r FROM t", "SIGN(x) * FLOOR(ABS(x) + 0.5)")]
    [InlineData("SELECT round(CAST(x AS DOUBLE), -2) AS r FROM t", "SIGN(x / 100.0)")]
    public void Round_of_a_double_scales_rounds_half_away_from_zero_and_unscales(string sql, string expected)
    {
        foreach (var target in new[] { "sqlserver", "postgres" })
        {
            Assert.Equal([TargetRules.RoundDouble], TargetRules.Apply(sql, target).Rules);
            Assert.Contains(expected, TargetRules.Apply(sql, target).Sql.ToUpperInvariant().Replace("  ", " ").Replace("\n", "").Replace("( ", "(").Replace(" )", ")"), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("SELECT round(x, 2) AS r FROM t")]                                       // not marked as a double: a decimal rounds correctly on every engine
    [InlineData("SELECT round(CAST(x AS DOUBLE), d) AS r FROM t")]                        // digits not a constant
    [InlineData("SELECT round(CAST(x AS DOUBLE), 40) AS r FROM t")]                       // beyond what a double holds
    public void Round_is_left_alone_unless_it_is_marked_and_has_constant_digits(string sql)
    {
        Assert.Empty(TargetRules.Apply(sql, "sqlserver").Rules);
        Assert.Empty(TargetRules.Apply(sql, "postgres").Rules);
    }

    [Fact]
    public void Try_cast_of_a_string_treats_blank_as_NULL_on_SQL_Server_and_tests_the_text_on_PostgreSQL()
    {
        const string sql = "SELECT TRY_CAST(CAST(s AS VARCHAR) AS INTEGER) AS i FROM t";
        Assert.Equal("SELECT TRY_CAST(NULLIF(TRIM(s), '') AS INTEGER) AS i FROM t", Transpiled(sql, "sqlserver"));
        var pg = Transpiled(sql, "postgres");
        Assert.Contains("CASE WHEN s ~ '^\\s*[+-]?[0-9]+\\s*$' THEN CASE WHEN CAST(s AS DECIMAL(30, 0)) BETWEEN -2147483648 AND 2147483647 THEN CAST(s AS INT) END END", pg);
        Assert.DoesNotContain("TRY_CAST", pg);
        Assert.Equal([TargetRules.TryCastParse], TargetRules.Apply(sql, "postgres").Rules);
    }

    [Fact]
    public void Try_cast_to_decimal_checks_the_range_the_target_column_holds()
    {
        var pg = Transpiled("SELECT TRY_CAST(CAST(s AS VARCHAR) AS DECIMAL(10, 2)) AS d FROM t", "postgres");
        Assert.Contains("ABS(CAST(s AS DECIMAL(38, 2))) < 100000000", pg);
        Assert.Contains("THEN CAST(s AS DECIMAL(10, 2))", pg);
    }

    [Fact]
    public void Try_cast_to_a_date_is_not_emulated_on_PostgreSQL_because_a_date_cannot_be_tested_without_raising()
    {
        const string sql = "SELECT TRY_CAST(CAST(s AS VARCHAR) AS DATE) AS d FROM t";
        Assert.Empty(TargetRules.Apply(sql, "postgres").Rules);
        Assert.Equal([TargetRules.TryCastParse], TargetRules.Apply(sql, "sqlserver").Rules);
    }

    [Fact]
    public void Try_cast_that_is_not_marked_as_a_string_parse_is_left_alone()
    {
        Assert.Empty(TargetRules.Apply("SELECT TRY_CAST(x AS INTEGER) AS i FROM t", "sqlserver").Rules);
        Assert.Empty(TargetRules.Apply("SELECT TRY_CAST(CAST(s AS VARCHAR) AS VARCHAR) AS i FROM t", "sqlserver").Rules);
    }

    [Fact]
    public void Rules_apply_inside_subqueries_and_other_clauses()
    {
        var r = TargetRules.Apply("SELECT a FROM (SELECT a, length(s) AS n FROM t) q WHERE length(a) > 2 ORDER BY length(a)", "sqlserver");
        var t = Transpiled("SELECT a FROM (SELECT a, length(s) AS n FROM t) q WHERE length(a) > 2 ORDER BY length(a)", "sqlserver"); Assert.DoesNotContain("LENGTH(", t); Assert.Contains("LEN(s + \'x\') - 1", t); Assert.Contains("WHERE LEN(a + \'x\') - 1 > 2", t);
        Assert.Equal([TargetRules.LengthKeepsTrailingSpaces], r.Rules);
    }

    [Fact]
    public void The_lowerer_marks_exactly_the_expressions_the_rules_need()
    {
        using var c = new DuckDB.NET.Data.DuckDBConnection("DataSource=:memory:");
        c.Open();
        using (var cmd = c.CreateCommand()) { cmd.CommandText = "CREATE TABLE t (s VARCHAR, n INTEGER, x DOUBLE, d DECIMAL(10,2))"; cmd.ExecuteNonQuery(); }
        string Lower(string sql)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = DbDataBuild.Targets.DuckDb.QueryDescriber.PlanStatement(sql);
            return PlanLowerer.Lower((string)cmd.ExecuteScalar()!).Sql;
        }
        Assert.Contains("round(CAST(x AS DOUBLE), 2)", Lower("SELECT round(x, 2) AS r FROM t"));
        Assert.Contains("round(d, 2)", Lower("SELECT round(d, 2) AS r FROM t"));
        Assert.Contains("TRY_CAST(CAST(s AS VARCHAR) AS INTEGER)", Lower("SELECT TRY_CAST(s AS INTEGER) AS i FROM t"));
        Assert.Contains("TRY_CAST(n AS SMALLINT)", Lower("SELECT TRY_CAST(n AS SMALLINT) AS i FROM t"));
    }
}
