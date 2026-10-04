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
