# Interfaces: terminal, web page, MCP server and MCP app

All four are clients of the same command layer (`--format json` documents). None has logic or a database path of its own, and none can do more than the command line can.

| | Start it with | What it is for |
|---|---|---|
| Terminal | `dbdatabuild tui` | choose, plan and run commands interactively |
| Web page | `dbdatabuild web --project <dir>` | a person reads: health, lineage, models and their lowered and rendered scripts, plans (and answers their questions), sample data, table diff, tests, the support matrix; with `--allow-apply`, applies a plan they confirmed |
| MCP server | `dbdatabuild mcp --project <dir>` | an AI agent works in the project: the commands as tools, the skill and schemas as resources, workflows as prompts |
| MCP app | the same `mcp` server, in a host that supports MCP Apps | the web page inside the conversation: the person sees and decides, the model cannot press the buttons |

## Web page

```
dbdatabuild web --project my-project            # prints http://127.0.0.1:<port>/?token=...  open it
dbdatabuild web --project my-project --allow-apply
```

It listens on the loopback address only; every request needs the token printed at start. Without `--allow-apply` it can read, plan and compare, not apply. With it, applying needs the write login (`DBDATABUILD_<TARGET>_WRITE`) in the environment of `dbdatabuild web`, every allowance named in the page, and the plan's target typed back. The page reads the target with `DBDATABUILD_<TARGET>_READ` (plan, table diff). Details: `DESIGN.md` 9.7.

## MCP server

```
dbdatabuild mcp --project my-project                 # the commands that do not change a database
dbdatabuild mcp --project my-project --allow-writes  # also apply, run, init, ack, ...: each run needs the person's approval through the host
dbdatabuild mcp --project my-project --allow-apply   # the app's apply button (see below); the model is not offered it
```

What a model is never given: `diff --show-values`, `sample --data`, a path outside the project. Logins stay in the server's environment (the host's server configuration), never in a tool argument.

### Connecting a host

The server speaks MCP over standard input and output. The settings below are the usual shapes; check your host's current documentation, which changes.

Claude Code: `dbdatabuild agent-kit --write --mcp` adds the server to the project's `.mcp.json` (the other servers in the file are kept; the entry is `dbdatabuild mcp --project .`, read-only, passing the `DBDATABUILD_<TARGET>_READ` logins on by name from your environment, never the write login). It needs `dbdatabuild` on the PATH, and Claude Code asks before it first uses a project server. The skill the same command installs tells the agent how to work with the tools, and what is deliberately not offered.

Claude Desktop (`claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "dbdatabuild": {
      "command": "/path/to/dbdatabuild",
      "args": ["mcp", "--project", "/path/to/my-project"],
      "env": { "DBDATABUILD_POSTGRES_READ": "Host=...;Database=...;Username=...;Password=..." }
    }
  }
}
```

VS Code (`.vscode/mcp.json`):

```json
{
  "servers": {
    "dbdatabuild": {
      "type": "stdio",
      "command": "/path/to/dbdatabuild",
      "args": ["mcp", "--project", "${workspaceFolder}"]
    }
  }
}
```

`scripts/pack-mcpb.sh` builds an `.mcpb` bundle (a one-click install for hosts that take them) from a published executable. It is read-only by design (no `--allow-writes`, no `--allow-apply`, no logins); use the JSON above when you need those.

## The MCP app

A host that supports the MCP Apps extension and says so when it connects (`io.modelcontextprotocol/ui` in its capabilities) gets, besides the tools, the resource `ui://dbdatabuild/app` and a `show` tool. When the model calls `show`, `review`, `plan`, `graph`, `diff` or `sample`, the host renders the page in the conversation and opens the screen for that result. The page calls the server through tools only the app may call (`ui_run`, `ui_file`, `ui_apply`, ...: the extension has the host hide them from the model). A host that does not advertise the extension sees none of this, by design: it would show those tools to the model.

### Trying it

Without Claude Desktop or VS Code:

```
node scripts/mcp-app-host.mjs my-project --allow-apply     # a small stand-in host; open the address it prints
```

With the reference host of the extension (an independent implementation: a double sandbox, the SDK's AppBridge). It talks HTTP, and does not advertise the extension, so two small adapters sit in front of the server:

```
git clone https://github.com/modelcontextprotocol/ext-apps && cd ext-apps && npm install
(cd examples/basic-host && npm run build)
npx -y supergateway --stdio "node scripts/mcp-advertise-ui.mjs -- dbdatabuild mcp --project my-project --allow-apply" \
    --outputTransport streamableHttp --stateful --port 3001 --cors
(cd examples/basic-host && SERVERS='["http://localhost:3001/mcp"]' bun serve.ts)     # open http://localhost:8080
```

Pick the tool `show` with `{"screen": "plans"}`, or `review` with `{"plan": "plans/<target>/<id>.plan.yml"}`.

### A checklist for a real host

What differs between hosts is what is worth looking at. In Claude Desktop or VS Code:

1. Does the page appear when the model calls `show`, `review`, `plan`, `graph`, `diff` or `sample`? In which screen?
2. Does it follow the host's light or dark theme, and resize to its content?
3. Are the `ui_*` tools absent from what the model can call (ask it to list its tools)?
4. Click through Health, Lineage, Models (the Lowered and per-target tabs), Plans and Sample data. Anything that stays on "Running the commands…" or shows "Could not load" is a finding; note the text.
5. With `--allow-apply` and a sandbox database: open a plan, tick the allowances, type the target, run a dry run, then apply. The model must not be able to do this on its own.
6. With `--allow-writes` instead: ask the model to apply a plan. The host should show a confirmation (an elicitation) with the command and the plan's steps; declining must run nothing. A host that cannot ask must make the model say what to run by hand.
7. Does a result of the page's own tool calls reach the model? It should not.

Report what you see with the host name and version.
