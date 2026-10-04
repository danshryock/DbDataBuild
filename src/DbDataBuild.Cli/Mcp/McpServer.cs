using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataBuild.Core;
using DbDataBuild.Tui.Model;

namespace DbDataBuild.Cli.Mcp;

/// <summary>
/// The Model Context Protocol over standard input and output (newline-delimited JSON-RPC 2.0), for an agent that works in a project without a shell. It is a client of the same JSON surface as the
/// terminal interface: every tool runs one command in process with `--format json` and returns its document. It holds no logic and no database access of its own (docs/research/web-and-mcp-interface.md).
/// What the model is not given: commands that change a target or its tracking tables (unless the person who configured the server said `--allow-writes`), the project option (the server has one root),
/// and the options that put row values in front of a reader (`diff --show-values`, `sample --data`): an agent works without data, an operator may look (DESIGN.md, principle 8).
/// </summary>
internal sealed class McpServer
{
    internal const string LatestProtocol = "2025-06-18";
    private static readonly string[] SupportedProtocols = [LatestProtocol, "2025-03-26", "2024-11-05"];

    /// <summary>The longest document returned in a tool result; a larger one is cut and the model is told how to ask for less.</summary>
    internal const int MaxDocumentCharacters = 120_000;

    private readonly string projectRoot;
    private readonly bool allowWrites;
    private readonly TextReader input;
    private readonly TextWriter output;
    private readonly ICommandHost host;
    private readonly ToolSurface surface;
    private IReadOnlyDictionary<string, CommandInfo> tools => surface.Tools;
    private string protocol = LatestProtocol;

    public McpServer(string projectRoot, bool allowWrites, TextReader input, TextWriter output, ICommandHost host)
    {
        this.projectRoot = Path.GetFullPath(projectRoot);
        this.allowWrites = allowWrites;
        this.input = input;
        this.output = output;
        this.host = host;
        surface = new ToolSurface(projectRoot, host.Commands.Where(c => c.Name is not ("tui" or "mcp" or "web") && (allowWrites || c.Impact is Impact.None or Impact.RepoFiles)));
    }

    public IReadOnlyCollection<string> ToolNames => tools.Keys.ToList();

    /// <summary>Reads requests until the input ends. A line that is not JSON gets a parse error; the server never throws out of the loop.</summary>
    public void Serve()
    {
        while (input.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonNode? message;
            try { message = JsonNode.Parse(line); }
            catch (JsonException) { Send(Error(null, -32700, "Parse error")); continue; }
            foreach (var reply in Handle(message)) Send(reply);
        }
    }

    private void Send(JsonNode message) => output.WriteLine(message.ToJsonString());

    /// <summary>The reply to one message (none for a notification). Exposed for tests.</summary>
    public IEnumerable<JsonNode> Handle(JsonNode? message)
    {
        if (message is JsonArray) return [Error(null, -32600, "Batches are not supported")];
        if (message is not JsonObject request || request["method"] is not JsonValue methodValue || !methodValue.TryGetValue<string>(out var method)) return [Error((message as JsonObject)?["id"]?.DeepClone(), -32600, "Invalid request")];
        var id = request["id"]?.DeepClone();
        if (id == null) return [];                                    // a notification (initialized, cancelled): nothing to answer
        try
        {
            var result = method switch
            {
                "initialize" => Initialize(request["params"] as JsonObject),
                "ping" => new JsonObject(),
                "tools/list" => new JsonObject { ["tools"] = new JsonArray(tools.Values.OrderBy(t => t.Name, StringComparer.Ordinal).Select(surface.Describe).ToArray()) },
                "tools/call" => CallTool(request["params"] as JsonObject),
                "resources/list" => new JsonObject { ["resources"] = new JsonArray(Resources.Listed().ToArray()) },
                "resources/templates/list" => new JsonObject { ["resourceTemplates"] = new JsonArray(Resources.Templates().ToArray()) },
                "resources/read" => Resources.Read(request["params"]?["uri"]?.GetValue<string>()),
                "prompts/list" => new JsonObject { ["prompts"] = new JsonArray(Prompts.Listed().ToArray()) },
                "prompts/get" => Prompts.Get(request["params"]?["name"]?.GetValue<string>(), request["params"]?["arguments"] as JsonObject),
                _ => throw new McpException(-32601, $"Method not found: {method}"),
            };
            return [new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }];
        }
        catch (McpException ex) { return [Error(id, ex.Code, ex.Message)]; }
    }

    private static JsonObject Error(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    private JsonObject Initialize(JsonObject? p)
    {
        var asked = p?["protocolVersion"]?.GetValue<string>();
        protocol = asked != null && SupportedProtocols.Contains(asked) ? asked : LatestProtocol;
        return new JsonObject
        {
            ["protocolVersion"] = protocol,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false }, ["resources"] = new JsonObject { ["listChanged"] = false, ["subscribe"] = false }, ["prompts"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = "dbdatabuild", ["title"] = ProductInfo.Name, ["version"] = ProductInfo.Version },
            ["instructions"] = "Tools are the dbdatabuild commands, run on the project this server was started for. Read the resource dbdatabuild://skill first: it says how to work in a project. " +
                "Every tool returns the command's JSON document. Row values are never returned to you; commands that change a target are " + (allowWrites ? "available (the operator allowed them)." : "not offered: a person runs those."),
        };
    }

    private JsonObject CallTool(JsonObject? p)
    {
        var name = p?["name"]?.GetValue<string>() ?? throw new McpException(-32602, "tools/call needs a tool name");
        if (!tools.TryGetValue(name, out var command)) throw new McpException(-32602, $"Unknown tool: {name}");
        var arguments = p?["arguments"] as JsonObject ?? new JsonObject();
        if (!surface.TryBuildArguments(command, arguments, out var argv, out var problem)) return Failure(problem!);

        var progressToken = p?["_meta"]?["progressToken"]?.DeepClone();
        var steps = 0;
        var hooks = progressToken == null ? null : new RunHooks(Progress: message =>
            Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/progress", ["params"] = new JsonObject { ["progressToken"] = progressToken.DeepClone(), ["progress"] = ++steps, ["message"] = message } }));
        var result = host.Run(argv, hooks);

        // exit 1 means findings: the document is the answer. Only a usage error or a crash is an error of the call.
        var failed = result.Exit >= CliApp.ExitUsage;
        var text = result.Out.Length > 0 ? result.Out.TrimEnd() : result.Err.Trim();
        JsonNode? document = null;
        if (text.Length <= MaxDocumentCharacters) { try { document = JsonNode.Parse(text) as JsonObject; } catch (JsonException) { } }
        else text = text[..MaxDocumentCharacters] + $"\n... cut: the document is {result.Out.Length:N0} characters. Ask for less (name models or a selector, a limit, or a narrower command).";
        var reply = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }), ["isError"] = failed };
        if (document != null && protocol == LatestProtocol) reply["structuredContent"] = document;
        return reply;
    }

    private static JsonObject Failure(string message) =>
        new() { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = message }), ["isError"] = true };
}

internal sealed class McpException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}
