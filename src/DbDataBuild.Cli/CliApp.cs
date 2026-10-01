using System.CommandLine;
using DbDataBuild.Core;
using DbDataBuild.Models;
using DbDataBuild.Sql.Matrix;
using DbDataBuild.Targets.Rendering;

namespace DbDataBuild.Cli;

public static class CliApp
{
    public const int ExitOk = 0, ExitFindings = 1, ExitUsage = 2, ExitNotImplemented = 3, ExitInternal = 70;

    /// <param name="input">Where interactive answers are read from. Only used when <paramref name="interactive"/> is true.</param>
    /// <param name="interactive">Whether a person is there to answer questions (a terminal). Commands that ask refuse to run without one unless they are given everything.</param>
    public static int Run(string[] args, TextWriter output, TextWriter error, TextReader? input = null, bool interactive = false) =>
        Guarded(args, error, () => Build(output, error, input ?? TextReader.Null, interactive).Parse(args).Invoke(new InvocationConfiguration { Output = output, Error = error }));

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

    public static RootCommand Build(TextWriter output, TextWriter error, TextReader input, bool interactive)
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
                case "define":
                    var paths = new Argument<string[]>("paths") { Description = "Model .sql or .yml files, or directories under models/ (default: every model)", Arity = ArgumentArity.ZeroOrMore };
                    var defineProject = new Option<DirectoryInfo>("--project") { Description = "Project root (contains models/)", DefaultValueFactory = _ => new DirectoryInfo(".") };
                    var answers = new Option<FileInfo?>("--answers") { Description = "Answers file for the questions (see schemas/answers.schema.json)" };
                    var write = new Option<bool>("--write") { Description = "Non-interactive: write the definitions without asking (needs --answers for any open questions)" };
                    var check = new Option<bool>("--check") { Description = "CI: fail if any definition is out of sync with its query; asks nothing, writes nothing" };
                    var accept = new Option<bool>("--accept-inferred") { Description = "Accept inferred proposals marked high certainty (names from paths, types from DuckDB, nullability from lineage)" };
                    cmd.Arguments.Add(paths);
                    cmd.Options.Add(defineProject); cmd.Options.Add(answers); cmd.Options.Add(write); cmd.Options.Add(check); cmd.Options.Add(accept);
                    cmd.SetAction(pr => DefineCommand.Run(spec, pr.GetValue(defineProject)!.FullName, pr.GetValue(paths) ?? [], pr.GetValue(answers), pr.GetValue(write), pr.GetValue(check), pr.GetValue(accept), output, error, input, interactive));
                    break;
                case "render":
                    var renderModels = new Argument<string[]>("models") { Description = "Model names (marts.fct_orders), model files, or directories (default: every model)", Arity = ArgumentArity.ZeroOrMore };
                    var renderProject = new Option<DirectoryInfo>("--project") { Description = "Project root (contains models/)", DefaultValueFactory = _ => new DirectoryInfo(".") };
                    var renderTarget = new Option<string[]>("--target") { Description = "Only these targets (sqlserver, fabric, postgres)", AllowMultipleArgumentsPerToken = false, DefaultValueFactory = _ => [] };
                    var renderWrite = new Option<bool>("--write") { Description = "Write the committed rendered/ files (and remove stale generated ones)" };
                    var renderCheck = new Option<bool>("--check") { Description = "CI: fail if the committed rendered/ files differ from a fresh render; writes nothing" };
                    cmd.Arguments.Add(renderModels);
                    cmd.Options.Add(renderProject); cmd.Options.Add(renderTarget); cmd.Options.Add(renderWrite); cmd.Options.Add(renderCheck);
                    cmd.SetAction(pr => RenderCommand.Render(spec, pr.GetValue(renderProject)!.FullName, pr.GetValue(renderModels) ?? [], pr.GetValue(renderTarget) ?? [], pr.GetValue(renderWrite), pr.GetValue(renderCheck), output, error));
                    break;
                case "loads":
                    var loadsProject = new Option<DirectoryInfo>("--project") { Description = "Project root (contains models/)", DefaultValueFactory = _ => new DirectoryInfo(".") };
                    cmd.Options.Add(loadsProject);
                    cmd.SetAction(pr => RenderCommand.Loads(spec, pr.GetValue(loadsProject)!.FullName, output, error));
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
        var matrix = MatrixLoader.LoadEmbedded(matrixDiags);
        if (matrixDiags.Count > 0) throw new InvalidOperationException("The embedded support matrix is invalid: " + string.Join("; ", matrixDiags.Select(d => d.Found)));
        var linter = new MatrixLinter(matrix);
        var renderer = new LoadRenderer(matrix, linter, config);

        // The effective settings are never hidden (DESIGN.md 7.4): printed even when they are the built-in defaults.
        var configured = File.Exists(Path.Combine(projectRoot, ProductInfo.ConfigFile)) && !diagnostics.Any(d => d.Severity == Severity.Error && d.Location.File == ProductInfo.ConfigFile);
        output.WriteLine($"Config: {(configured ? ProductInfo.ConfigFile : "built-in defaults")}");
        output.WriteLine($"Effective: {config.Describe()}");
        foreach (var source in result.Sources)
        {
            var sql = File.ReadAllText(Path.Combine(projectRoot, source.QueryFile));
            var targets = source.Definition.Targets ?? config.DefaultTargets;
            diagnostics.AddRange(linter.Lint(sql, source.QueryFile, targets, config));
            // every declared model x target x operation pair must render (in memory; nothing is written), and the scripts must pass offline validation
            diagnostics.AddRange(renderer.Render(source.Definition, sql, source.QueryFile, targets).Diagnostics.Where(d => d.Code != DiagnosticCatalog.SqlParseFailure.Code));
        }
        diagnostics.AddRange(CollationChecker.Check(config, result.Sources));

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
