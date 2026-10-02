using System.Data;
using System.Globalization;
using System.Text.Json;
using DbDataBuild.Core;
using DbDataBuild.Execution;
using DbDataBuild.Planning;
using DbDataBuild.State;
using DbDataBuild.Targets.Ddl;

namespace DbDataBuild.Apply;

/// <param name="AllowDestructive">Object names (`marts.fct`) whose destructive steps are allowed. Never "all".</param>
public sealed record ApplyOptions(bool DryRun, bool AllowRisky, IReadOnlySet<string> AllowDestructive, bool Resume, string TrackingSchema, string? GitCommit, bool GitDirty, string Invoker);

/// <param name="Status">ok, dry-run, skipped (done in an earlier attempt) or failed.</param>
public sealed record StepOutcome(string StepId, string Description, string Status, string? Detail = null);

public sealed record ApplyResult(IReadOnlyList<StepOutcome> Outcomes, IReadOnlyList<Diagnostic> Refusals)
{
    public bool Success => Refusals.Count == 0 && Outcomes.All(o => o.Status != "failed");
}

/// <summary>
/// `apply` (DESIGN.md 10.3). Verifies the plan against the live target, takes the application lock, and executes exactly the plan's recorded statements through
/// the mutation gate, one step at a time, recording each in the tracking tables. Anything unexpected stops the run; nothing resumes automatically.
/// </summary>
public static class ApplyEngine
{
    /// <summary>The refusals that need no connection: risk allowances (DESIGN.md 10.4).</summary>
    public static IReadOnlyList<Diagnostic> CheckAllowances(Plan plan, ApplyOptions o)
    {
        var refusals = new List<Diagnostic>();
        var risky = plan.Steps.Where(s => s.Risk == RiskClass.Risky).ToList();
        if (risky.Count > 0 && !o.AllowRisky)
            refusals.Add(new Diagnostic(DiagnosticCatalog.StepNeedsAllowance, new($"plan:{plan.Id}", 0, 0),
                $"The plan has {risky.Count} risky step(s) ({string.Join(", ", risky.Select(s => $"{s.Id}: {s.Description}"))}) and `--allow-risky` was not given."));
        foreach (var obj in plan.Steps.Where(s => s.Risk == RiskClass.Destructive).Select(s => s.Object).Distinct(StringComparer.Ordinal))
            if (!o.AllowDestructive.Contains(obj))
                refusals.Add(new Diagnostic(DiagnosticCatalog.StepNeedsAllowance, new($"plan:{plan.Id}", 0, 0),
                    $"The plan has destructive step(s) on `{obj}` ({string.Join(", ", plan.Steps.Where(s => s.Risk == RiskClass.Destructive && s.Object == obj).Select(s => $"{s.Id}: {s.Description}"))}) and `--allow-destructive {obj}` was not given."));
        return refusals;
    }

    public static async Task<ApplyResult> RunAsync(Plan plan, string planText, LoginSettings read, LoginSettings? write, ApplyOptions o, IStatementLog log, Guid runId, Action<string> progress, CancellationToken ct = default)
    {
        var target = plan.Target;
        var schema = o.TrackingSchema;
        var refusals = new List<Diagnostic>(CheckAllowances(plan, o));
        if (refusals.Count > 0) return new ApplyResult([], refusals);
        if (!o.DryRun && write == null) throw new ArgumentException("A real apply needs the write login.", nameof(write));
        var planHash = PlanDocument.ContentHash(plan);

        await using var reader = await ReadSession.OpenAsync(read, ct);
        var status = await TrackingStore.StatusAsync(reader, target, schema, ct);
        if (status.AsDiagnostic(schema) is { } notReady) return new ApplyResult([], [notReady]);

        await using var gate = o.DryRun
            ? MutationGate.DryRunGate("apply", StatementKind.Tracking | StatementKind.Data | StatementKind.Ddl, log, runId)
            : await MutationGate.OpenAsync(write!, "apply", StatementKind.Tracking | StatementKind.Data | StatementKind.Ddl, log, runId, ct);

        var lockName = $"{ProductInfo.Cli}:{schema}";
        if (!await gate.TryAcquireApplicationLockAsync(lockName, ct))
            return new ApplyResult([], [new Diagnostic(DiagnosticCatalog.ApplyLockHeld, new($"lock:{lockName}", 0, 0), $"Another apply holds the application lock `{lockName}` on this target.")]);
        try
        {
            return await RunLockedAsync(plan, planText, planHash, reader, gate, o, runId, progress, ct);
        }
        finally
        {
            try { await gate.ReleaseApplicationLockAsync(lockName, ct); } catch (Exception ex) when (ex is not OperationCanceledException) { /* closing the connection releases it */ }
        }
    }

    private static async Task<ApplyResult> RunLockedAsync(Plan plan, string planText, string planHash, ReadSession reader, MutationGate gate, ApplyOptions o, Guid runId, Action<string> progress, CancellationToken ct)
    {
        var target = plan.Target;
        var schema = o.TrackingSchema;
        var objects = plan.Steps.Select(s => s.Object).Concat(plan.Bases.Select(b => b.Object)).Distinct(StringComparer.Ordinal).ToList();
        var schemas = objects.Select(x => DdlGenerator.Split(x).Schema).Distinct(StringComparer.Ordinal).ToList();

        // ---- what this plan already did (resume) ----
        var progressInfo = await AuditLog.ProgressAsync(reader, target, schema, plan.Id, ct);
        var refusals = new List<Diagnostic>();
        var done = new HashSet<string>(StringComparer.Ordinal);
        if (progressInfo.MigrationStatuses.Contains("completed"))
            return new ApplyResult([], [new Diagnostic(DiagnosticCatalog.PlanAlreadyStarted, new($"plan:{plan.Id}", 0, 0), $"Plan {plan.Id} was already applied to this target. A plan is applied once.")]);
        if (progressInfo.MigrationStatuses.Count > 0 && !o.Resume)
            return new ApplyResult([], [new Diagnostic(DiagnosticCatalog.PlanAlreadyStarted, new($"plan:{plan.Id}", 0, 0), $"Plan {plan.Id} was started before and did not finish (recorded: {string.Join(", ", progressInfo.MigrationStatuses)}).")]);
        if (o.Resume && progressInfo.MigrationStatuses.Count == 0)
            return new ApplyResult([], [new Diagnostic(DiagnosticCatalog.PlanAlreadyStarted, new($"plan:{plan.Id}", 0, 0), $"--resume was given, but plan {plan.Id} was never started on this target.")]);

        foreach (var s in plan.Steps)
            if (IsDone(s, progressInfo)) done.Add(s.Id);
        var firstTodo = plan.Steps.ToList().FindIndex(s => !done.Contains(s.Id));
        if (firstTodo >= 0 && plan.Steps.Skip(firstTodo).Any(s => done.Contains(s.Id)))
            return new ApplyResult([], [new Diagnostic(DiagnosticCatalog.PlanAlreadyStarted, new($"plan:{plan.Id}", 0, 0), "The steps that finished are not a prefix of the plan, so it cannot be resumed. Generate a new plan.")]);

        // ---- verify the plan against the live target (a mismatch is a stale plan, never guessed around) ----
        var snapshot = await TargetSnapshotReader.ReadAsync(reader, target, o.TrackingSchema, schemas, ct);
        var lastDone = plan.Steps.Where(s => done.Contains(s.Id) && s.Type == StepType.Ddl && s.HashAfter != null).GroupBy(s => s.Object).ToDictionary(g => g.Key, g => g.Last());
        foreach (var b in plan.Bases)
        {
            var live = snapshot.Live.GetValueOrDefault(b.Object);
            if (lastDone.TryGetValue(b.Object, out var step))
            {
                if (live?.ShapeHash != step.HashAfter)
                    refusals.Add(Stale($"{b.Object} is not in the state step {step.Id} left it in (expected {step.HashAfter![..12]}, found {live?.ShapeHash[..12] ?? "missing"})."));
                continue;
            }
            if (done.Count > 0 && plan.Steps.Any(s => done.Contains(s.Id) && s.Object == b.Object && s.Type == StepType.Track)) continue; // the track step already recorded it
            var recorded = snapshot.RecordedShapeHashes.GetValueOrDefault(b.Object);
            var state = Drift.Classify(live, recorded);
            if (state != b.State || live?.ShapeHash != b.LiveShapeHash || recorded != b.RecordedShapeHash)
                refusals.Add(Stale($"{b.Object} changed since the plan was made (planned: {b.State} {b.LiveShapeHash?[..12] ?? "-"}, now: {state} {live?.ShapeHash[..12] ?? "-"})."));
        }
        foreach (var s in plan.Steps.Where(s => s.HasResolver && !done.Contains(s.Id)))
        {
            if (!snapshot.Live.ContainsKey(s.Object)) { if (s.ResolverResult != null) refusals.Add(Stale($"the resolver of step {s.Id} was planned against a table that no longer exists.")); continue; }
            var type = s.Parameters.FirstOrDefault(p => p.Source == "resolver")?.Type ?? "TEXT";
            var now = await TargetSnapshotReader.RunResolverAsync(reader, s.ResolverText!, type, ct);
            if (now.Error != null || now.Value != s.ResolverResult)
                refusals.Add(Stale($"the resolver of step {s.Id} returned {(now.Error != null ? now.Error : now.Value ?? "NULL")}, but the plan recorded {s.ResolverResult ?? "NULL"}."));
        }
        if (refusals.Count > 0) return new ApplyResult([], refusals);

        // ---- execute ----
        var outcomes = new List<StepOutcome>();
        var before = string.Join(",", plan.Bases.Select(b => b.LiveShapeHash ?? "-"));
        if (!gate.DryRun) await AuditLog.MigrationAsync(gate, target, schema, "migration:start", plan.Id, planHash, planText, o.GitCommit, o.Invoker, "started", Hashing.Sha256Hex(before), null, ct);

        foreach (var step in plan.Steps)
        {
            if (done.Contains(step.Id)) { outcomes.Add(new(step.Id, step.Description, "skipped", "finished in an earlier attempt")); continue; }
            progress($"step {step.Id}/{plan.Steps.Count}: {step.Description}");
            try
            {
                var diagnostic = await ExecuteStepAsync(plan, step, reader, gate, o, runId, ct);
                if (diagnostic != null)
                {
                    outcomes.Add(new(step.Id, step.Description, "failed", diagnostic.Found));
                    await FinishMigration(gate, plan, planHash, planText, o, "failed", ct);
                    return new ApplyResult(outcomes, [diagnostic]);
                }
                outcomes.Add(new(step.Id, step.Description, gate.DryRun ? "dry-run" : "ok"));
            }
            catch (GateRefusedException ex)
            {
                outcomes.Add(new(step.Id, step.Description, "failed", ex.Diagnostic.Found));
                await FinishMigration(gate, plan, planHash, planText, o, "failed", ct);
                return new ApplyResult(outcomes, [ex.Diagnostic]);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // type only: a driver message can quote data (DESIGN.md 14.2); the statement log has the error number
                var detail = $"step {step.Id} ({step.Description}) failed with {ex.GetType().Name}";
                outcomes.Add(new(step.Id, step.Description, "failed", detail));
                await FinishMigration(gate, plan, planHash, planText, o, "failed", ct);
                return new ApplyResult(outcomes, [new Diagnostic(DiagnosticCatalog.StepFailed, new($"step:{step.Id}", 0, 0), detail + ". Later steps did not run.")]);
            }
        }
        await FinishMigration(gate, plan, planHash, planText, o, "completed", ct);
        return new ApplyResult(outcomes, []);
    }

    private static Diagnostic Stale(string detail) => new(DiagnosticCatalog.PlanIsStale, new("plan", 0, 0), detail);

    private static bool IsDone(PlanStep s, PlanProgress p) => s.Type switch
    {
        StepType.Ddl => p.CompletedDdlHashes.Contains(Hashing.ScriptHash(s.Text)),
        StepType.Load or StepType.Backfill or StepType.Hook => p.CompletedRunSteps.Contains(s.Id),
        StepType.Track => s.HashAfter != null && p.RecordedShapes.Contains((s.Object, s.HashAfter)),
        _ => false,
    };

    private static async Task FinishMigration(MutationGate gate, Plan plan, string planHash, string planText, ApplyOptions o, string status, CancellationToken ct)
    {
        if (gate.DryRun) return;
        try { await AuditLog.MigrationAsync(gate, plan.Target, o.TrackingSchema, "migration:" + status, plan.Id, planHash, planText, o.GitCommit, o.Invoker, status, null, null, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* the failure that got us here matters more; the statement log has what ran */ }
    }

    private static async Task<ObjectShape?> LiveAsync(ReadSession reader, string target, string obj, CancellationToken ct)
    {
        var (schema, _) = DdlGenerator.Split(obj);
        return (await CatalogReader.ReadSchemaAsync(reader, target, schema, ct)).GetValueOrDefault(obj);
    }

    /// <summary>Executes one step. Returns a diagnostic when the step ran but its result is not what the plan promised.</summary>
    private static async Task<Diagnostic?> ExecuteStepAsync(Plan plan, PlanStep step, ReadSession reader, MutationGate gate, ApplyOptions o, Guid runId, CancellationToken ct)
    {
        var target = plan.Target;
        var schema = o.TrackingSchema;
        switch (step.Type)
        {
            case StepType.Ddl:
            {
                var beforeShape = gate.DryRun ? null : await LiveAsync(reader, target, step.Object, ct);
                var ddlId = Guid.NewGuid();
                var hash = Hashing.ScriptHash(step.Text);
                if (!gate.DryRun) await AuditLog.BeginDdlAsync(gate, target, schema, step.Id + ":log", ddlId, step.Object, step.Text, hash, beforeShape?.ShapeHash, o.Invoker, plan.Id, o.GitCommit, ct);
                try { await gate.ExecuteAsync(GateStatement.FromPlanStep(step.Id, StatementKind.Ddl, step.Text), ct); }
                catch (Exception) when (!gate.DryRun)
                {
                    await AuditLog.FinishDdlAsync(gate, target, schema, step.Id + ":log", ddlId, "failed", null, ct);
                    throw;
                }
                if (gate.DryRun) return null;

                var after = await LiveAsync(reader, target, step.Object, ct);
                if (step.HashAfter != null && after?.ShapeHash != step.HashAfter)
                {
                    await AuditLog.FinishDdlAsync(gate, target, schema, step.Id + ":log", ddlId, "mismatch", after?.ShapeHash, ct);
                    return new Diagnostic(DiagnosticCatalog.StepResultDiffers, new($"step:{step.Id}", 0, 0),
                        $"After step {step.Id} ({step.Description}) `{step.Object}` has shape {after?.ShapeHash[..12] ?? "missing"}, but the plan promised {step.HashAfter[..12]}. Nothing after this step ran.");
                }
                if (step.Expect != null && IndexMismatch(step.Expect, after) is { } mismatch)
                {
                    await AuditLog.FinishDdlAsync(gate, target, schema, step.Id + ":log", ddlId, "mismatch", after?.ShapeHash, ct);
                    return new Diagnostic(DiagnosticCatalog.StepResultDiffers, new($"step:{step.Id}", 0, 0), $"After step {step.Id} ({step.Description}) {mismatch}. Nothing after this step ran.");
                }
                await AuditLog.FinishDdlAsync(gate, target, schema, step.Id + ":log", ddlId, "ok", after?.ShapeHash, ct);
                if (after != null)
                    await TrackingStore.RecordSchemaVersionAsync(gate, target, schema, step.Id + ":version", step.Object, after.ShapeHash, after.PhysicalHash, "tool", plan.Id, o.GitCommit, ct);
                return null;
            }

            case StepType.Track:
            {
                if (gate.DryRun) return null; // a tracking write has no target statement to show beyond the plan's own description
                var live = await LiveAsync(reader, target, step.Object, ct);
                if (live == null || live.ShapeHash != step.HashAfter)
                    return new Diagnostic(DiagnosticCatalog.StepResultDiffers, new($"step:{step.Id}", 0, 0), $"`{step.Object}` is not in the shape the track step recorded; nothing was recorded.");
                await TrackingStore.RecordSchemaVersionAsync(gate, target, schema, step.Id + ":version", step.Object, live.ShapeHash, live.PhysicalHash, step.ShapeSource ?? "tool", plan.Id, o.GitCommit, ct);
                return null;
            }

            case StepType.Load:
            case StepType.Backfill:
            {
                var parameters = step.Parameters.Select(ToGate).ToList();
                var shapeStart = gate.DryRun ? null : (await LiveAsync(reader, target, step.Object, ct))?.ShapeHash;
                var json = JsonSerializer.Serialize(step.Parameters.Select(p => new { p.Name, p.Type, p.Source, p.Value }), new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                var watermark = step.Parameters.FirstOrDefault(p => p.Name == "watermark")?.Value;
                if (!gate.DryRun)
                    await AuditLog.BeginRunAsync(gate, target, schema, step.Id, runId, step.Object, step.Operation ?? "load", plan.Id, o.GitCommit, step.DefinitionHash, shapeStart, step.Operation,
                        step.FileHash, step.ResolverText == null ? null : Hashing.ScriptHash(step.ResolverText), json, watermark, ct);
                long rows;
                try { rows = await gate.ExecuteAsync(GateStatement.FromPlanStep(step.Id, StatementKind.Data, step.Text, parameters), ct); }
                catch (Exception) when (!gate.DryRun)
                {
                    await AuditLog.FinishRunAsync(gate, target, schema, step.Id, runId, "failed", null, null, ct);
                    throw;
                }
                if (gate.DryRun) return null;
                var shapeEnd = (await LiveAsync(reader, target, step.Object, ct))?.ShapeHash;
                await AuditLog.FinishRunAsync(gate, target, schema, step.Id, runId, "ok", rows, shapeEnd, ct);
                // the range of data this operation produced, under the shape it produced it in (DESIGN.md 12.3 reads these)
                var start = step.Parameters.FirstOrDefault(p => p.Name is "start" or "watermark")?.Value;
                var end = step.Parameters.FirstOrDefault(p => p.Name == "end")?.Value;
                if (start != null || end != null)
                    await AuditLog.IntervalAsync(gate, target, schema, step.Id + ":interval", step.Object, runId, start, end, shapeEnd ?? "", step.Type == StepType.Backfill ? "backfill" : "load", ct);
                return null;
            }

            case StepType.Hook:
            {
                // a hook is native SQL run exactly as committed; it is logged like a load (operation = its event, load_name = its name)
                var kind = step.Effect == "data" ? StatementKind.Data : StatementKind.Ddl;
                var before = gate.DryRun ? null : await LiveAsync(reader, target, step.Object, ct);
                if (!gate.DryRun)
                    await AuditLog.BeginRunAsync(gate, target, schema, step.Id, runId, step.Object, step.Operation ?? "hook", plan.Id, o.GitCommit, null, before?.ShapeHash, step.Hook, step.FileHash, null, null, null, ct);
                long rows;
                try { rows = await gate.ExecuteAsync(GateStatement.FromPlanStep(step.Id, kind, step.Text), ct); }
                catch (Exception) when (!gate.DryRun)
                {
                    await AuditLog.FinishRunAsync(gate, target, schema, step.Id, runId, "failed", null, null, ct);
                    throw;
                }
                if (gate.DryRun) return null;
                var after = await LiveAsync(reader, target, step.Object, ct);
                await AuditLog.FinishRunAsync(gate, target, schema, step.Id, runId, "ok", rows, after?.ShapeHash, ct);
                // a script that changed the object's shape was the operator's own decision: record the new shape so it is not mistaken for an outside change
                if (kind == StatementKind.Ddl && after != null && before != null && after.ShapeHash != before.ShapeHash)
                    await TrackingStore.RecordSchemaVersionAsync(gate, target, schema, step.Id + ":version", step.Object, after.ShapeHash, after.PhysicalHash, "hook", plan.Id, o.GitCommit, ct);
                return null;
            }

            default:
                throw new InvalidOperationException($"Step type {step.Type} cannot be applied yet.");
        }
    }

    /// <summary>Checks an index step's post-condition (`index:&lt;name&gt;=&lt;canonical&gt;`) against the live object. Returns what is wrong, or null.</summary>
    private static string? IndexMismatch(string expect, ObjectShape? live)
    {
        var body = expect["index:".Length..];
        var eq = body.IndexOf('=');
        var (name, canonical) = (body[..eq], body[(eq + 1)..]);
        var found = live?.Physical.FirstOrDefault(p => p.Kind == "index" && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (found == null) return $"index `{name}` does not exist on `{live?.QualifiedName ?? "the object"}`";
        return string.Equals(found.Definition, canonical, StringComparison.OrdinalIgnoreCase) ? null : $"index `{name}` is `{found.Definition}`, but the plan promised `{canonical}`";
    }

    /// <summary>A plan parameter as a bound value: the plan holds invariant-culture text, the driver gets a typed value.</summary>
    public static GateParameter ToGate(PlanParameter p)
    {
        var type = p.Type.Trim().ToUpperInvariant();
        if (p.Value == null)
            return new(p.Name, type.StartsWith("TIMESTAMP") ? DbType.DateTime2 : type == "DATE" ? DbType.Date : type is "BIGINT" or "INTEGER" or "SMALLINT" ? DbType.Int64 : DbType.String, null);
        if (type == "DATE") return new(p.Name, DbType.Date, DateTime.ParseExact(p.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture));
        if (type.StartsWith("TIMESTAMP"))
            return new(p.Name, DbType.DateTime2, DateTime.ParseExact(p.Value, ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFFFFF", "yyyy-MM-ddTHH:mm:ss.FFFFFF", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None));
        if (type is "BIGINT" or "INTEGER" or "SMALLINT") return new(p.Name, DbType.Int64, long.Parse(p.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture));
        return new(p.Name, DbType.String, p.Value);
    }
}
