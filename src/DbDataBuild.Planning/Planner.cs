using System.Globalization;
using DbDataBuild.Core;
using DbDataBuild.Core.Questions;
using DbDataBuild.Models;
using DbDataBuild.Sql;
using DbDataBuild.State;
using DbDataBuild.Targets;
using DbDataBuild.Targets.Ddl;

namespace DbDataBuild.Planning;

/// <summary>A hook for one target with its script read from the project. The text is executed exactly as written.</summary>
public sealed record PlannedHook(ResolvedHook Hook, string Text, string FileHash);

/// <param name="Hooks">The model's hooks for the target being planned, groups expanded, in run order.</param>
/// <summary>Where a copy's rows come from: the origin connection with its engine, and the table (`schema.table`) there. <see cref="SliceValue"/> is the value that tells this origin's rows apart (the copy's slice, resolved for this origin).</summary>
public sealed record CopyOrigin(string Connection, string Engine, string Table, string? SliceValue = null);

public sealed record PlannedModel(ModelDefinition Definition, string BodySql, string QueryFile, string DefinitionHash, IReadOnlyList<string> BaseTables, IReadOnlyList<PlannedHook>? Hooks = null, IReadOnlyList<CopyOrigin>? Origins = null)
{
    public IReadOnlyList<PlannedHook> HookList => Hooks ?? [];

    /// <summary>The origins of a copy, in the order they are loaded; empty for any other model.</summary>
    public IReadOnlyList<CopyOrigin> OriginList => Origins ?? [];
}

/// <summary>A committed rendered load operation, read from `rendered/` (the command has already checked it is fresh).</summary>
public sealed record RenderedLoad(string Operation, bool IsDefault, string Script, string FileHash, string? ResolverText, IReadOnlyList<RenderedParameter> Parameters, WatermarkSpec? Watermark);

/// <param name="Error">Set when the resolver did not return exactly one row and one column of the right type.</param>
public sealed record ResolverOutcome(string? Value, string? Error);

public static class Acknowledgements
{
    /// <summary>`DDB-430|marts.fct|&lt;shape hash&gt;`: the acknowledgement of one specific block, so a later, different change blocks again.</summary>
    public static string Key(string code, string obj, string detail) => $"{code}|{obj}|{detail}";
}

public sealed record PlanInput(
    string Target,
    ProjectConfig Config,
    IReadOnlyList<PlannedModel> Models,
    IReadOnlyDictionary<string, ObjectShape> Live,
    IReadOnlySet<string> LiveSchemas,
    IReadOnlyDictionary<string, string> RecordedShapeHashes,
    IReadOnlyDictionary<string, string> LastViewStatementHashes,
    IReadOnlyDictionary<string, string> LastLoadDefinitionHashes,
    IReadOnlySet<string> Acknowledged,
    IReadOnlyDictionary<string, IReadOnlyList<RenderedLoad>> Loads,
    IReadOnlyDictionary<string, ResolverOutcome> Resolved,
    IReadOnlyDictionary<string, string>? OperationChoice = null,
    IReadOnlySet<string>? Backfills = null,
    IReadOnlyDictionary<string, ColumnBounds>? RangeBounds = null)
{
    /// <summary>The engine of the connection being planned: what the SQL is written for.</summary>
    public string Engine => Config.EngineOf(Target) ?? Target;

    public static string ResolverKey(string model, string operation) => $"{model}|{operation}";
}

/// <summary>The smallest and largest value of a range load's column on the target, read at plan time so a range can be compared with what the table holds.</summary>
public sealed record ColumnBounds(string? Min, string? Max, string? Error);

public static class RangeLoads
{
    /// <summary>The column a `delete_insert_by_range` operation deletes and loads by (its own, else the kind's time column); null when the operation is not a range load.</summary>
    public static string? ColumnOf(ModelDefinition def, string target, string operation)
    {
        var op = LoadPlan.For(def, target).FirstOrDefault(o => o.Name == operation);
        return op is { Strategy: LoadStrategies.DeleteInsertByRange } ? op.Column ?? def.TimeColumn : null;
    }
}

/// <param name="Questions">Open decisions. While any remain, no plan may be generated.</param>
/// <param name="Blocks">Models that cannot be planned, with the diagnostic saying why. Their downstream models are skipped (<see cref="Skipped"/>).</param>
public sealed record PlanResult(
    IReadOnlyList<Question> Questions, IReadOnlyList<Diagnostic> Blocks, IReadOnlyList<Diagnostic> Skipped, IReadOnlyList<ObjectBase> Bases,
    IReadOnlyList<PlanStep> Steps, IReadOnlyList<ResolvedAnswer> UsedAnswers, IReadOnlyList<string> Noticed)
{
    public bool Complete => Questions.Count == 0;
}

/// <summary>
/// The planning decision table (DESIGN.md 11, `matrix/decision-table.yml`) as code: a pure function of the project, the live catalog snapshot, the
/// tracking records and the answers. It reads nothing and writes nothing. Each step names the decision-table row that produced it.
/// </summary>
public static class Planner
{
    public static PlanResult Plan(PlanInput input, IReadOnlyList<ResolvedAnswer> answers)
    {
        var byId = answers.ToDictionary(a => a.QuestionId);
        var questions = new List<Question>();
        var blocks = new List<Diagnostic>();
        var skipped = new List<Diagnostic>();
        var bases = new List<ObjectBase>();
        var used = new Dictionary<string, ResolvedAnswer>();
        var noticed = new List<string>();
        var ddlSteps = new List<PlanStep>();
        var loadSteps = new List<PlanStep>();
        var schemasCreated = new HashSet<string>(StringComparer.Ordinal);

        var target = TargetRegistry.Get(input.Engine);
        var ddl = target.CreateDdl(input.Config);
        var order = ModelOrder.Sort(input.Models, out var cycle);
        if (cycle != null)
            blocks.Add(new Diagnostic(DiagnosticCatalog.ModelCycle, new(cycle[0], 0, 0), $"The models depend on each other in a cycle: {string.Join(" -> ", cycle)}."));

        var unplanned = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // blocked or skipped models: nothing downstream of them is planned
        foreach (var model in order)
        {
            var def = model.Definition;
            var blockedUpstream = model.BaseTables.FirstOrDefault(unplanned.Contains);
            if (blockedUpstream != null)
            {
                skipped.Add(new Diagnostic(DiagnosticCatalog.UpstreamBlocked, new(model.QueryFile, 0, 0), $"{def.Name} was not planned because `{blockedUpstream}`, which it reads from, is blocked or skipped."));
                unplanned.Add(def.Name);
                continue;
            }

            var ctx = new ModelContext(input, model, ddl, byId, used, questions, noticed, schemasCreated);
            var ok = def.KindType == ModelKinds.View ? PlanView(ctx, ddlSteps, bases, blocks) : PlanTable(ctx, ddlSteps, loadSteps, bases, blocks);
            if (!ok) unplanned.Add(def.Name);
        }

        var steps = ddlSteps.Concat(loadSteps).Select((s, i) => s with { Id = (i + 1).ToString(CultureInfo.InvariantCulture) }).ToList();
        if (input.Engine == TargetNames.Fabric) noticed.Add("Fabric support is unverified: no Fabric engine has been available to run these statements.");
        return new PlanResult(questions.OrderBy(q => q.Id, StringComparer.Ordinal).ToList(), blocks, skipped, bases, steps, used.Values.OrderBy(a => a.QuestionId, StringComparer.Ordinal).ToList(), noticed);
    }

    private sealed class ModelContext(PlanInput input, PlannedModel model, DdlGenerator ddl, Dictionary<string, ResolvedAnswer> answers, Dictionary<string, ResolvedAnswer> used,
        List<Question> questions, List<string> noticed, HashSet<string> schemasCreated)
    {
        public PlanInput Input => input;
        public PlannedModel Model => model;
        public ModelDefinition Def => model.Definition;
        public DdlGenerator Ddl => ddl;
        public List<string> Noticed => noticed;
        public (string Schema, string Name) Name => DdlGenerator.Split(Def.Name);

        public ResolvedAnswer? Answer(Question q)
        {
            if (answers.TryGetValue(q.Id, out var a)) { used[q.Id] = a; return a; }
            questions.Add(q);
            return null;
        }

        public bool NeedsSchema(string schema) => !input.LiveSchemas.Contains(schema) && schemasCreated.Add(schema);
    }

    private static PlanStep Step(StepType type, string obj, string description, string text, RiskClass risk, IEnumerable<string> reasons, string? hashAfter = null,
        IReadOnlyList<PlanParameter>? parameters = null, string? shapeSource = null) =>
        new("", type, obj, description, text, risk, reasons.ToList(), hashAfter, parameters ?? [], ShapeSource: shapeSource);

    // ------------------------------------------------------------------------------------------------------------------------------------
    // Object-level rows shared by every kind: missing, untracked (adopt), out of band (block), in sync.

    private enum Gate { Proceed, Blocked, Waiting }

    private static Gate Baseline(ModelContext c, ObjectShape? live, List<PlanStep> ddlSteps, List<ObjectBase> bases, List<Diagnostic> blocks, out ObjectState state)
    {
        var obj = c.Def.Name;
        var recorded = c.Input.RecordedShapeHashes.GetValueOrDefault(obj);
        state = Drift.Classify(live, recorded);
        bases.Add(new ObjectBase(obj, state, live?.ShapeHash, recorded));
        switch (state)
        {
            case ObjectState.Missing or ObjectState.InSync:
                return Gate.Proceed;

            case ObjectState.Untracked:
            {
                var q = new Question(QuestionIds.Adopt(obj),
                    $"`{obj}` exists on the target, but the tool has no record of creating it. Adopt it?",
                    [$"Live shape hash: {live!.ShapeHash}", $"The model declares {c.Def.Columns.Count} column(s); the object has {live.Columns.Count}."],
                    [new("adopt", "Record the live shape as the baseline and plan any difference from the model", "A track step is added to the plan; later changes are diffed against this shape."),
                     new("stop", "Do not plan this model", "The model and everything downstream of it are skipped.")]);
                var a = c.Answer(q);
                if (a == null) return Gate.Waiting;
                if (a.Choice == "stop")
                {
                    blocks.Add(new Diagnostic(DiagnosticCatalog.AdoptionDeclined, new(c.Model.QueryFile, 0, 0), $"{obj} exists on the target and adoption was declined (answer `{q.Id}` is `stop`)."));
                    return Gate.Blocked;
                }
                ddlSteps.Add(Step(StepType.Track, obj, $"adopt {obj}", $"record shape {live.ShapeHash}", RiskClass.Safe, ["obj.untracked", $"answer {q.Id} = adopt"], live.ShapeHash, shapeSource: "adopted"));
                return Gate.Proceed;
            }

            default: // out of band
            {
                var key = Acknowledgements.Key(DiagnosticCatalog.ObjectChangedOutsideTool.Code, obj, live!.ShapeHash);
                if (!c.Input.Acknowledged.Contains(key))
                {
                    blocks.Add(new Diagnostic(DiagnosticCatalog.ObjectChangedOutsideTool, new(c.Model.QueryFile, 0, 0),
                        $"{obj} has shape {live.ShapeHash[..12]}, but the last recorded shape is {recorded![..12]}. Something changed it outside the tool."));
                    return Gate.Blocked;
                }
                ddlSteps.Add(Step(StepType.Track, obj, $"record acknowledged change to {obj}", $"record shape {live.ShapeHash}", RiskClass.Safe, ["obj.drift.acknowledged"], live.ShapeHash, shapeSource: "out_of_band"));
                return Gate.Proceed;
            }
        }
    }

    // ------------------------------------------------------------------------------------------------------------------------------------

    private static bool PlanView(ModelContext c, List<PlanStep> ddlSteps, List<ObjectBase> bases, List<Diagnostic> blocks)
    {
        var def = c.Def;
        var (schema, name) = c.Name;
        IReadOnlyList<NativeColumn> native;
        try { native = c.Ddl.MapAll(def); }
        catch (DdlUnsupportedException ex) { blocks.Add(ex.Diagnostic); return false; }

        var (outcome, body) = Polyglot.TranspileOne(DbDataBuild.Targets.Rules.TargetRules.Apply(c.Model.BodySql, c.Input.Engine, RewriteCatalog.For(c.Input.Config, c.Def), c.Input.Config.TargetVersions.TryGetValue(c.Input.Target, out var tv) ? tv : null).Sql, Dialects.Canonical, TargetRegistry.Get(c.Input.Engine).Dialect);
        if (body != null) body = DbDataBuild.Targets.Rules.TargetRules.Finish(body, c.Input.Engine);
        if (body == null)
        {
            blocks.Add(new Diagnostic(DiagnosticCatalog.ModelUnplannable, new(c.Model.QueryFile, 0, 0), $"{def.Name}: the transpiler reported: {outcome.Error}."));
            return false;
        }
        var statement = c.Ddl.CreateOrReplaceView(schema, name, native, body);
        var live = c.Input.Live.GetValueOrDefault(def.Name);
        var gate = Baseline(c, live, ddlSteps, bases, blocks, out var state);
        if (gate != Gate.Proceed) return gate == Gate.Waiting;
        var viewStart = ddlSteps.Count;

        var statementHash = Hashing.ScriptHash(statement);
        if (state == ObjectState.Missing)
        {
            if (c.NeedsSchema(schema)) ddlSteps.Add(Step(StepType.Ddl, def.Name, $"create schema {schema}", c.Ddl.CreateSchema(schema), RiskClass.Safe, ["obj.missing.view"]));
            ddlSteps.Add(Step(StepType.Ddl, def.Name, $"create view {def.Name}", statement, RiskClass.Safe, ["obj.missing.view"]));
        }
        else if (c.Input.LastViewStatementHashes.GetValueOrDefault(def.Name) != statementHash)
        {
            ddlSteps.Add(Step(StepType.Ddl, def.Name, $"alter view {def.Name}", statement, RiskClass.Safe, ["view.changed"]));
        }
        var viewSteps = ddlSteps.Skip(viewStart).ToList();
        ddlSteps.RemoveRange(viewStart, ddlSteps.Count - viewStart);
        ddlSteps.AddRange(Hooked.Structure(c.Model, state == ObjectState.Missing, viewSteps));
        return true;
    }

    // ------------------------------------------------------------------------------------------------------------------------------------

    private static bool PlanTable(ModelContext c, List<PlanStep> ddlSteps, List<PlanStep> loadSteps, List<ObjectBase> bases, List<Diagnostic> blocks)
    {
        var def = c.Def;
        var (schema, name) = c.Name;
        IReadOnlyList<NativeColumn> native;
        try { native = c.Ddl.MapAll(def); }
        catch (DdlUnsupportedException ex) { blocks.Add(ex.Diagnostic); return false; }

        var live = c.Input.Live.GetValueOrDefault(def.Name);
        var local = new List<PlanStep>();
        var gate = Baseline(c, live, local, bases, blocks, out var state);
        if (gate != Gate.Proceed) { if (gate == Gate.Waiting) ddlSteps.AddRange(local); return gate == Gate.Waiting; }

        // an incremental model whose query changed since its last load is blocked (rows already loaded came from the old query)
        if (def.KindType is ModelKinds.IncrementalByUniqueKey or ModelKinds.IncrementalByTimeRange && c.Input.LastLoadDefinitionHashes.TryGetValue(def.Name, out var lastHash) && lastHash != c.Model.DefinitionHash)
        {
            var key = Acknowledgements.Key(DiagnosticCatalog.LoadDefinitionChanged.Code, def.Name, c.Model.DefinitionHash);
            if (!c.Input.Acknowledged.Contains(key))
            {
                blocks.Add(new Diagnostic(DiagnosticCatalog.LoadDefinitionChanged, new(c.Model.QueryFile, 0, 0),
                    $"{def.Name}'s query changed since its last load (definition hash {lastHash[..12]} then, {c.Model.DefinitionHash[..12]} now)."));
                return false;
            }
        }

        var waiting = false;
        if (state == ObjectState.Missing)
        {
            if (c.NeedsSchema(schema)) local.Add(Step(StepType.Ddl, def.Name, $"create schema {schema}", c.Ddl.CreateSchema(schema), RiskClass.Safe, ["obj.missing.table"]));
            local.Add(Step(StepType.Ddl, def.Name, $"create table {def.Name}", c.Ddl.CreateTable(schema, name, native), RiskClass.Safe, ["obj.missing.table"],
                Hashing.ShapeHash(DdlGenerator.ExpectedShape(native))));
        }
        else
        {
            var before = blocks.Count;
            waiting = !Diff(c, live!, native, local, blocks);
            if (blocks.Count > before) return false;
        }
        if (!PlanIndexes(c, live, local, blocks)) return false;
        ddlSteps.AddRange(Hooked.Structure(c.Model, state == ObjectState.Missing, local));

        return PlanLoads(c, state == ObjectState.Missing, loadSteps, blocks) && !waiting;
    }

    // Declared indexes against the live ones, by name. Indexes are what the operator declared: nothing is inferred, an undeclared live index is left alone and reported.
    private static bool PlanIndexes(ModelContext c, ObjectShape? live, List<PlanStep> steps, List<Diagnostic> blocks)
    {
        var def = c.Def;
        var wanted = def.Indexes.Where(i => i.AppliesTo(c.Input.Target)).ToList();
        if (wanted.Count == 0 && live == null) return true;
        var (schema, name) = c.Name;

        if (wanted.Count > 0 && c.Input.Engine == TargetNames.Fabric)
        {
            blocks.Add(new Diagnostic(DiagnosticCatalog.IndexNotSupported, new(c.Model.QueryFile, 0, 0),
                $"index.unsupported: {def.Name} declares index(es) {string.Join(", ", wanted.Select(i => $"`{i.Name}`"))}, and Fabric has no CREATE INDEX."));
            return false;
        }

        var liveIndexes = (live?.Physical ?? []).Where(p => p.Kind is "index" or "constraint_index").ToList();
        foreach (var index in wanted)
        {
            var canonical = IndexText.Canonical(index.Unique, index.Columns, index.Include);
            var existing = liveIndexes.FirstOrDefault(p => string.Equals(p.Name, index.Name, StringComparison.OrdinalIgnoreCase));
            var expect = $"index:{index.Name}={canonical}";
            if (existing == null)
                steps.Add(Step(StepType.Ddl, def.Name, $"create index {index.Name}", c.Ddl.CreateIndex(schema, name, index), RiskClass.Safe, ["index.added"]) with { Expect = expect });
            else if (existing.Kind == "constraint_index")
            {
                blocks.Add(new Diagnostic(DiagnosticCatalog.ModelUnplannable, new(c.Model.QueryFile, 0, 0),
                    $"index.constraint_name: {def.Name} declares index `{index.Name}`, but a PRIMARY KEY or UNIQUE constraint of that name exists on the target. Rename the index in the model."));
                return false;
            }
            else if (!string.Equals(existing.Definition, canonical, StringComparison.OrdinalIgnoreCase))
                steps.Add(Step(StepType.Ddl, def.Name, $"rebuild index {index.Name}", c.Ddl.DropIndex(schema, name, index.Name) + "\n" + c.Ddl.CreateIndex(schema, name, index), RiskClass.Risky,
                    ["index.changed", $"live definition {existing.Definition}; declared {canonical}"]) with { Expect = expect });
        }

        foreach (var extra in liveIndexes.Where(p => p.Kind == "index" && !wanted.Any(i => string.Equals(i.Name, p.Name, StringComparison.OrdinalIgnoreCase))))
            c.Noticed.Add($"Index `{extra.Name}` on {def.Name} exists on the target but is not declared in the model; it was left alone ({extra.Definition}).");
        return true;
    }

    // Column diff of a live table against the declared columns. Returns false when a question is open.
    private static bool Diff(ModelContext c, ObjectShape live, IReadOnlyList<NativeColumn> declared, List<PlanStep> steps, List<Diagnostic> blocks)
    {
        var def = c.Def;
        var (schema, name) = c.Name;
        var current = live.Columns.ToList();                                  // running shape, so every step knows the hash it must leave behind
        var liveByName = live.Columns.ToDictionary(x => x.Name, StringComparer.Ordinal);
        var liveAtStart = liveByName.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        var declaredByName = declared.ToDictionary(x => x.Name, StringComparer.Ordinal);
        var open = false;
        var blocksBefore = blocks.Count;

        string HashNow() => Hashing.ShapeHash(current);
        void Replace(string oldName, ColumnShape? to)
        {
            var i = current.FindIndex(x => x.Name == oldName);
            if (to == null) current.RemoveAt(i); else current[i] = to;
        }

        void RenameColumn(string from, string to)
        {
            var shape = liveByName[from] with { Name = to };
            Replace(from, shape);
            liveByName.Remove(from);
            liveByName[to] = shape;
        }

        // declared renames first (explicit in the model; never inferred)
        var renamed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in def.Renames)
        {
            if (!liveByName.ContainsKey(r.From) || liveByName.ContainsKey(r.To) || !declaredByName.ContainsKey(r.To)) continue;
            RenameColumn(r.From, r.To);
            steps.Add(Step(StepType.Ddl, def.Name, $"rename column {r.From} to {r.To}", c.Ddl.RenameColumn(schema, name, r.From, r.To), RiskClass.Risky, ["col.renamed", "declared in the model"], HashNow()));
            renamed.Add(r.From);
            renamed.Add(r.To);
        }

        var removed = live.Columns.Select(x => x.Name).Where(n => !declaredByName.ContainsKey(n) && !renamed.Contains(n)).ToList();
        var added = declared.Select(x => x.Name).Where(n => !liveByName.ContainsKey(n) && !renamed.Contains(n)).ToList();

        // a missing and a new column might be a rename: ask, never infer
        var renameTargets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in removed.ToList())
        {
            if (added.Count == 0) break;
            var oldShape = liveAtStart[r];
            var same = added.Where(a => declaredByName[a].Expected with { Name = r } == oldShape).ToList();
            Proposal? proposal = removed.Count == 1 && added.Count == 1 && same.Count == 1
                ? new Proposal("rename_to", same[0], ProposalCertainty.Normal, [$"`{r}` is gone and `{same[0]}` is new, with the identical type, length, nullability and collation."])
                : null;
            var q = new Question(QuestionIds.Rename(def.Name, r),
                $"`{r}` is no longer declared on {def.Name}, and new column(s) {string.Join(", ", added.Select(a => $"`{a}`"))} appear. Is `{r}` renamed?",
                [$"`{r}` is {oldShape.Type}{Size(oldShape)}; undeclared renames plan as a destructive drop plus an add."],
                [new("rename_to", "Rename the column, keeping its data", "A rename step replaces the drop and the add.", TakesValue: true, ValueHint: "the new column name, one of: " + string.Join(", ", added)),
                 new("drop_and_add", "Drop the old column and add the new one", "The old column's data is lost (destructive).")], proposal);
            var a = c.Answer(q);
            if (a == null) { open = true; continue; }
            if (a.Choice != "rename_to") continue;
            if (!added.Contains(a.Value!) || renameTargets.Contains(a.Value!))
            {
                blocks.Add(new Diagnostic(DiagnosticCatalog.AnswerValueMismatch, new(def.Name, 0, 0), $"col.rename.invalid: the answer to `{q.Id}` renames `{r}` to `{a.Value}`, which is not a new column of {def.Name} that is still free. New columns: {string.Join(", ", added)}."));
                continue;
            }
            renameTargets.Add(a.Value!);
            RenameColumn(r, a.Value!);
            steps.Add(Step(StepType.Ddl, def.Name, $"rename column {r} to {a.Value}", c.Ddl.RenameColumn(schema, name, r, a.Value!), RiskClass.Risky, ["col.renamed", $"answer {q.Id} = rename_to {a.Value}"], HashNow()));
            removed.Remove(r);
            added.Remove(a.Value!);
            // the renamed column may differ in more than its name; the common-column check below handles that
        }
        if (blocks.Count > blocksBefore) return false;

        foreach (var r in removed)
        {
            Replace(r, null);
            liveByName.Remove(r);
            steps.Add(Step(StepType.Ddl, def.Name, $"drop column {r}", c.Ddl.DropColumn(schema, name, r), RiskClass.Destructive, ["col.removed"], HashNow()));
        }

        foreach (var a in added)
        {
            var col = declaredByName[a];
            current.Add(col.Expected);
            var reasons = new List<string> { col.Nullable ? "col.added" : "col.added.notnull" };
            var q = new Question(QuestionIds.History(def.Name, a),
                $"{def.Name} gains column `{a}`. What about the rows that are already loaded?",
                ["Rows loaded before this change will have NULL (or the column default) in the new column."],
                [new("not_backfilled", "Leave existing rows as they are", "Rows loaded before this change keep NULL in the column; recorded in the plan."),
                 new("backfill_later", "Request a backfill separately", "The plan records the intent; run a backfill plan afterwards to fill the column.")]);
            var answer = c.Answer(q);
            if (answer == null) { open = true; continue; }
            reasons.Add($"answer {q.Id} = {answer.Choice}");
            c.Noticed.Add(answer.Choice == "not_backfilled"
                ? $"Rows of {def.Name} loaded before this plan will have NULL in `{a}` (history disposition: not_backfilled{(answer.Note == null ? "" : ", " + answer.Note)})."
                : $"A backfill of `{a}` in {def.Name} was requested but is not part of this plan.");
            steps.Add(Step(StepType.Ddl, def.Name, $"add column {a}", c.Ddl.AddColumn(schema, name, col), col.Nullable ? RiskClass.Safe : RiskClass.Risky, reasons, HashNow()));
        }

        // columns present on both sides: type, nullability and collation
        foreach (var col in declared.Where(d => liveByName.ContainsKey(d.Name) && !added.Contains(d.Name)))
        {
            var from = liveByName[col.Name];
            var to = col.Expected;
            if (from == to) continue;
            if (from.Computed != null) { c.Noticed.Add($"`{col.Name}` of {def.Name} is a computed column on the target and was not changed."); continue; }
            var change = DdlGenerator.Classify(from, to);
            var risk = RiskClass.Safe;
            var reasons = new List<string>();
            if (change == TypeChange.Widening) reasons.Add("col.type.widen");
            else if (change == TypeChange.Other) { risk = RiskClass.Destructive; reasons.Add("col.type.other"); }
            if (from.Nullable != to.Nullable) { reasons.Add(to.Nullable ? "col.null.relax" : "col.null.tighten"); if (!to.Nullable && risk == RiskClass.Safe) risk = RiskClass.Risky; }
            if (from.Collation != to.Collation) { reasons.Add("col.collation"); if (risk == RiskClass.Safe) risk = RiskClass.Risky; }
            Replace(col.Name, to);
            steps.Add(Step(StepType.Ddl, def.Name, $"alter column {col.Name}", c.Ddl.AlterColumn(schema, name, col), risk, reasons, HashNow()));
        }
        return !open;

        static string Size(ColumnShape s) => s.Length != null ? $"({(s.Length == -1 ? "max" : s.Length.ToString())})" : s.Precision != null ? $"({s.Precision}, {s.Scale})" : "";
    }

    // ------------------------------------------------------------------------------------------------------------------------------------

    private static bool PlanLoads(ModelContext c, bool tableIsNew, List<PlanStep> loadSteps, List<Diagnostic> blocks)
    {
        var def = c.Def;
        if (!c.Input.Loads.TryGetValue(def.Name, out var loads) || loads.Count == 0) return true;
        if (def.IsCopy && c.Model.OriginList.Count == 0)
        {
            c.Noticed.Add($"{def.Name} has no origin to copy from in this plan (every origin was left out), so its table is not loaded.");
            return true;
        }
        var wanted = c.Input.OperationChoice?.GetValueOrDefault(def.Name);
        var backfill = c.Input.Backfills?.Contains(def.Name) == true;
        var load = wanted != null ? loads.FirstOrDefault(l => l.Operation == wanted) : loads.FirstOrDefault(l => l.IsDefault) ?? (loads.Count == 1 ? loads[0] : null);
        if (load == null)
        {
            blocks.Add(new Diagnostic(DiagnosticCatalog.ModelUnplannable, new(def.Name, 0, 0), wanted != null
                ? $"load.operation.missing: {def.Name} has no rendered load operation named `{wanted}` for {c.Input.Target}."
                : $"{def.Name} declares several load operations and none is the default for {c.Input.Target}; name one with --operation."));
            return false;
        }

        var parameters = new List<PlanParameter>();
        string? resolverResult = null;
        var open = false;
        foreach (var p in load.Parameters)
        {
            if (p.Source == "resolver")
            {
                string? raw = null;
                if (!tableIsNew)
                {
                    var outcome = c.Input.Resolved.GetValueOrDefault(PlanInput.ResolverKey(def.Name, load.Operation));
                    if (outcome == null || outcome.Error != null)
                    {
                        blocks.Add(new Diagnostic(DiagnosticCatalog.ResolverResultInvalid, new(def.Name, 0, 0),
                            $"{def.Name} / {load.Operation}: the resolver for `@{p.Name}` {(outcome?.Error ?? "was not run")}."));
                        return false;
                    }
                    raw = outcome.Value;
                }
                resolverResult = raw;
                var value = raw;
                if (value == null)
                {
                    // the watermark is NULL: the declared on_null decides, never a guess
                    if (load.Watermark is { OnNull: WatermarkSpec.InitialLiteral, Initial: { } initial }) value = initial;
                    else
                    {
                        var q = ParamQuestion(def.Name, load, p, $"The resolver for `@{p.Name}` returned NULL ({(tableIsNew ? "the table does not exist yet" : "the target is empty")}), and the model says `on_null: require_param`.");
                        var a = c.Answer(q);
                        if (a == null) { open = true; continue; }
                        if (a.Choice == "skip_load") return true;
                        value = a.Value;
                    }
                }
                if (value != null && !ParameterFits(c, load, p, value, blocks)) return false;
                parameters.Add(new PlanParameter(p.Name, p.Type, "resolver", value));
            }
            else
            {
                var q = ParamQuestion(def.Name, load, p, $"The load needs a value for `@{p.Name}` ({p.Type}){(p.Constraint == null ? "" : ", " + p.Constraint)}.");
                var a = c.Answer(q);
                if (a == null) { open = true; continue; }
                if (a.Choice == "skip_load") return true;
                if (!ParameterFits(c, load, p, a.Value!, blocks)) return false;
                parameters.Add(new PlanParameter(p.Name, p.Type, "runtime", a.Value));
            }
        }
        if (open) return true;
        if (!SpanFits(c, load, parameters, blocks)) return false;
        RangeNotice(c, load, parameters);

        var loadStep = new PlanStep("", backfill ? StepType.Backfill : StepType.Load, def.Name, $"{(backfill ? "backfill" : "load")} {def.Name} ({load.Operation})", load.Script,
            backfill ? RiskClass.Risky : RiskClass.Safe, backfill ? ["load.backfill", "requested with --backfill"] : ["load.routine"], null, parameters,
            load.ResolverText, resolverResult, HasResolver: load.ResolverText != null, FileHash: load.FileHash, Operation: load.Operation, DefinitionHash: c.Model.DefinitionHash);
        if (def.IsCopy && c.Model.OriginList.Count > 0)
        {
            // a copy: for each origin the rows are staged on the destination, then loaded from there by the ordinary strategy (a copy with a slice replaces only that origin's rows); the staging table is dropped at the end
            foreach (var origin in c.Model.OriginList)
            {
                loadSteps.Add(TransferStep(c, origin));
                loadSteps.AddRange(Hooked.Around(c.Model, backfill ? "backfill" : "load", loadStep));
            }
            var (stagingSchema, stagingTable) = (CopyModels.StagingSchema(c.Input.Config), CopyModels.StagingTable(def.Name));
            loadSteps.Add(Step(StepType.Ddl, $"{stagingSchema}.{stagingTable}", $"drop staging table of {def.Name}", c.Ddl.DropTableIfExists(stagingSchema, stagingTable), RiskClass.Safe, ["copy.staging.drop"]));
            return true;
        }
        loadSteps.AddRange(Hooked.Around(c.Model, backfill ? "backfill" : "load", loadStep));
        return true;
    }

    /// <summary>
    /// The step that moves a copy's rows: its text creates the staging table (declared columns, so a NOT NULL column refuses a NULL at once), and apply reads <see cref="TransferSpec.ReadText"/> on the origin
    /// and writes the rows to it. The read names every column in the origin's own quoting, in the declared order.
    /// </summary>
    private static PlanStep TransferStep(ModelContext c, CopyOrigin origin)
    {
        var def = c.Def;
        var (stagingSchema, stagingTable) = (CopyModels.StagingSchema(c.Input.Config), CopyModels.StagingTable(def.Name));
        var create = c.Ddl.DropTableIfExists(stagingSchema, stagingTable) + "\n" + c.Ddl.CreateTable(stagingSchema, stagingTable, c.Ddl.MapAll(def));
        var originDdl = TargetRegistry.Get(origin.Engine).CreateDdl(c.Input.Config);
        var (originSchema, originName) = DdlGenerator.Split(origin.Table);
        // a column the copy adds (its slice, when the origin has none) is not read: the value is written into every row
        var read = $"SELECT {string.Join(", ", def.Columns.Where(x => !(def.SliceColumnAdded && string.Equals(x.Name, def.Slice!.Column, StringComparison.OrdinalIgnoreCase))).Select(x => originDdl.QuoteIdentifier(x.Name)))} FROM {originDdl.Qualified(originSchema, originName)}";
        var slice = def.Slice != null && origin.SliceValue != null ? new PlanSlice(def.Slice.Column, origin.SliceValue, def.SliceColumnAdded) : null;
        var spec = new TransferSpec(origin.Connection, read, $"{stagingSchema}.{stagingTable}", def.Columns.Select(x => new PlanColumn(x.Name, x.Type)).ToList(), slice);
        return new PlanStep("", StepType.Transfer, def.Name, $"copy {def.Name} from {origin.Connection} ({origin.Table})", create, RiskClass.Safe, [slice == null ? "copy.transfer" : "copy.transfer.slice"], null, [], Transfer: spec);
    }

    private static bool ParameterFits(ModelContext c, RenderedLoad load, RenderedParameter p, string value, List<Diagnostic> blocks)
    {
        if (ColumnTypes.LiteralFits(p.Type, value)) return true;
        blocks.Add(new Diagnostic(DiagnosticCatalog.AnswerValueMismatch, new(c.Def.Name, 0, 0), $"{c.Def.Name} / {load.Operation}: `{value}` is not a valid {p.Type} value for `@{p.Name}`."));
        return false;
    }

    /// <summary>The `max_span` constraint of a range load: the end may be at most that far after the start.</summary>
    private static bool SpanFits(ModelContext c, RenderedLoad load, List<PlanParameter> parameters, List<Diagnostic> blocks)
    {
        var endDef = load.Parameters.FirstOrDefault(p => p.Name == "end" && p.Constraint?.StartsWith("max_span ", StringComparison.Ordinal) == true);
        var start = parameters.FirstOrDefault(p => p.Name == "start")?.Value;
        var end = parameters.FirstOrDefault(p => p.Name == "end")?.Value;
        if (start == null || end == null) return true;
        if (!DateTime.TryParse(start, CultureInfo.InvariantCulture, DateTimeStyles.None, out var s) || !DateTime.TryParse(end, CultureInfo.InvariantCulture, DateTimeStyles.None, out var e)) return true;
        if (e <= s)
        {
            blocks.Add(new Diagnostic(DiagnosticCatalog.AnswerValueMismatch, new(c.Def.Name, 0, 0), $"{c.Def.Name} / {load.Operation}: the range {start} to {end} is empty or backwards (the end is exclusive and must be after the start)."));
            return false;
        }
        if (endDef == null || LoadDuration.TryParse(endDef.Constraint![9..]) is not { } span) return true;
        var limit = span.Unit switch
        {
            DurationUnit.Minute => s.AddMinutes(span.Amount),
            DurationUnit.Hour => s.AddHours(span.Amount),
            DurationUnit.Day => s.AddDays(span.Amount),
            DurationUnit.Week => s.AddDays(7 * span.Amount),
            _ => s.AddMonths(span.Amount),
        };
        if (e <= limit) return true;
        blocks.Add(new Diagnostic(DiagnosticCatalog.AnswerValueMismatch, new(c.Def.Name, 0, 0), $"{c.Def.Name} / {load.Operation}: the range {start} to {end} is longer than the operation's max_span of {span}."));
        return false;
    }

    /// <summary>
    /// A reload range that the target's data does not overlap is legal (a first load of a period, a gap being filled) but easy to get wrong by a year or a unit, so the plan says what
    /// the target holds and what the range will therefore do.
    /// </summary>
    private static void RangeNotice(ModelContext c, RenderedLoad load, List<PlanParameter> parameters)
    {
        var bounds = c.Input.RangeBounds?.GetValueOrDefault(PlanInput.ResolverKey(c.Def.Name, load.Operation));
        var start = parameters.FirstOrDefault(p => p.Name == "start")?.Value;
        var end = parameters.FirstOrDefault(p => p.Name == "end")?.Value;
        if (bounds == null || bounds.Error != null || start == null || end == null) return;
        var who = $"{c.Def.Name} / {load.Operation}";
        if (bounds.Min == null || bounds.Max == null)
        {
            c.Noticed.Add($"{who}: the target table holds no rows, so the DELETE for {start} to {end} removes nothing and the load fills that range for the first time.");
            return;
        }
        if (!DateTime.TryParse(start, CultureInfo.InvariantCulture, DateTimeStyles.None, out var s) || !DateTime.TryParse(end, CultureInfo.InvariantCulture, DateTimeStyles.None, out var e) ||
            !DateTime.TryParse(bounds.Min, CultureInfo.InvariantCulture, DateTimeStyles.None, out var min) || !DateTime.TryParse(bounds.Max, CultureInfo.InvariantCulture, DateTimeStyles.None, out var max)) return;
        if (e <= min || s > max)
            c.Noticed.Add($"{who}: the target holds rows from {bounds.Min} to {bounds.Max}; the range {start} to {end} does not overlap them, so the DELETE removes nothing and the load adds that period ({(s > max ? "after" : "before")} what is there). Check the dates if that is not what you meant.");
    }

    private static Question ParamQuestion(string model, RenderedLoad load, RenderedParameter p, string why) =>
        new(QuestionIds.Param(model, load.Operation, p.Name), $"{model} / {load.Operation}: what value should `@{p.Name}` take?", [why],
            [new("provide", "Run the load with this value", "The value is recorded in the plan and in the run log.", TakesValue: true, ValueHint: $"a {p.Type} literal"),
             new("skip_load", "Do not load this model in this plan", "Its structure is still planned; no rows are loaded.")]);
}

/// <summary>
/// Where hooks go in a plan. A hook is attached to an event (`pre_`/`post_` create, alter, load, backfill); this is the one place that says what each event wraps, so
/// a new kind of hook is a row in <see cref="HookEvents"/> and a case here. Hooks of one event run in the order the model lists them.
/// </summary>
internal static class Hooked
{
    private static PlanStep Step(PlannedModel model, PlannedHook h) => new("", StepType.Hook, model.Definition.Name, $"hook {h.Hook.Name} ({h.Hook.Event})", h.Text,
        Enum.Parse<RiskClass>(h.Hook.Risk, ignoreCase: true), h.Hook.Group == null ? ["hook.fired", $"event {h.Hook.Event}"] : ["hook.fired", $"event {h.Hook.Event}", $"group {h.Hook.Group}"],
        null, [], Operation: h.Hook.Event, FileHash: h.FileHash, Hook: h.Hook.Name, Effect: h.Hook.Effect);

    private static IEnumerable<PlanStep> Of(PlannedModel model, string phase, string action) =>
        model.HookList.Where(h => h.Hook.Event == $"{phase}_{action}").Select(h => Step(model, h));

    /// <summary>The structure steps of one model, wrapped: create hooks around a creation, alter hooks around changes to an existing object.</summary>
    public static IEnumerable<PlanStep> Structure(PlannedModel model, bool creating, List<PlanStep> steps)
    {
        // adoption's track step and schema creation are bookkeeping, not changes to the object
        var changes = steps.Where(s => s.Type == StepType.Ddl && !s.Description.StartsWith("create schema", StringComparison.Ordinal)).ToList();
        if (changes.Count == 0) return steps;
        var action = creating ? "create" : "alter";
        var first = steps.IndexOf(changes[0]);
        var last = steps.IndexOf(changes[^1]);
        return steps.Take(first).Concat(Of(model, "pre", action)).Concat(steps.Skip(first).Take(last - first + 1)).Concat(Of(model, "post", action)).Concat(steps.Skip(last + 1));
    }

    public static IEnumerable<PlanStep> Around(PlannedModel model, string action, PlanStep step) =>
        Of(model, "pre", action).Append(step).Concat(Of(model, "post", action));
}
