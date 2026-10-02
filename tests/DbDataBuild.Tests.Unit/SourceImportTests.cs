using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Models;
using DbDataBuild.State;
using Json.Schema;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>`import-sources`: native types to logical types, merging with committed descriptors, and source documents in the metadata. The catalog reads are covered against real engines.</summary>
public class SourceImportTests
{
    private static ColumnShape Col(string type, int? length = null, int? precision = null, int? scale = null, bool nullable = true, string name = "c") =>
        new(name, type, length, precision, scale, nullable, null);

    // target, native type, length, precision, scale, expected logical type ("" none), expected fit
    [Theory]
    [InlineData("sqlserver", "bigint", null, null, null, "BIGINT", "Exact")]
    [InlineData("sqlserver", "int", null, null, null, "INTEGER", "Exact")]
    [InlineData("sqlserver", "smallint", null, null, null, "SMALLINT", "Exact")]
    [InlineData("sqlserver", "tinyint", null, null, null, "SMALLINT", "Widened")]
    [InlineData("sqlserver", "bit", null, null, null, "BOOLEAN", "Exact")]
    [InlineData("sqlserver", "decimal", null, 14, 2, "DECIMAL(14, 2)", "Exact")]
    [InlineData("sqlserver", "numeric", null, 10, 0, "DECIMAL(10, 0)", "Exact")]
    [InlineData("sqlserver", "money", null, null, null, "DECIMAL(19, 4)", "Widened")]
    [InlineData("sqlserver", "smallmoney", null, null, null, "DECIMAL(10, 4)", "Widened")]
    [InlineData("sqlserver", "float", null, null, null, "DOUBLE", "Exact")]
    [InlineData("sqlserver", "real", null, null, null, "FLOAT", "Exact")]
    [InlineData("sqlserver", "date", null, null, null, "DATE", "Exact")]
    [InlineData("sqlserver", "datetime2", null, null, 6, "TIMESTAMP", "Exact")]
    [InlineData("sqlserver", "datetime2", null, null, 7, "TIMESTAMP", "Lossy")]
    [InlineData("sqlserver", "datetime", null, null, null, "TIMESTAMP", "Widened")]
    [InlineData("sqlserver", "time", null, null, 3, "TIME", "Exact")]
    [InlineData("sqlserver", "datetimeoffset", null, null, 7, "TIMESTAMP WITH TIME ZONE", "Lossy")]
    [InlineData("sqlserver", "uniqueidentifier", null, null, null, "UUID", "Exact")]
    [InlineData("sqlserver", "nvarchar", 50, null, null, "VARCHAR(50)", "Exact")]
    [InlineData("sqlserver", "varchar", 20, null, null, "VARCHAR(20)", "Exact")]
    [InlineData("sqlserver", "char", 3, null, null, "VARCHAR(3)", "Widened")]
    [InlineData("sqlserver", "nvarchar", -1, null, null, "", "None")]
    [InlineData("sqlserver", "varchar", -1, null, null, "", "None")]
    [InlineData("sqlserver", "text", null, null, null, "", "None")]
    [InlineData("sqlserver", "varbinary", -1, null, null, "BLOB", "Widened")]
    [InlineData("sqlserver", "xml", null, null, null, "", "None")]
    [InlineData("sqlserver", "geography", null, null, null, "", "None")]
    [InlineData("sqlserver", "rowversion", null, null, null, "", "None")]
    [InlineData("postgres", "bigint", null, null, null, "BIGINT", "Exact")]
    [InlineData("postgres", "integer", null, null, null, "INTEGER", "Exact")]
    [InlineData("postgres", "smallint", null, null, null, "SMALLINT", "Exact")]
    [InlineData("postgres", "boolean", null, null, null, "BOOLEAN", "Exact")]
    [InlineData("postgres", "numeric", null, 14, 2, "DECIMAL(14, 2)", "Exact")]
    [InlineData("postgres", "numeric", null, null, null, "", "None")]
    [InlineData("postgres", "numeric", null, 60, 4, "", "None")]
    [InlineData("postgres", "real", null, null, null, "FLOAT", "Exact")]
    [InlineData("postgres", "double precision", null, null, null, "DOUBLE", "Exact")]
    [InlineData("postgres", "timestamp without time zone", null, null, 6, "TIMESTAMP", "Exact")]
    [InlineData("postgres", "timestamp with time zone", null, null, 6, "TIMESTAMP WITH TIME ZONE", "Exact")]
    [InlineData("postgres", "time without time zone", null, null, 6, "TIME", "Exact")]
    [InlineData("postgres", "uuid", null, null, null, "UUID", "Exact")]
    [InlineData("postgres", "bytea", null, null, null, "BLOB", "Exact")]
    [InlineData("postgres", "character varying", 40, null, null, "VARCHAR(40)", "Exact")]
    [InlineData("postgres", "character varying", null, null, null, "", "None")]
    [InlineData("postgres", "character", 2, null, null, "VARCHAR(2)", "Widened")]
    [InlineData("postgres", "text", null, null, null, "", "None")]
    [InlineData("postgres", "jsonb", null, null, null, "", "None")]
    [InlineData("postgres", "interval", null, null, null, "", "None")]
    [InlineData("postgres", "ARRAY", null, null, null, "", "None")]
    public void Native_types_map_to_logical_types_with_their_fit(string target, string type, int? length, int? precision, int? scale, string logical, string fit)
    {
        var t = SourceTypes.From(target, Col(type, length, precision, scale));
        Assert.Equal(logical == "" ? null : logical, t.LogicalType);
        Assert.Equal(Enum.Parse<SourceTypeFit>(fit), t.Fit);
        Assert.Equal(t.Fit == SourceTypeFit.Exact, t.Reason.Length == 0);        // anything but exact says why
    }

    [Fact]
    public void Every_logical_type_a_mapping_produces_is_one_the_models_can_use()
    {
        foreach (var target in new[] { "sqlserver", "postgres" })
            foreach (var type in new[] { "bigint", "int", "integer", "smallint", "tinyint", "bit", "boolean", "float", "real", "double precision", "date", "datetime", "uniqueidentifier", "uuid", "bytea" })
                if (SourceTypes.From(target, Col(type)).LogicalType is { } logical)
                    Assert.Equal(logical, Define.LogicalTypes.Canonical(logical));   // canonical spelling, so `define` compares it as written
    }

    private static ObjectShape Shape(string schema, string name, params ColumnShape[] columns) => new(schema, name, ObjectKind.Table, columns, []);

    [Fact]
    public void A_new_descriptor_takes_live_columns_nullability_and_the_primary_key_as_grain()
    {
        var shape = Shape("staging", "orders", Col("bigint", nullable: false, name: "order_id"), Col("decimal", precision: 14, scale: 2, name: "amount"), Col("text", name: "notes"));
        var live = SourceImport.Describe("sqlserver", shape, ["order_id"]);
        Assert.Equal("sources/staging/orders.yml", live.File);
        var d = SourceImport.ToDescriptor(live, null);
        Assert.Equal("staging.orders", d.Name);
        Assert.Equal(["order_id"], d.Grain);
        Assert.Equal([("order_id", "BIGINT", false), ("amount", "DECIMAL(14, 2)", true)], d.Columns.Select(c => (c.Name, c.Type, c.Nullable)));   // `notes` has no logical type: left out
        Assert.Equal([SourceChangeKind.New], SourceImport.Compare(null, d).Select(c => c.Kind));
    }

    [Fact]
    public void Written_descriptors_load_back_identically_and_in_canonical_form()
    {
        var live = SourceImport.Describe("postgres", Shape("sales", "line items", Col("bigint", nullable: false, name: "id"), Col("character varying", 12, name: "Sku Code")), ["id"]);
        var d = SourceImport.ToDescriptor(live, null);
        var yaml = SourceDescriptorWriter.Yaml(d);
        Assert.Equal("name: sales.line items\ngrain: [id]\ncolumns:\n  - name: id\n    type: BIGINT\n    nullable: false\n  - name: Sku Code\n    type: VARCHAR(12)\n", yaml);
        var diags = new List<Diagnostic>();
        var loaded = SourceDescriptorLoader.Load(yaml, "sources/sales/line items.yml", "sales.line items", diags);
        Assert.Empty(diags);
        Assert.Equal(d.Columns.Select(c => (c.Name, c.Type, c.Nullable)), loaded!.Columns.Select(c => (c.Name, c.Type, c.Nullable)));
        Assert.Equal(d.Grain, loaded.Grain);
    }

    [Fact]
    public void A_committed_grain_and_a_hand_typed_column_survive_a_refresh_and_the_rest_follows_the_table()
    {
        var committed = new SourceDescriptor("staging.orders", [
            new ColumnDefinition("order_id", "INTEGER", true),         // the table says BIGINT NOT NULL now
            new ColumnDefinition("notes", "VARCHAR(500)", true),       // typed by hand: the catalog says text
            new ColumnDefinition("legacy", "INTEGER", true),           // gone from the table
        ], ["order_id", "notes"]);
        var shape = Shape("staging", "orders", Col("bigint", nullable: false, name: "order_id"), Col("text", name: "notes"), Col("date", name: "placed"));
        var live = SourceImport.Describe("sqlserver", shape, ["order_id"]);
        var d = SourceImport.ToDescriptor(live, committed);

        Assert.Equal(["order_id", "notes"], d.Grain);                                                  // human knowledge wins over the primary key
        Assert.Equal([("order_id", "BIGINT", false), ("notes", "VARCHAR(500)", true), ("placed", "DATE", true)], d.Columns.Select(c => (c.Name, c.Type, c.Nullable)));
        var changes = SourceImport.Compare(committed, d).Select(c => (c.Kind, c.Column)).ToList();
        Assert.Contains((SourceChangeKind.TypeChanged, "order_id"), changes);
        Assert.Contains((SourceChangeKind.NullabilityChanged, "order_id"), changes);
        Assert.Contains((SourceChangeKind.ColumnAdded, "placed"), changes);
        Assert.Contains((SourceChangeKind.ColumnRemoved, "legacy"), changes);
        Assert.DoesNotContain(changes, c => c.Column == "notes");
        Assert.Empty(SourceImport.Compare(d, d));
    }

    [Fact]
    public void Synonyms_and_column_order_are_not_differences()
    {
        var committed = new SourceDescriptor("s.t", [new ColumnDefinition("b", "INT"), new ColumnDefinition("a", "NUMERIC(10,2)")], []);
        var live = new SourceDescriptor("s.t", [new ColumnDefinition("a", "DECIMAL(10, 2)"), new ColumnDefinition("B", "INTEGER")], []);
        Assert.Empty(SourceImport.Compare(committed, live));
    }

    [Fact]
    public void Names_that_cannot_be_paths_are_not_importable()
    {
        Assert.Null(SourceDescriptorWriter.PathFor("sales", "a.b"));
        Assert.Null(SourceDescriptorWriter.PathFor("s/x", "t"));
        Assert.Null(SourceImport.Describe("postgres", Shape("sales", "a.b", Col("bigint")), null).File);
        Assert.Equal("sources/sales/orders.yml", SourceDescriptorWriter.PathFor("sales", "orders"));
    }

    // ---- the command, without a database ----

    private static (int Exit, string Out, string Err) Run(params string[] args)
    {
        var o = new StringWriter(); var e = new StringWriter();
        var exit = CliApp.Run(args, o, e, environment: _ => null);
        return (exit, o.ToString(), e.ToString());
    }

    private static string Project(bool withSource)
    {
        var dir = NewProjectDir();
        File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), "default_targets: [sqlserver]\n");
        if (withSource)
        {
            Directory.CreateDirectory(Path.Combine(dir, "sources/staging"));
            File.WriteAllText(Path.Combine(dir, "sources/staging/orders.yml"), "name: staging.orders\ngrain: [order_id]\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: amount, type: \"DECIMAL(14, 2)\"}\n");
        }
        File.WriteAllText(Path.Combine(dir, "models/marts/fct_orders.yml"), "name: marts.fct_orders\nkind: {type: view}\ntargets: [sqlserver]\ncolumns:\n  - {name: order_id, type: BIGINT, nullable: false}\n");
        File.WriteAllText(Path.Combine(dir, "models/marts/fct_orders.sql"), "SELECT o.order_id FROM staging.orders o\n");
        return dir;
    }

    [Fact]
    public void Import_needs_the_read_login_and_changes_nothing_without_it()
    {
        var dir = Project(withSource: true);
        var before = Snapshot(dir);
        var (exit, _, err) = Run("import-sources", "--project", dir, "--write");
        Assert.Equal(1, exit);
        Assert.Contains("DDB-501", err);
        Assert.Equal(before, Snapshot(dir));
    }

    [Fact]
    public void Import_without_patterns_and_without_descriptors_asks_for_a_pattern()
    {
        var (exit, _, err) = Run("import-sources", "--project", Project(withSource: false));
        Assert.Equal(CliApp.ExitUsage, exit);
        Assert.Contains("no source descriptors yet", err);
    }

    [Fact]
    public void Import_refuses_check_with_write_and_a_pattern_without_a_schema()
    {
        var dir = Project(withSource: true);
        Assert.Equal(CliApp.ExitUsage, Run("import-sources", "--project", dir, "--check", "--write").Exit);
        var (exit, _, err) = Run("import-sources", "--project", dir, "orders");
        Assert.Equal(CliApp.ExitUsage, exit);
        Assert.Contains("not `schema.table`", err);
    }

    // ---- source documents in the metadata ----

    [Fact]
    public void Metadata_carries_a_document_per_source_with_the_models_that_read_it()
    {
        var dir = Project(withSource: true);
        var o = new StringWriter();
        Assert.Equal(0, CliApp.Run(["metadata", "--project", dir, "--format", "json"], o, new StringWriter(), environment: _ => null));
        var doc = JsonNode.Parse(o.ToString())!;
        var metadata = SchemaConformanceTests.LoadSchema("metadata");
        bool Valid(JsonNode? n) => metadata.Evaluate(JsonSerializer.SerializeToNode(n), new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid;

        var source = doc["data"]!["sources"]!.AsArray().Single()!;
        Assert.True(Valid(source), source.ToJsonString());
        Assert.Equal("dbdatabuild.source/1", (string?)source["schema"]);
        Assert.Equal("staging.orders", (string?)source["name"]);
        Assert.Equal("sources/staging/orders.yml", (string?)source["file"]);
        Assert.Equal(["marts.fct_orders"], source["consumers"]!.AsArray().Select(x => (string)x!));
        Assert.Equal(["order_id"], source["grain"]!.AsArray().Select(x => (string)x!));
        Assert.Equal("staging.orders", (string?)doc["data"]!["project"]!["sources"]![0]!["name"]);
        var broken = JsonNode.Parse(source.ToJsonString())!;
        broken["schema"] = "dbdatabuild.source/2";
        Assert.False(Valid(broken));
    }

    [Fact]
    public void Publishing_includes_source_documents_that_do_not_change_with_the_models_selected()
    {
        var dir = Project(withSource: true);
        var ctx = ProjectContext.Load(dir);
        var all = MetadataPublisher.Collect(ctx, null, null);
        Assert.Equal(["project", "model", "source"], all.Select(d => d.Kind));
        var one = MetadataPublisher.Collect(ctx, ["marts.fct_orders"], null);
        Assert.Equal(all.Single(d => d.Kind == "source").Hash, one.Single(d => d.Kind == "source").Hash);
        Assert.DoesNotContain(MetadataPublisher.Collect(ctx, [], null), d => d.Kind == "source");   // no model selected, no source it reads
    }
}
