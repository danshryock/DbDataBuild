using DbDataBuild.Core;
using DbDataBuild.Models;
using DbDataBuild.Sample;
using DbDataBuild.Sql.Analysis;

namespace DbDataBuild.Cli;

/// <summary>
/// `dbdatabuild sample` (DESIGN.md 15.2). Effect class: offline only. Runs models on generated or supplied sample data in an in-memory DuckDB and shows what they return.
/// Models they read from run first, so a downstream model sees its upstream model's output. Nothing is connected to or written.
/// </summary>
internal static class SampleCommand
{
    public static int Run(CommandSpec spec, string projectRoot, string[] models, int rows, int seed, int limit, int? scale, string? dataDir, bool showSources, TextWriter output, TextWriter error)
    {
        output.WriteLine($"{ProductInfo.Cli} {spec.Name}  |  effect: {spec.Effect.Describe()}  |  connection: none");
        if (rows < 1 || rows > 100_000) { error.WriteLine("--rows must be between 1 and 100000."); return CliApp.ExitUsage; }
        if (limit < 0) { error.WriteLine("--limit cannot be negative."); return CliApp.ExitUsage; }
        if (dataDir != null && !Directory.Exists(dataDir)) { error.WriteLine($"--data directory `{dataDir}` does not exist."); return CliApp.ExitUsage; }

        var ctx = ProjectContext.Load(projectRoot);
        var selected = ctx.Select(models, error);
        if (selected == null) return CliApp.ExitUsage;
        if (selected.Count == 0) { error.WriteLine("The project has no valid models."); return CliApp.ExitUsage; }

        var all = ctx.Project.Sources.Select(s =>
        {
            var sql = s.ReadQuery(projectRoot);
            var upstream = QueryAnalyzer.Analyze(sql).Facts?.BaseTables.Select(t => t.QualifiedName).ToList() ?? [];
            return new SampleModel(s.Definition.Name, s.Definition.Columns, sql, upstream);
        }).ToList();

        SampleResult result;
        try { result = SampleRun.Run(ctx.Project.AllDescriptors, all, selected.Select(m => m.Source.Definition.Name).ToList(), new SampleOptions(rows, seed, limit, dataDir, SeedLoader.Load(projectRoot), scale)); }
        catch (SampleException ex) { error.WriteLine(ex.Message); return CliApp.ExitFindings; }

        var tables = result.Tables.Where(t => t.Kind == "model" || showSources || t.Error != null).ToList();
        output.Payload("rows_per_source", rows);
        output.Payload("seed", seed);
        output.Payload("scale", scale);
        output.Payload("limit", limit);
        output.Payload("tables", tables.Select(t => new
        {
            name = t.Name, kind = t.Kind, origin = t.Origin, columns = t.Columns.Select(c => new { name = c.Name, type = c.Type }).ToList(),
            row_count = t.RowCount, rows = t.Rows, error = t.Error, warnings = t.Warnings,
        }).ToList());

        var failed = 0;
        foreach (var t in tables)
        {
            output.WriteLine();
            output.WriteLine($"{t.Name}  ({t.Kind}{(t.Origin != null ? ", " + t.Origin : "")}{(t.Error == null ? $", {t.RowCount} row(s)" : "")})");
            if (t.Error != null) { failed++; error.Diag(new Diagnostic(DiagnosticCatalog.QueryNotDescribable, new(t.Name, 0, 0), $"{t.Name} failed on sample data: {t.Error}")); output.WriteLine($"  failed: {t.Error}"); continue; }
            foreach (var w in t.Warnings) output.WriteLine($"  warning: {w}");
            Table(output, t);
            if (t.RowCount > t.Rows.Count) output.WriteLine($"  ... {t.RowCount - t.Rows.Count} more row(s); --limit shows more.");
        }
        output.WriteLine();
        output.WriteLine(failed == 0 ? $"OK: {tables.Count(t => t.Kind == "model")} model(s) ran on sample data (seed {seed})." : $"FAILED: {failed} table(s) did not run.");
        return failed == 0 ? CliApp.ExitOk : CliApp.ExitFindings;
    }

    private static void Table(TextWriter output, SampleTable t)
    {
        const int Max = 28;
        string Cell(string? v) => v == null ? "NULL" : v.Length > Max ? v[..(Max - 1)] + "…" : v.Replace("\n", " ");
        var header = t.Columns.Select(c => c.Name).ToArray();
        var widths = header.Select((h, i) => Math.Max(h.Length, t.Rows.Count == 0 ? 0 : t.Rows.Max(r => Cell(r[i]).Length))).ToArray();
        string Line(IEnumerable<string> cells) => "  " + string.Join("  ", cells.Select((c, i) => c.PadRight(widths[i]))).TrimEnd();
        output.WriteLine(Line(header));
        output.WriteLine(Line(widths.Select(w => new string('-', w))));
        foreach (var r in t.Rows) output.WriteLine(Line(r.Select(Cell)));
    }
}
