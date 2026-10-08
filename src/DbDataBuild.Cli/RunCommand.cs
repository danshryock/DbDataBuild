using DbDataBuild.Core;
using DbDataBuild.Planning;

namespace DbDataBuild.Cli;

/// <summary>
/// `dbdatabuild run` (DESIGN.md 9.1): plan and apply in one command, allowed only when the plan is routine loads. It refuses, and points to `plan`, when anything else
/// is in the picture: an open question, a block, a skip, or any step that is not a safe load (DDL, a track step, a risky or destructive step). The plan is written like
/// any other plan before it is applied, so the tracking tables and the plan files show exactly what ran.
/// </summary>
internal static class RunCommand
{
    public static int Run(CommandSpec spec, string root, string? targetArg, string[] models, bool allowDirty, TextWriter output, TextWriter error, Func<string, string?> env)
    {
        // the header names the login and effect once; the planning session prints its own header for `run`
        var (session, exit) = PlanningSession.Prepare(spec, root, targetArg, models, output, error, env);
        if (session == null) return exit;

        var result = Planner.Plan(session.Input, []);
        var reasons = new List<string>();
        if (!result.Complete) reasons.Add($"{result.Questions.Count} open question(s) ({string.Join(", ", result.Questions.Select(q => q.Id))})");
        if (result.Blocks.Count > 0) reasons.Add($"{result.Blocks.Count} blocked model(s) ({string.Join(", ", result.Blocks.Select(b => b.Code).Distinct())})");
        if (result.Skipped.Count > 0) reasons.Add($"{result.Skipped.Count} skipped model(s)");
        // a data hook around the load (pre_load, post_load) is part of a routine load; anything else is not
        var notLoads = result.Steps.Where(s => !(s.Risk == RiskClass.Safe && (s.Type == StepType.Load || (s.Type == StepType.Hook && s.Effect == "data" && s.Operation is "pre_load" or "post_load")))).ToList();
        if (notLoads.Count > 0) reasons.Add($"{notLoads.Count} step(s) that are not routine loads ({string.Join("; ", notLoads.Take(3).Select(s => $"{s.Type.ToString().ToLowerInvariant()}: {s.Description}"))}{(notLoads.Count > 3 ? "; ..." : "")})");
        if (reasons.Count > 0)
        {
            foreach (var d in result.Blocks.Concat(result.Skipped)) error.Diag(d);
            output.WriteLine($"`{ProductInfo.Cli} {spec.Name}` only runs routine loads, and this is not one: {string.Join("; ", reasons)}.");
            output.WriteLine($"Nothing was executed. Use `{ProductInfo.Cli} plan` to review and decide, then `{ProductInfo.Cli} apply`.");
            return CliApp.ExitFindings;
        }
        if (result.Steps.Count == 0)
        {
            output.WriteLine("Nothing to do: no load applies.");
            return CliApp.ExitOk;
        }

        var (commit, dirty) = GitInfo.Read(root);
        var draft = new Plan("", session.Target, commit, dirty, ProductInfo.Version, result.Bases, result.UsedAnswers, result.Steps, result.Noticed);
        var plan = draft with { Id = PlanDocument.CreateRunId(DateTime.UtcNow) };
        var dir = Path.Combine(root, PlanCommand.PlansDir, session.Target);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, plan.Id + ".plan.yml");
        PlanCommand.WriteAtomic(path, PlanDocument.Serialize(plan));
        PlanCommand.WriteAtomic(Path.Combine(dir, plan.Id + ".plan.md"), PlanReport.Markdown(plan));
        output.WriteLine($"Plan {plan.Id}: {plan.Steps.Count} routine load(s), written to {Path.GetRelativePath(root, path)}.");

        var apply = CommandSpecs.All.First(c => c.Name == "apply");
        return ApplyCommand.Run(apply with { Effect = spec.Effect }, path, root, dryRun: false, allowRisky: false, allowDestructive: [], resume: false, allowDirty, output, error, env);
    }
}
