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

    public static int Plan(CommandSpec spec, string root, string? targetArg, string[] models, FileInfo? answersFile, bool acceptInferred, DirectoryInfo? outDir, string[] ops, string[] backfillArgs,
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
                foreach (var d in fileDiags) error.Write(DiagnosticFormatter.Format(d));
                return CliApp.ExitFindings;
            }
        }
        if (answersFile == null && !interactive && !acceptInferred)
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
            foreach (var d in resolution.Diagnostics) error.Write(DiagnosticFormatter.Format(d));
            if (!resolution.Complete)
            {
                output.WriteLine($"Nothing was planned: {resolution.Unanswered.Count} question(s) are open.");
                return CliApp.ExitFindings;
            }
            answers.AddRange(resolution.Answers);
            foreach (var q in fresh) asked.Add(q.Id);
        }
        if (file != null)
            foreach (var a in file.Answers.Where(a => !result.UsedAnswers.Any(u => u.QuestionId == a.QuestionId)))
                error.Write(DiagnosticFormatter.Format(new Diagnostic(DiagnosticCatalog.AnswerForUnknownQuestion, a.Location, $"The answers file has an answer for `{a.QuestionId}`, which was not asked in this run.")));

        foreach (var d in result.Skipped) error.Write(DiagnosticFormatter.Format(d));
        foreach (var d in result.Blocks) error.Write(DiagnosticFormatter.Format(d));

        if (result.Steps.Count == 0)
        {
            output.WriteLine(result.Blocks.Count > 0
                ? $"Nothing was planned: {result.Blocks.Count} blocked model(s) and {result.Skipped.Count} skipped."
                : "Nothing to do: every model matches its target and no load applies.");
            return result.Blocks.Count > 0 ? CliApp.ExitFindings : CliApp.ExitOk;
        }

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
        output.WriteLine($"Plan {plan.Id}: {plan.Steps.Count} step(s) ({plan.Steps.Count(s => s.Type == StepType.Ddl)} ddl, {plan.Steps.Count(s => s.Type == StepType.Load)} load, {plan.Steps.Count(s => s.Type == StepType.Backfill)} backfill, {plan.Steps.Count(s => s.Type == StepType.Track)} track); {risky} risky, {destructive} destructive.");
        output.WriteLine($"  report: {Path.GetRelativePath(root, mdPath)}");
        output.WriteLine($"  plan:   {Path.GetRelativePath(root, yamlPath)}");
        if (result.Blocks.Count + result.Skipped.Count > 0)
            output.WriteLine($"  NOT planned: {result.Blocks.Count} blocked, {result.Skipped.Count} skipped (see above and the report).");
        output.WriteLine($"Review the report, then `{ProductInfo.Cli} apply {Path.GetRelativePath(root, yamlPath)}`.");
        return result.Blocks.Count > 0 ? CliApp.ExitFindings : CliApp.ExitOk;
    }

    /// <summary>`model=operation` pairs. A model may be named once.</summary>
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
        foreach (var d in result.Blocks.Concat(result.Skipped)) error.Write(DiagnosticFormatter.Format(d));

        // the string profile, against what the catalog says (DESIGN.md 9.4)
        var liveCollation = new List<Diagnostic>();
        foreach (var m in session.Input.Models)
            if (session.Input.Live.TryGetValue(m.Definition.Name, out var shape))
                liveCollation.AddRange(CollationChecker.CheckLive(session.Context.Config, session.Target, m.Definition,
                    shape.Columns.Where(c => c.Type is "nvarchar" or "varchar" or "character varying" or "char").Select(c => (c.Name, c.Collation))));
        foreach (var d in liveCollation) error.Write(DiagnosticFormatter.Format(d));

        var width = Math.Max(6, result.Bases.Count == 0 ? 0 : result.Bases.Max(b => b.Object.Length));
        output.WriteLine($"{"object".PadRight(width)}  state");
        foreach (var b in result.Bases.OrderBy(b => b.Object, StringComparer.Ordinal))
            output.WriteLine($"{b.Object.PadRight(width)}  {b.State switch { ObjectState.Missing => "missing (a plan would create it)", ObjectState.Untracked => "exists, not tracked (a plan would ask to adopt it)", ObjectState.InSync => "in sync", _ => "CHANGED OUTSIDE THE TOOL" }}");
        output.WriteLine();
        var steps = result.Steps;
        output.WriteLine($"A plan now would have {steps.Count} step(s) ({steps.Count(s => s.Risk == RiskClass.Risky)} risky, {steps.Count(s => s.Risk == RiskClass.Destructive)} destructive) and ask {result.Questions.Count} question(s) first; {result.Blocks.Count} blocked, {result.Skipped.Count} skipped.");
        return result.Blocks.Count > 0 || result.Bases.Any(b => b.State == ObjectState.OutOfBand) || liveCollation.Any(d => d.Severity == Severity.Error) ? CliApp.ExitFindings : CliApp.ExitOk;
    }

    internal static void WriteAtomic(string path, string content)
    {
        var temp = path + ".ddb-" + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllBytes(temp, new UTF8Encoding(false).GetBytes(content));
        File.Move(temp, path, overwrite: true);
    }
}
