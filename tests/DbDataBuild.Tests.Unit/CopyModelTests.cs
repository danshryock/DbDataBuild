using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Models;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>A copy: a table filled with the rows of another model on another connection. Declared here, resolved and rendered; the rows move at apply.</summary>
public class CopyModelTests
{
    private const string Config = "connections:\n  crm: { engine: postgres }\n  wh: { engine: sqlserver }\n  lake: { engine: postgres }\ndefaults:\n  connections: [wh]\n";
    private const string Customers = "name: crm.customers\nkind: {type: mapped}\nconnections=: [crm]\ngrain: [customer_id]\ncolumns:\n  - {name: customer_id, type: BIGINT, nullable: false}\n  - {name: name, type: \"VARCHAR(50)\"}\n  - {name: born, type: DATE}\n";

    private static string Project(string config = Config)
    {
        var dir = NewProjectDir();
        File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), config);
        Write(dir, "models/crm/customers.yml", Customers);
        return dir;
    }

    private static void Write(string dir, string path, string text)
    {
        var full = Path.Combine(dir, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private static void Copy(string dir, string name, string from, string extra = "")
    {
        Write(dir, "models/" + name.Replace('.', '/') + ".yml", $"name: {name}\nkind: {{type: copy, from: {from}}}\n{extra}");
    }

    private static (ProjectValidationResult Result, string Text) Validate(string dir)
    {
        var result = ProjectValidator.Validate(dir);
        return (result, string.Join("\n", result.Diagnostics.Select(DiagnosticFormatter.Format)));
    }

    [Fact]
    public void A_copy_has_the_columns_and_grain_of_its_origin_and_a_generated_query_over_its_staging_table()
    {
        var dir = Project();
        Copy(dir, "warehouse.customers", "crm.customers");
        var (result, text) = Validate(dir);
        Assert.False(result.HasErrors, text);
        var copy = result.Sources.Single();
        Assert.Equal((ModelKinds.Copy, "crm.customers"), (copy.Definition.KindType, copy.Definition.From));
        Assert.Equal(["customer_id", "name", "born"], copy.Definition.Columns.Select(c => c.Name));
        Assert.Equal(["customer_id"], copy.Definition.Grain);
        Assert.False(copy.Definition.Columns[0].Nullable);
        Assert.Equal("models/warehouse/customers.yml", copy.QueryFile);                              // no .sql: diagnostics point at the definition
        Assert.Equal("SELECT \"customer_id\", \"name\", \"born\" FROM \"dbdatabuild\".\"stg_warehouse__customers\"\n", copy.GeneratedQuery);
        // the staging table is declared to the project for binding, and is not one of its mapped models
        Assert.Equal(["crm.customers"], result.Descriptors.Select(d => d.Name));
        Assert.Contains(result.AllDescriptors, d => d.Name == "dbdatabuild.stg_warehouse__customers" && d.IsGenerated);
    }

    [Fact]
    public void A_copy_renders_a_full_replace_from_its_staging_table_for_its_connection()
    {
        var dir = Project();
        Copy(dir, "warehouse.customers", "crm.customers", "connections=: [wh]\n");
        var o = new StringWriter(); var e = new StringWriter();
        Assert.True(CliApp.Run(["render", "--write", "--project", dir], o, e, environment: _ => null) == 0, o + "\n" + e);
        var script = File.ReadAllText(Path.Combine(dir, "rendered/wh/warehouse.customers/load.default.sql"));
        Assert.Contains("FROM dbdatabuild.stg_warehouse__customers", script);                          // reads the staging table, in the destination's dialect
        Assert.Contains("-- model:           warehouse.customers", script);
        Assert.Empty(Directory.GetDirectories(Path.Combine(dir, "rendered")).Where(d => !d.EndsWith("wh") && !d.EndsWith("lowered")));
        Assert.Equal(0, CliApp.Run(["render", "--check", "--project", dir], new StringWriter(), new StringWriter(), environment: _ => null));
    }

    [Fact]
    public void A_copy_of_a_copy_and_a_copy_of_a_model_that_is_built_resolve()
    {
        var dir = Project();
        Copy(dir, "warehouse.customers", "crm.customers");                                              // on wh
        Copy(dir, "lake.customers", "warehouse.customers", "connections=: [lake]\n");                    // on lake, from the copy on wh
        Write(dir, "models/crm/recent.yml", "name: crm.recent\nkind: {type: view}\nconnections=: [crm]\ncolumns:\n  - {name: customer_id, type: BIGINT, nullable: false}\n");
        Write(dir, "models/crm/recent.sql", "SELECT customer_id FROM crm.customers\n");
        Copy(dir, "lake.recent", "crm.recent", "connections=: [lake]\n");
        var (result, text) = Validate(dir);
        Assert.False(result.HasErrors, text);
        Assert.Equal(["customer_id", "name", "born"], result.Sources.Single(s => s.Definition.Name == "lake.customers").Definition.Columns.Select(c => c.Name));
        Assert.Equal(["customer_id"], result.Sources.Single(s => s.Definition.Name == "lake.recent").Definition.Columns.Select(c => c.Name));
    }

    [Theory]
    [InlineData("crm.nothing", "", "which is not a model, a mapped model or a copy")]
    [InlineData("crm.customers", "connections=: [crm]\n", "moves rows between connections")]                // the origin's own connection
    [InlineData("crm.customers", "columns:\n  - {name: a, type: INT}\n", "has no `columns`")]
    [InlineData("a", "", "DDB-106")]
    public void A_copy_that_cannot_be_resolved_says_why(string from, string extra, string expected)
    {
        var dir = Project();
        Copy(dir, "warehouse.customers", from, extra);
        var (result, text) = Validate(dir);
        Assert.True(result.HasErrors);
        Assert.Contains(expected, text);
    }

    [Fact]
    public void A_copy_reads_from_exactly_one_connection()
    {
        var dir = Project();
        Write(dir, "models/crm/orders.yml", "name: crm.orders\nkind: {type: mapped}\nconnections=: [crm, lake]\ncolumns:\n  - {name: id, type: BIGINT}\n");
        Copy(dir, "warehouse.orders", "crm.orders");
        Assert.Contains("a copy reads from exactly one connection", Validate(dir).Text);
    }

    [Fact]
    public void Copies_that_copy_each_other_are_refused_not_looped_on()
    {
        var dir = Project();
        Copy(dir, "warehouse.a", "warehouse.b", "connections=: [wh]\n");
        Copy(dir, "warehouse.b", "warehouse.a", "connections=: [lake]\n");
        var (result, text) = Validate(dir);
        Assert.True(result.HasErrors);
        Assert.Contains("copies itself, through the others", text);
    }

    [Fact]
    public void A_query_beside_a_copy_is_refused()
    {
        var dir = Project();
        Copy(dir, "warehouse.customers", "crm.customers");
        Write(dir, "models/warehouse/customers.sql", "SELECT 1\n");
        Assert.Contains("has no query of its own", Validate(dir).Text);
    }

    [Fact]
    public void The_staging_name_is_stable_and_short_enough_for_every_engine()
    {
        Assert.Equal("stg_warehouse__customers", CopyModels.StagingTable("warehouse.customers"));
        var longName = "warehouse." + new string('x', 80);
        var staged = CopyModels.StagingTable(longName);
        Assert.True(staged.Length <= 63);
        Assert.Equal(staged, CopyModels.StagingTable(longName));                                       // deterministic: an interrupted run is run again on the same table
        Assert.NotEqual(staged, CopyModels.StagingTable("warehouse." + new string('x', 79) + "y"));
    }
}
