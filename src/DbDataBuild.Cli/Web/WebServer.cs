using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataBuild.Cli.Mcp;
using DbDataBuild.Tui.Model;

namespace DbDataBuild.Cli.Web;

/// <summary>
/// `dbdatabuild web`: a second client of the JSON surface, for a person reading a project (docs/research/web-and-mcp-interface.md). One page, served from the executable, and two endpoints: run a
/// read-only command and get its document, and read a file of the project. It has no logic of its own and no way to a database; the commands it can run only read.
/// It listens on the loopback address only, and a request needs the random token this process printed (in the address, then in a header), the right Host and, when the browser sends one, the right Origin:
/// that is what keeps a web page on another site from calling it (DNS rebinding, cross-site requests). There is no CORS header.
/// </summary>
internal sealed class WebServer : IDisposable
{
    /// <summary>The commands the page may run. All of them only read (render and define lose their write flags); none touches a target.</summary>
    internal static readonly string[] ReadOnlyCommands = ["validate", "graph", "metadata", "test", "loads", "matrix", "explain", "render", "review", "plan"];

    /// <summary>The parts of a project a person may read through the page: what the project is made of, not the plans, the state or the environment.</summary>
    private static readonly string[] ReadableDirectories = ["models", "sources", "seeds", "tests", "rendered", "hooks"];
    private static readonly string[] ReadableExtensions = [".sql", ".yml", ".yaml", ".md", ".json", ".txt", ".csv"];
    private const int MaxBodyBytes = 1 << 20, MaxFileBytes = 2 << 20;

    private readonly string projectRoot;
    private readonly ToolSurface surface;
    private readonly ICommandHost host;
    private readonly HttpListener listener = new();
    private readonly SemaphoreSlim oneCommandAtATime = new(1, 1);
    private readonly WebActions actions;
    private readonly string page;
    private CancellationTokenSource? stop;

    public string Token { get; }
    public int Port { get; }
    public string Address => $"http://127.0.0.1:{Port}/?token={Token}";

    public WebServer(string projectRoot, ICommandHost host, int port = 0, bool allowApply = false)
    {
        this.projectRoot = Path.GetFullPath(projectRoot);
        this.host = host;
        // plan writes plan files in the project and reads the target with the read login; the person answers each question themselves, so `plan --accept-inferred` is not offered
        surface = new ToolSurface(this.projectRoot, host.Commands.Where(c => ReadOnlyCommands.Contains(c.Name)), withholdWriteFlags: true, alsoWithheld: ["plan --accept-inferred"]);
        actions = new WebActions(this.projectRoot, host, oneCommandAtATime, allowApply);
        Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        Port = port != 0 ? port : FreePort();
        page = ReadPage().Replace("__TOKEN__", Token).Replace("__PROJECT__", WebUtility.HtmlEncode(Path.GetFileName(this.projectRoot.TrimEnd('/', '\\'))));
        listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
        finally { probe.Stop(); }
    }

    private static string ReadPage()
    {
        using var s = typeof(WebServer).Assembly.GetManifestResourceStream("web/index.html") ?? throw new InvalidOperationException("The web page is not embedded in the executable. This is a tool bug.");
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd().Replace("\r\n", "\n");
    }

    /// <summary>Starts listening and answers requests in the background until <see cref="Dispose"/> or <paramref name="cancel"/>.</summary>
    public Task StartAsync(CancellationToken cancel = default)
    {
        stop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        listener.Start();
        return Task.Run(async () =>
        {
            using var registration = stop.Token.Register(() => { try { listener.Stop(); } catch (ObjectDisposedException) { } });
            while (!stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync(); }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) { break; }
                _ = Task.Run(() => Answer(context));
            }
        });
    }

    public void Dispose() { stop?.Cancel(); try { listener.Close(); } catch (ObjectDisposedException) { } }

    // ---- requests ---------------------------------------------------------------------------------------------------------------------------------------

    private void Answer(HttpListenerContext context)
    {
        try
        {
            var (status, contentType, body) = Route(context.Request);
            Write(context.Response, status, contentType, body);
        }
        catch (Exception)
        {
            Write(context.Response, 500, "text/plain; charset=utf-8", "The server failed on this request. Nothing was changed.");     // never a stack trace or a message that could carry data
        }
    }

    private (int Status, string ContentType, string Body) Route(HttpListenerRequest request)
    {
        // a request for another host name, even one that resolves to this address, is a rebinding attempt
        var hostHeader = request.Headers["Host"] ?? "";
        if (hostHeader != $"127.0.0.1:{Port}" && hostHeader != $"localhost:{Port}") return Refuse(421, "Wrong host.");
        var origin = request.Headers["Origin"];
        if (origin != null && origin != $"http://127.0.0.1:{Port}" && origin != $"http://localhost:{Port}") return Refuse(403, "Wrong origin.");

        var path = request.Url!.AbsolutePath;
        if (request.HttpMethod == "GET" && path == "/")
            return TokenMatches(request.QueryString["token"]) ? (200, "text/html; charset=utf-8", page) : Refuse(403, "Open the address `dbdatabuild web` printed: it carries the token.");

        if (!TokenMatches(request.Headers["X-DDB-Token"])) return Refuse(403, "Missing or wrong token.");
        if (request.HttpMethod == "POST" && path == "/api/run") return Run(request);
        if (request.HttpMethod == "GET" && path == "/api/capabilities") return (200, "application/json; charset=utf-8", new JsonObject { ["apply"] = actions.AllowApply, ["answers_file"] = WebActions.AnswersFile }.ToJsonString());
        if (request.HttpMethod == "POST" && path == "/api/answers") return WithJsonBody(request, actions.SaveAnswers);
        if (request.HttpMethod == "POST" && path == "/api/apply") return WithJsonBody(request, actions.StartApply);
        if (request.HttpMethod == "GET" && path == "/api/job") return actions.JobStatus(request.QueryString["id"], int.TryParse(request.QueryString["from"], out var from) ? from : 0);
        if (request.HttpMethod == "POST" && path == "/api/job/stop") return actions.StopJob(request.QueryString["id"]);
        if (request.HttpMethod == "GET" && path == "/api/file") return ReadFile(request.QueryString["path"]);
        return Refuse(404, "Not found.");
    }

    private static (int, string, string) Refuse(int status, string message) => (status, "text/plain; charset=utf-8", message);

    private bool TokenMatches(string? given) =>
        given != null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(Token));

    private (int, string, string) WithJsonBody(HttpListenerRequest request, Func<JsonObject?, (int, string, string)> handle)
    {
        if (request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true) return Refuse(415, "Send application/json.");
        if (request.ContentLength64 is < 0 or > MaxBodyBytes) return Refuse(413, "Body too large.");
        using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
        try { return handle(JsonNode.Parse(reader.ReadToEnd()) as JsonObject); }
        catch (JsonException) { return Refuse(400, "Not JSON."); }
    }

    private (int, string, string) Run(HttpListenerRequest request)
    {
        if (request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true) return Refuse(415, "Send application/json.");
        if (request.ContentLength64 is < 0 or > MaxBodyBytes) return Refuse(413, "Body too large.");
        JsonObject? body;
        using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
        {
            try { body = JsonNode.Parse(reader.ReadToEnd()) as JsonObject; }
            catch (JsonException) { return Refuse(400, "Not JSON."); }
        }
        var name = body?["command"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        if (name == null || !surface.Tools.TryGetValue(name, out var command)) return Refuse(404, $"`{name}` is not a command this page can run. It can run: {string.Join(", ", ReadOnlyCommands)}.");
        if (!surface.TryBuildArguments(command, body!["arguments"] as JsonObject ?? new JsonObject(), out var argv, out var problem)) return Refuse(400, problem!);

        CommandResult result;
        oneCommandAtATime.Wait();       // commands share process state (the DuckDB connections, the console encoding): one at a time
        try { result = host.Run(argv); }
        finally { oneCommandAtATime.Release(); }
        JsonNode? document = null;
        try { document = JsonNode.Parse(result.Out); } catch (JsonException) { }
        var reply = new JsonObject { ["exit"] = result.Exit, ["document"] = document, ["text"] = document == null ? (result.Out.Length > 0 ? result.Out : result.Err) : null };
        return (200, "application/json; charset=utf-8", reply.ToJsonString());
    }

    /// <summary>A text file of the project: under models, sources, seeds, tests, rendered or hooks, or the configuration; never through a link that leaves the project.</summary>
    internal (int, string, string) ReadFile(string? relative)
    {
        if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative) || relative.Contains('\0')) return Refuse(400, "A path relative to the project.");
        var parts = relative.Replace('\\', '/').Split('/');
        if (parts.Any(p => p is ".." or "." or "")) return Refuse(400, "A path inside the project.");
        var allowed = relative == "dbdatabuild.yml" || ReadableDirectories.Contains(parts[0]) && parts.Length > 1;
        if (!allowed || !ReadableExtensions.Contains(Path.GetExtension(relative).ToLowerInvariant())) return Refuse(403, "Not a file this page shows.");
        var full = Path.GetFullPath(Path.Combine(projectRoot, relative));
        // no part of the path below the project root may be a link: a link could lead out of it
        var walk = projectRoot;
        foreach (var part in parts)
        {
            walk = Path.Combine(walk, part);
            if (File.Exists(walk) || Directory.Exists(walk))
                if ((File.GetAttributes(walk) & FileAttributes.ReparsePoint) != 0) return Refuse(403, "Not a file this page shows.");
        }
        var info = new FileInfo(full);
        if (!info.Exists) return Refuse(404, "No such file.");
        if (info.Length > MaxFileBytes) return Refuse(413, "File too large to show.");
        return (200, "text/plain; charset=utf-8", File.ReadAllText(full, Encoding.UTF8));
    }

    private static void Write(HttpListenerResponse response, int status, string contentType, string body)
    {
        var bytes = new UTF8Encoding(false).GetBytes(body);
        response.StatusCode = status;
        response.ContentType = contentType;
        response.ContentLength64 = bytes.Length;
        response.Headers["Cache-Control"] = "no-store";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
        // the page is one file with its own script and style; it reaches nothing but this server, and shows file text only as text
        response.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; img-src data:; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
        response.OutputStream.Write(bytes, 0, bytes.Length);
        response.Close();
    }
}
