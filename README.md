# dbdatabuild

Builds analytics tables and views on **SQL Server**, **PostgreSQL** and **Microsoft Fabric** from models written once in **DuckDB's SQL dialect**.

It is explicit, offline-first and plan-then-apply:

- A model is a `SELECT` plus a YAML definition that declares its columns. DuckDB binds the query offline and the tool lowers it to one explicit query, then renders the load scripts for each target. The scripts are committed.
- `plan` reads the target (read-only login) and writes a plan file: every statement, its risk, and the reasons. A person reads it. `apply` runs exactly what the plan recorded and refuses a plan that was edited, goes stale, or has drifted from the target.
- Every statement to a database goes through one gate with a statement log. The write login is separate from the read login, and nothing falls back from one to the other.
- Everything is data you can inspect: every command prints one JSON document (`--format json`) whose shape is a schema in `schemas/`.

> **Status.** Pre-1.0 and not yet used in production. SQL Server 2022 and PostgreSQL 17 are verified by a real-engine test suite. **Fabric has never been run against a real engine**; its support is marked `unverified` throughout.

## A first look

```
dbdatabuild validate                      # check config and models, lower and lint every query
dbdatabuild sample marts.fct_orders       # run a model on generated sample data, offline
dbdatabuild render --write                # write the committed load scripts
dbdatabuild plan --target postgres        # read the target, write a plan file
dbdatabuild apply plans/postgres/<id>.plan.yml --dry-run
dbdatabuild tui                           # the same, interactively
```

`dbdatabuild --help` lists every command with its effect class (offline, repo files, database read-only, tracking tables, data writes). `dbdatabuild explain DDB-nnn` explains any diagnostic. `dbdatabuild agent-kit --write` installs a skill and the JSON Schemas for AI coding agents (`docs/agents.md`).

## Build

Requires the .NET 10 SDK. The SQL parser is a native library built from source once:

```
scripts/build-polyglot.sh     # needs a Rust toolchain; writes native/<rid>/
dotnet build
dotnet test tests/DbDataBuild.Tests.Unit
scripts/publish.sh linux-x64  # one self-contained executable
```

The real-engine suite uses throwaway SQL Server and PostgreSQL containers (`scripts/test-engines.sh up`; see `CLAUDE.md`). linux-x64 is built and tested; win-x64 builds from Linux (`TARGET_RID=win-x64 scripts/build-polyglot.sh`) and has been run under Wine only.

## Documentation

- `DESIGN.md`: the design. Sections marked "as built" describe what exists.
- `docs/operations.md`: running it against real databases, scheduling, failure and recovery.
- `docs/agents.md`: working with AI agents.
- `docs/progress/`: the build log (`state-and-apply.md`), the owner's review summary (`REVIEW.md`) and what is unfinished (`OPEN-ITEMS.md`).
- `docs/research/duckdb-plan-lowering/`: how queries are lowered, with the prototype code.
- `CLAUDE.md`: for anyone (human or agent) changing the code.

## License

MIT. See `LICENSE` and `THIRD-PARTY-NOTICES.md`.
