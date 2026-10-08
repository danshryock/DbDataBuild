using DbDataBuild.Core;
using DbDataBuild.Execution;
using DbDataBuild.Models;
using DbDataBuild.Planning;
using DbDataBuild.State;

namespace DbDataBuild.Cli;

/// <summary>
/// `dbdatabuild connection refresh`: run the routine loads of a connection from the refresh plan `project compile` wrote. It reads that plan and the committed scripts, not the models; it asks the connection
/// nothing about its structure unless the chosen check says to (`refresh.check`: none, project, objects, live); it never changes structure, and each run is an event of its own. Anything that is not a routine
/// load was left out of the plan when it was compiled, and a deploy handles it.
/// </summary>
internal static class RefreshCommand
{
    public static int Run(CommandSpec spec, string root, string? targetArg, string[] models, string? checkArg, string? onFailArg, bool allowDirty, bool dryRun, TextWriter output, TextWriter error, Func<string, string?> env)
    {
        var configDiags = new List<Diagnostic>();
        var config = ProjectConfigLoader.LoadFromProject(root, configDiags);
        foreach (var d in configDiags.Where(d => d.Severity == Severity.Error)) error.Diag(d);
        if (configDiags.Any(d => d.Severity == Severity.Error)) return CliApp.ExitFindings;
        var connection = CommandTargets.Resolve(config, targetArg, error);
        if (connection == null) return CliApp.ExitUsage;

        var settings = config.LifecycleOf(connection.Name);
        var check = settings.Check; var onFail = settings.OnFail;
        if (checkArg != null && !Enum.TryParse(checkArg, ignoreCase: true, out check)) { error.WriteLine($"--check is none, project, objects or live, not `{checkArg}`."); return CliApp.ExitUsage; }
        if (onFailArg != null && !Enum.TryParse(onFailArg, ignoreCase: true, out onFail)) { error.WriteLine($"--on-fail is block or warn, not `{onFailArg}`."); return CliApp.ExitUsage; }

        var (read, readMissing) = LoginSettings.FromEnvironment(connection.Name, connection.Engine, Login.Read, env);
        output.WriteLine($"{ProductInfo.Cli} {spec.Name}  |  effect: {spec.Effect.Describe()}  |  {spec.Marks}{(dryRun ? " (DRY RUN: nothing will be executed)" : "")}  |  connection: {connection.Name}  |  check: {LifecycleSettings.Name(check)}");
        if (readMissing != null) { error.Diag(readMissing); output.WriteLine("Nothing was executed."); return CliApp.ExitFindings; }

        // ---- the compiled plan, and the committed files it names ----
        var planPath = Path.Combine(root, RenderCommand.RenderedDir, connection.Name, RefreshPlanDocument.FileName);
        var relative = Path.GetRelativePath(root, planPath).Replace('\\', '/');
        if (!File.Exists(planPath))
        {
            error.Diag(new Diagnostic(DiagnosticCatalog.RefreshPlanMissing, new(relative, 0, 0), $"There is no `{relative}`."));
            output.WriteLine("Nothing was executed.");
            output.Next("project compile");
            return CliApp.ExitFindings;
        }
        var planText = File.ReadAllText(planPath);
        var parseDiags = new List<Diagnostic>();
        var compiled = RefreshPlanDocument.Parse(planText, relative, parseDiags);
        if (compiled == null) { foreach (var d in parseDiags) error.Diag(d); output.WriteLine("Nothing was executed."); output.Next("project compile"); return CliApp.ExitFindings; }
        if (compiled.Connection != connection.Name)
        {
            error.Diag(new Diagnostic(DiagnosticCatalog.PlanFileInvalid, new(relative, 0, 0), $"The refresh plan is for `{compiled.Connection}`, not `{connection.Name}`."));
            return CliApp.ExitFindings;
        }

        var loads = compiled.Loads.Where(l => models.Length == 0 || models.Any(m => Matches(m, l.Model))).ToList();
        if (models.Length > 0 && loads.Count == 0)
        {
            error.WriteLine($"No routine load of `{connection.Name}` matches {string.Join(", ", models.Select(m => $"`{m}`"))}. The refresh plan has: {string.Join(", ", compiled.Loads.Select(l => l.Model))}.");
            return CliApp.ExitUsage;
        }
        output.WriteLine($"Refresh plan {relative}: {loads.Count} routine load(s){(compiled.Excluded.Count > 0 ? $"; {compiled.Excluded.Count} model(s) are not routine (a deploy handles them): {string.Join(", ", compiled.Excluded.Take(3).Select(x => x.Model))}{(compiled.Excluded.Count > 3 ? ", …" : "")}" : "")}.");
        if (loads.Count == 0) { output.WriteLine("Nothing to do: no load applies."); return CliApp.ExitOk; }

        var scripts = new Dictionary<string, string>(StringComparer.Ordinal);
        var changed = new List<Diagnostic>();
        string? Read(string file, string hash, string what)
        {
            var path = Path.GetFullPath(Path.Combine(file.StartsWith("hooks/", StringComparison.Ordinal) || !file.Contains('/') ? root : Path.Combine(root, RenderCommand.RenderedDir), file));
            if (!File.Exists(path)) { changed.Add(new Diagnostic(DiagnosticCatalog.RefreshFileChanged, new(file, 0, 0), $"The {what} `{file}`, which the refresh plan names, does not exist.")); return null; }
            var text = File.ReadAllText(path);
            if (Hashing.ScriptHash(text) != hash) { changed.Add(new Diagnostic(DiagnosticCatalog.RefreshFileChanged, new(file, 0, 0), $"The {what} `{file}` is not the one the refresh plan was compiled from (its content has another hash).")); return null; }
            return text;
        }
        foreach (var l in loads)
        {
            if (Read(l.File, l.FileHash, "load script") is { } script) scripts[l.File] = script;
            if (l.ResolverFile != null && Read(l.ResolverFile, l.ResolverHash ?? "", "resolver") is { } resolver) scripts[l.ResolverFile] = resolver;
            foreach (var h in l.Before.Concat(l.After))
                if (Read(h.File, h.FileHash, "hook script") is { } hook) scripts[h.File] = hook;
        }
        if (changed.Count > 0) { foreach (var d in changed) error.Diag(d); output.WriteLine("Nothing was executed."); output.Next("project compile"); return CliApp.ExitFindings; }

        // ---- the check, and the values only the connection knows ----
        var tracking = config.TrackingOf(connection.Name);
        LoginSettings? trackingRead = null;
        if (tracking.Target is { } tt && tt.Connection != connection.Name)
        {
            var (tr, trm) = LoginSettings.FromEnvironment(tt.Connection, tt.Engine, Login.Read, env);
            if (tr == null) { error.Diag(trm!); output.WriteLine("Nothing was executed."); return CliApp.ExitFindings; }
            trackingRead = tr;
        }
        var scope = tracking.Target is { } ts ? new TrackingScope(ts.Engine, ts.SchemaName, connection.Name) : null;
        var findings = new List<Diagnostic>();
        var steps = new List<PlanStep>();
        if (RefreshChecks.NeedsTracking(check, compiled, scope) is { } untracked) findings.Add(untracked);
        // a check that cannot run (nothing to read it from) stops a blocking refresh before the connection is opened
        if (!(findings.Count > 0 && onFail == OnFail.Block))
            try
            {
                Task.Run(async () =>
                {
                    await using var data = await ReadSession.OpenAsync(read!);
                    await using var own = trackingRead == null ? null : await ReadSession.OpenAsync(trackingRead);
                    var track = scope == null ? null : own ?? data;
                    if (check != RefreshCheck.None && findings.Count == 0) findings.AddRange(await RefreshChecks.RunAsync(check, compiled, loads, connection.Engine, data, track, scope));
                    if (findings.Count > 0 && onFail == OnFail.Block) return;
                    foreach (var l in loads) steps.AddRange(await StepsOfAsync(compiled, l, scripts, data, connection.Engine, findings, onFail));
                }).GetAwaiter().GetResult();
            }
            catch (GateRefusedException ex) { error.Diag(ex.Diagnostic); output.WriteLine("Nothing was executed."); return CliApp.ExitFindings; }

        foreach (var d in findings) error.Diag(d with { SeverityOverride = onFail == OnFail.Warn ? Severity.Warning : null });
        output.Payload("check", LifecycleSettings.Name(check));
        output.Payload("on_fail", LifecycleSettings.Name(onFail));
        output.Payload("loads", loads.Select(l => new { model = l.Model, operation = l.Operation }).ToList());
        output.Payload("findings", findings.Select(f => f.Code).ToList());
        if (findings.Count > 0 && onFail == OnFail.Block)
        {
            output.WriteLine($"Nothing was executed: {findings.Count} finding(s) from the {LifecycleSettings.Name(check)} check. A deploy brings the connection to the structure the project expects; `--check none` skips the check and `--on-fail warn` runs anyway.");
            output.Next("connection deploy");
            return CliApp.ExitFindings;
        }
        if (steps.Count == 0) { output.WriteLine("Nothing to do: no load applies."); return CliApp.ExitOk; }

        // ---- run it as an event of its own ----
        var (commit, dirty) = GitInfo.Read(root);
        var id = PlanDocument.CreateRunId(DateTime.UtcNow);
        var plan = new Plan(id, connection.Name, commit, dirty, ProductInfo.Version, [], [], steps.Select((s, i) => s with { Id = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) }).ToList(), []);
        var audit = settings.Refresh.Audit;
        return ApplyCommand.RunPlan(spec, plan, audit == AuditLevel.Full ? planText : "", relative, root, dryRun, allowRisky: false, allowDestructive: [], allowDirty,
            new ApplyCommand.ApplyMode(Refresh: true, PlanHash: RefreshPlanDocument.ContentHash(compiled), RecordShapes: check == RefreshCheck.Live, Header: false), output, error, env);
    }

    /// <summary>The steps of one load: the hooks before it, the load with the values found now, the hooks after it. A resolver that finds nothing falls back to the initial value the model declared, or stops the load.</summary>
    private static async Task<IEnumerable<PlanStep>> StepsOfAsync(RefreshPlan plan, RefreshLoad l, Dictionary<string, string> scripts, ReadSession read, string engine, List<Diagnostic> findings, OnFail onFail)
    {
        var parameters = new List<PlanParameter>();
        string? resolverText = null, resolverResult = null;
        foreach (var p in l.Parameters)
        {
            if (p.Source != "resolver") { parameters.Add(new(p.Name, p.Type, "parameter", p.Value)); continue; }
            resolverText = scripts[l.ResolverFile!];
            var found = await TargetSnapshotReader.RunResolverAsync(read, resolverText, p.Type);
            if (found.Error != null)
            {
                findings.Add(new Diagnostic(DiagnosticCatalog.ResolverResultInvalid, new(l.Model, 0, 0), $"{l.Model} / {l.Operation}: the resolver for `@{p.Name}` {found.Error}. A table that was never deployed has no watermark."));
                return [];
            }
            resolverResult = found.Value;
            var value = found.Value;
            if (value == null)
            {
                if (l.OnNull == WatermarkSpec.InitialLiteral && l.Initial != null) value = l.Initial;
                else
                {
                    findings.Add(new Diagnostic(DiagnosticCatalog.ResolverResultInvalid, new(l.Model, 0, 0), $"{l.Model} / {l.Operation}: the resolver for `@{p.Name}` returned NULL and the model says `on_null: require_param`, so a person gives the value (a deploy asks for it)."));
                    return [];
                }
            }
            parameters.Add(new(p.Name, p.Type, "resolver", value));
        }
        PlanStep Hook(RefreshHook h) => new("", StepType.Hook, l.Model, $"hook {h.Name} ({h.Event})", scripts[h.File], RiskClass.Safe, ["hook.fired", $"event {h.Event}"], null, [], Operation: h.Event, FileHash: h.FileHash, Hook: h.Name, Effect: h.Effect);
        var load = new PlanStep("", StepType.Load, l.Model, $"load {l.Model} ({l.Operation})", scripts[l.File], RiskClass.Safe, ["load.routine"], null, parameters, resolverText, resolverResult, HasResolver: resolverText != null,
            FileHash: l.FileHash, Operation: l.Operation, DefinitionHash: l.DefinitionHash);
        return l.Before.Select(Hook).Append(load).Concat(l.After.Select(Hook)).ToList();
    }

    /// <summary>A model name, or a pattern with `*` and `?`.</summary>
    private static bool Matches(string pattern, string model) =>
        pattern == model || System.Text.RegularExpressions.Regex.IsMatch(model, "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
}

/// <summary>The safety checks before a refresh (`refresh.check`), from cheapest to strictest. Each compares what the compiled project expects of the objects the refresh uses with something recorded or live.</summary>
internal static class RefreshChecks
{
    /// <summary>The levels that read the tracking tables cannot run when nothing is tracked for the connection.</summary>
    public static Diagnostic? NeedsTracking(RefreshCheck level, RefreshPlan plan, TrackingScope? scope) =>
        level is RefreshCheck.Project or RefreshCheck.Objects && scope == null
            ? new Diagnostic(DiagnosticCatalog.RefreshCheckFailed, new($"connection:{plan.Connection}", 0, 0), $"The `{LifecycleSettings.Name(level)}` check reads the tracking tables, and nothing is tracked for `{plan.Connection}`. Use `--check live` (the catalog) or `--check none`.")
            : null;

    public static async Task<IReadOnlyList<Diagnostic>> RunAsync(RefreshCheck level, RefreshPlan plan, IReadOnlyList<RefreshLoad> loads, string engine, ReadSession data, ReadSession? tracking, TrackingScope? scope)
    {
        var findings = new List<Diagnostic>();
        Diagnostic Behind(string what, string found) => new(DiagnosticCatalog.RefreshCheckFailed, new(what, 0, 0), found);
        if (level != RefreshCheck.Live && (tracking == null || scope == null))
        {
            findings.Add(Behind($"connection:{plan.Connection}", $"The `{LifecycleSettings.Name(level)}` check reads the tracking tables, and nothing is tracked for `{plan.Connection}`. Use `--check live` (the catalog) or `--check none`."));
            return findings;
        }
        if (scope != null && tracking != null)
        {
            var status = await TrackingStore.StatusAsync(tracking, scope.Engine, scope.SchemaName);
            if (status.AsDiagnostic(scope.SchemaName) is { } notReady) return [notReady];
        }
        var expected = plan.Requires.Where(r => loads.Any(l => l.Model == r.Object)).ToList();
        switch (level)
        {
            case RefreshCheck.Project:
            {
                var (deployed, eventId) = await AuditLog.LastDeployedProjectAsync(tracking!, scope!);
                if (deployed == null) findings.Add(Behind($"connection:{plan.Connection}", "No completed deploy of this connection recorded the project's structure, so it cannot be said that this project was deployed here."));
                else if (deployed != plan.ProjectHash) findings.Add(Behind($"connection:{plan.Connection}", $"The project expects structure {plan.ProjectHash[..12]}, but the last completed deploy ({eventId}) recorded {deployed[..12]}."));
                break;
            }
            case RefreshCheck.Objects:
            {
                var recorded = await TrackingStore.LatestShapeHashesAsync(tracking!, scope!);
                foreach (var r in expected)
                    if (!recorded.TryGetValue(r.Object, out var have)) findings.Add(Behind(r.Object, $"`{r.Object}` has never been deployed by the tool (no recorded shape)."));
                    else if (have != r.ShapeHash) findings.Add(Behind(r.Object, $"`{r.Object}` was last deployed with shape {have[..12]}, but the project expects {r.ShapeHash[..12]}."));
                break;
            }
            case RefreshCheck.Live:
            {
                var schemas = expected.Select(r => DbDataBuild.Targets.Ddl.DdlGenerator.Split(r.Object).SchemaName);
                var snapshot = await TargetSnapshotReader.ReadAsync(data, tracking, scope, engine, schemas);
                foreach (var r in expected)
                {
                    var live = snapshot.Live.GetValueOrDefault(r.Object);
                    if (live == null) findings.Add(Behind(r.Object, $"`{r.Object}` does not exist on the connection."));
                    else if (scope != null && Drift.Classify(live, snapshot.RecordedShapeHashes.GetValueOrDefault(r.Object)) == ObjectState.OutOfBand)
                        findings.Add(Behind(r.Object, $"`{r.Object}` was changed outside the tool: its shape is {live.ShapeHash[..12]}, the last one recorded is {snapshot.RecordedShapeHashes[r.Object][..12]}, and the project expects {r.ShapeHash[..12]}."));
                    else if (live.ShapeHash != r.ShapeHash) findings.Add(Behind(r.Object, $"`{r.Object}` has shape {live.ShapeHash[..12]} on the connection, but the project expects {r.ShapeHash[..12]}."));
                }
                break;
            }
        }
        return findings;
    }
}
