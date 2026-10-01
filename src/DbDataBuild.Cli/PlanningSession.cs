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
    /// <summary>The target a command works on: the flag, or the project's only default target.</summary>
    public static string? Resolve(ProjectConfig config, string? arg, TextWriter error)
    {
        var target = arg ?? (config.DefaultTargets.Count == 1 ? config.DefaultTargets[0] : null);
        if (target == null) { error.WriteLine($"--target is required: the project has {config.DefaultTargets.Count} default targets ({string.Join(", ", config.DefaultTargets)})."); return null; }
        if (!TargetNames.All.Contains(target)) { error.WriteLine($"Unknown target `{target}`. One of: {string.Join(", ", TargetNames.All)}."); return null; }
        return target;
    }
}

internal static class GitInfo
{
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
                var o = p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit();
                return p.ExitCode == 0 ? o.Trim() : null;
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
    public static (PlanningSession? Session, int Exit) Prepare(CommandSpec spec, string root, string? targetArg, string[] models, TextWriter output, TextWriter error, Func<string, string?> env)
    {
        var ctx = ProjectContext.Load(root);
        var target = CommandTargets.Resolve(ctx.Config, targetArg, error);
        if (target == null) return (null, CliApp.ExitUsage);

        var (login, missing) = LoginSettings.FromEnvironment(target, Login.Read, env);
        output.WriteLine($"{ProductInfo.Cli} {spec.Name}  |  effect: {spec.Effect.Describe()}  |  target: {target}  |  login: {login?.Describe() ?? "none"}");
        output.WriteLine($"Effective: {ctx.Config.Describe()}");
        if (missing != null) { error.Write(DiagnosticFormatter.Format(missing)); return (null, CliApp.ExitFindings); }

        var selected = ctx.Select(models, error);
        if (selected == null) return (null, CliApp.ExitUsage);
        var mine = selected.Where(m => ctx.TargetsOf(m.Source.Definition).Contains(target)).ToList();
        foreach (var skipped in selected.Except(mine)) output.WriteLine($"note: {skipped.Source.Definition.Name} does not declare target `{target}` and is not planned.");

        // ---- offline preflight: nothing is planned from a project that does not validate (DESIGN.md 11) ----
        var findings = new List<Diagnostic>(ctx.Diagnostics.Where(d => d.Code != DiagnosticCatalog.OrphanFile.Code));
        var sources = mine.Select(m => m.Source).ToList();
        findings.AddRange(ProjectChecks.Run(sources, ctx.Config, [target], root));
        var defineTargets = mine.Select(m => new DefineTarget(m.Source.Definition.Name, m.Source.DefinitionFile, m.Source.QueryFile, m.Sql,
            File.ReadAllText(Path.Combine(root, m.Source.DefinitionFile)), m.Source.Definition, [])).ToList();
        var graph = new ModelGraph(ctx.Project.Models, ctx.Project.Descriptors);
        findings.AddRange(new DefineEngine(graph, ctx.Config, ctx.Linter).Check(defineTargets));

        var renderedOps = new List<RenderedOperation>();
        foreach (var m in mine)
        {
            var result = ctx.Renderer.Render(m.Source.Definition, m.Sql, m.Source.QueryFile, [target]);
            renderedOps.AddRange(result.Loads);
            foreach (var file in result.Files)
            {
                var path = Path.Combine(root, RenderCommand.RenderedDir, file.Path);
                if (!File.Exists(path)) findings.Add(new Diagnostic(DiagnosticCatalog.RenderedFileOutOfDate, new($"{RenderCommand.RenderedDir}/{file.Path}", 0, 0), $"`{RenderCommand.RenderedDir}/{file.Path}` is missing."));
                else if (File.ReadAllText(path) != file.Content) findings.Add(new Diagnostic(DiagnosticCatalog.RenderedFileOutOfDate, new($"{RenderCommand.RenderedDir}/{file.Path}", 0, 0), $"`{RenderCommand.RenderedDir}/{file.Path}` differs from a fresh render."));
            }
        }
        var distinct = findings.DistinctBy(d => (d.Code, d.Location, d.Found)).ToList();
        foreach (var d in distinct.Where(d => d.Severity != Severity.Error)) error.WriteLine(DiagnosticFormatter.Format(d));
        var errors = distinct.Where(d => d.Severity == Severity.Error).ToList();
        if (errors.Count > 0)
        {
            foreach (var d in errors) error.WriteLine(DiagnosticFormatter.Format(d));
            output.WriteLine($"Nothing was planned: {errors.Count} error(s) in the project. Fix them (`{ProductInfo.Cli} validate`, `{ProductInfo.Cli} define --check`, `{ProductInfo.Cli} render --check` show them).");
            return (null, CliApp.ExitFindings);
        }

        // ---- the live side, on the read login ----
        var planned = mine.Select(m =>
        {
            var hash = AstHasher.Hash(m.Sql).Hash ?? "";
            var bases = QueryAnalyzer.Analyze(m.Sql).Facts?.BaseTables.Select(t => t.QualifiedName).ToList() ?? [];
            return new PlannedModel(m.Source.Definition, m.Sql, m.Source.QueryFile, hash, bases);
        }).ToList();

        TargetSnapshot snapshot;
        var resolved = new Dictionary<string, ResolverOutcome>();
        try
        {
            using var cts = new CancellationTokenSource();
            (snapshot, resolved) = Task.Run(async () =>
            {
                await using var read = await ReadSession.OpenAsync(login!);
                var status = await TrackingStore.StatusAsync(read, target, ctx.Config.TrackingSchema);
                if (status.AsDiagnostic(ctx.Config.TrackingSchema) is { } notReady) throw new GateRefusedException(notReady);
                var snap = await TargetSnapshotReader.ReadAsync(read, target, ctx.Config.TrackingSchema, planned.Select(p => DdlSchema(p.Definition.Name)));
                var results = new Dictionary<string, ResolverOutcome>();
                foreach (var op in renderedOps.Where(o => o.IsDefault && o.Resolver != null && snap.Live.ContainsKey(o.Model)))
                {
                    var type = op.Parameters.First(p => p.Source == "resolver").Type;
                    var r = await TargetSnapshotReader.RunResolverAsync(read, op.Resolver!, type);
                    results[PlanInput.ResolverKey(op.Model, op.Operation)] = new ResolverOutcome(r.Value, r.Error);
                }
                return (snap, results);
            }).GetAwaiter().GetResult();
        }
        catch (GateRefusedException ex)
        {
            error.Write(DiagnosticFormatter.Format(ex.Diagnostic));
            return (null, CliApp.ExitFindings);
        }

        var loads = renderedOps.GroupBy(o => o.Model).ToDictionary(g => g.Key, g => (IReadOnlyList<RenderedLoad>)g
            .Select(o => new RenderedLoad(o.Operation, o.IsDefault, o.Script, DbDataBuild.State.Hashing.ScriptHash(o.Script), o.Resolver, o.Parameters, o.Watermark)).ToList());
        var input = new PlanInput(target, ctx.Config, planned, snapshot.Live, snapshot.Schemas, snapshot.RecordedShapeHashes, snapshot.LastViewStatementHashes,
            snapshot.LastLoadDefinitionHashes, snapshot.Acknowledged, loads, resolved);
        return (new PlanningSession { Root = root, Target = target, Context = ctx, Input = input, Warnings = distinct.Where(d => d.Severity != Severity.Error).ToList() }, CliApp.ExitOk);
    }

    private static string DdlSchema(string model) => DbDataBuild.Targets.Ddl.DdlGenerator.Split(model).Schema;
}
