using System.Text.RegularExpressions;
using DbDataBuild.Core;
using DbDataBuild.Define;
using DbDataBuild.Execution;
using DbDataBuild.Models;
using DbDataBuild.State;

namespace DbDataBuild.Cli;

/// <summary>
/// `dbdatabuild import-sources` (effect: target read-only; with --write it also writes `sources/` files). Reads the columns, types, nullability and primary key of tables and views in the
/// target through the read login and exports them as source descriptors (DESIGN.md 6.5.1), so models over those tables bind offline against what the tables really are. It never
/// runs a query against the data and never changes the target. Without arguments it refreshes the descriptors the project already has.
/// The live table wins for columns, types and nullability; a committed grain and a column the catalog cannot type are kept (SourceImport).
/// </summary>
internal static class ImportSourcesCommand
{
    private sealed record Row(string Name, string Kind, string? File, string Status, IReadOnlyList<SourceChange> Changes, ImportedObject? Live, SourceDescriptor? Descriptor, string? NewText, string? OldText, string? ExpectedHash);
    private sealed record Skipped(string Object, string Reason);

    public static int Run(CommandSpec spec, string root, string? targetArg, string[] patterns, bool write, bool check, TextWriter output, TextWriter error, Func<string, string?> env)
    {
        if (write && check) { error.WriteLine("--check writes nothing, so it cannot be combined with --write."); return CliApp.ExitUsage; }
        var ctx = ProjectContext.Load(root);
        var target = CommandTargets.Resolve(ctx.Config, targetArg, error);
        if (target == null) return CliApp.ExitUsage;
        var (login, missing) = LoginSettings.FromEnvironment(target, Login.Read, env);

        var committed = ctx.Project.Descriptors.ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);
        var models = ctx.Project.Models.Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // what to look at: the patterns, or (none given) every descriptor the project has
        var wanted = new List<(Regex Schema, Regex Table, string? LiteralSchema)>();
        var refresh = patterns.Length == 0;
        if (refresh)
        {
            if (committed.Count == 0)
            {
                error.WriteLine($"The project has no source descriptors yet. Name what to import, for example `{ProductInfo.Cli} {spec.Name} staging.*` or `{ProductInfo.Cli} {spec.Name} sales.orders`.");
                return CliApp.ExitUsage;
            }
            foreach (var n in committed.Keys) { var (s, t) = Split(n)!.Value; wanted.Add((Glob(s), Glob(t), s)); }
        }
        else
            foreach (var p in patterns)
            {
                if (Split(p) is not { } st) { error.WriteLine($"`{p}` is not `schema.table`. Use `*` and `?` as wildcards, for example `staging.*` or `*.orders`."); return CliApp.ExitUsage; }
                wanted.Add((Glob(st.Schema), Glob(st.Table), st.Schema.AsSpan().IndexOfAny('*', '?') < 0 ? st.Schema : null));
            }

        output.WriteLine($"{ProductInfo.Cli} {spec.Name}  |  effect: {spec.Effect.Describe()}{(write ? " + writes sources/" : "")}  |  target: {target}  |  login: {login?.Describe() ?? "none"}");
        if (missing != null) { error.Diag(missing); return CliApp.ExitFindings; }

        var rows = new List<Row>();
        var skipped = new List<Skipped>();
        var diags = new List<Diagnostic>();
        try
        {
            Task.Run(async () =>
            {
                await using var read = await ReadSession.OpenAsync(login!);
                var allSchemas = await SourceCatalogReader.SchemasAsync(read, target);
                var schemas = allSchemas.Where(s => wanted.Any(w => w.Schema.IsMatch(s))).ToList();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var schema in schemas)
                {
                    if (string.Equals(schema, ctx.Config.TrackingSchema, StringComparison.OrdinalIgnoreCase)) { skipped.Add(new(schema, "the tracking schema holds the tool's own tables")); continue; }
                    var shapes = await CatalogReader.ReadSchemaAsync(read, target, schema);
                    var keys = await SourceCatalogReader.PrimaryKeysAsync(read, target, schema);
                    var foreignKeys = await SourceCatalogReader.ForeignKeysAsync(read, target, schema);
                    foreach (var (qualified, shape) in shapes.OrderBy(k => k.Key, StringComparer.Ordinal))
                    {
                        if (!wanted.Any(w => w.Schema.IsMatch(shape.Schema) && w.Table.IsMatch(shape.Name))) continue;
                        seen.Add(qualified);
                        if (models.Contains(qualified)) { skipped.Add(new(qualified, "it is a model, not a source")); continue; }
                        var live = SourceImport.Describe(target, shape, keys.GetValueOrDefault(shape.Name), foreignKeys.GetValueOrDefault(shape.Name));
                        if (live.File == null)
                        {
                            diags.Add(new Diagnostic(DiagnosticCatalog.SourceNotImportable, new(qualified, 0, 0), $"`{qualified}` has a dot, slash or backslash in its schema or table name, so it cannot be a path under sources/."));
                            skipped.Add(new(qualified, "its name cannot be a file name"));
                            continue;
                        }
                        rows.Add(Compare(root, live, committed.GetValueOrDefault(qualified), diags));
                    }
                }
                // descriptors the project has for tables the target does not show (in the schemas searched)
                foreach (var d in committed.Values.Where(d => wanted.Any(w => { var (s, t) = Split(d.Name)!.Value; return w.Schema.IsMatch(s) && w.Table.IsMatch(t); })))
                {
                    var (s, _) = Split(d.Name)!.Value;
                    if (seen.Contains(d.Name) || models.Contains(d.Name)) continue;
                    var file = SourceDescriptorWriter.PathFor(s, d.Name[(s.Length + 1)..]);
                    diags.Add(new Diagnostic(DiagnosticCatalog.SourceNotImportable, new(file ?? d.Name, 0, 0), $"`{d.Name}` has a source descriptor, but {target} shows no table or view of that name.", Fix: "Delete the descriptor or restore the table. The tool never deletes it for you."));
                    rows.Add(new Row(d.Name, "unknown", file, "stale", [], null, d, null, null, null));
                }
            }).GetAwaiter().GetResult();
        }
        catch (GateRefusedException ex) { error.Diag(ex.Diagnostic); return CliApp.ExitFindings; }

        rows = rows.OrderBy(r => r.Name, StringComparer.Ordinal).ToList();
        foreach (var d in diags) error.Diag(d);

        output.Payload("target", target);
        output.Payload("mode", write ? "write" : check ? "check" : "preview");
        output.Payload("sources", rows.Select(r => new
        {
            name = r.Name, kind = r.Kind, file = r.File, status = r.Status, grain = r.Descriptor?.Grain ?? [],
            indexes = (r.Descriptor?.Indexes ?? []).Select(i => new { name = i.Name, columns = i.Columns, unique = i.Unique, include = i.Include }).ToList(),
            foreign_keys = (r.Descriptor?.ForeignKeys ?? []).Select(f => new { name = f.Name, columns = f.Columns, references = new { table = f.Table, columns = f.ReferencedColumns } }).ToList(),
            notes = r.Live?.Notes ?? [],
            changes = r.Changes.Select(c => new { kind = c.Kind.ToString().ToLowerInvariant(), column = c.Column, detail = c.Detail }).ToList(),
            columns = r.Live?.Columns.Select(c => new { name = c.Name, native_type = c.NativeType, logical_type = c.LogicalType, fit = c.Fit.ToString().ToLowerInvariant(), reason = c.Reason.Length == 0 ? null : c.Reason, nullable = c.Nullable }).ToList(),
        }).ToList());
        output.Payload("skipped", skipped.Select(s => new { @object = s.Object, reason = s.Reason }).ToList());

        if (rows.Count == 0)
        {
            output.WriteLine(refresh ? "Nothing to refresh." : $"No table or view matched {string.Join(", ", patterns.Select(p => $"`{p}`"))} in {target}.");
            foreach (var s in skipped) output.WriteLine($"skipped {s.Object}: {s.Reason}");
            return CliApp.ExitOk;
        }

        output.WriteLine();
        foreach (var r in rows)
        {
            output.WriteLine($"{r.Status,-9} {r.Name}  ({r.Kind}{(r.Live != null ? $", {r.Live.Columns.Count} column(s)" : "")})");
            foreach (var note in r.Live?.Notes ?? []) output.WriteLine($"          note: {note}");
            foreach (var c in r.Live?.Columns ?? [])
                if (c.Fit != SourceTypeFit.Exact)
                    output.WriteLine($"          {c.Name}: {c.NativeType} -> {c.LogicalType ?? "no logical type"}{(c.Reason.Length > 0 ? " (" + c.Reason + ")" : "")}");
        }
        foreach (var s in skipped) output.WriteLine($"skipped   {s.Object}: {s.Reason}");

        var changed = rows.Where(r => r.Status is "new" or "changed").ToList();
        foreach (var r in changed) { output.WriteLine(); output.Write(UnifiedDiff.Create(r.OldText, r.NewText!, r.File!)); }
        var stale = rows.Count(r => r.Status == "stale");
        output.WriteLine();

        if (check)
        {
            if (changed.Count == 0 && stale == 0) { output.WriteLine($"{rows.Count} source descriptor(s) match {target}."); return CliApp.ExitOk; }
            foreach (var r in changed) error.Diag(new Diagnostic(DiagnosticCatalog.SourceOutOfSync, new(r.File!, 0, 0), $"`{r.File}` differs from {target}: {string.Join("; ", r.Changes.Select(c => c.Column == null ? c.Kind.ToString().ToLowerInvariant() : $"{c.Kind.ToString().ToLowerInvariant()} {c.Column} ({c.Detail})"))}."));
            output.WriteLine($"{changed.Count} descriptor(s) differ from {target}{(stale > 0 ? $", {stale} describe(s) no table" : "")}.");
            return CliApp.ExitFindings;
        }
        if (changed.Count == 0) { output.WriteLine($"{rows.Count} source descriptor(s) already match {target}; nothing to write."); return CliApp.ExitOk; }
        if (!write) { output.WriteLine($"{changed.Count} descriptor(s) would be written. Review the diff above, then run again with --write."); return CliApp.ExitOk; }

        var failed = false;
        var written = new List<string>();
        foreach (var r in changed)
        {
            var path = Path.Combine(root, r.File!);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var problem = DefinitionFile.WriteIfUnchanged(path, r.File!, r.ExpectedHash, r.NewText!);
            if (problem != null) { error.Diag(problem); failed = true; }
            else { output.WriteLine($"wrote {r.File}"); written.Add(r.File!); }
        }
        output.Payload("written", written);
        output.WriteLine($"Wrote {written.Count} descriptor(s). Run `{ProductInfo.Cli} validate` and `{ProductInfo.Cli} define --check` to see what they change for the models.");
        return failed ? CliApp.ExitFindings : CliApp.ExitOk;
    }

    private static Row Compare(string root, ImportedObject live, SourceDescriptor? committed, List<Diagnostic> diags)
    {
        var path = Path.Combine(root, live.File!);
        var exists = File.Exists(path);
        var kind = live.Kind.ToString().ToLowerInvariant();
        if (exists && committed == null)
        {
            // a file is there that did not load: a damaged descriptor is never overwritten unseen
            diags.Add(new Diagnostic(DiagnosticCatalog.InvalidValue, new(live.File!, 0, 0), $"`{live.File}` exists but is not a valid source descriptor, so it was left alone.", Fix: $"Fix it (`{ProductInfo.Cli} validate` shows what is wrong), or delete it and import again."));
            return new Row(live.QualifiedName, kind, live.File, "invalid", [], live, null, null, null, null);
        }
        foreach (var c in live.Columns.Where(c => c.LogicalType == null))
        {
            var kept = committed?.Columns.Any(x => string.Equals(x.Name, c.Name, StringComparison.OrdinalIgnoreCase)) == true;
            diags.Add(new Diagnostic(DiagnosticCatalog.SourceColumnNoLogicalType, new(live.File!, 0, 0),
                $"`{live.QualifiedName}.{c.Name}` is {c.NativeType}: {c.Reason}. {(kept ? "The committed declaration was kept." : "The column was left out of the descriptor.")}"));
        }
        var descriptor = SourceImport.ToDescriptor(live, committed);
        var changes = SourceImport.Compare(committed, descriptor);
        var oldText = exists ? File.ReadAllText(path) : null;
        var hash = exists ? DefinitionFile.Hash(File.ReadAllBytes(path)) : null;
        var status = committed == null ? "new" : changes.Count == 0 ? "unchanged" : "changed";
        return new Row(live.QualifiedName, kind, live.File, status, changes, live, descriptor, status is "new" or "changed" ? SourceDescriptorWriter.Yaml(descriptor) : null, oldText, hash);
    }

    private static (string Schema, string Table)? Split(string name)
    {
        var i = name.IndexOf('.');
        return i <= 0 || i == name.Length - 1 ? null : (name[..i], name[(i + 1)..]);
    }

    private static Regex Glob(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
