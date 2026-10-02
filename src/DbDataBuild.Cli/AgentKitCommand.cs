using System.Reflection;
using System.Text;
using DbDataBuild.Core;
using DbDataBuild.State;

namespace DbDataBuild.Cli;

/// <summary>
/// `dbdatabuild agent-kit` (DESIGN.md 9.7). Effect class: repo files only. The knowledge an AI coding agent needs to work in a project as someone who knows the tool: a skill
/// (SKILL.md: the rules, the loop, how to write models, how to read plans) and the JSON Schemas of every file and of every command's output. The files are embedded in the
/// executable, so the kit always matches the version of the tool that writes it. By default nothing is written: the files are listed. `--write` installs them (default
/// `.claude/skills/dbdatabuild/` in the project), `--check` fails if an installed copy differs from this version's.
/// </summary>
internal static class AgentKitCommand
{
    public const string DefaultDir = ".claude/skills/dbdatabuild";

    public static IReadOnlyList<(string Path, string Content)> Files()
    {
        var asm = typeof(AgentKitCommand).Assembly;
        var files = new List<(string, string)>();
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("agentkit/", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var s = asm.GetManifestResourceStream(name)!;
            using var r = new StreamReader(s, Encoding.UTF8);
            files.Add((name["agentkit/".Length..], r.ReadToEnd().Replace("\r\n", "\n")));
        }
        return files;
    }

    public static int Run(CommandSpec spec, string projectRoot, string? dir, bool write, bool check, TextWriter output, TextWriter error)
    {
        output.WriteLine($"{ProductInfo.Cli} {spec.Name}  |  effect: {spec.Effect.Describe()}  |  target: none");
        if (write && check) { error.WriteLine("--write and --check cannot be combined: --check writes nothing."); return CliApp.ExitUsage; }
        var target = Path.GetFullPath(Path.Combine(projectRoot, dir ?? DefaultDir));
        var rel = Path.GetRelativePath(projectRoot, target).Replace('\\', '/');
        var files = Files();
        var listing = files.Select(f => new { path = $"{rel}/{f.Path}", hash = Hashing.ScriptHash(f.Content) }).ToList();
        output.Payload("directory", rel);
        output.Payload("files", listing);

        var wrote = new List<string>();
        var stale = new List<string>();
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(target, path);
            var current = File.Exists(full) ? File.ReadAllText(full).Replace("\r\n", "\n") : null;
            if (current == content) continue;
            stale.Add($"{rel}/{path}");
            if (!write) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var temp = full + ".ddb-" + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            File.WriteAllBytes(temp, new UTF8Encoding(false).GetBytes(content));
            File.Move(temp, full, overwrite: true);
            wrote.Add($"{rel}/{path}");
        }
        output.Payload("wrote", wrote);
        output.Payload("out_of_date", check ? stale : []);

        if (write)
        {
            foreach (var w in wrote) output.WriteLine($"wrote {w}");
            output.WriteLine(wrote.Count == 0 ? $"{files.Count} file(s) already up to date in {rel}." : $"{files.Count} file(s) in {rel}; {wrote.Count} written. Commit them with the project.");
            return CliApp.ExitOk;
        }
        if (check)
        {
            foreach (var s in stale) error.Diag(new Diagnostic(DiagnosticCatalog.RenderedFileOutOfDate, new(s, 0, 0), $"`{s}` is missing or differs from this version's agent kit."));
            output.WriteLine(stale.Count == 0 ? $"OK: the agent kit in {rel} matches this version." : $"FAILED: {stale.Count} file(s) out of date. Run `{ProductInfo.Cli} {spec.Name} --write`.");
            return stale.Count == 0 ? CliApp.ExitOk : CliApp.ExitFindings;
        }
        foreach (var f in listing) output.WriteLine(f.path);
        output.WriteLine();
        output.WriteLine($"{files.Count} file(s). `{ProductInfo.Cli} {spec.Name} --write` installs them in {rel}; point your agent at {rel}/SKILL.md.");
        return CliApp.ExitOk;
    }
}
