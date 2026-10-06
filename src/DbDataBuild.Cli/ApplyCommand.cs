using DbDataBuild.Apply;
using DbDataBuild.Core;
using DbDataBuild.Execution;
using DbDataBuild.Models;
using DbDataBuild.Planning;

namespace DbDataBuild.Cli;

/// <summary>
/// `dbdatabuild apply &lt;plan&gt;` (DESIGN.md 10.3). Effect class: target writes, exactly what the plan states. It refuses a plan that was edited, a stale plan, a plan that needs
/// an allowance it was not given, and a dirty working tree. `--dry-run` runs the same checks and the same code path and executes nothing.
/// </summary>
internal static class ApplyCommand
{
    /// <summary>`metadata.store_on_apply`: after a successful apply, store the project, the touched models and the plan as JSON documents. A failure here never turns a good apply into a bad one.</summary>
    private static void StoreMetadata(Plan plan, string root, ProjectConfig config, LoginSettings read, LoginSettings write, string? commit, TextWriter output, TextWriter error)
    {
        try
        {
            var ctx = ProjectContext.Load(root);
            if (ctx.Diagnostics.Any(d => d.Severity == Severity.Error && d.Code != DiagnosticCatalog.OrphanFile.Code))
            {
                output.WriteLine("note: metadata was not stored: the project has errors now (`validate` shows them).");
                return;
            }
            var touched = plan.Steps.Select(s => s.Object).Distinct(StringComparer.Ordinal).ToList();
            var docs = MetadataPublisher.Collect(ctx, touched, plan);
            var stored = Task.Run(() => MetadataPublisher.PublishAsync(docs, plan.Target, config.TrackingSchema, read, write, "apply-metadata", root, plan.Id, commit)).GetAwaiter().GetResult();
            output.WriteLine($"Metadata stored: {stored.Written.Count} document(s) written, {stored.Unchanged.Count} unchanged.");
            output.Payload("metadata_stored", stored.Written.Select(d => new { kind = d.Kind, subject = d.Subject, hash = d.Hash }).ToList());
        }
        catch (Exception ex) when (ex is GateRefusedException or IOException or InvalidOperationException)
        {
            output.WriteLine($"note: the plan was applied, but its metadata was not stored ({ex.GetType().Name}).");
        }
    }

    public static int Run(CommandSpec spec, string planPath, string root, bool dryRun, bool allowRisky, string[] allowDestructive, bool resume, bool allowDirty,
        TextWriter output, TextWriter error, Func<string, string?> env)
    {
        if (!File.Exists(planPath)) { error.WriteLine($"Plan file `{planPath}` does not exist."); return CliApp.ExitUsage; }
        var planText = File.ReadAllText(planPath);
        var diags = new List<Diagnostic>();
        var plan = PlanDocument.Parse(planText, Path.GetFileName(planPath), diags);
        if (plan == null)
        {
            foreach (var d in diags) error.Diag(d);
            return CliApp.ExitFindings;
        }

        var configDiags = new List<Diagnostic>();
        var config = ProjectConfigLoader.LoadFromProject(root, configDiags);
        foreach (var d in configDiags.Where(d => d.Severity == Severity.Error)) error.Diag(d);
        if (configDiags.Any(d => d.Severity == Severity.Error)) return CliApp.ExitFindings;

        if (!config.Connections.TryGetValue(plan.Target, out var connection))
        {
            error.WriteLine($"The plan is for the connection `{plan.Target}`, which this project does not have. Connections: {string.Join(", ", config.Connections.Keys.Order(StringComparer.Ordinal))}.");
            return CliApp.ExitFindings;
        }
        var (read, readMissing) = LoginSettings.FromEnvironment(connection.Name, connection.Engine, Login.Read, env);
        var (write, writeMissing) = LoginSettings.FromEnvironment(connection.Name, connection.Engine, Login.Write, env);
        var logins = dryRun ? $"read {read?.Describe() ?? "none"}; nothing is written" : $"read {read?.Describe() ?? "none"}, write {write?.Describe() ?? "none"}";
        output.WriteLine($"{ProductInfo.Cli} {spec.Name}  |  effect: {spec.Effect.Describe()}{(dryRun ? " (DRY RUN: nothing will be executed)" : "")}  |  target: {plan.Target}  |  login: {logins}");
        output.Payload("effect", spec.Effect.Describe());
        output.Payload("target", plan.Target);
        output.Payload("dry_run", dryRun);
        output.Payload("plan_id", plan.Id);
        output.WriteLine($"Plan {plan.Id}: {plan.Steps.Count} step(s); objects that may be touched: {string.Join(", ", plan.Steps.Select(s => s.Object).Distinct(StringComparer.Ordinal))}");

        var (commit, dirty) = GitInfo.Read(root);
        var options = new ApplyOptions(dryRun, allowRisky, allowDestructive.ToHashSet(StringComparer.Ordinal), resume, config.TrackingSchema, commit, dirty, write?.User ?? Environment.UserName, CommandContext.Hooks?.StopRequested);

        // refusals that need no connection come first
        var offline = new List<Diagnostic>(ApplyEngine.CheckAllowances(plan, options));
        if (dirty && !allowDirty)
        {
            var dirtyDiag = new Diagnostic(DiagnosticCatalog.DirtyWorkingTree, new("git", 0, 0), $"The working tree at `{root}` has uncommitted changes (commit {commit?[..Math.Min(12, commit.Length)]}).");
            if (dryRun) error.Diag(dirtyDiag with { SeverityOverride = Severity.Warning }); else offline.Add(dirtyDiag);
        }
        if (readMissing != null) offline.Add(readMissing);
        if (!dryRun && writeMissing != null) offline.Add(writeMissing);
        if (offline.Count > 0)
        {
            foreach (var d in offline) error.Diag(d);
            output.WriteLine("Nothing was executed.");
            return CliApp.ExitFindings;
        }

        var runId = Guid.NewGuid();
        var hooks = CommandContext.Hooks;       // captured here: the apply runs on another thread
        ApplyResult result;
        string? logPath = null;
        {
            using var log = new FileStatementLog(Path.Combine(root, InitCommand.StatementLogDir), dryRun ? "apply-dry-run" : "apply", runId);
            logPath = Path.GetRelativePath(root, log.Path);
            output.WriteLine($"Statement log: {logPath}");
            result = Task.Run(() => ApplyEngine.RunAsync(plan, planText, read!, write, options, log, runId, line => { output.WriteLine(line); hooks?.Progress?.Invoke(line); })).GetAwaiter().GetResult();
        }

        if (dryRun)
            foreach (var step in plan.Steps)
            {
                output.WriteLine();
                output.WriteLine($"-- step {step.Id} [{step.Type.ToString().ToLowerInvariant()}, {step.Risk.ToString().ToLowerInvariant()}] {step.Description}");
                if (step.Type != StepType.Track) output.WriteLine(step.Text.TrimEnd());
                foreach (var p in step.Parameters) output.WriteLine($"-- @{p.Name} ({p.Type}) = {p.Value ?? "NULL"}");
            }
        if (result.Success && !dryRun && config.StoreMetadataOnApply) StoreMetadata(plan, root, config, read!, write!, commit, output, error);
        output.Payload("outcomes", result.Outcomes.Select(o => new { step = o.StepId, description = o.Description, status = o.Status, detail = o.Detail }).ToList());
        output.Payload("statement_log", logPath?.Replace('\\', '/'));
        output.Payload("success", result.Success);
        if (dryRun) output.Payload("statements", plan.Steps.Where(s => s.Type != StepType.Track).Select(s => new { step = s.Id, type = s.Type, risk = s.Risk, text = s.Text, parameters = s.Parameters }).ToList());
        foreach (var d in result.Refusals) error.Diag(d);
        output.WriteLine();
        foreach (var o in result.Outcomes) output.WriteLine($"  step {o.StepId}: {o.Status}{(o.Detail != null && o.Status != "ok" ? " (" + o.Detail + ")" : "")}  {o.Description}");
        if (result.Success)
            output.WriteLine(dryRun ? "Dry run complete: every check passed and nothing was executed." : $"Applied plan {plan.Id}: {result.Outcomes.Count(o => o.Status == "ok")} step(s) executed.");
        else
            output.WriteLine($"Plan {plan.Id} did not complete. {result.Outcomes.Count(o => o.Status == "ok")} step(s) ran before the stop; see the statement log {logPath}.");
        return result.Success ? CliApp.ExitOk : CliApp.ExitFindings;
    }
}
