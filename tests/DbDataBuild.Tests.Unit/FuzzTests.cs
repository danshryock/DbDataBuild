using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Core.Questions;
using DbDataBuild.Planning;
using DbDataBuild.State;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>
/// Hardening (DESIGN.md 15.5): every file a person edits is read by code that must answer a damaged file with a diagnostic, never with an unhandled exception.
/// Each test takes a valid file, applies seeded mutations (cut, delete a line, duplicate a line, replace a character, insert a token, swap two lines) and requires that
/// the tool still finishes with a normal exit code and, for the CLI, never reports an internal error (DDB-900). The seed is fixed so a failure is reproducible.
/// </summary>
public class FuzzTests
{
    private const int Seed = 20261002;
    private const int Rounds = 40;

    private static readonly string[] Tokens =
        ["", " ", "\t", ":", "- ", "[", "]", "{", "}", "\"", "'", "#", "&a", "*a", "!!str", "? ", "|", ">", "---", "...", "null", "~", "true", "-1", "1e999", "0x10", "\u0000", "é", "\u202e", "\r", "\\", "%", "@", "`", "$$", "{{x}}", "<<: *a"];

    internal static IEnumerable<string> Mutations(string text, int seed, int count)
    {
        var rng = new Random(seed);
        for (var i = 0; i < count; i++)
        {
            var lines = text.Split('\n').ToList();
            var pick = rng.Next(7);
            switch (pick)
            {
                case 0: yield return text[..rng.Next(text.Length + 1)]; break;                                                           // truncated
                case 1: if (lines.Count > 0) lines.RemoveAt(rng.Next(lines.Count)); yield return string.Join('\n', lines); break;       // line deleted
                case 2: { var k = rng.Next(lines.Count); lines.Insert(k, lines[k]); yield return string.Join('\n', lines); break; }      // line duplicated
                case 3: { var chars = text.ToCharArray(); if (chars.Length > 0) chars[rng.Next(chars.Length)] = Tokens[rng.Next(Tokens.Length)].FirstOrDefault('?'); yield return new string(chars); break; }
                case 4: { var at = rng.Next(text.Length + 1); yield return text.Insert(at, Tokens[rng.Next(Tokens.Length)]); break; }   // token inserted
                case 5: { var a = rng.Next(lines.Count); var b = rng.Next(lines.Count); (lines[a], lines[b]) = (lines[b], lines[a]); yield return string.Join('\n', lines); break; }
                default: { var k = rng.Next(lines.Count); lines[k] = new string(' ', rng.Next(8)) + lines[k].TrimStart(); yield return string.Join('\n', lines); break; }   // indentation changed
            }
        }
    }

    private const string Config = """
        defaults: {connections: [sqlserver, postgres]}
        connections:
          sqlserver: { version: 17 }
        string_semantics:
          case: insensitive
          accent: sensitive
          trailing_space: ignored
        metadata: { store_on_apply: false }
        lowering: { enabled: true }
        hook_groups:
          standard:
            - { name: grant, event: post_create, script: hooks/grant.sql }
        """;

    private const string Source = "name: staging.events\nkind:\n  type: mapped\ngrain: [event_id]\ncolumns:\n  - {name: event_id, type: BIGINT, nullable: false}\n  - {name: event_ts, type: TIMESTAMP, nullable: false}\n  - {name: seq, type: BIGINT, nullable: false}\n  - {name: payload, type: \"VARCHAR(100)\"}\n";

    private const string Answers = "answers:\n  - {id: Q-history-marts.fct_events.payload, answer: not_backfilled, note: \"no history\"}\n";

    private const string FolderFile = "defaults:\n  connections: [sqlserver]\n  lint_ignore: [DDB-223]\n  hooks:\n    - {name: audit, event: post_load, script: hooks/grant.sql}\n";

    private static string Project(string? config = null, string? source = null, string? model = null, string? sql = null, string? folder = null)
    {
        var dir = NewProjectDir();
        File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), config ?? Config);
        Directory.CreateDirectory(Path.Combine(dir, "models", "staging"));
        Directory.CreateDirectory(Path.Combine(dir, "hooks"));
        File.WriteAllText(Path.Combine(dir, "hooks", "grant.sql"), "GRANT SELECT ON marts.fct_events TO reader;\n");
        File.WriteAllText(Path.Combine(dir, "models", "staging", "events.yml"), source ?? Source);
        File.WriteAllText(Path.Combine(dir, "models", "marts", "fct_events.yml"), model ?? (LoadRendererTests.AllOpsYaml + "\nindexes:\n  - {name: ix_seq, columns: [seq]}\nhooks:\n  - {use: standard}\n"));
        File.WriteAllText(Path.Combine(dir, "models", "marts", "fct_events.sql"), sql ?? LoadRendererTests.AllOpsSql);
        File.WriteAllText(Path.Combine(dir, "answers.yml"), Answers);
        File.WriteAllText(Path.Combine(dir, "models", "marts", "_dbdatabuild.yml"), folder ?? FolderFile);
        return dir;
    }

    private static void RunAll(string dir, string what)
    {
        foreach (var args in new[] { new[] { "validate" }, new[] { "render", "--check" }, new[] { "loads" }, new[] { "define", "--check" }, new[] { "metadata" } })
        {
            var o = new StringWriter(); var e = new StringWriter();
            var exit = CliApp.Run([.. args, "--project", dir, "--format", "json"], o, e);
            Assert.True(exit != CliApp.ExitInternal && !o.ToString().Contains("DDB-900") && !e.ToString().Contains("DDB-900"), $"`{string.Join(' ', args)}` failed internally on {what}:\n{o}\n{e}");
            if (args[0] == "validate" || args[0] == "render") System.Text.Json.JsonDocument.Parse(o.ToString());      // the JSON document stays well formed whatever the input
        }
    }

    [Fact]
    public void The_unmutated_project_is_a_valid_starting_point()
    {
        var dir = Project();
        try
        {
            var o = new StringWriter(); var e = new StringWriter();
            var exit = CliApp.Run(["validate", "--project", dir], o, e);
            Assert.True(exit is CliApp.ExitOk or CliApp.ExitFindings, o + "\n" + e);
            Assert.DoesNotContain("error DDB-1", e.ToString());      // no config, definition or YAML error in the base files
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("config")]
    [InlineData("source")]
    [InlineData("model")]
    [InlineData("sql")]
    [InlineData("folder")]
    public void A_damaged_project_file_is_answered_with_diagnostics_not_an_exception(string which)
    {
        var baseText = which switch { "folder" => FolderFile, "config" => Config, "source" => Source, "model" => LoadRendererTests.AllOpsYaml + "\nindexes:\n  - {name: ix_seq, columns: [seq]}\nhooks:\n  - {use: standard}\n", _ => LoadRendererTests.AllOpsSql };
        var n = 0;
        foreach (var mutated in Mutations(baseText, Seed + which.Length, Rounds))
        {
            var dir = which switch { "config" => Project(config: mutated), "source" => Project(source: mutated), "model" => Project(model: mutated), "folder" => Project(folder: mutated), _ => Project(sql: mutated) };
            try { RunAll(dir, $"{which} mutation {n++}: {mutated.Replace("\n", "\\n")}"); }
            finally { Directory.Delete(dir, true); }
        }
    }

    [Fact]
    public void A_damaged_answers_file_does_not_crash_define_or_plan()
    {
        var n = 0;
        foreach (var mutated in Mutations(Answers, Seed, Rounds))
        {
            var dir = Project();
            File.WriteAllText(Path.Combine(dir, "answers.yml"), mutated);
            try
            {
                var o = new StringWriter(); var e = new StringWriter();
                var exit = CliApp.Run(["define", "--check", "--answers", Path.Combine(dir, "answers.yml"), "--project", dir], o, e);
                Assert.True(exit != CliApp.ExitInternal, $"answers mutation {n++} failed internally: {mutated.Replace("\n", "\\n")}\n{e}");
            }
            finally { Directory.Delete(dir, true); }
        }
    }

    [Fact]
    public void Every_prefix_of_a_yaml_document_is_read_without_an_exception()
    {
        // found by the plan-file fuzz: a file cut off mid-structure made YamlDotNet throw EndOfStreamException, which the reader did not catch
        const string doc = "a:\n  - b: [1, 2]\n    c: {d: e, f: \"g\"}\nh: |\n  text\n  more\ni: 'q'\n";
        for (var n = 0; n <= doc.Length; n++)
            DbDataBuild.Models.Yaml.StrictYamlReader.Read(doc[..n], "x.yml", new List<Diagnostic>());
    }

    [Fact]
    public void A_damaged_plan_file_is_refused_with_a_diagnostic()
    {
        var plan = new Plan("2026-10-12-abcd1234", "sqlserver", "4af31c2", true, "0.1.0",
            [new ObjectBase("marts.fct", ObjectState.InSync, new string('a', 64), new string('a', 64))],
            [new ResolvedAnswer("Q-history-marts.fct.x", "not_backfilled", null, "n", AnswerSource.File)],
            [new PlanStep("1", StepType.Ddl, "marts.fct", "add column", "ALTER TABLE t ADD x int;", RiskClass.Safe, ["col.added"], new string('b', 64), []),
             new PlanStep("2", StepType.Load, "marts.fct", "load", "SELECT 'é'\r\nGO", RiskClass.Safe, ["load.routine"], null, [new PlanParameter("w", "TIMESTAMP", "resolver", "2024-03-01")], "SELECT 1", "x", true, new string('c', 64), "default")],
            ["note"]);
        var text = PlanDocument.Serialize(plan);
        var parsedClean = new List<Diagnostic>();
        Assert.NotNull(PlanDocument.Parse(text, "plan.yml", parsedClean));
        var n = 0;
        foreach (var mutated in Mutations(text, Seed, Rounds * 6))
        {
            var diags = new List<Diagnostic>();
            var parsed = PlanDocument.Parse(mutated, "plan.yml", diags);       // must not throw
            // an edited plan is either rejected or, if the edit left the content unchanged, equal to the original: it never parses to something different
            if (parsed != null && diags.All(d => d.Severity != Severity.Error))
                Assert.True(PlanDocument.Serialize(parsed) == text, $"mutation {n} parsed to a different plan without an error: {mutated.Replace("\n", "\\n")}");
            n++;
        }
    }
}
