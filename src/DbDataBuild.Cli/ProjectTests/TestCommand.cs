using System.Globalization;
using DbDataBuild.Core;
using DbDataBuild.Models;
using DbDataBuild.Targets.DuckDb;

namespace DbDataBuild.Cli;

/// <summary>
/// `dbdatabuild test` (effect: offline only; DESIGN.md 9.8). Runs the project's tests: today the metadata rules in `tests/metadata/`, DuckDB SELECTs over the metadata views that return the
/// violations. Everything runs in an in-memory DuckDB; no target is contacted and nothing is written. Tests can be selected by name or path and by tag; an `error` rule that returns rows
/// (or cannot run) makes the run fail, a `warning` rule only reports (unless `--strict`).
/// </summary>
internal static class TestCommand
{
    private sealed record Outcome(TestRule? Rule, string Name, string File, string Status, int Violations, IReadOnlyList<string> Columns, IReadOnlyList<Dictionary<string, object?>> Rows, string? Error);

    public static int Run(CommandSpec spec, string root, string[] names, string[] tags, int limit, bool strict, TextWriter output, TextWriter error)
    {
        output.WriteLine($"{ProductInfo.Cli} {spec.Name}  |  effect: {spec.Effect.Describe()}  |  target: none");
        if (limit < 0) { error.WriteLine("--limit must not be negative."); return CliApp.ExitUsage; }

        var files = TestRuleLoader.Discover(root);
        var selected = Select(files, names, error);
        if (selected == null) return CliApp.ExitUsage;

        var ctx = ProjectContext.Load(root);
        var invalid = ctx.Diagnostics.Where(d => d.Severity == Severity.Error && d.Code != DiagnosticCatalog.OrphanFile.Code).ToList();
        foreach (var d in invalid) error.Diag(d);
        if (invalid.Count > 0)
        {
            output.WriteLine($"No tests were run: {invalid.Count} error(s) in the project. Fix them (`{ProductInfo.Cli} validate` shows them).");
            return CliApp.ExitFindings;
        }

        // load every selected file first: a damaged file is a finding of its own and never stops the others
        var outcomes = new List<Outcome>();
        var rules = new List<TestRule>();
        foreach (var file in selected)
        {
            var problems = new List<Diagnostic>();
            var rule = TestRuleLoader.Load(File.ReadAllText(Path.Combine(root, file)), file, problems);
            if (rule == null)
            {
                foreach (var p in problems) error.Diag(p);
                outcomes.Add(new Outcome(null, TestRuleLoader.NameOf(file), file, "error", 0, [], [], string.Join(" ", problems.Select(p => p.Found))));
            }
            else if (tags.Length == 0 || rule.Tags.Any(t => tags.Contains(t, StringComparer.Ordinal))) rules.Add(rule);
        }

        if (rules.Count > 0)
        {
            using var db = MetadataDatabase.Open(MetadataPublisher.Collect(ctx, null, plan: null).Select(d => new MetadataDocument(d.Kind, d.Subject, d.Json, d.Hash)), ProductInfo.Version);
            foreach (var rule in rules) outcomes.Add(Evaluate(db, rule, limit, error));
        }
        outcomes = outcomes.OrderBy(o => o.Name, StringComparer.Ordinal).ToList();

        var counts = new
        {
            total = outcomes.Count,
            passed = outcomes.Count(o => o.Status == "pass"),
            failed = outcomes.Count(o => o.Status == "fail"),
            warned = outcomes.Count(o => o.Status == "warn"),
            errored = outcomes.Count(o => o.Status == "error"),
        };
        output.Payload("counts", counts);
        output.Payload("tests", outcomes.Select(o => new
        {
            name = o.Name, kind = "metadata", file = o.File, description = o.Rule?.Description, severity = o.Rule?.Severity ?? "error", tags = o.Rule?.Tags ?? [],
            status = o.Status, violations = o.Violations, columns = o.Columns, rows = o.Rows, error = o.Error,
        }).ToList());

        if (outcomes.Count == 0)
        {
            output.WriteLine(files.Count == 0 ? $"No tests: {TestRuleLoader.Directory}/ has no .sql files." : "No tests matched.");
            return CliApp.ExitOk;
        }
        output.WriteLine();
        foreach (var o in outcomes)
        {
            var label = o.Status switch { "pass" => "pass", "fail" => "FAIL", "warn" => "warn", _ => "ERROR" };
            output.WriteLine($"{label,-5} {o.Name}{(o.Violations > 0 ? $"  ({o.Violations} violation(s))" : "")}{(o.Rule?.Description is { } d ? "  " + d : "")}");
            if (o.Error != null && o.Rule != null) output.WriteLine($"      {o.Error}");
            foreach (var row in o.Rows) output.WriteLine("      " + string.Join(" | ", row.Select(kv => $"{kv.Key}={Show(kv.Value)}")));
            if (o.Violations > o.Rows.Count) output.WriteLine($"      ... and {o.Violations - o.Rows.Count} more (--limit)");
        }
        output.WriteLine();
        output.WriteLine($"{counts.total} test(s): {counts.passed} passed, {counts.failed} failed, {counts.warned} warned, {counts.errored} could not run.");
        return counts.failed + counts.errored > 0 || (strict && counts.warned > 0) ? CliApp.ExitFindings : CliApp.ExitOk;
    }

    private static List<string>? Select(IReadOnlyList<string> files, string[] names, TextWriter error)
    {
        if (names.Length == 0) return files.ToList();
        var chosen = new List<string>();
        foreach (var n in names)
        {
            var path = n.Replace('\\', '/');
            var match = files.FirstOrDefault(f => f == path || TestRuleLoader.NameOf(f) == n);
            if (match == null)
            {
                error.WriteLine($"No test named `{n}`. Tests: {(files.Count == 0 ? "none" : string.Join(", ", files.Select(TestRuleLoader.NameOf)))}.");
                return null;
            }
            if (!chosen.Contains(match)) chosen.Add(match);
        }
        return chosen;
    }

    private static Outcome Evaluate(MetadataDatabase db, TestRule rule, int limit, TextWriter error)
    {
        var run = db.Run(rule.Sql, limit);
        if (run.Outcome != RuleOutcome.Ran)
        {
            var d = run.Outcome == RuleOutcome.NotASelect
                ? new Diagnostic(DiagnosticCatalog.TestNotASelect, new(rule.File, 1, 1), $"`{rule.Name}`: {run.Message}")
                : new Diagnostic(DiagnosticCatalog.TestCouldNotRun, new(rule.File, 1, 1), $"`{rule.Name}` could not run: {run.Message}");
            error.Diag(d);
            return new Outcome(rule, rule.Name, rule.File, "error", 0, [], [], run.Message);
        }
        if (run.Count == 0) return new Outcome(rule, rule.Name, rule.File, "pass", 0, run.Columns, [], null);

        var warning = rule.Severity == "warning";
        var first = run.Rows.Count > 0 ? string.Join(", ", run.Rows[0].Select(kv => $"{kv.Key}={Show(kv.Value)}")) : "";
        error.Diag(new Diagnostic(DiagnosticCatalog.TestFailed, new(rule.File, 1, 1), $"`{rule.Name}` returned {run.Count} violation(s){(first.Length > 0 ? $", the first: {first}" : "")}.",
            SeverityOverride: warning ? Severity.Warning : null));
        return new Outcome(rule, rule.Name, rule.File, warning ? "warn" : "fail", run.Count, run.Columns, run.Rows, null);
    }

    private static string Show(object? v) => v switch
    {
        null => "NULL",
        System.Collections.IEnumerable list and not string => "[" + string.Join(", ", list.Cast<object?>().Select(Show)) + "]",
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };
}
