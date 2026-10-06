using DbDataBuild.Core;
using DbDataBuild.Execution;
using DbDataBuild.Models;
using DbDataBuild.State;

namespace DbDataBuild.Cli;

/// <summary>
/// `dbdatabuild diff` (effect: target read-only; DESIGN.md 9.10). Compares the data of two tables or views of one target, for example a model's table against a copy in a development schema: the schemas, the row
/// counts, and a full outer join on a key inside the engine that counts the rows on one side only and the rows whose values differ, column by column. By default no value is read back (counts only), so an operator can
/// work without seeing real data; `--show-values` asks for the smallest and largest value of each column and up to --limit sample rows of each kind of difference, which then appear in the output you asked for
/// and nowhere else (not in the statement log, not in a plan).
/// </summary>
internal static class DiffCommand
{
    public static int Run(CommandSpec spec, string root, string table, string? against, string? againstSchema, string? targetArg, string[] keyArg, string[] only, string[] except, bool showValues, int limit,
        TextWriter output, TextWriter error, Func<string, string?> env)
    {
        if (limit < 0) { error.WriteLine("--limit must not be negative."); return CliApp.ExitUsage; }
        if ((against == null) == (againstSchema == null)) { error.WriteLine("Name the other table with exactly one of --against <schema.table> or --against-schema <schema> (the same table name in another schema)."); return CliApp.ExitUsage; }
        if (Split(table) is not { } left) { error.WriteLine($"`{table}` is not `schema.table`."); return CliApp.ExitUsage; }
        (string Schema, string Name) right;
        if (against != null)
        {
            if (Split(against) is not { } r) { error.WriteLine($"`{against}` is not `schema.table`."); return CliApp.ExitUsage; }
            right = r;
        }
        else right = (againstSchema!, left.Name);
        if (string.Equals(left.Schema, right.Schema, StringComparison.OrdinalIgnoreCase) && string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase)) { error.WriteLine("Both sides name the same table."); return CliApp.ExitUsage; }

        var ctx = ProjectContext.Load(root);
        var connection = CommandTargets.Resolve(ctx.Config, targetArg, error);
        if (connection == null) return CliApp.ExitUsage;
        var target = connection.Name; var engine = connection.Engine;

        // the key: given, or the table's own grain (a model's grain or unique key, a source's grain)
        var key = keyArg.SelectMany(k => k.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
        var keySource = "option";
        if (key.Count == 0)
        {
            var qualified = $"{left.Schema}.{left.Name}";
            var model = ctx.Project.Sources.FirstOrDefault(s => string.Equals(s.Definition.Name, qualified, StringComparison.OrdinalIgnoreCase))?.Definition;
            var descriptor = ctx.Project.Descriptors.FirstOrDefault(d => string.Equals(d.Name, qualified, StringComparison.OrdinalIgnoreCase));
            if (model is { Grain.Count: > 0 }) (key, keySource) = (model.Grain.ToList(), "grain");
            else if (model is { UniqueKey.Count: > 0 }) (key, keySource) = (model.UniqueKey.ToList(), "unique_key");
            else if (descriptor is { Grain.Count: > 0 }) (key, keySource) = (descriptor.Grain.ToList(), "grain");
            else { error.WriteLine($"{qualified} has no grain or unique key in the project, so there is nothing to match rows on. Give the columns with --key a,b."); return CliApp.ExitUsage; }
        }

        var (login, missing) = LoginSettings.FromEnvironment(connection.Name, connection.Engine, Login.Read, env);
        output.WriteLine($"{ProductInfo.Cli} {spec.Name}  |  effect: {spec.Effect.Describe()}  |  target: {target}  |  login: {login?.Describe() ?? "none"}  |  values: {(showValues ? "shown (requested)" : "not read")}");
        if (missing != null) { error.Diag(missing); return CliApp.ExitFindings; }

        DiffPlan? plan = null;
        DiffOutcome? outcome = null;
        string? problem = null;
        try
        {
            Task.Run(async () =>
            {
                await using var read = await ReadSession.OpenAsync(login!);
                async Task<DiffTable?> Find((string Schema, string Name) t)
                {
                    var shapes = await CatalogReader.ReadSchemaAsync(read, engine, t.Schema);
                    var shape = shapes.Values.FirstOrDefault(s => string.Equals(s.Name, t.Name, StringComparison.OrdinalIgnoreCase));
                    return shape == null ? null : new DiffTable(shape.Schema, shape.Name, shape.Columns);
                }
                var l = await Find(left);
                if (l == null) { problem = $"{left.Schema}.{left.Name} is not a table or view of {target} (or the read login cannot see it)."; return; }
                var r = await Find(right);
                if (r == null) { problem = $"{right.Schema}.{right.Name} is not a table or view of {target} (or the read login cannot see it)."; return; }
                (plan, problem) = TableDiffer.Plan(engine, l, r, key, only, except);
                if (plan == null) return;
                outcome = await TableDiffer.RunAsync(read, plan, showValues, limit);
            }).GetAwaiter().GetResult();
        }
        catch (GateRefusedException ex) { error.Diag(ex.Diagnostic); return CliApp.ExitFindings; }
        if (plan == null || outcome == null) { error.WriteLine(problem); return CliApp.ExitUsage; }

        Report(output, plan, outcome, keySource, showValues, limit, target);
        return outcome.Identical ? CliApp.ExitOk : CliApp.ExitFindings;
    }

    private static (string Schema, string Name)? Split(string text)
    {
        var i = text.IndexOf('.');
        return i <= 0 || i == text.Length - 1 ? null : (text[..i], text[(i + 1)..]);
    }

    private static void Report(TextWriter output, DiffPlan p, DiffOutcome o, string keySource, bool showValues, int limit, string target)
    {
        var others = p.Compared.Where(c => !p.Key.Contains(c.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        output.Payload("target", target);
        output.Payload("left", new { table = p.Left.Qualified, rows = o.LeftRows, columns = p.Left.Columns.Count });
        output.Payload("right", new { table = p.Right.Qualified, rows = o.RightRows, columns = p.Right.Columns.Count });
        output.Payload("key", new { columns = p.Key, source = keySource });
        output.Payload("schema", new
        {
            compared_columns = p.Compared.Select(c => c.Name).ToList(),
            only_left = p.OnlyLeft, only_right = p.OnlyRight,
            not_compared = p.Skipped.Select(s => new { column = s.Column, left_type = s.Left, right_type = s.Right, reason = s.Reason }).ToList(),
            type_differences = p.Compared.Where(c => !string.Equals(Type(c.Left), Type(c.Right), StringComparison.OrdinalIgnoreCase)).Select(c => new { column = c.Name, left_type = Type(c.Left), right_type = Type(c.Right) }).ToList(),
        });
        output.Payload("duplicate_keys", new { left = o.LeftDuplicateKeys, right = o.RightDuplicateKeys });
        output.Payload("rows", new
        {
            compared = o.RowsCompared, only_left = o.OnlyLeft, only_right = o.OnlyRight, matched = o.Matched, differing = o.Differing,
            differing_by_column = others.Where(c => o.DifferingByColumn.GetValueOrDefault(c.Name) > 0).ToDictionary(c => c.Name, c => o.DifferingByColumn[c.Name]),
        });
        output.Payload("column_stats", o.Stats.Select(s => new { column = s.Column, left_non_null = s.LeftNonNull, right_non_null = s.RightNonNull, left_min = s.LeftMin, left_max = s.LeftMax, right_min = s.RightMin, right_max = s.RightMax }).ToList());
        output.Payload("samples", showValues ? new
        {
            only_left = o.OnlyLeftSamples.Select(s => new { key = s.Key, values = s.Values }).ToList(),
            only_right = o.OnlyRightSamples.Select(s => new { key = s.Key, values = s.Values }).ToList(),
            differing = o.DifferingSamples.Select(s => new { key = s.Key, columns = s.Columns.ToDictionary(c => c.Key, c => new { left = c.Value.Left, right = c.Value.Right }) }).ToList(),
        } : null);
        output.Payload("identical", o.Identical);

        output.WriteLine();
        output.WriteLine($"{p.Left.Qualified}  ({o.LeftRows} row(s), {p.Left.Columns.Count} column(s))   against   {p.Right.Qualified}  ({o.RightRows} row(s), {p.Right.Columns.Count} column(s))");
        output.WriteLine($"matched on {string.Join(", ", p.Key)} ({keySource})");
        output.WriteLine();
        output.WriteLine("Columns");
        output.WriteLine($"  compared: {p.Compared.Count}{(others.Count < p.Compared.Count ? $" ({p.Key.Count} of them the key)" : "")}");
        if (p.OnlyLeft.Count > 0) output.WriteLine($"  only in {p.Left.Qualified}: {string.Join(", ", p.OnlyLeft)}");
        if (p.OnlyRight.Count > 0) output.WriteLine($"  only in {p.Right.Qualified}: {string.Join(", ", p.OnlyRight)}");
        foreach (var c in p.Compared.Where(c => !string.Equals(Type(c.Left), Type(c.Right), StringComparison.OrdinalIgnoreCase))) output.WriteLine($"  type differs: {c.Name} is {Type(c.Left)} and {Type(c.Right)}");
        foreach (var s in p.Skipped) output.WriteLine($"  not compared: {s.Column} ({s.Reason})");

        output.WriteLine();
        if (!o.RowsCompared)
        {
            output.WriteLine($"Rows were not compared: the key {string.Join(", ", p.Key)} is not unique ({o.LeftDuplicateKeys} duplicated key(s) on the left, {o.RightDuplicateKeys} on the right). Choose columns that identify one row with --key.");
            return;
        }
        output.WriteLine("Rows");
        output.WriteLine($"  matched on the key: {o.Matched}   only in {p.Left.Qualified}: {o.OnlyLeft}   only in {p.Right.Qualified}: {o.OnlyRight}");
        output.WriteLine($"  matched rows whose values differ: {o.Differing}");
        foreach (var c in others.Where(c => o.DifferingByColumn.GetValueOrDefault(c.Name) > 0))
            output.WriteLine($"    {c.Name}: {o.DifferingByColumn[c.Name]}");
        var nulls = o.Stats.Where(s => s.LeftNonNull != s.RightNonNull).ToList();
        foreach (var s in nulls) output.WriteLine($"  non-NULL values of {s.Column}: {s.LeftNonNull} against {s.RightNonNull}");

        if (showValues)
        {
            foreach (var s in o.Stats.Where(s => s.LeftMin != null || s.RightMin != null))
                output.WriteLine($"  {s.Column}: {s.LeftMin} .. {s.LeftMax}   against   {s.RightMin} .. {s.RightMax}");
            string Show(IReadOnlyDictionary<string, string?> d) => string.Join(", ", d.Select(kv => $"{kv.Key}={kv.Value ?? "NULL"}"));
            if (o.OnlyLeftSamples.Count > 0) { output.WriteLine($"\nOnly in {p.Left.Qualified} (first {o.OnlyLeftSamples.Count} of {o.OnlyLeft}):"); foreach (var s in o.OnlyLeftSamples) output.WriteLine($"  {Show(s.Key)} | {Show(s.Values)}"); }
            if (o.OnlyRightSamples.Count > 0) { output.WriteLine($"\nOnly in {p.Right.Qualified} (first {o.OnlyRightSamples.Count} of {o.OnlyRight}):"); foreach (var s in o.OnlyRightSamples) output.WriteLine($"  {Show(s.Key)} | {Show(s.Values)}"); }
            if (o.DifferingSamples.Count > 0)
            {
                output.WriteLine($"\nDifferent values (first {o.DifferingSamples.Count} of {o.Differing}):");
                foreach (var s in o.DifferingSamples) output.WriteLine($"  {Show(s.Key)}: {string.Join("; ", s.Columns.Select(c => $"{c.Key} {c.Value.Left ?? "NULL"} -> {c.Value.Right ?? "NULL"}"))}");
            }
        }
        else if (o.OnlyLeft + o.OnlyRight + o.Differing > 0) output.WriteLine($"\nNo value was read. Add --show-values (and --limit {Math.Max(limit, 10)}) to see the keys and values behind these counts.");

        output.WriteLine();
        output.WriteLine(o.Identical ? "The tables are identical on the compared columns." : "The tables differ.");
    }

    private static string Type(ColumnShape c) =>
        c.Type + (c.Precision != null ? $"({c.Precision}{(c.Scale != null ? $",{c.Scale}" : "")})" : c.Length != null ? $"({(c.Length == -1 ? "max" : c.Length.ToString())})" : "");
}
