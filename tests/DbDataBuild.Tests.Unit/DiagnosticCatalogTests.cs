using DbDataBuild.Core;
using DbDataBuild.Models;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

public class DiagnosticCatalogTests
{
    [Fact]
    public void Codes_are_unique_well_formed_and_fully_described()
    {
        Assert.Equal(DiagnosticCatalog.All.Count, DiagnosticCatalog.All.Select(d => d.Code).Distinct().Count());
        foreach (var d in DiagnosticCatalog.All)
        {
            Assert.Matches(@"^DDB-[1-59]\d\d$", d.Code);
            Assert.False(string.IsNullOrWhiteSpace(d.Title));
            Assert.False(string.IsNullOrWhiteSpace(d.Supported));
            Assert.False(string.IsNullOrWhiteSpace(d.Fix));
            Assert.False(string.IsNullOrWhiteSpace(d.Explanation));
        }
    }

    // One fixture per code (DESIGN.md 15.5). DDB-108 is covered in ProjectValidatorTests, DDB-109 in ProjectConfigTests, DDB-900 in CliTests,
    // DDB-310..312 in CollationCheckerTests, DDB-301..308 in MatrixLinterTests (one fixture per matrix row plus the uncovered/parse cases).
    public static TheoryData<string, string> Fixtures => new()
    {
        { "DDB-101", "a: [1, 2\n" },
        { "DDB-102", "name: a\nname: b\n" },
        { "DDB-103", "a: &x 1\n" },
        { "DDB-104", ValidModel + "\nbogus: 1\n" },
        { "DDB-105", "name: marts.fct_orders\n" },
        { "DDB-106", ValidModel.Replace("type: incremental_by_unique_key", "type: nope") },
        { "DDB-107", ValidModel.Replace("name: marts.fct_orders", "name: marts.other") },
        { "DDB-214", ValidModel.Replace("  unique_key: [order_id]\n", "") },
        { "DDB-215", ValidModel.Replace("incremental_by_unique_key\n  unique_key: [order_id]", "incremental_by_time_range") },
        { "DDB-216", ValidModel.Replace("grain: [order_id]", "grain: [customer_id]") },
        { "DDB-217", ValidModel.Replace("grain: [order_id]", "grain: [order_id, ghost]") },
    };

    [Theory, MemberData(nameof(Fixtures))]
    public void Fixture_produces_code_with_location_and_fix(string code, string yaml)
    {
        var (_, diags) = Load(yaml);
        var d = diags.First(x => x.Code == code);
        Assert.Equal("models/marts/fct_orders.yml", d.Location.File);
        Assert.True(d.Location.Line >= 1);
        var text = DiagnosticFormatter.Format(d);
        Assert.Contains("Supported:", text);
        Assert.Contains("Fix:", text);
        Assert.Contains($"dbdatabuild explain {code}", text);
        Assert.DoesNotContain("   at ", text); // no stack trace
    }

    [Fact]
    public void Every_code_has_a_fixture_or_a_documented_other_test()
    {
        var covered = Fixtures.Select(f => (string)f[0]).Concat(["DDB-108", "DDB-109", "DDB-900", "DDB-301", "DDB-302", "DDB-303", "DDB-304", "DDB-305", "DDB-306", "DDB-307", "DDB-308", "DDB-310", "DDB-311", "DDB-312"]).ToHashSet();
        Assert.Equal(DiagnosticCatalog.All.Select(d => d.Code).ToHashSet(), covered);
    }
}
