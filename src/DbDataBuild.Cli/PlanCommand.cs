using System.Text;
using DbDataBuild.Core;
using DbDataBuild.Core.Questions;
using DbDataBuild.Models;
using DbDataBuild.Planning;
using DbDataBuild.State;

namespace DbDataBuild.Cli;

/// <summary>
/// `dbdatabuild plan` and `dbdatabuild check` (DESIGN.md 9.1, 10.1, 11). Effect class: target read-only; `plan` also writes plan files under the project.
/// Planning refuses a project that does not validate, asks every open question (interactively, or from `--answers`, never by default), and writes a plan
/// only when nothing is open. Blocked models are reported, not planned, and everything downstream of them is skipped.
/// </summary>
internal static class PlanCommand
{
    public const string PlansDir = "plans";
    private const int MaxRounds = 12;

    public static int Plan(CommandSpec spec, string root, string? targetArg, string[] models, FileInfo? answersFile, bool acceptInferred, DirectoryInfo? outDir, string[] ops, string[] backfillArgs, string[] paramArgs,
        TextWriter output, TextWriter error, TextReader input, bool interactive, Func<string, string?> env)
    {
        // ---- answers file first: a bad file is a usage problem and must not need a database ----
        AnswerFile? file = null;
        var answersPath = answersFile?.FullName ?? QuestionResolver.NoAnswersFile;
        if (answersFile != null)
        {
            var fileDiags = new List<Diagnostic>();
            file = AnswerFileLoader.Load(File.ReadAllText(answersFile.FullName), answersFile.Name, fileDiags);
            if (file == null)
            {
                foreach (var d in fileDiags) error.Diag(d);
                return CliApp.ExitFindings;
            }
        }
        // --param model.operation.parameter=value is the answer to that parameter's question, so a scheduled backfill needs no file
        if (!ParseParams(paramArgs, file, error, out var paramAnswers)) return CliApp.ExitUsage;
        if (paramAnswers.Count > 0) { file = new AnswerFile([.. file?.Answers ?? [], .. paramAnswers]); if (answersFile == null) answersPath = "--param"; }
        if (answersFile == null && paramAnswers.Count == 0 && !interactive && !acceptInferred)
            output.WriteLine("note: not interactive and no --answers file: any open question will be listed and nothing will be planned.");

        if (!ParseOps(ops, "--op", error, out var operations) || !ParseOps(backfillArgs, "--backfill", error, out var backfillOps)) return CliApp.ExitUsage;
        foreach (var (model, op) in backfillOps)
            if (!operations.TryAdd(model, op) && operations[model] != op) { error.WriteLine($"`{model}` is given two different operations (--op {operations[model]}, --backfill {op})."); return CliApp.ExitUsage; }

        var (session, exit) = PlanningSession.Prepare(spec, root, targetArg, models, output, error, env, operations, backfillOps.Keys.ToHashSet(StringComparer.Ordinal));
        if (session == null) return exit;

        // ---- ask, plan again, until nothing is open ----
        IPrompter? prompter = interactive ? new ConsolePrompter(input, output) : null;
        var answers = new List<ResolvedAnswer>();
        var asked = new HashSet<string>();
        PlanResult result;
        var round = 0;
        while (true)
        {
            result = Planner.Plan(session.Input, answers);
            if (result.Complete) break;
            if (++round > MaxRounds) throw new InvalidOperationException("Planning did not settle: questions keep changing between rounds. This is a tool bug.");
            var fresh = result.Questions.Where(q => !answers.Any(a => a.QuestionId == q.Id)).ToList();
            var resolution = QuestionResolver.Resolve(fresh, file, answersPath, prompter, new ResolveOptions(acceptInferred, WarnOnUnknownAnswers: false));
            foreach (var d in resolution.Diagnostics) error.Diag(d);
            if (!resolution.Complete)
            {
                output.Payload("open_questions", resolution.Unanswered.Select(QuestionJson).ToList());
                output.WriteLine($"Nothing was planned: {resolution.Unanswered.Count} question(s) are open.");
                return CliApp.ExitFindings;
            }
            answers.AddRange(resolution.Answers);
            foreach (var q in fresh) asked.Add(q.Id);
        }
        if (file != null)
            foreach (var a in file.Answers.Where(a => !result.UsedAnswers.Any(u => u.QuestionId == a.QuestionId)))
                error.Diag(new Diagnostic(DiagnosticCatalog.AnswerForUnknownQuestion, a.Location, $"The answers file has an answer for `{a.QuestionId}`, which was not asked in this run."));

        foreach (var d in result.Skipped) error.Diag(d);
        foreach (var d in result.Blocks) error.Diag(d);

        if (result.Steps.Count == 0)
        {
            output.WriteLine(result.Blocks.Count > 0
                ? $"Nothing was planned: {result.Blocks.Count} blocked model(s) and {result.Skipped.Count} skipped."
                : "Nothing to do: every model matches its target and no load applies.");
            return result.Blocks.Count > 0 ? CliApp.ExitFindings : CliApp.ExitOk;
        }

        output.Payload("target", session.Target);
        output.Payload("questions_asked", asked.Count);
        output.Payload("answers", result.UsedAnswers);
        output.Payload("noticed", result.Noticed);
        output.Payload("blocked", result.Blocks.Select(b => b.Code).ToList());
        output.Payload("skipped", result.Skipped.Select(b => b.Code).ToList());

        // ---- build, write ----
        var (commit, dirty) = GitInfo.Read(root);
        var draft = new DbDataBuild.Planning.Plan("", session.Target, commit, dirty, ProductInfo.Version, result.Bases, result.UsedAnswers, result.Steps, result.Noticed);
        var plan = draft with { Id = PlanDocument.CreateId(DateOnly.FromDateTime(DateTime.UtcNow), draft) };
        var dir = outDir?.FullName ?? Path.Combine(root, PlansDir, session.Target);
        var yamlPath = Path.Combine(dir, plan.Id + ".plan.yml");
        var mdPath = Path.Combine(dir, plan.Id + ".plan.md");
        Directory.CreateDirectory(dir);
        WriteAtomic(yamlPath, PlanDocument.Serialize(plan));
        WriteAtomic(mdPath, PlanReport.Markdown(plan, result.Blocks, result.Skipped));

        var risky = plan.Steps.Count(s => s.Risk == RiskClass.Risky);
        var destructive = plan.Steps.Count(s => s.Risk == RiskClass.Destructive);
        output.Payload("plan", plan);
        output.Payload("files", new { plan = Path.GetRelativePath(root, yamlPath).Replace('\\', '/'), report = Path.GetRelativePath(root, mdPath).Replace('\\', '/') });
        output.WriteLine($"Plan {plan.Id}: {plan.Steps.Count} step(s) ({plan.Steps.Count(s => s.Type == StepType.Ddl)} ddl, {plan.Steps.Count(s => s.Type == StepType.Load)} load, {plan.Steps.Count(s => s.Type == StepType.Backfill)} backfill, {plan.Steps.Count(s => s.Type == StepType.Hook)} hook, {plan.Steps.Count(s => s.Type == StepType.Track)} track); {risky} risky, {destructive} destructive.");
        output.WriteLine($"  report: {Path.GetRelativePath(root, mdPath)}");
        output.WriteLine($"  plan:   {Path.GetRelativePath(root, yamlPath)}");
        if (result.Blocks.Count + result.Skipped.Count > 0)
            output.WriteLine($"  NOT planned: {result.Blocks.Count} blocked, {result.Skipped.Count} skipped (see above and the report).");
        output.WriteLine($"Review the report, then `{ProductInfo.Cli} apply {Path.GetRelativePath(root, yamlPath)}`.");
        return result.Blocks.Count > 0 ? CliApp.ExitFindings : CliApp.ExitOk;
    }

    /// <summary>`model=operation` pairs. A model may be named once.</summary>
    /// <summary>`model.operation.parameter=value`: the model name has dots of its own, the operation and parameter names do not, so the last two parts before `=` are those.</summary>
    internal static bool ParseParams(string[] args, AnswerFile? file, TextWriter error, out List<Answer> answers)
    {
        answers = [];
        foreach (var a in args)
        {
            var eq = a.IndexOf('=');
            var parts = eq <= 0 ? [] : a[..eq].Split('.');
            if (parts.Length < 3 || parts.Any(p => p.Length == 0) || eq == a.Length - 1)
            {
                error.WriteLine($"--param takes model.operation.parameter=value (for example marts.fct_events.reload_period.start=2024-01-01), not `{a}`.");
                return false;
            }
            var id = QuestionIds.Param(string.Join('.', parts[..^2]), parts[^2], parts[^1]);
            if (answers.Any(x => x.QuestionId == id) || file?.Answers.Any(x => x.QuestionId == id) == true)
            {
                error.WriteLine($"The parameter in `{a}` is answered more than once (--param and the answers file both name {id}).");
                return false;
            }
            answers.Add(new Answer(id, "provide", a[(eq + 1)..], null, false, new SourceLocation("--param", 0, 0)));
        }
        return true;
    }

    private static bool ParseOps(string[] args, string flag, TextWriter error, out Dictionary<string, string> result)
    {
        result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var a in args)
        {
            var i = a.IndexOf('=');
            if (i <= 0 || i == a.Length - 1) { error.WriteLine($"{flag} takes model=operation (for example marts.fct_orders=reload_period), not `{a}`."); return false; }
            if (!result.TryAdd(a[..i], a[(i + 1)..])) { error.WriteLine($"{flag} names `{a[..i]}` more than once."); return false; }
        }
        return true;
    }

    /// <summary>`check`: the findings a plan would act on, without asking anything or writing anything.</summary>
    public static int Check(CommandSpec spec, string root, string? targetArg, string[] models, TextWriter output, TextWriter error, Func<string, string?> env)
    {
        var (session, exit) = PlanningSession.Prepare(spec, root, targetArg, models, output, error, env);
        if (session == null) return exit;
        var result = Planner.Plan(session.Input, []);
        foreach (var d in result.Blocks.Concat(result.Skipped)) error.Diag(d);

        // the string profile, against what the catalog says (DESIGN.md 9.4)
        var liveCollation = new List<Diagnostic>();
        foreach (var m in session.Input.Models)
            if (session.Input.Live.TryGetValue(m.Definition.Name, out var shape))
                liveCollation.AddRange(CollationChecker.CheckLive(session.Context.Config, session.Target, m.Definition,
                    shape.Columns.Where(c => c.Type is "nvarchar" or "varchar" or "character varying" or "char").Select(c => (c.Name, c.Collation))));
        foreach (var d in liveCollation) error.Diag(d);

        var stepsNow = result.Steps;
        output.Payload("target", session.Target);
        output.Payload("objects", result.Bases.OrderBy(b => b.Object, StringComparer.Ordinal).Select(b => new { name = b.Object, state = b.State, live_shape_hash = b.LiveShapeHash, recorded_shape_hash = b.RecordedShapeHash }).ToList());
        output.Payload("preview", new
        {
            steps = stepsNow.Count, risky = stepsNow.Count(s => s.Risk == RiskClass.Risky), destructive = stepsNow.Count(s => s.Risk == RiskClass.Destructive),
            questions = result.Questions.Select(QuestionJson).ToList(), blocked = result.Blocks.Select(b => b.Code).ToList(), skipped = result.Skipped.Select(b => b.Code).ToList(),
        });
        output.Payload("noticed", result.Noticed);

        var width = Math.Max(6, result.Bases.Count == 0 ? 0 : result.Bases.Max(b => b.Object.Length));
        output.WriteLine($"{"object".PadRight(width)}  state");
        foreach (var b in result.Bases.OrderBy(b => b.Object, StringComparer.Ordinal))
            output.WriteLine($"{b.Object.PadRight(width)}  {b.State switch { ObjectState.Missing => "missing (a plan would create it)", ObjectState.Untracked => "exists, not tracked (a plan would ask to adopt it)", ObjectState.InSync => "in sync", _ => "CHANGED OUTSIDE THE TOOL" }}");
        output.WriteLine();
        var steps = result.Steps;
        output.WriteLine($"A plan now would have {steps.Count} step(s) ({steps.Count(s => s.Risk == RiskClass.Risky)} risky, {steps.Count(s => s.Risk == RiskClass.Destructive)} destructive) and ask {result.Questions.Count} question(s) first; {result.Blocks.Count} blocked, {result.Skipped.Count} skipped.");
        return result.Blocks.Count > 0 || result.Bases.Any(b => b.State == ObjectState.OutOfBand) || liveCollation.Any(d => d.Severity == Severity.Error) ? CliApp.ExitFindings : CliApp.ExitOk;
    }

    internal static object QuestionJson(Question q) => new
    {
        id = q.Id, prompt = q.Prompt, context = q.Context,
        options = q.Options.Select(o => new { key = o.Key, description = o.Description, consequence = o.Consequence, takes_value = o.TakesValue, value_hint = o.ValueHint }).ToList(),
        proposal = q.Proposal == null ? null : new { option = q.Proposal.OptionKey, value = q.Proposal.Value, certainty = q.Proposal.Certainty.ToString().ToLowerInvariant(), evidence = q.Proposal.Evidence },
    };

    internal static void WriteAtomic(string path, string content)
    {
        var temp = path + ".ddb-" + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllBytes(temp, new UTF8Encoding(false).GetBytes(content));
        File.Move(temp, path, overwrite: true);
    }
}
