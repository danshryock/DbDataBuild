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
    internal static string[] ReadOnlyCommands => WebBackend.ReadOnlyCommands;
    private const int MaxBodyBytes = 1 << 20;

    private readonly string projectRoot;
    private HttpListener listener = new();
    private readonly WebBackend backend;
    private readonly string page;
    private CancellationTokenSource? stop;

    public string Token { get; }
    public int Port { get; private set; }
    private readonly bool portWasChosenHere;
    public string Address => $"http://127.0.0.1:{Port}/?token={Token}";

    public WebServer(string projectRoot, ICommandHost host, int port = 0, bool allowApply = false)
    {
        this.projectRoot = Path.GetFullPath(projectRoot);
        backend = new WebBackend(this.projectRoot, host, allowApply);
        Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        portWasChosenHere = port == 0;
        Port = port != 0 ? port : FreePort();
        page = PageHtml("http", Token, this.projectRoot);
        listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
        finally { probe.Stop(); }
    }

    /// <summary>The page, for one of its two carriers: `http` (this server, with its token) or `mcp` (an MCP app: no token, the host is the transport).</summary>
    internal static string PageHtml(string transport, string token, string projectRoot) =>
        ReadPage().Replace("__TRANSPORT__", transport).Replace("__TOKEN__", token).Replace("__PROJECT__", WebUtility.HtmlEncode(Path.GetFileName(projectRoot.TrimEnd('/', '\\'))));

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
        // a free port found a moment ago can be taken by the time it is bound (another process, or another server of this one): choose again
        for (var attempt = 0; ; attempt++)
        {
            try { listener.Start(); break; }
            catch (HttpListenerException) when (portWasChosenHere && attempt < 10)
            {
                Port = FreePort();
                // a listener that failed to start cannot even remove its prefix again (it throws the same error): start over with a new one
                try { listener.Close(); } catch (HttpListenerException) { }
                listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            }
        }
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

    public void Dispose() { stop?.Cancel(); try { listener.Close(); } catch (Exception ex) when (ex is ObjectDisposedException or HttpListenerException) { } }

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
        if (request.HttpMethod == "POST" && path == "/api/run") return WithJsonBody(request, backend.RunCommand);
        if (request.HttpMethod == "GET" && path == "/api/capabilities") return backend.Capabilities();
        if (request.HttpMethod == "POST" && path == "/api/answers") return WithJsonBody(request, backend.SaveAnswers);
        if (request.HttpMethod == "POST" && path == "/api/apply") return WithJsonBody(request, backend.StartApply);
        if (request.HttpMethod == "GET" && path == "/api/job") return backend.JobStatus(request.QueryString["id"], int.TryParse(request.QueryString["from"], out var from) ? from : 0);
        if (request.HttpMethod == "POST" && path == "/api/job/stop") return backend.StopJob(request.QueryString["id"]);
        if (request.HttpMethod == "GET" && path == "/api/file") return backend.ReadFile(request.QueryString["path"]);
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

    internal (int, string, string) ReadFile(string? relative) => backend.ReadFile(relative);

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
