using DbDataBuild.Core;
using DbDataBuild.Models;
using static DbDataBuild.Tests.Unit.SchemaConformanceTests;

namespace DbDataBuild.Tests.Unit;

public class SourceDescriptorTests
{
    public sealed record Case(string Name, string Yaml, bool SchemaValid, params string[] Codes)
    {
        public override string ToString() => Name;
    }

    private const string Cols = "columns:\n  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: amount, type: \"DECIMAL(14, 2)\"}\n";

    public static readonly Case[] Cases =
    [
        new("minimal", "name: staging.orders\n" + Cols, true),
        new("with grain", "name: staging.orders\ngrain: [order_id]\n" + Cols, true),
        new("composite grain", "name: staging.orders\ngrain: [order_id, amount]\n" + Cols, true),
        new("missing name", Cols, false, "DDB-105"),
        new("missing columns", "name: staging.orders\n", false, "DDB-105"),
        new("empty columns", "name: staging.orders\ncolumns: []\n", false, "DDB-106"),
        new("unknown key", "name: staging.orders\nkind: view\n" + Cols, false, "DDB-104"),
        new("empty grain", "name: staging.orders\ngrain: []\n" + Cols, false, "DDB-106"),
        new("duplicate grain column", "name: staging.orders\ngrain: [order_id, order_id]\n" + Cols, false, "DDB-106"),
        new("column without type", "name: staging.orders\ncolumns:\n  - name: a\n", false, "DDB-105"),
        new("nullable maybe", "name: staging.orders\ncolumns:\n  - {name: a, type: INT, nullable: maybe}\n", false, "DDB-106"),
        new("name does not match path", "name: staging.other\n" + Cols, true, "DDB-107"),
        new("grain names an undeclared column", "name: staging.orders\ngrain: [ghost]\n" + Cols, true, "DDB-217"),
        new("duplicate column names", "name: staging.orders\ncolumns:\n  - {name: a, type: INT}\n  - {name: A, type: INT}\n", true, "DDB-102"),
    ];

    public static TheoryData<Case> Data
    {
        get { var d = new TheoryData<Case>(); foreach (var c in Cases) d.Add(c); return d; }
    }

    [Theory, MemberData(nameof(Data))]
    public void Source_schema_and_loader_agree(Case c)
    {
        Assert.Equal(c.SchemaValid, SchemaAccepts(LoadSchema("source"), c.Yaml));
        var diags = new List<Diagnostic>();
        var d = SourceDescriptorLoader.Load(c.Yaml, "sources/staging/orders.yml", "staging.orders", diags);
        if (c.Codes.Length == 0)
        {
            Assert.Empty(diags.Select(DiagnosticFormatter.Format));
            Assert.NotNull(d);
        }
        else
        {
            foreach (var code in c.Codes) Assert.Contains(diags, x => x.Code == code);
            Assert.Null(d);
        }
    }

    [Fact]
    public void Descriptor_is_read_completely()
    {
        var d = SourceDescriptorLoader.Load("name: staging.orders\ngrain: [order_id]\n" + Cols, "f.yml", null, [])!;
        Assert.Equal("staging.orders", d.Name);
        Assert.Equal(["order_id"], d.Grain);
        Assert.Equal(["order_id", "amount"], d.Columns.Select(c => c.Name));
        Assert.False(d.Columns[0].Nullable);
        Assert.Equal("DECIMAL(14, 2)", d.Columns[1].Type);
    }

    [Fact]
    public void Project_validator_loads_sources_by_path_and_reports_problems()
    {
        var dir = TestSupport.NewProjectDir();
        Directory.CreateDirectory(Path.Combine(dir, "sources", "staging"));
        File.WriteAllText(Path.Combine(dir, "sources/staging/orders.yml"), "name: staging.orders\n" + Cols);
        File.WriteAllText(Path.Combine(dir, "sources/staging/bad.yml"), "name: staging.nope\n" + Cols);
        var result = ProjectValidator.Validate(dir);
        Assert.Equal("staging.orders", Assert.Single(result.Descriptors).Name);
        Assert.Contains(result.Diagnostics, d => d.Code == "DDB-107" && d.Location.File == "sources/staging/bad.yml");
    }

    [Fact]
    public void A_name_cannot_be_both_a_source_and_a_model()
    {
        var dir = TestSupport.NewProjectDir();
        Directory.CreateDirectory(Path.Combine(dir, "sources", "marts"));
        File.WriteAllText(Path.Combine(dir, "sources/marts/fct_orders.yml"), "name: marts.fct_orders\n" + Cols);
        File.WriteAllText(Path.Combine(dir, "models/marts/fct_orders.yml"), TestSupport.ValidModel);
        File.WriteAllText(Path.Combine(dir, "models/marts/fct_orders.sql"), "SELECT 1");
        var result = ProjectValidator.Validate(dir);
        Assert.Contains(result.Diagnostics, d => d.Code == "DDB-102" && d.Found.Contains("both a source and a model"));
    }

    [Fact]
    public void Sources_are_optional()
    {
        var dir = TestSupport.NewProjectDir();
        Assert.Empty(ProjectValidator.Validate(dir).Descriptors);
    }
}
