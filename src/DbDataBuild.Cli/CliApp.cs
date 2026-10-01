using System.CommandLine;
using DbDataBuild.Core;
using DbDataBuild.Models;

namespace DbDataBuild.Cli;

public static class CliApp
{
    public const int ExitOk = 0, ExitFindings = 1, ExitUsage = 2, ExitNotImplemented = 3, ExitInternal = 70;

    public static int Run(string[] args, TextWriter output, TextWriter error) =>
        Guarded(args, error, () => Build(output, error).Parse(args).Invoke(new InvocationConfiguration { Output = output, Error = error }));

    /// <summary>Top-level guard: unhandled exceptions become an internal-error diagnostic, never a stack trace.</summary>
    public static int Guarded(string[] args, TextWriter error, Func<int> body)
    {
        try
        {
            return body();
        }
        catch (Exception ex)
        {
            // Never a stack trace as primary output (DESIGN.md 14.2). Full detail would go to a scrubbed log file.
            error.Write(DiagnosticFormatter.Format(new Diagnostic(DiagnosticCatalog.InternalError, new("<internal>", 0, 0),
                $"The tool failed with {ex.GetType().Name} while running `{string.Join(' ', args)}`. No target statements ran (none are issued by this command surface yet).")));
            return ExitInternal;
        }
    }

    public static RootCommand Build(TextWriter output, TextWriter error)
    {
        var root = new RootCommand($"{ProductInfo.Name}: explicit SQL transformation tool. Every command declares an effect class.");

        foreach (var spec in CommandSpecs.All)
        {
            var cmd = new Command(spec.Name, $"[{spec.Effect.Describe()}] {spec.Purpose}");
            switch (spec.Name)
            {
                case "validate":
                    var project = new Option<DirectoryInfo>("--project") { Description = "Project root (contains models/)", DefaultValueFactory = _ => new DirectoryInfo(".") };
                    cmd.Options.Add(project);
                    cmd.SetAction(pr => Validate(spec, pr.GetValue(project)!.FullName, output, error));
                    break;
                case "explain":
                    var code = new Argument<string>("code") { Description = "Diagnostic code, e.g. DDB-214" };
                    cmd.Arguments.Add(code);
                    cmd.SetAction(pr => Explain(spec, pr.GetValue(code)!, output, error));
                    break;
                default:
                    cmd.SetAction(_ =>
                    {
                        WriteHeader(spec, output);
                        error.WriteLine($"`{ProductInfo.Cli} {spec.Name}` is not implemented yet. Nothing was run.");
                        return ExitNotImplemented;
                    });
                    break;
            }
            root.Subcommands.Add(cmd);
        }
        return root;
    }

    private static void WriteHeader(CommandSpec spec, TextWriter output) =>
        output.WriteLine($"{ProductInfo.Cli} {spec.Name}  |  effect: {spec.Effect.Describe()}  |  target: none");

    private static int Validate(CommandSpec spec, string projectRoot, TextWriter output, TextWriter error)
    {
        WriteHeader(spec, output);
        var result = ProjectValidator.Validate(projectRoot);
        foreach (var d in result.Diagnostics) error.WriteLine(DiagnosticFormatter.Format(d));
        var errors = result.Diagnostics.Count(d => d.Severity == Severity.Error);
        output.WriteLine(errors == 0
            ? $"OK: {result.Models.Count} model(s) valid."
            : $"FAILED: {errors} error(s).");
        return errors == 0 ? ExitOk : ExitFindings;
    }

    private static int Explain(CommandSpec spec, string code, TextWriter output, TextWriter error)
    {
        var d = DiagnosticCatalog.Find(code);
        if (d == null)
        {
            error.WriteLine($"Unknown diagnostic code `{code}`. Known codes: {string.Join(", ", DiagnosticCatalog.All.Select(x => x.Code))}.");
            return ExitUsage;
        }
        output.Write(DiagnosticFormatter.Explain(d));
        return ExitOk;
    }
}
