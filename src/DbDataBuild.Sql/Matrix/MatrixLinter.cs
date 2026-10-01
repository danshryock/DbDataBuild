using System.Text.Json;
using DbDataBuild.Core;
using DbDataBuild.Sql.Ast;

namespace DbDataBuild.Sql.Matrix;

/// <summary>
/// Maps the AST of a model body (DuckDB dialect) onto the support matrix and reports, per declared target, what each
/// construct costs. Offline: parses only, never connects. Constructs the matrix does not cover are reported, not assumed safe.
/// </summary>
public sealed class MatrixLinter(SupportMatrix matrix)
{
    private static readonly string[] QueryRoots = ["select", "union", "intersect", "except", "subquery"];

    // Select fields that may be non-null without the matrix saying anything: the clauses the passing cases exercised.
    private static readonly HashSet<string> CoveredSelectFields =
    [
        "expressions", "from", "joins", "where_clause", "group_by", "having", "qualify", "order_by", "limit", "offset",
        "distinct", "distinct_on", "sample", "with", "leading_comments",
    ];

    private static readonly HashSet<string> CoveredJoinKinds = ["Inner", "Left", "Right", "Full", "Cross", "Natural"];

    public IReadOnlyList<Diagnostic> Lint(string sql, string file, IReadOnlyList<string> targets)
    {
        var diags = new List<Diagnostic>();
        var parsed = Polyglot.Parse(sql, Dialects.Canonical);
        if (!parsed.Ok)
        {
            diags.Add(new Diagnostic(DiagnosticCatalog.SqlParseFailure, new(file, 0, 0), $"The DuckDB parser reported: {parsed.Error}"));
            return diags;
        }

        var root = AstNode.Parse(parsed.Data!);
        if (root.Children.Count != 1 || !QueryRoots.Contains(root.Children[0].Type))
        {
            diags.Add(new Diagnostic(DiagnosticCatalog.NotASingleSelect, new(file, 0, 0),
                root.Children.Count != 1 ? $"The file contains {root.Children.Count} statements." : $"The statement is a {root.Children[0].Type}, not a query."));
            return diags;
        }

        var reportedUncovered = new HashSet<string>();
        foreach (var node in root.Descendants())
        {
            var (line, col) = node.Location();
            var loc = new SourceLocation(file, line, col);
            var matched = matrix.Rows.Where(r => r.Detect.Any(d => Matches(d, node))).ToList();

            foreach (var row in matched)
            {
                var rowLoc = LocateRow(row, node, file) ?? loc;
                foreach (var target in targets)
                    diags.AddRange(Report(row, target, rowLoc));
            }

            if (!matched.Any(r => r.Detect.Any(d => d.Kind is DetectKind.Node or DetectKind.Function)))
                CheckCovered(node, loc, reportedUncovered, diags);
            CheckClauses(node, loc, reportedUncovered, diags);
        }
        return diags;
    }

    private static SourceLocation? LocateRow(ConstructRow row, AstNode node, string file)
    {
        foreach (var d in row.Detect.Where(d => d.Kind == DetectKind.Detector && Matches(d, node)))
            if (Detectors.LocationField.TryGetValue(d.Name, out var field) && node.LocationOfField(field) is var (line, col))
                return new SourceLocation(file, line, col);
        return null;
    }

    private static bool Matches(DetectRule rule, AstNode node) => rule.Kind switch
    {
        DetectKind.Node => node.Type == rule.Name,
        DetectKind.Function => node.Type == "function" && string.Equals(node.GetString("name"), rule.Name, StringComparison.OrdinalIgnoreCase),
        DetectKind.Detector => Detectors.All[rule.Name](node),
        _ => false,
    };

    private IEnumerable<Diagnostic> Report(ConstructRow row, string target, SourceLocation loc)
    {
        if (!row.Targets.TryGetValue(target, out var entry)) yield break;
        var note = string.IsNullOrEmpty(entry.Note) ? "" : " " + entry.Note;
        string Found(string verb) => $"`{row.Id}` is {verb} on {target}.{note}";

        switch (entry.Status)
        {
            case SupportStatus.Unsupported:
                yield return new Diagnostic(DiagnosticCatalog.ConstructUnsupported, loc, Found("unsupported"),
                    Fix: $"Rewrite the model without `{row.Id}`, or remove `{target}` from `targets:`.");
                break;
            case SupportStatus.Approximated:
                yield return new Diagnostic(DiagnosticCatalog.ConstructApproximated, loc, Found("approximated"));
                break;
            case SupportStatus.Emulated:
                yield return new Diagnostic(DiagnosticCatalog.ConstructEmulated, loc, Found("emulated"));
                break;
            case SupportStatus.Unverified:
                yield return new Diagnostic(DiagnosticCatalog.ConstructUnverified, loc, Found("unverified"));
                break;
        }
        if (entry.MinVersion is { } v && entry.Status is not SupportStatus.Unsupported)
            yield return new Diagnostic(DiagnosticCatalog.ConstructNeedsVersion, loc,
                $"`{row.Id}` on {target} needs engine version {v} or later.", Fix: $"Confirm the {target} version is at least {v}, or avoid `{row.Id}`.");
    }

    private void CheckCovered(AstNode node, SourceLocation loc, HashSet<string> seen, List<Diagnostic> diags)
    {
        if (node.Type == "function")
        {
            var name = node.GetString("name") ?? "<unnamed>";
            if (!matrix.IsCoveredFunction(name) && seen.Add($"fn:{name}:{loc.Line}:{loc.Column}"))
                diags.Add(NotCovered(loc, $"function `{name.ToUpperInvariant()}`"));
            return;
        }
        if (!matrix.IsCoveredNode(node.Type) && seen.Add($"node:{node.Type}:{loc.Line}:{loc.Column}"))
            diags.Add(NotCovered(loc, $"expression `{node.Type}`"));

        if (node.Type is "cast" or "try_cast" && node.TryGet("to", out var to) && to.ValueKind == JsonValueKind.Object &&
            to.TryGetProperty("data_type", out var dt) && dt.GetString() is { } type &&
            !matrix.Covered.Any(c => c.Kind == DetectKind.DataType && c.Name == type) && seen.Add($"type:{type}:{loc.Line}:{loc.Column}"))
            diags.Add(NotCovered(loc, $"data type `{type}` in a cast"));
    }

    private static void CheckClauses(AstNode node, SourceLocation loc, HashSet<string> seen, List<Diagnostic> diags)
    {
        if (node.Type != "select") return;
        foreach (var p in node.Body.EnumerateObject())
            if (!CoveredSelectFields.Contains(p.Name) && Detectors.NonNull(node, p.Name) && seen.Add($"clause:{p.Name}:{loc.Line}:{loc.Column}"))
                diags.Add(NotCovered(loc, $"clause `{p.Name}`"));

        foreach (var j in Detectors.Joins(node))
            if (j.TryGetProperty("kind", out var k) && k.GetString() is { } kind && !CoveredJoinKinds.Contains(kind) && seen.Add($"join:{kind}:{loc.Line}:{loc.Column}"))
                diags.Add(NotCovered(loc, $"join kind `{kind}`"));

        if (node.TryGet("with", out var with) && with.ValueKind == JsonValueKind.Object &&
            with.TryGetProperty("recursive", out var rec) && rec.ValueKind == JsonValueKind.True && seen.Add($"recursive:{loc.Line}:{loc.Column}"))
            diags.Add(NotCovered(loc, "recursive CTE"));
    }

    private static Diagnostic NotCovered(SourceLocation loc, string what) =>
        new(DiagnosticCatalog.ConstructNotCovered, loc, $"The {what} is not covered by the support matrix, so its behavior on the targets is unknown.");
}
