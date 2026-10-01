using DbDataBuild.Core;
using DbDataBuild.Execution;
using DbDataBuild.Models;
using DbDataBuild.State;

namespace DbDataBuild.Cli;

/// <summary>
/// `dbdatabuild init` (DESIGN.md 9.1, 12). Effect class: tracking tables only. By default it prints the idempotent init script for review and connects to
/// nothing; `--apply` runs exactly that script on the write login, through the mutation gate, so every statement is in the statement log.
/// </summary>
internal static class InitCommand
{
    public const string StatementLogDir = ".dbdatabuild/statement-log";

    public static int Run(CommandSpec spec, string projectRoot, string? targetArg, bool apply, TextWriter output, TextWriter error, Func<string, string?> environment)
    {
        var diags = new List<Diagnostic>();
        var config = ProjectConfigLoader.LoadFromProject(projectRoot, diags);
        foreach (var d in diags.Where(d => d.Severity == Severity.Error)) error.WriteLine(DiagnosticFormatter.Format(d));
        if (diags.Any(d => d.Severity == Severity.Error)) return CliApp.ExitFindings;

        var target = targetArg ?? (config.DefaultTargets.Count == 1 ? config.DefaultTargets[0] : null);
        if (target == null)
        {
            error.WriteLine($"--target is required: the project has {config.DefaultTargets.Count} default targets ({string.Join(", ", config.DefaultTargets)}).");
            return CliApp.ExitUsage;
        }
        if (!TargetNames.All.Contains(target))
        {
            error.WriteLine($"Unknown target `{target}`. One of: {string.Join(", ", TargetNames.All)}.");
            return CliApp.ExitUsage;
        }

        LoginSettings? write = null;
        if (apply)
        {
            var (settings, missing) = LoginSettings.FromEnvironment(target, Login.Write, environment);
            if (missing != null)
            {
                output.WriteLine($"{ProductInfo.Cli} {spec.Name}  |  effect: {spec.Effect.Describe()}  |  target: {target}  |  login: none");
                error.Write(DiagnosticFormatter.Format(missing));
                return CliApp.ExitFindings;
            }
            write = settings;
        }

        var ddl = TrackingDdl.For(target);
        var script = ddl.InitScript(config.TrackingSchema, ProductInfo.Version);
        output.WriteLine($"{ProductInfo.Cli} {spec.Name}  |  effect: {spec.Effect.Describe()}  |  target: {target}  |  login: {(write?.Describe() ?? "none (not applying)")}");
        output.WriteLine($"Tracking schema: {config.TrackingSchema}. Statements: {script.Count}. The script only creates what is missing; it never alters or drops.");
        if (ddl.Unverified) output.WriteLine($"note: this script has not been run on {target} (no engine was available to verify it).");

        if (!apply)
        {
            output.WriteLine();
            output.Write(TrackingDdl.Render(script));
            output.WriteLine($"Review the script above, then run `{ProductInfo.Cli} {spec.Name} --target {target} --apply` with the write login configured.");
            return CliApp.ExitOk;
        }

        var runId = Guid.NewGuid();
        try
        {
            using var log = new FileStatementLog(Path.Combine(projectRoot, StatementLogDir), spec.Name, runId);
            output.WriteLine($"Statement log: {Path.GetRelativePath(projectRoot, log.Path)}");
            var gate = Task.Run(() => MutationGate.OpenAsync(write!, spec.Name, StatementKind.Tracking, log, runId)).GetAwaiter().GetResult();
            try { Task.Run(() => TrackingStore.InitAsync(gate, target, config.TrackingSchema)).GetAwaiter().GetResult(); }
            finally { gate.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }
        catch (GateRefusedException ex)
        {
            error.Write(DiagnosticFormatter.Format(ex.Diagnostic));
            return CliApp.ExitFindings;
        }
        output.WriteLine($"Applied {script.Count} statements. The tracking tables are ready.");
        return CliApp.ExitOk;
    }
}
