using System.Text.RegularExpressions;
using DbDataBuild.Cli;
using DbDataBuild.Execution;
using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>`trimmed: true` on the real engines: `check` counts the rows whose value ends in a space, and says so without showing a value.</summary>
[Trait("Group", "apply")]
public class StringProfileConformanceTests
{
    public static TheoryData<string> Engines => new() { "sqlserver", "postgres" };

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task Check_counts_the_values_that_end_in_a_space_in_a_column_declared_trimmed(string name)
    {
        var engine = EngineEnv.Require(name);
        await engine.StartAsync();
        await using var _ = engine;
        var dir = Path.Combine(Path.GetTempPath(), "ddb-trimmed-" + Guid.NewGuid().ToString("N"));
        string? Env(string v) => v == LoginSettings.VariableName(name, Login.Read) || v == LoginSettings.VariableName(name, Login.Write) ? engine.ConnectionString : null;
        (int Exit, string Out, string Err) Cli(params string[] args)
        {
            var o = new StringWriter(); var e = new StringWriter();
            var exit = CliApp.Run([args[0], "--project", dir, .. args.Skip(1)], o, e, environment: Env);
            return (exit, o.ToString(), e.ToString());
        }
        void Write(string rel, string text) { var p = Path.Combine(dir, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, text); }
        try
        {
            await engine.ExecAsync(name == "postgres" ? "CREATE SCHEMA IF NOT EXISTS src" : "IF SCHEMA_ID('src') IS NULL EXEC('CREATE SCHEMA src')");
            await engine.ExecAsync("DROP TABLE IF EXISTS src.codes");
            await engine.ExecAsync("CREATE TABLE src.codes (id bigint NOT NULL, code varchar(10) NOT NULL, label varchar(10) NOT NULL)");
            await engine.ExecAsync("INSERT INTO src.codes VALUES (1, 'a', 'x '), (2, 'b ', 'y'), (3, 'c  ', 'z')");
            Write("dbdatabuild.yml", $"defaults: {{connections: [{name}]}}\ntracking: none\n" + (name == "postgres" ? "string_semantics:\n  case: sensitive\n  trailing_space: significant\n  collations:\n    default: { duckdb: NFC, postgres: C }\n" : ""));
            Write("models/src/codes.yml", "name: src.codes\nkind: {type: mapped}\ngrain: [id]\ncolumns:\n  - {name: id, type: BIGINT, nullable: false}\n  - {name: code, type: \"VARCHAR(10)\", nullable: false, trimmed: true}\n  - {name: label, type: \"VARCHAR(10)\", nullable: false}\n");
            Write("models/marts/m.yml", "name: marts.m\nkind: {type: full}\ncolumns:\n  - {name: id, type: BIGINT, nullable: false}\n");
            Write("models/marts/m.sql", "SELECT id FROM src.codes\n");

            Assert.Equal(0, Cli("render", "--write").Exit);
            var broken = Cli("check", "--connection", name);
            Assert.NotEqual(0, broken.Exit);
            Assert.Contains("DDB-237", broken.Err + broken.Out);
            Assert.Contains("`src.codes.code` is declared `trimmed: true`", broken.Err + broken.Out);
            Assert.Contains("2 row(s) hold a value that ends in a space", broken.Err + broken.Out);                  // the two rows of `code`; `label` is not declared, its row is not counted
            Assert.DoesNotContain("'b '", broken.Err + broken.Out);                                                    // a count, never a value

            await engine.ExecAsync(name == "postgres" ? "UPDATE src.codes SET code = rtrim(code)" : "UPDATE src.codes SET code = RTRIM(code)");
            var fixedUp = Cli("check", "--connection", name);
            Assert.DoesNotContain("DDB-237", fixedUp.Err + fixedUp.Out);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
