using System.Text.RegularExpressions;
using DbDataBuild.Core;

namespace DbDataBuild.Models;

/// <summary>
/// A metadata rule (`tests/metadata/&lt;name&gt;.sql`, DESIGN.md 9.8): a DuckDB SELECT over the metadata views that returns the violations; no rows means it passes.
/// Its settings are `-- key: value` comments at the top of the file. <c>Severity</c> is `error` (fails the run) or `warning`; <c>Tags</c> name groups, so a run (and, later, a gate) can select them.
/// </summary>
public sealed record TestRule(string Name, string File, string Sql, string? Description, string Severity, IReadOnlyList<string> Tags);

public static partial class TestRuleLoader
{
    public const string Directory = "tests/metadata";
    private static readonly string[] Keys = ["description", "severity", "tags"];

    [GeneratedRegex(@"^--\s*([A-Za-z_][A-Za-z0-9_]*)\s*:\s*(.*?)\s*$")]
    private static partial Regex KeyLine();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9_\-]*$")]
    private static partial Regex TagName();

    /// <summary>The project-relative paths of the rule files, in order.</summary>
    public static IReadOnlyList<string> Discover(string projectRoot)
    {
        var dir = Path.Combine(projectRoot, Directory);
        if (!System.IO.Directory.Exists(dir)) return [];
        return System.IO.Directory.EnumerateFiles(dir, "*.sql", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(projectRoot, f).Replace('\\', '/')).OrderBy(f => f, StringComparer.Ordinal).ToList();
    }

    /// <summary>`tests/metadata/naming/no_max.sql` is the rule `naming.no_max`.</summary>
    public static string NameOf(string file) => file[(Directory.Length + 1)..^".sql".Length].Replace('/', '.');

    public static TestRule? Load(string text, string file, List<Diagnostic> diags)
    {
        var before = diags.Count;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        string? description = null, severity = null;
        List<string>? tags = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;
            if (!line.StartsWith("--", StringComparison.Ordinal)) break;            // the header is the comments before the first line of SQL
            if (KeyLine().Match(line) is not { Success: true } m) continue;          // prose is allowed
            var (key, value) = (m.Groups[1].Value, m.Groups[2].Value);
            var at = new SourceLocation(file, i + 1, 1);
            if (!Keys.Contains(key)) { diags.Add(new Diagnostic(DiagnosticCatalog.UnknownKey, at, $"`{key}` is not a setting of a metadata rule.", Fix: $"Use one of: {string.Join(", ", Keys)}. A comment that is not a setting should not start with `word:`.")); continue; }
            if (!seen.Add(key)) { diags.Add(new Diagnostic(DiagnosticCatalog.DuplicateKey, at, $"`{key}` is set more than once.")); continue; }
            switch (key)
            {
                case "description": description = value.Length == 0 ? null : value; break;
                case "severity":
                    if (value is "error" or "warning") severity = value;
                    else diags.Add(new Diagnostic(DiagnosticCatalog.InvalidValue, at, $"severity is `{value}`.", Fix: "error or warning (lowercase)."));
                    break;
                case "tags":
                    tags = [];
                    foreach (var t in value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (!TagName().IsMatch(t)) diags.Add(new Diagnostic(DiagnosticCatalog.InvalidValue, at, $"The tag `{t}` is not valid.", Fix: "Lowercase letters, digits, `_` and `-`, separated by commas or spaces."));
                        else if (!tags.Contains(t)) tags.Add(t);
                    }
                    break;
            }
        }
        if (!lines.Any(l => l.Trim() is { Length: > 0 } t && !t.StartsWith("--", StringComparison.Ordinal)))
            diags.Add(new Diagnostic(DiagnosticCatalog.TestNotASelect, new(file, 1, 1), "The file has no SQL, only comments."));
        return diags.Count > before ? null : new TestRule(NameOf(file), file, text, description, severity ?? "error", tags ?? []);
    }
}
