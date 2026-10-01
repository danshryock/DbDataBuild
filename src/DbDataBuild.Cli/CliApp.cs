using System.CommandLine;
using DbDataBuild.Core;
using DbDataBuild.Models;
using DbDataBuild.Sql.Matrix;

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
                case "matrix":
                    cmd.SetAction(_ => PrintMatrix(spec, output, error));
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
        var diagnostics = new List<Diagnostic>(result.Diagnostics);
        var config = ProjectConfigLoader.LoadFromProject(projectRoot, diagnostics);

        var matrixDiags = new List<Diagnostic>();
        var linter = new MatrixLinter(MatrixLoader.LoadEmbedded(matrixDiags));
        if (matrixDiags.Count > 0) throw new InvalidOperationException("The embedded support matrix is invalid: " + string.Join("; ", matrixDiags.Select(d => d.Found)));

        // The effective settings are never hidden (DESIGN.md 7.4): printed even when they are the built-in defaults.
        var configured = File.Exists(Path.Combine(projectRoot, ProductInfo.ConfigFile)) && !diagnostics.Any(d => d.Severity == Severity.Error && d.Location.File == ProductInfo.ConfigFile);
        output.WriteLine($"Config: {(configured ? ProductInfo.ConfigFile : "built-in defaults")}");
        output.WriteLine($"Effective: {config.Describe()}");
        foreach (var source in result.Sources)
        {
            var sql = File.ReadAllText(Path.Combine(projectRoot, source.QueryFile));
            diagnostics.AddRange(linter.Lint(sql, source.QueryFile, source.Definition.Targets ?? config.DefaultTargets, config));
        }

        foreach (var d in diagnostics) error.WriteLine(DiagnosticFormatter.Format(d));
        var errors = diagnostics.Count(d => d.Severity == Severity.Error);
        var warnings = diagnostics.Count(d => d.Severity == Severity.Warning);
        var notes = diagnostics.Count(d => d.Severity == Severity.Note);
        var tail = $"{warnings} warning(s), {notes} note(s).";
        output.WriteLine(errors == 0
            ? $"OK: {result.Sources.Count} model(s) valid. {tail}"
            : $"FAILED: {errors} error(s), {tail}");
        return errors == 0 ? ExitOk : ExitFindings;
    }

    private static int PrintMatrix(CommandSpec spec, TextWriter output, TextWriter error)
    {
        WriteHeader(spec, output);
        var diags = new List<Diagnostic>();
        var matrix = MatrixLoader.LoadEmbedded(diags);
        if (diags.Count > 0)
        {
            foreach (var d in diags) error.WriteLine(DiagnosticFormatter.Format(d));
            return ExitFindings;
        }
        output.WriteLine($"{"construct",-26} {"sqlserver",-13} {"fabric",-13} {"postgres",-13}");
        foreach (var row in matrix.Rows)
        {
            string Cell(string t) => row.Targets[t].Status.ToString().ToLowerInvariant() + (row.Targets[t].MinVersion is { } v ? $" (>= {v})" : "");
            output.WriteLine($"{row.Id,-26} {Cell("sqlserver"),-13} {Cell("fabric"),-13} {Cell("postgres"),-13}");
            foreach (var t in SupportMatrix.Targets.Where(t => row.Targets[t].Note is { Length: > 0 }))
                output.WriteLine($"    {t}: {row.Targets[t].Note}");
        }
        output.WriteLine($"\n{matrix.Rows.Count} construct row(s); {matrix.Covered.Count} verified node/function/type entries (anything else is reported as DDB-305).");
        return ExitOk;
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
