# Operations guide

For the people who run `dbdatabuild` against a real database: what it needs, what it does, how to schedule it, and what to do when something goes wrong. Everything here was checked against the behavior of the tool on SQL Server 2022 and PostgreSQL 17. **Fabric has not been run against a real engine**, so nothing here is claimed for it.

## 1. Install

`scripts/publish.sh linux-x64` produces one file, `publish/linux-x64/dbdatabuild` (about 200 MB: the .NET runtime, the tool, the polyglot SQL library and DuckDB). Copy it anywhere; nothing else needs installing. On first start the runtime unpacks the native libraries into `$DOTNET_BUNDLE_EXTRACT_BASE_DIR` (default `~/.net`); on a locked-down host point that variable at a writable directory. Only `linux-x64` has been built and run. The `win-x64` build needs the polyglot library built for Windows first (`scripts/build-polyglot.sh`), which has not been done.

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
- **Metadata**: `dbdatabuild publish-metadata` (or `metadata: { store_on_apply: true }` in `dbdatabuild.yml`) stores the metadata of every model as JSON in the tracking schema, readable through the views `metadata_current` and `metadata_columns`.

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
