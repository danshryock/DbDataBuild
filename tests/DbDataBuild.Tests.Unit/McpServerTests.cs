using System.Text.Json.Nodes;
using DbDataBuild.Cli;
using DbDataBuild.Cli.Mcp;

namespace DbDataBuild.Tests.Unit;

/// <summary>The Model Context Protocol server: what it offers a model, what it withholds, and that a tool is the command's own JSON document.</summary>
public class McpServerTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "ddb-mcp-" + Guid.NewGuid().ToString("N")[..8]);

    public McpServerTests()
    {
        Assert.Equal(0, CliApp.Run(["new", "starter", dir, "--format", "json"], new StringWriter(), new StringWriter()));
    }

    public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }

    private McpServer Server(bool allowWrites = false) => new(dir, allowWrites, TextReader.Null, new StringWriter(), new TuiCommand.CliHost(_ => null));

    private static JsonObject Request(string method, JsonObject? p = null, int id = 1) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = p ?? new JsonObject() };

    private static JsonObject Result(McpServer s, string method, JsonObject? p = null) =>
        (JsonObject)s.Handle(Request(method, p)).Single()["result"]!;

    private static JsonObject Call(McpServer s, string tool, JsonObject? arguments = null) =>
        Result(s, "tools/call", new JsonObject { ["name"] = tool, ["arguments"] = arguments ?? new JsonObject() });

    [Fact]
    public void Initialize_negotiates_a_protocol_version_and_names_the_server()
    {
        var r = Result(Server(), "initialize", new JsonObject { ["protocolVersion"] = "2025-03-26" });
        Assert.Equal("2025-03-26", (string)r["protocolVersion"]!);
        Assert.Equal("dbdatabuild", (string)r["serverInfo"]!["name"]!);
        Assert.Equal(McpServer.LatestProtocol, (string)Result(Server(), "initialize", new JsonObject { ["protocolVersion"] = "1999-01-01" })["protocolVersion"]!);
    }

    [Fact]
    public void Commands_that_change_a_target_are_not_offered_unless_the_operator_allowed_them()
    {
        var names = Server().ToolNames;
        foreach (var write in new[] { "apply", "run", "load-seeds", "init", "ack", "publish-metadata" }) Assert.DoesNotContain(write, names);
        foreach (var read in new[] { "validate", "plan", "render", "graph", "metadata", "test", "explain", "diff" }) Assert.Contains(read, names);
        Assert.DoesNotContain("tui", names);
        Assert.DoesNotContain("mcp", names);
        Assert.Contains("apply", Server(allowWrites: true).ToolNames);
    }

    [Fact]
    public void The_model_is_never_offered_a_way_to_see_values_or_to_leave_the_project()
    {
        var tools = Result(Server(), "tools/list")["tools"]!.AsArray();
        foreach (var tool in tools)
        {
            var properties = ((JsonObject)tool!["inputSchema"]!["properties"]!).Select(p => p.Key).ToList();
            Assert.DoesNotContain("project", properties);
            Assert.DoesNotContain("show_values", properties);
            if ((string)tool["name"]! == "sample") Assert.DoesNotContain("data", properties);
        }
        Assert.True((bool)tools.Single(t => (string)t!["name"]! == "validate")!["annotations"]!["readOnlyHint"]!);
        Assert.False((bool)tools.Single(t => (string)t!["name"]! == "render")!["annotations"]!["readOnlyHint"]!);       // it writes with --write
    }

    [Fact]
    public void A_tool_returns_the_commands_own_document_and_a_finding_is_not_an_error_of_the_call()
    {
        var r = Call(Server(), "validate");
        Assert.False((bool)r["isError"]!);
        Assert.Equal("validate", (string)r["structuredContent"]!["command"]!);
        Assert.Equal("validate", (string)JsonNode.Parse((string)r["content"]![0]!["text"]!)!["command"]!);
    }

    [Fact]
    public void A_path_outside_the_project_an_unknown_input_and_a_withheld_option_are_refused_without_running_anything()
    {
        var s = Server();
        foreach (var arguments in new[]
        {
            new JsonObject { ["models"] = new JsonArray("../other") },
            new JsonObject { ["models"] = new JsonArray("/etc/passwd") },
            new JsonObject { ["nonsense"] = true },
            new JsonObject { ["project"] = "/tmp" },
        })
        {
            var r = Call(s, "render", arguments);
            Assert.True((bool)r["isError"]!);
        }
        Assert.True((bool)Call(s, "diff", new JsonObject { ["show_values"] = true })["isError"]!);
        Assert.True((bool)Call(s, "define", new JsonObject { ["answers"] = "../../secrets.yml" })["isError"]!);
    }

    [Fact]
    public void An_unknown_tool_and_an_unknown_method_are_protocol_errors()
    {
        var s = Server();
        Assert.Equal(-32602, (int)s.Handle(Request("tools/call", new JsonObject { ["name"] = "apply" })).Single()["error"]!["code"]!);       // not offered
        Assert.Equal(-32601, (int)s.Handle(Request("no/such")).Single()["error"]!["code"]!);
        Assert.Empty(s.Handle(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }));      // a notification is not answered
    }

    [Fact]
    public void The_skill_the_schemas_and_every_diagnostic_explanation_are_resources()
    {
        var s = Server();
        var listed = Result(s, "resources/list")["resources"]!.AsArray().Select(r => (string)r!["uri"]!).ToList();
        Assert.Contains("dbdatabuild://skill", listed);
        Assert.Contains("dbdatabuild://schemas/output.schema.json", listed);
        Assert.Contains("dbdatabuild", (string)Result(s, "resources/read", new JsonObject { ["uri"] = "dbdatabuild://skill" })["contents"]![0]!["text"]!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DDB-214", (string)Result(s, "resources/read", new JsonObject { ["uri"] = "dbdatabuild://explain/DDB-214" })["contents"]![0]!["text"]!);
        Assert.Equal(-32002, (int)s.Handle(Request("resources/read", new JsonObject { ["uri"] = "dbdatabuild://explain/DDB-0" })).Single()["error"]!["code"]!);
    }

    [Fact]
    public void The_workflows_of_the_skill_are_prompts_that_name_tools_the_server_really_has()
    {
        var s = Server();
        var listed = Result(s, "prompts/list")["prompts"]!.AsArray();
        Assert.Contains("add-model", listed.Select(p => (string)p!["name"]!));
        var text = (string)Result(s, "prompts/get", new JsonObject { ["name"] = "add-model", ["arguments"] = new JsonObject { ["name"] = "marts.fct_x", ["purpose"] = "one row per x" } })["messages"]![0]!["content"]!["text"]!;
        Assert.Contains("marts.fct_x", text);
        Assert.Contains("dbdatabuild://skill", text);
        // every command a prompt tells the model to call exists as a tool here, and none is a command the server withholds
        foreach (var prompt in listed)
        {
            var args = new JsonObject();
            foreach (var a in prompt!["arguments"]!.AsArray()) args[(string)a!["name"]!] = "x.y";
            var body = (string)Result(s, "prompts/get", new JsonObject { ["name"] = (string)prompt["name"]!, ["arguments"] = args })["messages"]![0]!["content"]!["text"]!;
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(body, @"`([a-z-]+)`"))
                if (CommandSpecs.All.Any(c => c.Name == m.Groups[1].Value)) Assert.Contains(m.Groups[1].Value, s.ToolNames);
        }
        Assert.Equal(-32602, (int)s.Handle(Request("prompts/get", new JsonObject { ["name"] = "add-model" })).Single()["error"]!["code"]!);       // a required argument is missing
        Assert.Equal(-32602, (int)s.Handle(Request("prompts/get", new JsonObject { ["name"] = "nope" })).Single()["error"]!["code"]!);
    }

    /// <summary>A command that runs until it is asked to stop.</summary>
    private sealed class SlowHost : DbDataBuild.Tui.Model.ICommandHost
    {
        public readonly ManualResetEventSlim Started = new();
        public bool Stopped;
        public IReadOnlyList<DbDataBuild.Tui.Model.CommandInfo> Commands { get; } = [new("slow", "Takes a while", "Offline only", DbDataBuild.Tui.Model.Impact.None, [], [])];
        public DbDataBuild.Tui.Model.CommandResult Run(IReadOnlyList<string> args, DbDataBuild.Tui.Model.RunHooks? hooks = null)
        {
            Started.Set();
            var until = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < until) { if (hooks?.StopRequested?.Invoke() == true) { Stopped = true; break; } Thread.Sleep(10); }
            return new(0, "{}", "");
        }
    }

    /// <summary>Lines the test hands to the server one at a time, as a client would.</summary>
    private sealed class Feed : TextReader
    {
        private readonly System.Collections.Concurrent.BlockingCollection<string> lines = new();
        public void Send(string line) => lines.Add(line);
        public void End() => lines.CompleteAdding();
        public override string? ReadLine() => lines.TryTake(out var l, Timeout.Infinite) ? l : null;
    }

    [Fact]
    public void A_cancelled_request_stops_its_command_and_gets_no_reply_while_the_server_goes_on()
    {
        var host = new SlowHost();
        var feed = new Feed();
        var output = new StringWriter();
        var server = new McpServer(dir, false, feed, output, host);
        var serving = Task.Run(server.Serve);
        feed.Send(Request("tools/call", new JsonObject { ["name"] = "slow" }, id: 7).ToJsonString());
        Assert.True(host.Started.Wait(TimeSpan.FromSeconds(10)));
        feed.Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/cancelled", ["params"] = new JsonObject { ["requestId"] = 7 } }.ToJsonString());
        feed.Send(Request("ping", id: 8).ToJsonString());
        feed.End();
        Assert.True(serving.Wait(TimeSpan.FromSeconds(15)));
        Assert.True(host.Stopped);
        var replies = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!).ToList();
        Assert.Equal([8], replies.Select(r => (int)r["id"]!));        // nothing for 7, an answer for 8
    }

    /// <summary>A writer the test can read while the server writes to it.</summary>
    private sealed class SyncWriter : StringWriter
    {
        public override void WriteLine(string? value) { lock (this) base.WriteLine(value); }
        public override string ToString() { lock (this) return base.ToString(); }
    }

    private sealed class ApplyHost : DbDataBuild.Tui.Model.ICommandHost
    {
        public readonly List<List<string>> Ran = [];
        public IReadOnlyList<DbDataBuild.Tui.Model.CommandInfo> Commands { get; } =
        [
            new("apply", "Apply a plan", "Target writes", DbDataBuild.Tui.Model.Impact.Target,
                [new("plan", "The plan", false, true, [])],
                [new("--dry-run", "Check only", DbDataBuild.Tui.Model.OptionKind.Flag, null, []), new("--allow-risky", "Allow risky steps", DbDataBuild.Tui.Model.OptionKind.Flag, null, []), new("--project", "Project", DbDataBuild.Tui.Model.OptionKind.Path, null, [])], "!--dry-run"),
            new("validate", "Validate", "Offline only", DbDataBuild.Tui.Model.Impact.None, [], [new("--project", "Project", DbDataBuild.Tui.Model.OptionKind.Path, null, [])]),
        ];
        public DbDataBuild.Tui.Model.CommandResult Run(IReadOnlyList<string> args, DbDataBuild.Tui.Model.RunHooks? hooks = null) { lock (Ran) Ran.Add([.. args]); return new(0, "{\"ok\":true}", ""); }
    }

    private static string WaitForLine(SyncWriter output, string contains)
    {
        for (var i = 0; i < 400; i++)
        {
            var line = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(l => l.Contains(contains));
            if (line != null) return line;
            Thread.Sleep(25);
        }
        throw new TimeoutException($"The server never wrote a line with `{contains}`.");
    }

    /// <summary>Runs a session: initialize (with or without elicitation), then a call of apply; `answer` gets the question the server asks and returns what the person answered (null: nothing asked is expected).</summary>
    private (List<JsonNode> Replies, ApplyHost Host) ApplySession(bool clientAsks, Func<JsonNode, JsonObject>? answer, bool dryRun = false)
    {
        var plan = Path.Combine(dir, "plans", "postgres");
        Directory.CreateDirectory(plan);
        var id = "2026-10-05-eeee0005";
        File.WriteAllText(Path.Combine(plan, id + ".plan.yml"), DbDataBuild.Planning.PlanDocument.Serialize(new DbDataBuild.Planning.Plan(id, "postgres", null, false, "0.1.0",
            [new DbDataBuild.Planning.ObjectBase("marts.fct", DbDataBuild.State.ObjectState.InSync, new string('a', 64), new string('a', 64))], [],
            [new DbDataBuild.Planning.PlanStep("1", DbDataBuild.Planning.StepType.Ddl, "marts.fct", "drop column x", "ALTER TABLE marts.fct DROP COLUMN x;", DbDataBuild.Planning.RiskClass.Destructive, ["col.dropped"], new string('b', 64), [])], [])));
        var host = new ApplyHost(); var feed = new Feed(); var output = new SyncWriter();
        var server = new McpServer(dir, true, feed, output, host) { AnswerTimeout = TimeSpan.FromSeconds(20) };
        var serving = Task.Run(server.Serve);
        feed.Send(Request("initialize", new JsonObject { ["protocolVersion"] = McpServer.LatestProtocol, ["capabilities"] = clientAsks ? new JsonObject { ["elicitation"] = new JsonObject() } : new JsonObject() }, id: 1).ToJsonString());
        var arguments = new JsonObject { ["plan"] = $"plans/postgres/{id}.plan.yml" };
        if (dryRun) arguments["dry_run"] = true;
        feed.Send(Request("tools/call", new JsonObject { ["name"] = "apply", ["arguments"] = arguments }, id: 2).ToJsonString());
        if (answer != null)
        {
            var asked = JsonNode.Parse(WaitForLine(output, "elicitation/create"))!;
            Assert.Contains("drop column x", (string)asked["params"]!["message"]!);          // the person is shown what the plan does
            feed.Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = asked["id"]!.DeepClone(), ["result"] = answer(asked) }.ToJsonString());
        }
        WaitForLine(output, "\"id\":2");
        feed.End();
        Assert.True(serving.Wait(TimeSpan.FromSeconds(15)));
        return (output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!).ToList(), host);
    }

    private static JsonObject Accept(bool approve, string typed) => new() { ["action"] = "accept", ["content"] = new JsonObject { ["approve"] = approve, ["type_to_confirm"] = typed } };

    [Fact]
    public void A_command_that_changes_a_database_runs_only_when_the_person_approves_through_the_host()
    {
        var (replies, host) = ApplySession(clientAsks: true, _ => Accept(true, "apply"));
        Assert.False((bool)replies.Single(r => r["id"]?.ToJsonString() == "2")["result"]!["isError"]!);
        var ran = Assert.Single(host.Ran);
        Assert.Equal("apply", ran[0]);
        Assert.EndsWith("eeee0005.plan.yml", ran[1]);
        Assert.True(Path.IsPathRooted(ran[1]));          // the plan is found from the project, not from wherever the server was started
    }

    [Theory]
    [InlineData("decline")]
    [InlineData("cancel")]
    [InlineData("not approved")]
    [InlineData("wrong text")]
    public void Anything_but_a_clear_approval_runs_nothing(string how)
    {
        var (replies, host) = ApplySession(clientAsks: true, _ => how switch
        {
            "decline" => new JsonObject { ["action"] = "decline" },
            "cancel" => new JsonObject { ["action"] = "cancel" },
            "not approved" => Accept(false, "apply"),
            _ => Accept(true, "yes please"),
        });
        Assert.True((bool)replies.Single(r => r["id"]?.ToJsonString() == "2")["result"]!["isError"]!);
        Assert.Empty(host.Ran);
    }

    [Fact]
    public void A_host_that_cannot_ask_gets_a_refusal_that_says_what_to_run_by_hand()
    {
        var (replies, host) = ApplySession(clientAsks: false, answer: null);
        var result = replies.Single(r => r["id"]?.ToJsonString() == "2")["result"]!;
        Assert.True((bool)result["isError"]!);
        Assert.Contains("Run it yourself: dbdatabuild apply", (string)result["content"]![0]!["text"]!);
        Assert.Empty(host.Ran);
    }

    [Fact]
    public void A_dry_run_changes_nothing_so_it_needs_no_approval()
    {
        var (replies, host) = ApplySession(clientAsks: false, answer: null, dryRun: true);
        Assert.False((bool)replies.Single(r => r["id"]?.ToJsonString() == "2")["result"]!["isError"]!);
        Assert.Contains("--dry-run", Assert.Single(host.Ran));
    }

    [Fact]
    public void Serve_answers_each_line_and_survives_a_line_that_is_not_json()
    {
        var output = new StringWriter();
        var input = new StringReader(Request("ping").ToJsonString() + "\nthis is not json\n\n" + Request("tools/list", id: 2).ToJsonString() + "\n");
        new McpServer(dir, false, input, output, new TuiCommand.CliHost(_ => null)).Serve();
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!).ToList();
        Assert.Equal(3, lines.Count);
        Assert.Equal(-32700, (int)lines[1]["error"]!["code"]!);
        Assert.NotNull(lines[2]["result"]!["tools"]);
    }

    [Fact]
    public void The_command_refuses_a_json_format_and_a_missing_project()
    {
        Assert.Equal(CliApp.ExitUsage, CliApp.Run(["mcp", "--project", dir, "--format", "json"], new StringWriter(), new StringWriter()));
        var (o, e) = (new StringWriter(), new StringWriter());
        Assert.Equal(CliApp.ExitUsage, CliApp.Run(["mcp", "--project", Path.Combine(dir, "nope")], o, e));
        Assert.Equal("", o.ToString());      // nothing but protocol ever goes to standard output
    }
}
