# Operations guide

For the people who run `dbdatabuild` against a real database: what it needs, what it does, how to schedule it, and what to do when something goes wrong. Everything here was checked against the behavior of the tool on SQL Server 2022 and PostgreSQL 17. **Fabric has not been run against a real engine**, so nothing here is claimed for it.

## 1. Install

`scripts/publish.sh <rid>` produces one file, `publish/<rid>/dbdatabuild` (`.exe` on Windows; about 200 MB: the .NET runtime, the tool, the polyglot SQL library and DuckDB). Copy it anywhere; nothing else needs installing. On first start the runtime unpacks the native libraries into `$DOTNET_BUNDLE_EXTRACT_BASE_DIR` (default `~/.net`, `%TEMP%\.net` on Windows); on a locked-down host point that variable at a writable directory.

- **linux-x64**: built and tested (unit and real-engine suites).
- **win-x64**: builds from Linux (`TARGET_RID=win-x64 scripts/build-polyglot.sh` cross-compiles the SQL library with MinGW, then `scripts/publish.sh win-x64`). The Windows build has been run only under Wine (every offline command gave byte-identical output and identical rendered files to Linux, and the unit suite was run there; see `docs/progress/state-and-apply.md` entry 34). It has **not** been run on real Windows, the single-file form could not be run under Wine (a Wine limitation with single-file .NET), and the terminal interface has not been tried on a Windows console.
- Other platforms are not built.

## 2. Logins

Connection strings come from environment variables, never from files in the repository:

| Variable | Used by | Needs |
|---|---|---|
| `DBDATABUILD_SQLSERVER_READ`, `DBDATABUILD_POSTGRES_READ` | `check`, `plan`, `report`, and the read side of `apply` | catalog and tracking-table read |
| `DBDATABUILD_SQLSERVER_WRITE`, `DBDATABUILD_POSTGRES_WRITE` | `init --apply`, `apply`, `run`, `ack`, `publish-metadata` | DDL and DML in the managed schemas, and insert/update on the tracking tables |

There is no fallback from one login to the other: a missing variable is an error that names it. Every command prints which variable and which user it used (never the password). Use two separate accounts; the read account should not be able to write at all.

Offline commands (`validate`, `render`, `loads`, `matrix`, `explain`, `define`) connect to nothing.

## 3. The normal cycle

1. `dbdatabuild validate` and `dbdatabuild render --check` in CI. The first reads models and sources, lowers each query with DuckDB, and lints it per target; the second fails when the committed `rendered/` files differ from a fresh render. Run `render --write` and commit the result when a model changes.
2. `dbdatabuild check` (read-only) shows drift: objects whose live shape no longer matches what the tool last recorded.
3. `dbdatabuild plan` writes `plans/<target>/<id>.plan.yml`. Commit it. The plan records every statement, its risk class and a SHA-256 of its own content; **`apply` refuses a plan that was edited by hand**.
4. `dbdatabuild apply <plan>` runs exactly the recorded statements. `--dry-run` runs every check and prints every statement without executing anything. Risky steps need `--allow-risky`; destructive steps need `--allow-destructive <object>` for each object. `--allow-dirty` permits a working tree with uncommitted changes (recorded).
5. `dbdatabuild run` is `plan` + `apply` for routine loads only. It refuses (and points at `plan`) when the plan would contain DDL, a question or a risky step. This is the command to schedule.

## 4. Scheduling (SQL Agent, cron, any scheduler)

Schedule `dbdatabuild run` for routine loads; use `plan` and `apply` by hand or in a reviewed pipeline for anything that changes structure.

Exit codes: `0` ok, `1` findings (a diagnostic with an error severity, a refused plan, a failed step), `2` usage (a bad option, a missing file), `3` command not implemented, `70` internal error (a tool bug; please report it). A scheduler should treat anything but `0` as a failure. `--format json` prints one JSON document on standard output (shape in `schemas/output.schema.json`) with the data, the diagnostics and the human text; use it to feed monitoring.

SQL Agent job step (type: operating system command, run as a proxy account that holds the two connection strings in its environment):

```
dbdatabuild run --project D:\etl\project --target sqlserver --format json > D:\etl\logs\run.json
```

Make the working directory or `--project` a checkout of the repository: the tool reads the committed `rendered/` files and records the git commit with each plan.

Only one `apply` or `run` can work on a target at a time: the tool takes an application lock (`sp_getapplock` on SQL Server, an advisory lock on PostgreSQL). A second run exits with a finding that says the lock is held; it does not wait. If a job overlaps its own previous run, that is the reason.

## 5. When something fails

- **Every statement the tool sends is written to `.dbdatabuild/statement-log/` before it is sent**, with its hash and step id, and the outcome after. If that log cannot be written the statement is not executed. It holds full statement text, so keep it out of version control (the repository's `.gitignore` excludes `.dbdatabuild/`) and ship it to your log store if you want it kept; it is the first thing to read.
- A failed step stops the plan and reports the **exception type and error number only**. Driver messages are not echoed because they can quote data (a conversion failure quotes the offending value). The error number and the statement log identify the failure; reproduce the query in the database to see the full message.
- **Resume**: `dbdatabuild apply <plan> --resume` continues a plan that stopped part-way, but only if the live objects are exactly in the intermediate state the log recorded. Otherwise it refuses and says what differs.
- **Drift**: if someone changed an object outside the tool, `check` and `plan` say so and block that object. Accept the change with `dbdatabuild ack drift <object> --reason "..."` (recorded with your login), or restore the object. `ack definition <model> --reason ...` accepts a changed query for an incremental model whose already-loaded rows were produced by the old query. `ack history <model>.<column> --reason ...` silences a recorded backfill that never happened; the report keeps the fact.
- **The tracking tables** live in the schema `dbdatabuild` (configurable as `tracking_schema`). `dbdatabuild report` summarizes them: applied plans, DDL, loads, recorded shapes, and anything that started and never finished. `dbdatabuild init` is safe to re-run; without `--apply` it only prints the script for review.
- **Metadata**: `dbdatabuild publish-metadata` (or `metadata: { store_on_apply: true }` in `dbdatabuild.yml`) stores the metadata of the project, every source descriptor and every model as JSON in the tracking schema, readable through the views `metadata_current` and `metadata_columns` (one row per model or source column; `kind` says which). Tracking layout 3 added the source documents: a target initialised by an older tool is refused (DDB-505) until `dbdatabuild init --apply` upgrades it, which only creates what is missing.
- **Choosing models**: `render`, `plan`, `check`, `run`, `sample`, `metadata`, `publish-metadata` and `graph` take selectors: `+model`, `model+`, `2+model`, `@model`, `kind:`, `target:`, `path:`, `changed:<git ref>`, `a,b` (both), `exclude:<selector>`. `dbdatabuild plan changed:origin/main+` plans what a branch changed and everything that depends on it. `dbdatabuild graph` shows the graph (`--columns`, `--column model.column`, `--diagram dot|mermaid`); see DESIGN.md 9.9.
- **Tests**: `dbdatabuild test` runs the project's tests offline (nothing is contacted or written; `--kind metadata|model`, `--tag`, `--strict`). **Model tests** (`tests/models/<model>.yml`) give rows per table the model reads and the rows its query must return (`expect`, optionally `ordered`) or a query over `result` that returns violations (`assert`); they run in DuckDB, so they check the logic, not an engine. **Metadata rules** are in `tests/metadata/*.sql`: a DuckDB SELECT over the `metadata_*` views that returns the violations, with `-- severity: error|warning`, `-- tags: ...` and `-- description: ...` comments at the top. It exits 1 if an error-severity rule returns rows or cannot run (`--strict` also for warnings); `--tag` selects a group. Run it in CI next to `validate`, `define --check` and `render --check`; nothing else gates on it yet (DESIGN.md 9.8).
- **Sources**: `dbdatabuild import-sources [schema.table ...]` exports the tables and views a project reads but does not build, as `sources/<schema>/<table>.yml`, with the read login only (it needs no tracking tables). It shows a diff and writes nothing until `--write`; with no arguments it refreshes the descriptors the project has; `--check` fails (DDB-227) when one has gone stale, for CI. Columns whose type has no honest logical type (unlimited text, xml, json, arrays) are left out and reported (DDB-226): declare them by hand with a type you choose, and a refresh keeps them.

## 6. Who changed what: out-of-band DDL

The tool detects drift by hashing each object's catalog shape; it does **not** install triggers or event sessions and cannot say who changed something or what statement they ran. For that, configure the database's own audit:

- **SQL Server**: SQL Server Audit with a database audit specification on the managed schemas for `SCHEMA_OBJECT_CHANGE_GROUP` (and `DATABASE_OBJECT_CHANGE_GROUP`), writing to a file or the security log. The audit records the login, time and statement text. This is the authoritative record and is configured by the DBAs.
- **PostgreSQL**: set `log_statement = 'ddl'` (cheap, in the server log) or install `pgaudit` with `pgaudit.log = 'ddl'` for structured entries.
- **Fabric**: not checked.

Typical use: `dbdatabuild check` reports drift on `marts.fct`; the audit says which login ran the `ALTER TABLE`; the operator decides to `ack drift` or to revert.

## 7. What the tool will not do

- Run any statement that is not in a plan, a tracking-table writer, or a recorded hook script.
- Drop an index it was not told about (an undeclared index is left alone and reported).
- Guess: a query it cannot lower (DDB-324), a type with no exact equivalent, or a construct the support matrix marks unsupported on the target is an error naming the model and the target. `dbdatabuild explain DDB-nnn` explains any code, and `dbdatabuild matrix` prints what is known to differ per engine.
