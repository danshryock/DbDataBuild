using System.Diagnostics;
using DbDataBuild.Core;
using DbDataBuild.Define;
using DbDataBuild.Execution;
using DbDataBuild.Models;
using DbDataBuild.Planning;
using DbDataBuild.Sql.Analysis;
using DbDataBuild.Targets.Rendering;

namespace DbDataBuild.Cli;

internal static class CommandTargets
{
    /// <summary>The connection names of the project (the engine-named ones included), for checks that run before the project is fully loaded. A damaged project file gives the engine names; the command that reads it reports the damage.</summary>
    public static IReadOnlyList<string> NamesOf(string projectRoot) =>
        [.. ProjectConfigLoader.LoadFromProject(projectRoot, []).Connections.Keys.Order(StringComparer.Ordinal)];

    /// <summary>The connection a command works on: the flag, or the project's only default connection.</summary>
    public static ConnectionConfig? Resolve(ProjectConfig config, string? arg, TextWriter error)
    {
        var name = arg ?? (config.DefaultConnections.Count == 1 ? config.DefaultConnections[0] : null);
        if (name == null) { error.WriteLine($"--connection is required: the project has {config.DefaultConnections.Count} default connections ({string.Join(", ", config.DefaultConnections)})."); return null; }
        if (!config.Connections.TryGetValue(name, out var connection)) { error.WriteLine($"Unknown connection `{name}`. One of: {string.Join(", ", config.Connections.Keys.Order(StringComparer.Ordinal))}."); return null; }
        return connection;
    }
}

internal static class GitInfo
{
    /// <summary>
    /// The files (relative to <paramref name="root"/>, with `/`) that differ from <paramref name="gitRef"/> in the working tree, committed or not, and the untracked ones. Returns the reason instead when git cannot say
    /// (not a repository, no such ref).
    /// </summary>
    public static (HashSet<string>? Files, string? Error) ChangedFiles(string root, string gitRef)
    {
        (string? Out, string Err) Git(string args)
        {
            try
            {
                var psi = new ProcessStartInfo("git", args) { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                using var p = Process.Start(psi);
                if (p == null) return (null, "git could not be started");
                // both streams are read at once: git can write enough to stderr (line-ending warnings, on Windows) to block while standard output is being read
                var o = p.StandardOutput.ReadToEndAsync();
                var e = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(60_000)) { try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } return (null, "git did not finish within a minute"); }
                return (p.ExitCode == 0 ? o.GetAwaiter().GetResult() : null, e.GetAwaiter().GetResult().Split('\n')[0].Trim());
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return (null, "git is not available"); }
        }
        var diff = Git($"diff --name-only --relative {Quote(gitRef)} --");
        if (diff.Out == null) return (null, $"git could not compare with `{gitRef}`: {(diff.Err.Length > 0 ? diff.Err : "unknown error")}");
        var untracked = Git("ls-files --others --exclude-standard");
        var files = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in (diff.Out + "\n" + (untracked.Out ?? "")).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) files.Add(line.Replace('\\', '/'));
        return (files, null);
    }

    private static string Quote(string arg) => "\"" + arg.Replace("\"", "\\\"") + "\"";

    /// <summary>The commit and whether the working tree has uncommitted changes. Null commit when this is not a git repository or git is unavailable.</summary>
    public static (string? Commit, bool Dirty) Read(string root)
    {
        static string? Run(string root, string args)
        {
            try
            {
                var psi = new ProcessStartInfo("git", args) { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                using var p = Process.Start(psi);
                if (p == null) return null;
                var o = p.StandardOutput.ReadToEndAsync();
                p.StandardError.ReadToEndAsync();                                  // read at once: a full error pipe would block git while standard output is awaited
                if (!p.WaitForExit(60_000)) { try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } return null; }
                return p.ExitCode == 0 ? o.GetAwaiter().GetResult().Trim() : null;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
        }
        var commit = Run(root, "rev-parse HEAD");
        if (commit == null) return (null, false);
        // untracked files inside plans/ and .dbdatabuild/ are this tool's own output and do not make the tree dirty
        var status = Run(root, "status --porcelain -- . :(exclude)plans :(exclude).dbdatabuild");
        return (commit, !string.IsNullOrEmpty(status));
    }
}

/// <summary>
/// What `plan` and `check` share: the offline preflight, the read login, the live snapshot and the planner input. Nothing here writes to the target
/// or to the repository.
/// </summary>
internal sealed class PlanningSession
{
    public required string Root { get; init; }
    public required string Target { get; init; }
    public required ProjectContext Context { get; init; }
    public required PlanInput Input { get; init; }
    public required IReadOnlyList<Diagnostic> Warnings { get; init; }

    /// <summary>Returns the session, or null and the exit code after printing why not.</summary>
    public static (PlanningSession? Session, int Exit) Prepare(CommandSpec spec, string root, string? targetArg, string[] models, TextWriter output, TextWriter error, Func<string, string?> env,
        IReadOnlyDictionary<string, string>? operations = null, IReadOnlySet<string>? backfills = null)
    {
        var ctx = ProjectContext.Load(root);
        var connection = CommandTargets.Resolve(ctx.Config, targetArg, error);
        if (connection == null) return (null, CliApp.ExitUsage);
        var target = connection.Name; var engine = connection.Engine;

        var (login, missing) = LoginSettings.FromEnvironment(connection.Name, connection.Engine, Login.Read, env);
        output.WriteLine($"{ProductInfo.Cli} {spec.Name}  |  effect: {spec.Effect.Describe()}  |  connection: {target}  |  login: {login?.Describe() ?? "none"}");
        output.Payload("effect", spec.Effect.Describe());
        output.Payload("login", login?.Describe());
        output.WriteLine($"Effective: {ctx.Config.Describe()}");
        if (missing != null) { error.Diag(missing); return (null, CliApp.ExitFindings); }

        var selected = ctx.Select(models, error);
        if (selected == null) return (null, CliApp.ExitUsage);
        foreach (var named in (operations ?? new Dictionary<string, string>()).Keys.Concat(backfills ?? new HashSet<string>()))
            if (!ctx.Project.Sources.Any(m => m.Definition.Name == named)) { error.WriteLine($"`{named}` is not a model of this project."); return (null, CliApp.ExitUsage); }
        var mine = selected.Where(m => ctx.TargetsOf(m.Source.Definition).Contains(target)).ToList();
        foreach (var skipped in selected.Except(mine)) output.WriteLine($"note: {skipped.Source.Definition.Name} does not declare target `{target}` and is not planned.");

        // ---- offline preflight: nothing is planned from a project that does not validate (DESIGN.md 11) ----
        var findings = new List<Diagnostic>(ctx.Diagnostics.Where(d => d.Code != DiagnosticCatalog.OrphanFile.Code));
        var sources = mine.Select(m => m.Source).ToList();
        findings.AddRange(ProjectChecks.Run(sources, ctx.Config, [target], root, ctx.Lowering));
        var defineTargets = mine.Select(m => new DefineTarget(m.Source.Definition.Name, m.Source.DefinitionFile, m.Source.QueryFile, m.Sql,
            File.ReadAllText(Path.Combine(root, m.Source.DefinitionFile)), m.Source.Definition, [])).ToList();
        var graph = new ModelGraph(ctx.Project.Models, ctx.Project.AllDescriptors);
        findings.AddRange(new DefineEngine(graph, ctx.Config, ctx.Linter).Check(defineTargets));

        var renderedOps = new List<RenderedOperation>();
        foreach (var m in mine)
        {
            var (result, _) = ctx.RenderModel(m.Source, m.Sql, [target]);
            renderedOps.AddRange(result.Loads);
            foreach (var file in result.Files)
            {
                var path = Path.Combine(root, RenderCommand.RenderedDir, file.Path);
                if (!File.Exists(path)) findings.Add(new Diagnostic(DiagnosticCatalog.RenderedFileOutOfDate, new($"{RenderCommand.RenderedDir}/{file.Path}", 0, 0), $"`{RenderCommand.RenderedDir}/{file.Path}` is missing."));
                else if (File.ReadAllText(path) != file.Content) findings.Add(new Diagnostic(DiagnosticCatalog.RenderedFileOutOfDate, new($"{RenderCommand.RenderedDir}/{file.Path}", 0, 0), $"`{RenderCommand.RenderedDir}/{file.Path}` differs from a fresh render."));
            }
        }
        var distinct = findings.DistinctBy(d => (d.Code, d.Location, d.Found)).ToList();
        foreach (var d in distinct.Where(d => d.Severity != Severity.Error)) error.Diag(d);
        var errors = distinct.Where(d => d.Severity == Severity.Error).ToList();
        if (errors.Count > 0)
        {
            foreach (var d in errors) error.Diag(d);
            output.WriteLine($"Nothing was planned: {errors.Count} error(s) in the project. Fix them (`{ProductInfo.Cli} validate`, `{ProductInfo.Cli} define --check`, `{ProductInfo.Cli} render --check` show them).");
            return (null, CliApp.ExitFindings);
        }

        // ---- the live side, on the read login ----
        var planned = mine.Select(m =>
        {
            var hash = AstHasher.Hash(m.Sql).Hash ?? "";
            // a copy reads its staging table, which the plan's own transfer step creates: it has no base table to wait for
            var bases = m.Source.Definition.IsCopy ? [] : QueryAnalyzer.Analyze(m.Sql).Facts?.BaseTables.Select(t => t.QualifiedName).ToList() ?? [];
            // views are transpiled from the lowered query too (errors were reported in the preflight, so a failed lowering here is not reachable)
            var body = ctx.Lowering.Enabled && ctx.Lowering.Lower(m.Source, m.Sql).Model is { } lowered ? lowered.Sql : m.Sql;
            return new PlannedModel(m.Source.Definition, body, m.Source.QueryFile, hash, bases, HookLoader.Load(m.Source, ctx.Config, target, root, new List<Diagnostic>()), ctx.OriginOf(m.Source.Definition));
        }).ToList();

        TargetSnapshot snapshot;
        var resolved = new Dictionary<string, ResolverOutcome>();
        var rangeBounds = new Dictionary<string, ColumnBounds>();
        try
        {
            using var cts = new CancellationTokenSource();
            (snapshot, resolved, rangeBounds) = Task.Run(async () =>
            {
                await using var read = await ReadSession.OpenAsync(login!);
                var status = await TrackingStore.StatusAsync(read, engine, ctx.Config.TrackingSchema);
                if (status.AsDiagnostic(ctx.Config.TrackingSchema) is { } notReady) throw new GateRefusedException(notReady);
                var snap = await TargetSnapshotReader.ReadAsync(read, engine, ctx.Config.TrackingSchema, planned.Select(p => DdlSchema(p.Definition.Name)));
                var results = new Dictionary<string, ResolverOutcome>();
                foreach (var op in renderedOps.Where(o => (operations != null && operations.TryGetValue(o.Model, out var chosen) ? o.Operation == chosen : o.IsDefault) && o.Resolver != null && snap.Live.ContainsKey(o.Model)))
                {
                    var type = op.Parameters.First(p => p.Source == "resolver").Type;
                    var r = await TargetSnapshotReader.RunResolverAsync(read, op.Resolver!, type);
                    results[PlanInput.ResolverKey(op.Model, op.Operation)] = new ResolverOutcome(r.Value, r.Error);
                }
                // what the target holds in the column a chosen range load works on, so the plan can say whether the range overlaps it
                var bounds = new Dictionary<string, ColumnBounds>();
                foreach (var op in renderedOps.Where(o => (operations != null && operations.TryGetValue(o.Model, out var chosen) ? o.Operation == chosen : o.IsDefault) && snap.Live.ContainsKey(o.Model)))
                {
                    var def = planned.First(p => p.Definition.Name == op.Model).Definition;
                    if (RangeLoads.ColumnOf(def, target, op.Operation) is not { } column) continue;
                    var type = def.Columns.FirstOrDefault(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase))?.Type ?? "TIMESTAMP";
                    var (min, max, err) = await TargetSnapshotReader.ColumnBoundsAsync(read, engine, op.Model, column, type);
                    bounds[PlanInput.ResolverKey(op.Model, op.Operation)] = new ColumnBounds(min, max, err);
                }
                return (snap, results, bounds);
            }).GetAwaiter().GetResult();
        }
        catch (GateRefusedException ex)
        {
            error.Diag(ex.Diagnostic);
            return (null, CliApp.ExitFindings);
        }

        var loads = renderedOps.GroupBy(o => o.Model).ToDictionary(g => g.Key, g => (IReadOnlyList<RenderedLoad>)g
            .Select(o => new RenderedLoad(o.Operation, o.IsDefault, o.Script, DbDataBuild.State.Hashing.ScriptHash(o.Script), o.Resolver, o.Parameters, o.Watermark)).ToList());
        var input = new PlanInput(target, ctx.Config, planned, snapshot.Live, snapshot.Schemas, snapshot.RecordedShapeHashes, snapshot.LastViewStatementHashes,
            snapshot.LastLoadDefinitionHashes, snapshot.Acknowledged, loads, resolved, operations, backfills, rangeBounds);
        return (new PlanningSession { Root = root, Target = target, Context = ctx, Input = input, Warnings = distinct.Where(d => d.Severity != Severity.Error).ToList() }, CliApp.ExitOk);
    }

    private static string DdlSchema(string model) => DbDataBuild.Targets.Ddl.DdlGenerator.Split(model).Schema;
}
