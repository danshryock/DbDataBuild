using DbDataBuild.Core;
using DbDataBuild.Models;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>Parameters: one concept, four scopes in files (project, connection, origin, model). Layered like any project setting, except a model's own, which are only in its file.</summary>
public class ParametersTests
{
    private const string Config = "parameters:\n  region: eu\n  cutoff: { type: DATE, value: \"2024-01-01\" }\n  limit: { type: BIGINT, value: 5 }\nconnections:\n  wh: { engine: sqlserver, parameters: { site: main } }\ndefaults: {connections: [wh]}\n";

    private static void Write(string dir, string path, string text)
    {
        var full = Path.Combine(dir, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private static string Project(string config = Config)
    {
        var dir = NewProjectDir();
        Write(dir, "dbdatabuild.yml", config);
        return dir;
    }

    private static void Model(string dir, string name, string extra = "")
    {
        var stem = "models/" + name.Replace('.', '/');
        Write(dir, stem + ".yml", $"name: {name}\nkind: {{type: view}}\n{extra}columns:\n  - {{name: a, type: BIGINT}}\n");
        Write(dir, stem + ".sql", "SELECT 1 AS a\n");
    }

    [Fact]
    public void A_parameter_is_a_value_or_a_typed_value()
    {
        var cfg = ProjectConfigLoader.Load(Config, "dbdatabuild.yml", [])!;
        Assert.Equal(new ParameterValue("eu"), cfg.Parameters["region"]);
        Assert.Equal(new ParameterValue("2024-01-01", "DATE"), cfg.Parameters["cutoff"]);
        Assert.Equal(new ParameterValue("5", "BIGINT"), cfg.Parameters["limit"]);
        Assert.Equal("main", cfg.Connections["wh"].Parameters["site"].Value);
    }

    [Theory]
    [InlineData("parameters: [a]\n")]
    [InlineData("parameters: { Bad: x }\n")]
    [InlineData("parameters: { a: [1] }\n")]
    [InlineData("parameters: { a: { type: DATE, value: yesterday } }\n")]
    [InlineData("parameters: { a: { type: GEOGRAPHY, value: x } }\n")]
    [InlineData("parameters: { a: { type: DATE } }\n")]
    [InlineData("parameters: { a: { type: DATE, value: \"2024-01-01\", color: red } }\n")]
    public void A_parameter_that_cannot_be_read_is_refused(string yaml)
    {
        var diags = new List<Diagnostic>();
        Assert.Null(ProjectConfigLoader.Load(yaml, "dbdatabuild.yml", diags));
        Assert.NotEmpty(diags);
    }

    [Fact]
    public void A_model_sees_the_project_parameters_the_folders_above_it_override_and_its_own_and_never_inherits_the_second()
    {
        var dir = Project();
        Write(dir, "models/eu/_dbdatabuild.yml", "parameters:\n  region: eu-west\nconnections:\n  wh:\n    parameters: { site: branch }\n");
        Write(dir, "models/eu/uk/_dbdatabuild.yml", "parameters:\n  region: uk\n");
        Model(dir, "eu.a");
        Model(dir, "eu.uk.b", "parameters: { tone: loud }\n");
        Model(dir, "root.c");
        var result = ProjectValidator.Validate(dir);
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics.Select(DiagnosticFormatter.Format)));
        var cfg = ProjectConfigLoader.LoadFromProject(dir, []);
        string Seen(string model, string key) => result.Sources.Single(s => s.Definition.Name == model).ParametersFor(cfg, "wh")[key].Value;
        Assert.Equal("eu", Seen("root.c", "project.region"));                 // the root's
        Assert.Equal("eu-west", Seen("eu.a", "project.region"));               // a folder overrides it for what is beneath
        Assert.Equal("uk", Seen("eu.uk.b", "project.region"));                 // the nearest wins
        Assert.Equal("main", Seen("root.c", "connection.site"));
        Assert.Equal("branch", Seen("eu.a", "connection.site"));               // a folder may override a connection's parameter
        Assert.Equal("loud", Seen("eu.uk.b", "model.tone"));
        Assert.DoesNotContain("model.tone", result.Sources.Single(s => s.Definition.Name == "eu.a").ParametersFor(cfg, "wh").Keys);   // a model's parameters are its own
        Assert.Equal("2024-01-01", Seen("eu.a", "project.cutoff"));            // untouched ones come through
    }

    [Theory]
    [InlineData("parameters: [a]\n")]
    [InlineData("connections: { wh: { engine: sqlserver } }\n")]
    [InlineData("connections: { wh: { parameters: [x] } }\n")]
    [InlineData("parameters: { a: { type: DATE, value: nope } }\n")]
    public void A_folder_file_cannot_declare_a_connection_and_its_parameters_are_checked(string yaml)
    {
        var dir = Project();
        Write(dir, "models/x/_dbdatabuild.yml", yaml);
        Model(dir, "x.a");
        Assert.True(ProjectValidator.Validate(dir).HasErrors);
    }

    [Fact]
    public void A_model_parameter_is_declared_in_the_model_file_only()
    {
        var dir = Project();
        Write(dir, "models/x/_dbdatabuild.yml", "defaults:\n  parameters: { tone: loud }\n");
        Model(dir, "x.a");
        var d = Assert.Single(ProjectValidator.Validate(dir).Diagnostics, x => x.Found.Contains("Unknown key `parameters` in `defaults`"));
        Assert.Equal("models/x/_dbdatabuild.yml", d.Location.File);
    }

    [Fact]
    public void A_slice_may_use_project_and_model_parameters_and_a_missing_one_is_named()
    {
        var dir = Project("parameters:\n  region: eu\nconnections:\n  crm: { engine: postgres, parameters: { id: \"1\" } }\n  wh: { engine: sqlserver }\ndefaults: {connections: [wh]}\n");
        Write(dir, "models/crm/c.yml", "name: crm.c\nkind: {type: mapped}\nconnections=: [crm]\ngrain: [a]\ncolumns:\n  - {name: a, type: BIGINT, nullable: false}\n");
        Write(dir, "models/dst/c.yml", "name: dst.c\nkind:\n  type: copy\n  from: crm.c\n  slice: {column: region, value: \"${project.region}-${origin.id}\", type: \"VARCHAR(20)\"}\n");
        var ok = ProjectValidator.Validate(dir);
        Assert.False(ok.HasErrors, string.Join("\n", ok.Diagnostics.Select(DiagnosticFormatter.Format)));
        Write(dir, "models/dst/c.yml", "name: dst.c\nkind:\n  type: copy\n  from: crm.c\n  slice: {column: region, value: \"${project.nothing}-${origin.id}\", type: \"VARCHAR(20)\"}\n");
        Assert.Contains(ProjectValidator.Validate(dir).Diagnostics, d => d.Found.Contains("there is no such project parameter"));
    }
}
