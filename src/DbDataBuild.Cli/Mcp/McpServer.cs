using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataBuild.Core;
using DbDataBuild.Planning;
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

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> cancelled = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> awaiting = new();
    private int askCounter;
    private bool clientCanAsk;

    /// <summary>How long a person has to answer a question the server puts to them through the host.</summary>
    internal TimeSpan AnswerTimeout { get; set; } = TimeSpan.FromMinutes(10);
    private string? currentRequest;

    /// <summary>
    /// Reads requests until the input ends and answers them one at a time. A second thread keeps reading while a command runs, so that `notifications/cancelled` for the running request reaches it
    /// (the command stops between its steps, as it does for the terminal interface) and its reply is not sent, as the protocol says. A line that is not JSON gets a parse error; the loop never throws.
    /// </summary>
    public void Serve()
    {
        using var queue = new System.Collections.Concurrent.BlockingCollection<JsonNode?>();
        var reader = new Thread(() =>
        {
            try
            {
                while (input.ReadLine() is { } line)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    JsonNode? message;
                    try { message = JsonNode.Parse(line); }
                    catch (JsonException) { message = null; }
                    // the client's answer to a question the server asked (elicitation): handed to the call that is waiting for it
                    if (message is JsonObject { } reply && reply["method"] == null && reply["id"] is JsonValue replyId && replyId.TryGetValue<string>(out var askedId) && awaiting.TryRemove(askedId, out var waiting))
                    { waiting.TrySetResult(reply); continue; }
                    if (message is JsonObject { } n && n["id"] == null && (string?)n["method"] == "notifications/cancelled" && n["params"]?["requestId"] is { } target) { cancelled[target.ToJsonString()] = true; continue; }
                    queue.Add(message);
                }
            }
            finally { queue.CompleteAdding(); }
        }) { IsBackground = true, Name = "mcp-input" };
        reader.Start();
        foreach (var message in queue.GetConsumingEnumerable())
        {
            if (message == null) { Send(Error(null, -32700, "Parse error")); continue; }
            foreach (var reply in Handle(message))
                if (!(reply["id"] is { } id && cancelled.ContainsKey(id.ToJsonString()))) Send(reply);
        }
    }

    private readonly object writing = new();
    private void Send(JsonNode message) { lock (writing) output.WriteLine(message.ToJsonString()); }

    /// <summary>The reply to one message (none for a notification). Exposed for tests.</summary>
    public IEnumerable<JsonNode> Handle(JsonNode? message)
    {
        if (message is JsonArray) return [Error(null, -32600, "Batches are not supported")];
        if (message is not JsonObject request || request["method"] is not JsonValue methodValue || !methodValue.TryGetValue<string>(out var method)) return [Error((message as JsonObject)?["id"]?.DeepClone(), -32600, "Invalid request")];
        var id = request["id"]?.DeepClone();
        if (id == null) return [];                                    // a notification (initialized, cancelled): nothing to answer
        currentRequest = id.ToJsonString();
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
        clientCanAsk = p?["capabilities"]?["elicitation"] != null;
        return new JsonObject
        {
            ["protocolVersion"] = protocol,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false }, ["resources"] = new JsonObject { ["listChanged"] = false, ["subscribe"] = false }, ["prompts"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = "dbdatabuild", ["title"] = ProductInfo.Name, ["version"] = ProductInfo.Version },
            ["instructions"] = "Tools are the dbdatabuild commands, run on the project this server was started for. Read the resource dbdatabuild://skill first: it says how to work in a project. " +
                "Every tool returns the command's JSON document. Row values are never returned to you; commands that change a target are " + (allowWrites ? "offered, and each run of one needs the person's confirmation through the host (you cannot give it)." : "not offered: a person runs those."),
        };
    }

    private JsonObject CallTool(JsonObject? p)
    {
        var name = p?["name"]?.GetValue<string>() ?? throw new McpException(-32602, "tools/call needs a tool name");
        if (!tools.TryGetValue(name, out var command)) throw new McpException(-32602, $"Unknown tool: {name}");
        var arguments = p?["arguments"] as JsonObject ?? new JsonObject();
        if (!surface.TryBuildArguments(command, arguments, out var argv, out var problem)) return Failure(problem!);

        if (ChangesSomething(command, argv) && Approval(command, argv) is { } refused) return Failure(refused);

        var progressToken = p?["_meta"]?["progressToken"]?.DeepClone();
        var steps = 0;
        var running = currentRequest;
        var hooks = new RunHooks(
            Progress: progressToken == null ? null : message =>
                Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/progress", ["params"] = new JsonObject { ["progressToken"] = progressToken.DeepClone(), ["progress"] = ++steps, ["message"] = message } }),
            StopRequested: () => running != null && cancelled.ContainsKey(running));
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

    /// <summary>A command that changes a target or its tracking tables, as this call asks for it: those with a flag that decides (`init --apply`, `apply` unless `--dry-run`) by the flag, the others always.</summary>
    internal static bool ChangesSomething(CommandInfo command, IReadOnlyList<string> argv)
    {
        if (command.Impact is Impact.None or Impact.RepoFiles) return false;
        var flag = command.Name == "load-seeds" ? "--apply" : command.WriteFlag;
        if (flag == null) return true;
        return flag.StartsWith('!') ? !argv.Contains(flag[1..]) : argv.Contains(flag);
    }

    /// <summary>
    /// A command that changes a database is run only after the person says so, through the host (MCP elicitation), not the model: the question shows the exact command and, for apply, what the plan does;
    /// the answer must approve and type the command's name back. A host that cannot ask gets a refusal that says what to run by hand. Returns the refusal, or null when approved.
    /// </summary>
    private string? Approval(CommandInfo command, IReadOnlyList<string> argv)
    {
        var shown = $"dbdatabuild {string.Join(' ', argv.Where((a, i) => a != "--project" && (i == 0 || argv[i - 1] != "--project")).Select(a => a.StartsWith(projectRoot, StringComparison.Ordinal) ? Path.GetRelativePath(projectRoot, a).Replace('\\', '/') : a))}";
        if (!clientCanAsk) return $"`{command.Name}` changes a database, and this host cannot ask you to confirm it, so it is not run on an agent's say-so. Run it yourself: {shown}";
        var summary = command.Name == "apply" ? PlanSummary(argv) : "";
        var id = "ddb-elicit-" + Interlocked.Increment(ref askCounter);
        var answer = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        awaiting[id] = answer;
        Send(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = "elicitation/create",
            ["params"] = new JsonObject
            {
                ["message"] = $"An agent asks to run a command that changes a database ({command.Effect}):\n\n    {shown}\n{summary}\nApprove only if you have read the plan and mean it.",
                ["requestedSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["approve"] = new JsonObject { ["type"] = "boolean", ["title"] = "Approve", ["description"] = "Run exactly this command." },
                        ["type_to_confirm"] = new JsonObject { ["type"] = "string", ["title"] = $"Type {command.Name} to confirm" },
                    },
                    ["required"] = new JsonArray("approve", "type_to_confirm"),
                },
            },
        });
        try
        {
            if (!answer.Task.Wait(AnswerTimeout)) return $"No answer to the confirmation in {AnswerTimeout.TotalMinutes:0} minutes. Nothing was run.";
        }
        finally { awaiting.TryRemove(id, out _); }
        var result = answer.Task.Result["result"] as JsonObject;
        var content = result?["content"] as JsonObject;
        var approved = (string?)result?["action"] == "accept" && content?["approve"] is JsonValue a && a.TryGetValue<bool>(out var yes) && yes
            && content["type_to_confirm"] is JsonValue t && t.TryGetValue<string>(out var typed) && typed.Trim() == command.Name;
        return approved ? null : "The person did not approve this command. Nothing was run. Do not ask again unless they ask you to.";
    }

    private string PlanSummary(IReadOnlyList<string> argv)
    {
        var plan = argv.Skip(1).FirstOrDefault(a => a.EndsWith(".plan.yml", StringComparison.Ordinal));
        if (plan == null) return "";
        var (browser, _) = PlanBrowser.Load(plan);
        return browser == null ? "\nThe plan does not parse or was edited: apply will refuse it.\n"
            : $"\nPlan {browser.Plan.Id} on {browser.Plan.Target}: {browser.Plan.Steps.Count} steps, {browser.Risky} risky, {browser.Destructive} destructive" +
              (browser.DestructiveObjects.Count > 0 ? $" (on {string.Join(", ", browser.DestructiveObjects)})" : "") + ".\n" +
              string.Join("\n", browser.Plan.Steps.Take(12).Select(s => $"  {s.Id}. [{s.Risk.ToString().ToLowerInvariant()}] {s.Description}")) + (browser.Plan.Steps.Count > 12 ? $"\n  … and {browser.Plan.Steps.Count - 12} more" : "") + "\n";
    }

    private static JsonObject Failure(string message) =>
        new() { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = message }), ["isError"] = true };
}

internal sealed class McpException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}
