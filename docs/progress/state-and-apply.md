# Progress log: state, safety, planning, apply (milestones 4 to 6)

Written as work proceeds, one entry per commit, so a reviewer can follow the decisions. The final review summary is `docs/progress/REVIEW.md` (written last).

## 1. Hashes and the tracking-table definition (`DbDataBuild.State`)

New offline project, no database drivers.

- `Hashing`: `ShapeHash`, `PhysicalHash`, `ScriptHash`, built from a canonical text (`shape/v1`, `physical/v1`) so a hash changes only when what it covers changes. Shape hash is sorted by column name and ignores column order (ordinals are recorded separately as `OrdinalText`). The definition hash is the existing `AstHasher`.
- Canonical text escapes `\`, `|`, newline and `~` (the null marker). **A test found a real collision here**: null and the literal text `~` hashed the same. Fixed before anything was recorded anywhere.
- `TrackingSchema`: the seven tracking tables as one logical definition. `TrackingDdl`: idempotent init scripts for SQL Server, Fabric (unverified) and PostgreSQL, one statement per step with a stable id (`init-00`...).

Deviations from the illustrative DDL in DESIGN.md 12 (the design says that DDL is illustrative):

| Deviation | Why |
|---|---|
| Log ids (`ddl_id`, `block_id`) are tool-generated GUIDs, not `IDENTITY` | `IDENTITY` differs across engines and is unconfirmed on Fabric. Order comes from the timestamps. |
| Added `tracking_version` table | Lets a later tool detect and migrate an older layout. |
| Added `interval_id` and primary keys on every table | Every table gets a key so each engine can create it without engine-specific options. Fabric keys are `NONCLUSTERED ... NOT ENFORCED`. |
| Fabric text types are `varchar`, not `nvarchar` | Fabric has no `NVARCHAR` (Microsoft T-SQL surface area page). Unverified: no Fabric engine has been available. |

Tests: `HashingTests` (every covered attribute changes the hash, collisions), `TrackingDdlTests` (idempotent, no DROP/ALTER/TRUNCATE, parses with ScriptDOM and polyglot, quoting).

## 2. Logins, the mutation gate, the statement log (`DbDataBuild.Execution`)

The only project that references a database driver (Microsoft.Data.SqlClient, Npgsql). Implements DESIGN.md 9.2 and 9.3.

- **Logins** (`LoginSettings`): connection strings come from environment variables `DBDATABUILD_<TARGET>_<READ|WRITE>`, for example `DBDATABUILD_SQLSERVER_WRITE`. There is **no fallback between logins**: a missing read login is DDB-501 even when a write login exists. The header description shows the variable and user, never the password.
- **`MutationGate`**: the one handle on a write connection. It takes only `GateStatement`s, checks the statement's kind (tracking, data, DDL) against what the running command permits (DDB-502), logs the statement **before** executing it, logs the outcome after, and does not execute when the log cannot be written (DDB-503). Dry-run mode does the same checks and log entries and executes nothing. `GateStatement` has no public constructor: plan steps come in through `FromPlanStep` (which refuses `Tracking`), tracking statements through an internal factory.
- **Statement log**: JSON lines, one file per run, flushed to disk per line. Failure outcomes record the exception type and driver error number, never the driver message (which can quote data, DESIGN.md 14.2).
- **`ReadSession` and `ReadGuard`**: read-only queries only. The guard allows a single SELECT/WITH, refuses data-changing keywords outside quotes and comments, and refuses dollar-quoting. Tests include quote-hiding tricks (`$$'$$; DROP TABLE x; --'`, backticks). While writing it I removed backtick handling, which would have let `` ` `` hide a statement from the scanner on PostgreSQL. The guard is a second layer: the read login's permissions are the real enforcement. PostgreSQL read sessions also set `default_transaction_read_only=on`.
- **Invariant test** (`GateInvariantTests`, DESIGN.md 9.3): a source scan that fails if any file other than `MutationGate.cs` and `ReadSession.cs` calls a command API, if any project but Execution references a driver package, or if a connection opener becomes public. Sensitivity checked by adding a file with `ExecuteNonQuery` (test fails). **Limitation**: it is a source scan, not a call-graph analysis; a determined reflection call would not be seen.
- New diagnostics DDB-501 to DDB-505 (state and safety).

Tests: `MutationGateTests`, `ReadGuardTests`, `GateInvariantTests`. Unit suite: 649 tests passing.

## 3. Catalog reader, tracking store, `init` command

- **`CatalogReader`** (read session): live shapes of the tables and views in a schema on SQL Server and PostgreSQL: columns (types without parameters, length in characters, precision, scale, nullability, collation, computed text) and physical items (indexes, SQL Server compression and partition counts). Lengths are normalized (`nvarchar(20)` is 40 bytes on SQL Server, reported as 20).
- **`Drift.Classify`** (State, pure): `Missing`, `Untracked` (ask to adopt), `InSync`, `OutOfBand` (block) from the live shape and the newest recorded shape hash. These are the object-level rows of the decision table in DESIGN.md 11.
- **`TrackingStore`**: `InitAsync` (through the gate), `StatusAsync` (missing / ready / unknown layout, DDB-505), `LatestShapeHashesAsync`, `RecordSchemaVersionAsync`. Timestamps come from `TrackingClock`, strictly increasing at millisecond precision, so two records for one object in the same millisecond still order. The values are UTC wall time with Kind Unspecified because the columns are zone-less and Npgsql refuses UTC-kind values for them (found by the PostgreSQL conformance test).
- **`dbdatabuild init`** (effect: tracking tables only): prints the idempotent script by default and connects to nothing; `--target` is needed when the project has more than one default target; `--apply` runs it on the write login through the gate and writes `.dbdatabuild/statement-log/` (now git-ignored). No fallback from read to write login. The generic internal-error text no longer says "no target statements ran"; it points to the statement log.

Real-engine tests (SQL Server 2022 and PostgreSQL 17): init and rerun, unknown layout version, shape and physical hashes change exactly when a column, length or index changes, schema-version round trip with drift classification, failure outcomes without driver text, PostgreSQL read sessions are read-only, and the `init --apply` command end to end with its statement log.

## 4. DDL generation and the type table (`DbDataBuild.Targets/Ddl`)

`ITarget.CreateDdl(config)` gives a `DdlGenerator` per engine (`TSqlDdl` for SQL Server and Fabric, `PostgresDdl`): logical type to native type, expected live shape per column, and the statements CREATE SCHEMA/TABLE, ADD/DROP/RENAME/ALTER COLUMN, DROP TABLE and CREATE OR ALTER VIEW (PostgreSQL: DROP VIEW then CREATE VIEW, because `CREATE OR REPLACE VIEW` cannot change columns).

Mapping choices (DESIGN.md has no table for these; this is the proposal for review):

| Logical | SQL Server | Fabric (unverified) | PostgreSQL |
|---|---|---|---|
| BIGINT / INTEGER / SMALLINT | bigint / int / smallint | same | bigint / integer / smallint |
| TINYINT | **smallint** (T-SQL tinyint is unsigned, DuckDB's is signed) | smallint | smallint |
| DOUBLE / FLOAT | float(53) / real | same | double precision / real |
| BOOLEAN | bit | bit | boolean |
| TIMESTAMP, TIME | datetime2(6), time(6) | same | timestamp(6), time(6) |
| TIMESTAMP WITH TIME ZONE | datetimeoffset(6) | same | timestamp(6) with time zone |
| DECIMAL(p, s), bare DECIMAL | decimal(p, s), decimal(18, 3) | same | numeric(p, s), numeric(18, 3) |
| VARCHAR(n) | nvarchar(n), nvarchar(max) above 4000 | varchar(n), varchar(max) above 8000 | varchar(n) |
| UUID, BLOB | uniqueidentifier, varbinary(max) | same | uuid, bytea |

Refused with DDB-321 (new): unsigned integers, HUGEINT, a VARCHAR without a length, DECIMAL beyond 38 digits, structs, lists, JSON. Text columns always carry the profile collation (`default` or the column's declared logical collation, from `string_semantics.collations`); a missing entry is DDB-312, and collation names are validated before they reach DDL text. Nothing is left to the database default, so the expected shape is known offline.

`DdlGenerator.Classify` grades a type change: none, widening (larger length or precision at the same scale, wider integer), or other. Widening is safe; other is destructive (DESIGN.md 10.4).

**Verified on real engines** (SQL Server 2022, PostgreSQL 17): for 20 columns covering every mapped type, the shape the generator predicts equals, attribute for attribute, what the catalog reader reports after CREATE TABLE. ADD, widening ALTER, NULL/NOT NULL ALTER, rename, drop, and a view created then replaced with different columns all run and change the shape as predicted. The PostgreSQL test image (alpine) has no `en_US.utf8`, so its tests use the `C` and `POSIX` collations.

Not done here: constraints and indexes from the model (`unique_key` does not create a unique constraint; the physical hash only tracks what exists). Noted for the plan's "noticed but not done" list.

## 5. The planner and the decision table (`DbDataBuild.Planning`)

`Planner.Plan(input, answers)` is a pure function: the project's models, a catalog snapshot, tracking records, committed rendered loads, resolver results and the answers so far go in; open **questions**, **blocks**, **skips**, **steps**, the answers used and "noticed but not done" items come out. It reads nothing and writes nothing, so every row is unit-tested without a database. The `plan` command (next entry) loops it with the question resolver until nothing is open.

- **Decision table as data** (`matrix/decision-table.yml`, embedded, 32 rows): each row has an owner (`planner`, `command` or `apply`), the model kinds it covers, a result, and for planner rows the name of the test that proves it. Every step names its row id first in its reason chain. A test fails on an unknown id, on a planner row without a real test, and on a duplicate id. Rows owned by `command` and `apply` (stale rendered files, out-of-sync definitions, stale plan, resolver drift) are listed so the table is complete; those checks are built in the commands.
- **Object level**: missing (create schema and table or view), untracked (question `Q-adopt-*`, answers `adopt` or `stop`), out of band (block DDB-430 unless a person acknowledged that exact shape), in sync.
- **Columns**: add (always asks the history question; NOT NULL is risky), drop (destructive), declared or answered rename (risky), type change graded by `DdlGenerator.Classify`, nullability and collation changes graded. Running shapes are tracked so every DDL step records the hash the table must have after it.
- **Loads**: the default operation's committed script becomes a load step (structure first, then loads, both in dependency order). Runtime parameters without a value are `Q-param-*` questions with the explicit options `provide` and `skip_load`; resolver results come in from the command; a NULL watermark follows the declared `on_null`; values are checked against their type and `max_span`. A table that does not exist yet is empty by definition, so its resolver is not consulted.
- **Blocks and skips**: an incremental model whose query changed since its last recorded load is blocked (DDB-431) until acknowledged; a blocked or skipped model skips everything that reads from it (DDB-433); a cycle plans nothing (DDB-221); an unmappable type blocks only that model (DDB-321).
- New codes DDB-222 and DDB-430 to DDB-434. Acknowledgements are keyed by code, object and the specific hash (`DDB-430|marts.fct|<shape hash>`), so a later, different change blocks again.

Decisions for review:
1. **Views** record the hash of the applied `CREATE OR ALTER VIEW` text (from `ddl_log`) and are altered when it differs. A view's column types are derived by the engine and cannot be predicted offline, so a view step has no expected shape hash; apply will check column names and record the live shape.
2. A plan with blocks is still generated for the models that are not blocked; blocks and skips are printed prominently and the command exits non-zero. The alternative (refuse any plan while a block exists) is one line to change.
3. Unique-key constraints and indexes are not created from the model; the plan says so under "noticed but not done" once the command builds that list.

## 6. The plan document

`PlanDocument.Serialize/Parse` and `PlanReport.Markdown` (Planning project). A plan is two renderings of one object (DESIGN.md 10.2): a machine-readable YAML that `apply` consumes and a Markdown report for people.

- Every string is a JSON-quoted scalar (valid YAML), so scripts with quotes, backslashes, `\r\n`, tabs and non-ASCII text round-trip byte for byte (tested). Files use `\n` always, so the hash does not depend on the platform.
- The file carries a SHA-256 over everything except the hash line. `Parse` recomputes it and also requires the file to be **exactly** what `Serialize` would write, so even a cosmetic edit (a comment, re-indentation) is refused, as is any unknown key, bad enum value, missing hash or changed statement. All of these are DDB-435.
- Plan id: the date plus the first 8 hex of a hash over the plan's content excluding id, commit and hash, so the same plan gets the same id and a different plan does not.
- The Markdown report shows each step's script, risk, reason chain, the answers that decided it (with notes and how they were given), parameters, resolver results, what needs `--allow-risky` / `--allow-destructive`, blocked and skipped models, and "noticed but NOT done".

Tests: `PlanDocumentTests` (round trip with awkward text, fixed point, tamper cases, unknown keys, id, report content).

## 7. `plan` and `check`

- **`TargetSnapshotReader`** (Execution, read session): live shapes of the managed schemas, existing schemas, the newest recorded shape hash per object (`schema_version`), the newest successful DDL statement hash per object (`ddl_log`, used for views), the newest successful load's definition hash per model (`run_log`), and acknowledged blocks (`block_log`). `RunResolverAsync` runs a committed resolver and requires exactly one row and one column.
- **`PlanningSession`** (Cli): the offline preflight, then the live side. The project must validate (the shared `ProjectChecks` of `validate`, plus `define --check` and a comparison of committed `rendered/` files with a fresh render), or nothing is planned (DDB-420, DDB-424, DDB-317 and friends, listed in the decision table as `command` rows). Then the tracking tables must be ready (DDB-505), the snapshot is read, and resolvers run for models whose table already exists.
- **`dbdatabuild plan`**: asks every open question (interactively, or from `--answers`; `--accept-inferred` takes only high-certainty proposals), re-plans until nothing is open, then writes `plans/<target>/<id>.plan.yml` and `.plan.md`. Blocks and skips are printed and the exit code is 1 when there are blocks; the plan is still written for the rest. The git commit and dirty flag are recorded; this tool's own `plans/` and `.dbdatabuild/` output does not count as dirty.
- **`dbdatabuild check`**: prints each object's state (missing, in sync, not tracked, CHANGED OUTSIDE THE TOOL), then what a plan would contain and ask. Writes nothing, asks nothing.
- `LoadRenderer` now also returns the rendered operations (script, resolver, parameters, watermark rule) so a plan does not re-parse manifests.

## 8. `apply`, `ack`, and the audit tables

- **`ApplyEngine`** (new project `DbDataBuild.Apply`): allowances (`--allow-risky`; `--allow-destructive <object>` naming each object, never "all"), then the read side (tracking ready, resume bookkeeping), then the **base check**: every object's state and hashes must equal what the plan recorded, and every resolver must return what it returned at plan time, or the plan is stale (DDB-437) and nothing runs. Then the application lock, then the steps.
- **Application lock**: `sp_getapplock` (session owner, no wait) on SQL Server, `pg_try_advisory_lock` on PostgreSQL, one lock per tracking schema. Taken and released through the gate and logged. A second apply gets DDB-439; the tool does not wait.
- **Per step** (all writes through the gate): DDL is logged in `ddl_log` before it runs (status `started`), executed, then the object's shape is re-read and compared with the hash the plan promised (DDB-441 on a mismatch, nothing after it runs), the log row is updated, and a `schema_version` row is recorded. A load writes `run_log` (parameters, definition hash, shape hashes, file hash) around the script. A track step records an adopted or acknowledged shape. `migration_log` gets a `started` row and a `completed` or `failed` row.
- **Failure**: the first failing step stops the run (DDB-440). After a failed statement the gate issues a rollback so tracking writes can still happen; a mutation test confirmed this matters on PostgreSQL (an aborted transaction otherwise rejects the failure record). Error text in logs and output is the exception type and driver error number only.
- **Resume**: `apply --resume` continues only when the finished steps are a prefix of the plan and each touched object is exactly in the state its last finished step promised; otherwise stale. Without `--resume`, a plan that started before is refused (DDB-438); a completed plan is never run again.
- **Dry run** runs the same preflight and the same code path through a gate that logs and executes nothing; it needs the read login only. A dirty working tree refuses a real apply (DDB-442, `--allow-dirty` records it) and only warns on a dry run.
- **`dbdatabuild ack drift <object>` / `ack definition <model>`** (effect: tracking tables only) record a person's decision with the exact hash it is about, who made it and why (`--reason` is required). It clears exactly that block: a later, different change blocks again. A repeated ack is a no-op.
- New codes DDB-436 to DDB-442.

**Verified on SQL Server 2022 and PostgreSQL 17** by one scenario per engine (`ApplyConformanceTests`): init, render, check, plan, dry run (nothing created), apply (data, views, and all four audit tables), a refused re-apply, a routine load, stale rendered files (DDB-424), an incremental definition block (DDB-431) with the downstream view skipped (DDB-433), `ack`, an unanswered history question (DDB-414) then the answered plan with the note carried into the report, an out-of-band column (DDB-430) then `ack drift`, a destructive step refused without its allowance (and with the wrong object), a stale plan (DDB-437), an edited plan (DDB-435), a load that fails because its source vanished (DDB-440, failure recorded) then `--resume` completing without repeating finished steps, and a competing application lock (DDB-439). Statement logs were checked for secrets.

Known gaps, listed for review: unique-key constraints and indexes are not created; resolver and operation choice is the default operation only (no `--operation`); no `backfill` or `hook` steps yet (milestone 7); views are verified by existence, not column names; `report` is not built; `run` (plan plus apply for routine loads) is not built; the live-catalog collation check in `check` is not built; there is no JSON Schema for plan files (they are verified by their own parser and hash).

## 9. `run` and `report`

- **`dbdatabuild run`** (effect: target data writes): plans like `plan`, and applies only if the plan is routine loads and nothing else: no open question, no block, no skip, and no step that is not a safe load. Otherwise it prints why, executes nothing, writes nothing, and points to `plan` (DESIGN.md 9.1). When it does run, the plan is written like any plan and applied through the same `apply` code, so the plan files, `migration_log` and the statement log all show it. `run` cannot ask questions; a load with an unanswered runtime parameter is not routine.
- **`dbdatabuild report`** (effect: target read-only): recent applied plans, DDL and loads (`--last N`), every object the tool has recorded with how many shapes and whether it is in sync now, and a "needs attention" list: plans that never completed, DDL or loads that did not finish ok, and objects changed outside the tool, each with the command that deals with it. Exit code 1 when something needs attention. The per-column history consistency report of DESIGN.md 12.3 is not built.

All thirteen commands in the command table now exist.

## 10. Operation choice, backfill, and `operation_interval`

- `plan --op model=operation` loads a model with a named non-default operation (a routine, safe load). `plan --backfill model=operation` plans that operation as a **backfill step**: type `backfill`, risk **risky** (so `apply` needs `--allow-risky`), and the model has no routine load in the same plan. Both are repeatable; a model named twice with different operations, an unknown model, or malformed `model=operation` is a usage error before anything is read. A named operation that is not rendered for the target blocks the model (DDB-434, decision-table row `load.operation.missing`). The resolver of the chosen operation, not only the default one, is run at plan time.
- Backfill parameters are questions like any other (`Q-param-<model>-<op>-<param>`), and `max_span` is enforced at plan time (DDB-412).
- `apply` records every load and backfill that has a range in `operation_interval`: `range_start` and `range_end` for a range operation, the watermark as `range_start` for a watermark load, the shape hash after the step, and the operation (`load` or `backfill`). This is the input the history consistency report of DESIGN.md 12.3 needs; that report is still not built.
- Decision: a backfill does **not** clear the "incremental model changed since its last load" block (DDB-431). DESIGN.md 11 says "require `ack` or explicit backfill"; a range backfill does not necessarily cover everything the old query loaded, so clearing the block needs a person's `ack definition`.
- Verified on both engines (`Operations_can_be_chosen_...`): a watermark load on a new table uses the declared initial literal and records its interval; usage errors; an operation that is not rendered; a range longer than `max_span` refused at plan time; a backfill refused without `--allow-risky`, then applied, replacing rows that had been changed in the target from the source, with its `operation_interval` and `run_log` rows.

## 11. Column history in `report` (DESIGN.md 12.3)

`ColumnHistory.Build` (Planning, pure) joins three things the audit tables already keep: the history answers embedded in each completed plan (read back from `migration_log.plan_text` with the same tamper-checking parser `apply` uses), the object's recorded shapes, and `operation_interval` joined to `run_log` for timing. For each column a plan added it reports when, which schema version, what the person decided (with the note, how it was answered, and who applied it), how many recorded load ranges predate the change (they hold NULL) and how many came after. A requested `backfill_later` with no later recorded backfill is flagged and appears under "needs attention" (exit code 1) until a backfill is recorded; a backfill recorded before the column existed does not count. A plan text that cannot be read back is reported, not skipped silently.

Limits: the "ranges before the change hold NULL" count is about recorded ranges only (a full-replace or key load records none); it says what the tool did, not what the data contains. The unacknowledged-inconsistency policy of 12.3 ("a warning or a block for downstream models, using lineage") is not built: it is reported, not enforced.
