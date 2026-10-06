# dbdatabuild

Builds analytics tables and views on **SQL Server**, **PostgreSQL** and **Microsoft Fabric** from models written once in **DuckDB's SQL dialect**.

It is explicit, offline-first and plan-then-apply:

- A model is a `SELECT` plus a YAML definition that declares its columns. DuckDB binds the query offline and the tool lowers it to one explicit query, then renders the load scripts for each target. The scripts are committed.
- `plan` reads the target (read-only login) and writes a plan file: every statement, its risk, and the reasons. A person reads it. `apply` runs exactly what the plan recorded and refuses a plan that was edited, goes stale, or has drifted from the target.
- Every statement to a database goes through one gate with a statement log. The write login is separate from the read login, and nothing falls back from one to the other.
- Everything is data you can inspect: every command prints one JSON document (`--format json`) whose shape is a schema in `schemas/`.

> **Status.** Pre-1.0 and not yet used in production. SQL Server 2022 and PostgreSQL 17 are verified by a real-engine test suite. **Fabric has never been run against a real engine**; its support is marked `unverified` throughout.

## Install

Releases carry a single self-contained executable for **Windows** (x64) and **Linux** (x64, glibc 2.34+), with a `SHA256SUMS` file: <https://github.com/danshryock/DbDataBuild/releases>.

Windows, with [Scoop](https://scoop.sh) (the manifest is updated by each release):

```
scoop install https://raw.githubusercontent.com/danshryock/DbDataBuild/main/bucket/dbdatabuild.json
```

## A first look

```
dbdatabuild validate                      # check config and models, lower and lint every query
dbdatabuild import staging.*     # export tables from the connection as mapped models under models/ (diff first; --write to save)
dbdatabuild graph +marts.fct_orders       # what a model depends on (also: marts.fct_orders+, --column m.c, --diagram mermaid)
dbdatabuild diff marts.fct_orders --against-schema dev   # compare two tables of one target (counts only; --show-values to see rows)
dbdatabuild sample marts.fct_orders       # run a model on generated sample data, offline
dbdatabuild new adventureworks my-project   # a complete example project (seeds, models, tests) to explore offline; also starter, retail, chinook
dbdatabuild render --write                # write the committed load scripts
dbdatabuild plan --connection postgres        # read the target, write a plan file
dbdatabuild apply plans/postgres/<id>.plan.yml --dry-run
dbdatabuild test                          # run the project's tests (metadata rules in tests/metadata/)
dbdatabuild tui                           # the same, interactively
dbdatabuild web                           # the same in a browser, read-only unless --allow-apply (lineage, plans, diff, sample data)
dbdatabuild mcp                           # the commands as tools for an AI agent (Claude Desktop, VS Code); also an MCP app
```

`dbdatabuild --help` lists every command with its effect class (offline, repo files, database read-only, tracking tables, data writes). `dbdatabuild explain DDB-nnn` explains any diagnostic. `dbdatabuild agent-kit --write` installs a skill and the JSON Schemas for AI coding agents (`docs/agents.md`).

## Build

Requires the .NET 10 SDK. The SQL parser is a native library built from source once:

```
scripts/fetch-native.sh       # downloads the prebuilt SQL library for the pinned commit into native/<rid>/
scripts/build-polyglot.sh     # or build it yourself (needs a Rust toolchain, about 12 minutes)
dotnet build
dotnet test tests/DbDataBuild.Tests.Unit
scripts/publish.sh linux-x64  # one self-contained executable
```

The real-engine suite uses throwaway SQL Server and PostgreSQL containers (`scripts/test-engines.sh up`; see `CLAUDE.md`). linux-x64 is built and tested; win-x64 builds from Linux (`TARGET_RID=win-x64 scripts/build-polyglot.sh`) and has been run under Wine only.

## Documentation

- `DESIGN.md`: the design. Sections marked "as built" describe what exists.
- `docs/operations.md`: running it against real databases, scheduling, failure and recovery.
- `docs/agents.md`: working with AI agents.
- `docs/interfaces.md`: the terminal, web page, MCP server and MCP app; connecting a host.
- `docs/progress/`: the build log (`state-and-apply.md`), the owner's review summary (`REVIEW.md`) and what is unfinished (`OPEN-ITEMS.md`).
- `docs/research/duckdb-plan-lowering/`: how queries are lowered, with the prototype code.
- `CLAUDE.md`: for anyone (human or agent) changing the code.

## License

MIT. See `LICENSE` and `THIRD-PARTY-NOTICES.md`.
