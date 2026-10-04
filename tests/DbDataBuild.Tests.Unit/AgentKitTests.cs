using System.Text.RegularExpressions;
using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Models;
using static DbDataBuild.Tests.Unit.PolyglotBindingTests;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>
/// The skill an agent reads must not rot: every command, option, diagnostic code and schema file it names exists, its example model loads, and the files the command installs
/// are the ones in the repository.
/// </summary>
public partial class AgentKitTests
{
    private static string Skill => AgentKitCommand.Files().Single(f => f.Path == "SKILL.md").Content;

    private static (int Exit, string Out, string Err) Cli(params string[] args)
    {
        var o = new StringWriter(); var e = new StringWriter();
        return (CliApp.Run(args, o, e), o.ToString(), e.ToString());
    }

    [GeneratedRegex(@"`dbdatabuild ([a-z][a-z-]*)")]
    private static partial Regex CommandMention();

    [GeneratedRegex(@"(?<![\w-])(--[a-z][a-z-]*)")]
    private static partial Regex FlagMention();

    [Fact]
    public void The_skill_has_the_front_matter_an_agent_needs_to_find_and_use_it()
    {
        var lines = Skill.Split('\n');
        Assert.Equal("---", lines[0]);
        Assert.Equal("name: dbdatabuild", lines[1]);
        Assert.StartsWith("description: ", lines[2]);
        Assert.InRange(lines[2].Length, 100, 1024);
        Assert.Equal("---", lines[3]);
    }

    [Fact]
    public void Every_command_option_code_and_schema_the_skill_names_exists()
    {
        var catalog = TuiCommand.CliHost.Catalog();
        var commands = catalog.Select(c => c.Name).Concat(["tui", "agent-kit", "mcp"]).ToHashSet();
        var options = catalog.SelectMany(c => c.Options.Select(o => o.Name)).Concat(["--format"]).ToHashSet();
        foreach (Match m in CommandMention().Matches(Skill)) Assert.True(commands.Contains(m.Groups[1].Value), $"the skill mentions `dbdatabuild {m.Groups[1].Value}`, which is not a command");
        // the table names commands without the program name, so check the backticked first words too
        foreach (var name in catalog.Select(c => c.Name).Where(n => n is not ("tui" or "agent-kit" or "mcp"))) Assert.Contains($"`{name}", Skill);
        foreach (Match m in FlagMention().Matches(Skill))
            Assert.True(options.Contains(m.Groups[1].Value) || ConfigOnly.Contains(m.Groups[1].Value), $"the skill mentions {m.Groups[1].Value}, which is not an option of any command");

        var known = DiagnosticCatalog.All.Select(d => d.Code).ToHashSet();
        foreach (Match m in Regex.Matches(Skill, @"DDB-\d{3}")) Assert.True(known.Contains(m.Value), $"the skill mentions {m.Value}, which is not a diagnostic code");
        foreach (Match m in Regex.Matches(Skill, @"schemas/([a-z.]+\.json)")) Assert.Contains(AgentKitCommand.Files(), f => f.Path == "schemas/" + m.Groups[1].Value);
    }

    private static readonly string[] ConfigOnly = [];

    [Fact]
    public void The_example_model_in_the_skill_is_a_valid_definition()
    {
        var yaml = Regex.Match(Skill, "```yaml\n(.*?)```", RegexOptions.Singleline).Groups[1].Value;
        var (def, diags) = Load(yaml);
        Assert.Empty(diags.Select(DiagnosticFormatter.Format));
        Assert.Equal("marts.fct_orders", def!.Name);
        Assert.Equal(["order_id"], def.UniqueKey);
        Assert.Single(def.Indexes);
    }

    [Fact]
    public void The_kit_includes_every_schema_in_the_repository_unchanged()
    {
        var repo = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "schemas"), "*.json").Select(f => Path.GetFileName(f)).Order().ToList();
        var kit = AgentKitCommand.Files().Where(f => f.Path.StartsWith("schemas/")).ToList();
        Assert.Equal(repo, kit.Select(f => Path.GetFileName(f.Path)).Order());
        foreach (var (path, content) in kit) Assert.Equal(File.ReadAllText(Path.Combine(RepoRoot(), path)).Replace("\r\n", "\n"), content);
    }

    [Fact]
    public void Listing_writes_nothing_write_installs_and_check_notices_a_change()
    {
        var dir = NewProjectDir();
        var before = Snapshot(dir);
        var listed = Cli("agent-kit", "--project", dir);
        Assert.Equal(0, listed.Exit);
        Assert.Contains(".claude/skills/dbdatabuild/SKILL.md", listed.Out);
        Assert.Equal(before, Snapshot(dir));

        Assert.Equal(CliApp.ExitFindings, Cli("agent-kit", "--project", dir, "--check").Exit);          // nothing is installed yet
        var written = Cli("agent-kit", "--project", dir, "--write");
        Assert.Equal(0, written.Exit);
        var skill = Path.Combine(dir, ".claude", "skills", "dbdatabuild", "SKILL.md");
        Assert.Equal(Skill, File.ReadAllText(skill));
        Assert.True(File.Exists(Path.Combine(dir, ".claude", "skills", "dbdatabuild", "schemas", "output.schema.json")));
        Assert.Contains("already up to date", Cli("agent-kit", "--project", dir, "--write").Out);
        Assert.Equal(0, Cli("agent-kit", "--project", dir, "--check").Exit);

        File.AppendAllText(skill, "local edit\n");
        var stale = Cli("agent-kit", "--project", dir, "--check");
        Assert.Equal(CliApp.ExitFindings, stale.Exit);
        Assert.Contains("SKILL.md", stale.Err);
        Cli("agent-kit", "--project", dir, "--write");
        Assert.Equal(Skill, File.ReadAllText(skill));                                                  // an edited copy is replaced: the kit belongs to the tool version

        Assert.Equal(CliApp.ExitUsage, Cli("agent-kit", "--project", dir, "--write", "--check").Exit);
        var elsewhere = Cli("agent-kit", "--project", dir, "--write", "--dir", "agents/dbdatabuild");
        Assert.True(File.Exists(Path.Combine(dir, "agents", "dbdatabuild", "SKILL.md")));
        Assert.Equal(0, elsewhere.Exit);
    }
}
