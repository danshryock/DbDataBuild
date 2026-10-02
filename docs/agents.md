# AI agents and dbdatabuild

How an AI coding agent (Claude Code or another) should work with the tool, and why.

## Use the CLI

An agent should operate dbdatabuild through its **command line with `--format json`**. That interface already exists and is the same one the terminal interface uses:

- every command prints exactly one JSON document whose shape is a closed schema (`schemas/output.schema.json`), with structured diagnostics that each carry a code, a location, what was found, what is supported and a fix;
- every command declares an effect class (offline, repo files, database read-only, tracking tables, data writes), so what an agent may run without asking is decided by the command, not by trust;
- the plan file is the review gate: an agent can `plan`, a person reads the plan, and `apply` runs exactly what was recorded and refuses an edited plan; the write login is a separate environment variable that an agent's shell should not have;
- the agent's tool permissions can follow the same line (below), and nothing needs to be running in the background.

**Not an HTTP service.** A server would add a long-lived process, authentication, and a network surface to a tool whose design is explicit, offline-first and plan-then-apply, and it would offer nothing the CLI cannot do.

**MCP later, if wanted, as a thin wrapper.** A stdio MCP server over the same command layer would give an agent typed tools and let it read the schemas, the support matrix and `explain` as resources. It would expose the offline commands, `check` and `plan` by default and leave `apply`, `run`, `ack`, `init --apply` and `publish-metadata` off unless an operator turns them on. It is a convenience, not a new capability, so it has not been built.

## Give the agent the knowledge

The tool has rules a model will not guess: the SQL is DuckDB's dialect and the engine SQL is generated; `grain` and `unique_key` mean specific things; indexes are never inferred; some constructs are refused with a reason; a plan must not be edited; questions about data belong to a person. `dbdatabuild agent-kit --write` installs a **skill** (`.claude/skills/dbdatabuild/SKILL.md`) and the JSON Schemas next to it. They are embedded in the executable, so they always match the version of the tool; `agent-kit --check` in CI fails when an installed copy is stale, and tests keep every command, option, diagnostic code and schema the skill names real.

In a project's own `CLAUDE.md` (or equivalent), a pointer is enough:

```
This is a dbdatabuild project. Read .claude/skills/dbdatabuild/SKILL.md before writing models or running dbdatabuild.
Run dbdatabuild with --format json. Never apply, run, ack or publish without my say-so.
```

## Permissions that match the effect classes

For Claude Code, allow what changes nothing and make a person approve the rest. For example, in `.claude/settings.json` (check the current Claude Code documentation for the exact syntax of your version):

```json
{
  "permissions": {
    "allow": [
      "Bash(dbdatabuild validate:*)", "Bash(dbdatabuild sample:*)", "Bash(dbdatabuild metadata:*)", "Bash(dbdatabuild loads:*)",
      "Bash(dbdatabuild matrix:*)", "Bash(dbdatabuild explain:*)", "Bash(dbdatabuild render:*)", "Bash(dbdatabuild define:*)",
      "Bash(dbdatabuild check:*)", "Bash(dbdatabuild plan:*)", "Bash(dbdatabuild report:*)", "Bash(dbdatabuild agent-kit:*)"
    ],
    "ask": [
      "Bash(dbdatabuild apply:*)", "Bash(dbdatabuild run:*)", "Bash(dbdatabuild ack:*)", "Bash(dbdatabuild init:*)", "Bash(dbdatabuild publish-metadata:*)"
    ]
  }
}
```

Give the agent's shell the **read** login only (`DBDATABUILD_<TARGET>_READ`). With no write login in its environment the commands that change a database cannot succeed from the agent's session, whatever it is asked to do; a person runs `apply` from a shell that has it (or from `dbdatabuild tui`).

## What an agent is good for here

Writing and changing models and sources; reading `validate` and `plan` output and explaining it; running `sample` to check logic on generated data; keeping `rendered/` and definitions in sync; summarizing a plan's risky steps for a reviewer. What it should hand back to a person: the answers to data questions, the decision to apply, and any `ack`.
