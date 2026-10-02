using System.Collections.ObjectModel;
using DbDataBuild.Tui.Model;
using DbDataBuild.Tui.Views;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace DbDataBuild.Tui;

public sealed record TuiOptions(string ProjectRoot, string? Target);

/// <summary>
/// The terminal user interface (DESIGN.md 9.6). It owns no logic: every action is a command run through <see cref="ICommandHost"/> with `--format json`, and what it shows is that
/// document, so it can do exactly what the CLI can and nothing the CLI could not. Forms are generated from the command definitions, so a new option appears here by itself.
/// </summary>
public static class TuiApp
{
    public static int Run(ICommandHost host, TuiOptions options)
    {
        using var app = Application.Create();
        app.Init();
        var session = new TuiSession(app, host, options);
        using var main = new MainWindow(session);
        app.Run(main);
        return 0;
    }
}

/// <summary>What the views share: the application, the host that runs commands, and the project and target being worked on.</summary>
public sealed class TuiSession(IApplication app, ICommandHost host, TuiOptions options)
{
    public IApplication App { get; } = app;
    public ICommandHost Host { get; } = host;
    public string ProjectRoot { get; set; } = options.ProjectRoot;
    public string? Target { get; set; } = options.Target;
    public ResultModel? LastResult { get; set; }
    public Action<string>? Status { get; set; }

    /// <summary>
    /// The target to work on. A project with one default target needs no question; with several, the person chooses once and the choice is kept for the session.
    /// The commands would refuse to guess (`--target is required`), so the TUI asks before they have to.
    /// </summary>
    public string? EnsureTarget()
    {
        if (Target != null) return Target;
        var config = DbDataBuild.Models.ProjectConfigLoader.LoadFromProject(ProjectRoot, new List<DbDataBuild.Core.Diagnostic>());
        if (config.DefaultTargets.Count == 1) return Target = config.DefaultTargets[0];
        if (config.DefaultTargets.Count == 0) return null;
        var choice = MessageBox.Query(App, "Which target?", "This project builds for several targets. Work on:", [.. config.DefaultTargets]);
        if (choice is >= 0 and var i) Target = config.DefaultTargets[i];
        return Target;
    }

    /// <summary>Runs a command and returns what it printed as a document. The caller decides how to show it.</summary>
    public ResultModel Run(IReadOnlyList<string> args)
    {
        Status?.Invoke($"running: dbdatabuild {string.Join(' ', args.Take(1))} …");
        App.LayoutAndDraw(true);
        var result = ResultModel.From(args[0], Host.Run(args));
        LastResult = result;
        Status?.Invoke(result.Headline());
        return result;
    }
}
