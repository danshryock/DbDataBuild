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

Known gaps, listed for review: unique-key constraints and indexes are not created; resolver and operation choice is the default operation only (no `--operation`); no `backfill` or `hook` steps yet (milestone 7); views are verified by existence, not column names; `report` is not built; `run` (plan plus apply for routine loads) is not built; the live-catalog collation check in `check` is not built; the plan JSON Schema cannot verify the content hash.

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

## 12. Live collation check in `check`

`CollationChecker.CheckLive` applies the profile check to what the catalog reports for each managed table (DESIGN.md 9.4). Text columns left on the default collation must satisfy the profile (DDB-310); a column on the database default (the catalog reports no name, as PostgreSQL does) is DDB-311 because it cannot be verified; declared exceptions and columns the model does not declare are skipped. A DDB-310 makes `check` exit 1. Unit tests cover each case; the end-to-end scenario's `check` runs stay clean on both engines. Not covered by a real-engine test of a *violating* live column: it would also be drift, which the scenario already exercises.

## 13. JSON Schema for plan files

`schemas/plan.schema.json` describes the plan file for editors and reviewers (every key, enum, hash shape, and the rule that a step's reason chain is non-empty). Tests check three things: what the writer produces satisfies it; structural damage (unknown key, bad step type, risk, target, state, short hash) fails both the schema and the parser; and a well-formed *content* edit passes the schema but is refused by the parser, which is the point: the schema cannot verify the content hash, so it is for editors and the hash is the guard.

## 14. Model syntax for indexes and hooks (decisions from the operator)

Operator decisions recorded here: **indexes are declared, never implied** (a table with no unique constraint can still merge on a column; engine enforcement is the operator's choice, and lint or generation features may suggest or create them later), and **hooks are an open structure**: ordered, named, per-event, per-target, with named groups.

```yaml
indexes:
  - {name: uq_fct_orders_key, columns: [order_id], unique: true}
  - {name: ix_fct_orders_date, columns: [order_date], include: [amount], targets: [sqlserver]}

hooks:                                   # an ordered list: this is the run order within an event
  - {name: grant_reader, event: post_create, script: hooks/grant_reader.sql}
  - name: refresh_stats
    event: post_load
    script: {sqlserver: hooks/sqlserver/stats.sql, postgres: hooks/postgres/stats.sql}   # one file per engine
    effect: data                         # ddl (default) or data; `run` runs only data hooks
    risk: safe                           # the author's declaration: safe (default), risky, destructive
  - {use: standard_audit}                # insert the hooks of a group, in place

# dbdatabuild.yml
hook_groups:
  standard_audit:
    - {name: stamp, event: post_load, script: hooks/audit_stamp.sql, targets: [sqlserver]}
```

- **Events are a registry** (`HookEvents`): `pre_` and `post_` times `create`, `alter`, `drop`, `load`, `backfill`. Adding a kind of hook is one row there plus the place in the planner that fires it. An event the planner does not fire yet (`pre_drop`, `post_drop`: there is no step that drops a whole object) is reserved and refused, so a hook never silently does nothing.
- **Groups** are defined once in `dbdatabuild.yml`, referenced with `use:`, expand in place, do not nest, and their hooks appear in plans as `group.name`. Names must be unique per model after expansion. A group can contain per-target scripts and `targets:` filters, so one group serves every engine.
- **Scripts** are project-relative `.sql` paths (no `..`, no absolute or drive paths, forward slashes); each holds native SQL for one engine and is executed exactly as committed. Validated by the loader, `schemas/model.schema.json` and `schemas/config.schema.json`, and through one conformance corpus (all new fixtures pass in both).
- `HookReader.Resolve(model, config, target, ...)` produces the ordered hooks for one target: groups expanded, other targets dropped, unknown groups and clashes reported once.

Not wired yet at this commit: planning, applying and offline validation of the scripts, index DDL, and index drift.

## 15. Declared indexes: catalog, DDL, planning, apply

- **Catalog** (`CatalogReader`): indexes are now read structurally (uniqueness, key columns in order with `desc`, included columns, and whether the index backs a PRIMARY KEY or UNIQUE constraint) on both engines, and turned into an engine-neutral canonical text `unique=<0|1>;keys=a,b;include=c` (`IndexText`). Plain indexes are physical items of kind `index`; constraint-backed ones are `constraint_index`. SQL Server compression items are kept.
- **DDL**: `CREATE [UNIQUE] INDEX name ON t (cols) [INCLUDE (cols)]` and drop (`DROP INDEX n ON t` on SQL Server, schema-qualified `DROP INDEX s.n` on PostgreSQL, whose index names live in the schema).
- **Planner** (rows `index.added`, `index.changed`, `index.unmanaged`, `index.unsupported`, `index.constraint_name`): a declared index missing on the table is created (safe) after the table's column steps; one that exists under the same name with a different definition is rebuilt (drop and create in one step, **risky**); an index that exists but is not declared is **never dropped** and is listed under "noticed but not done"; so is an index whose declaration was removed from the model. A declared index with the name of a PRIMARY KEY or UNIQUE constraint blocks the model (DDB-434). Fabric has no CREATE INDEX, so a declared index for it blocks (new DDB-322). A view cannot declare indexes. Constraint-backed indexes are never noticed. Index steps leave the shape hash alone, so their post-check is `expect: index:<name>=<canonical>` in the plan, verified against the live catalog at apply (DDB-441 on a mismatch).
- **Decision** (for review): the planner never drops an undeclared index. Dropping something an operator may have added on purpose is not something a model edit should do silently, and there is no way to tell a tool-created index from a DBA's. Removing an index is therefore a manual step, or a hook. If you want a `drop: true` or a `managed_indexes: exclusive` setting, it is a small addition.
- Verified on SQL Server 2022 and PostgreSQL 17 (`Declared_indexes_...`): creation, enforcement by the engine of a unique index, the catalog text of each, the rebuild refused without `--allow-risky` and then applied, an index added by someone else left alone and reported, removing a declaration not dropping anything, and physical differences not counting as drift.

## 16. Hooks in plans and applies

- **Where an event lands** (`Hooked` in the planner; the one place that says what each event wraps): `pre_`/`post_create` around the model's creation (the create step plus its indexes; schema creation is outside), `pre_`/`post_alter` around the changes to an existing object (column and index steps, `alter view`), `pre_`/`post_load` around the routine load, `pre_`/`post_backfill` around a backfill. Hooks of one event run in the order the model lists them (groups expanded in place). A hook whose event the plan does not reach (nothing to alter, no load rendered) does not appear. A blocked or skipped model runs none of its hooks. All structure (and its hooks) comes before all loads (and theirs), as for every plan.
- **Hook steps**: type `hook`, the script text exactly as read from the project, its hash, the event (in `operation`), the hook name (`group.name` for members), its `effect` (`ddl` or `data`) and a risk class from the author's `risk:` (default safe; a risky or destructive hook needs the usual `--allow-risky` / `--allow-destructive <object>` at apply). Reason chain starts with decision-table row `hook.fired`.
- **Checked before planning** (`HookLoader`, DDB-323, in `validate` and `plan`): the script exists inside the project, is not empty, parses with the target's offline validator (ScriptDOM or the PostgreSQL parser), and the event fits the model's kind (a view has no load or backfill). Unknown groups and name clashes after expansion come from the resolver.
- **Apply**: a hook runs through the gate as a DDL or data statement according to its effect and is logged in `run_log` (operation = event, `load_name` = hook name, the script's file hash). A failing hook stops the apply (DDB-440) and the plan resumes after it with `--resume`. A DDL hook that changes the object's shape is recorded as a `schema_version` row with source `hook`, so the operator's own change is not later mistaken for an outside change. The model should then declare what the hook adds, or every plan will reconcile it away (the planner treats the model as the truth); that interaction is the operator's to manage and is not hidden.
- **`run`** treats a safe data hook on `pre_load`/`post_load` as part of a routine load; any other hook makes the plan not routine and `run` refuses with the hook named.
- Verified on both engines (`Hooks_run_around_...`): per-engine scripts picked per target, a group from `dbdatabuild.yml`, order across create and load events, a dry run that shows scripts and runs none, `run` with data hooks, a ddl hook making `run` refuse, a missing script, a script that does not parse and an unknown group refused before planning, and a failing hook recorded as failed then resumed.

## 17. Acknowledging history warnings (`ack history`)

Operator's rule: history inconsistencies are information; the operator can make the warning go away without changing data and decides what is a continuing concern. New code DDB-443 (a warning, never a block).

- `dbdatabuild ack history <model>.<column> --reason "..."` records the acceptance in `block_log` (who, when, why), keyed `DDB-443|model.column|<plan id>`: it is about the one plan whose `backfill_later` decision was never followed by a recorded backfill, so a later plan that decides the same column the same way is a new concern. Nothing in the data changes.
- `report` keeps the facts and adds the acknowledgement to the entry ("Acknowledged by bob on ...: reason"); the entry no longer appears under "needs attention", and `report` exits 0 when nothing else does. A repeated `ack` is a no-op; acknowledging something that is not there says so. The "needs attention" line now names both ways out: plan the backfill, or `ack history`.
- Refactor: `HistoryReader` reads the audit tables and builds the entries once for both `report` and `ack`.
- Tested: pure unit tests (acknowledged stays in the report but stops needing attention; one plan's acknowledgement does not cover a later plan's decision; a `not_backfilled` decision has nothing to acknowledge) and an end-to-end run on both engines (warn, refuse without reason, refuse a nonexistent column, accept, data unchanged, report calm with the reason shown, repeat is a no-op, one `block_log` row).

## 18. Machine-readable output (`--format json`) and the `metadata` command

Operator decision: machine-readable output for as much metadata as the tool can generate.

- **Every command takes `--format text|json`** (a recursive option). In JSON mode standard output holds **exactly one document** and nothing else, with the same exit codes as text mode: `{schema: "dbdatabuild.output/1", command, tool_version, exit_code, ok, data, diagnostics, messages, errors}`. `diagnostics` are the structured form of what text mode prints on standard error (code, severity, title, location, found, supported, fix); `messages` and `errors` keep the human text, so nothing is lost; `data` is the command's payload. `schemas/output.schema.json` describes the envelope and every test validates against it. An unhandled failure is also a document (DDB-900, no stack trace, no data values). JSON mode never prompts: an open question is data (`open_questions`, with options and proposal) and the exit code is 1.
- **How it is built**: commands write to the two text writers they already had; `CommandReport` swaps them for capturing writers in JSON mode, `error.Diag(d)` replaces formatting a diagnostic by hand (35 call sites) and `output.Payload(key, value)` adds a part of `data`. Text mode is unchanged (one cosmetic difference: no blank line between diagnostics).
- **Payloads**: `validate` (counts, config, project and the full model metadata), `matrix` (every construct with status per target, version), `explain` (the descriptor), `loads` and `render` (operations, files with hashes, written/removed, out of date), `init` (every statement), `check` (objects with states and hashes, preview of steps, questions, blocks), `plan` (the whole plan object, files, answers, noticed, questions asked), `apply` and `run` (outcomes, dry-run statements, statement log, success), `report` (applied plans, DDL, loads, objects, column history with acknowledgements, needs attention), `ack` (what was recorded), `metadata` (below). Plans, answers and objects serialize straight from the same records the planner uses.
- **`dbdatabuild metadata`** (effect: offline only, `--format json` for the documents): per model `dbdatabuild.model/1`: kind, grain, targets, files, definition hash, upstream (marked model, source or unknown), per column the declared logical type, nullability, collation, the **native type per target** (or why it cannot be mapped), **lineage and inferred nullability** from the analyzer, expected shape hash per target, renames, every rendered operation with script and resolver hashes, matrix status, findings and parameters, declared indexes, and the resolved hooks per target with script hashes. The project document `dbdatabuild.project/1` has tool and matrix versions, the effective configuration (string semantics, collations, policy, hook groups) and every model with its hash. The planned `publish-metadata` stores exactly these documents in the target.
- Tested: unit tests per offline command (valid against the schema, payload keys, structured diagnostics, usage errors, internal failure document, text unchanged by default) and a run on both engines through init, check, plan, dry run, apply, a refused re-apply, an open question as data, and report.

## 19. Metadata stored in the target (`publish-metadata`, tracking layout 2)

Operator decision: the option to store all metadata as JSON in the target database for introspection.

- **Tracking layout version 2** adds `metadata_document` (kind, subject, `document` JSON, `document_hash`, recorded time, tool version, plan id, commit; append-only, key on kind, subject and time) and two views. The JSON column is `jsonb` on PostgreSQL and `nvarchar(max)` with an `ISJSON` check on SQL Server (`varchar(max)`, no check, on Fabric: unverified). `init` is idempotent and upgrades an older layout by creating what is missing and recording version 2; until then the tool says the layout is version 1 and to run `init` (DDB-505). Tested: a layout-1 database is refused, `init` upgrades it, then planning works.
- **Introspection views**: `metadata_current` (the latest document per kind and subject) and `metadata_columns` (one row per model column: logical type, nullability, collation, the native type for each target, inferred nullability and upstream lineage, unpacked with `OPENJSON` or `jsonb_array_elements`; not created on Fabric, where `OPENJSON` is unverified). Ad hoc SQL works too (`JSON_VALUE(document, '$.definition_hash')`, `document ->> 'definition_hash'`).
- **`dbdatabuild publish-metadata`** (effect: tracking tables only; needs the read and write logins) stores the project document and the model documents `metadata` prints. A document is written only if its hash differs from the latest for that kind and subject, so republishing an unchanged project writes nothing; history is kept. Models can be named. The JSON is a bound parameter (cast to `jsonb` on PostgreSQL), never interpolated. Nothing is stored unless asked.
- **`metadata.store_on_apply: true`** in `dbdatabuild.yml` (default false) makes a successful `apply` also store the project, the models it touched, and the plan itself (`kind = 'plan'`). A failure while storing never turns a good apply into a bad one; it prints a note.
- Verified on both engines: nothing stored until asked, one document per project and model, republishing writes nothing, the stored hash equals the one `metadata --format json` prints, the column view returns the native type per engine, a changed model writes its own document and the project document only, `store_on_apply` writes four documents after an apply, and the layout upgrade path.
- Decision for review: documents are JSON as produced by `metadata` (snake_case, compact, stable order). They carry the tool and matrix versions, so a tool upgrade writes new documents even when nothing in the project changed.

## 20. Lowering is a stage of the tool

Operator decisions: the lowered query is a committed artifact; "cannot be lowered" is a hard error; the binder's rewrites are acceptable.

- **`DbDataBuild.Lowering`** (`PlanLowerer`): the C# port of the research prototype. DuckDB's plan comes from `QueryDescriber.SerializePlan` (offline, external access off, the query text a single string literal because `json_serialize_plan` rejects parameters; a second statement in it is rejected by the function). Differentially tested against DuckDB: every construct of the spike corpus either lowers to a query with the same rows and the same output names, or is refused by name (pinned list of eight), plus tests for window frames (ROWS versus RANGE, row by row with ties), null ordering, CTE order, literals, output types and every refusal.
- **Integration** (`ModelLowering`, `ProjectContext.RenderModel`): `validate`, `render`, `loads`, `plan` and `metadata` lower each model once (cached), lint and transpile the lowered query, and add the artifact `rendered/lowered/<model>/lowered.sql` to the rendered files, so `render --write/--check`, stale detection and removal of a deleted model's file work like every other generated file. Findings point at the artifact; views are transpiled from the lowered query too. `lowering: { enabled: false }` restores the old path. New code DDB-324; DuckDB's own parse errors stay DDB-306 and an undeclared table DDB-218.
- **Two things the end-to-end tests caught**: the plan does not carry the author's output names when no projection is at the top (a model with `AS size` came out as `col1` and the load failed), so names now come from `DESCRIBE` by position; and a duplicate name (a join of two `b` columns) is suffixed `_2`. Both are in the lowerer's differential test now.
- **Behavior on real engines**: with lowering on, all 69 real-engine tests pass, including a new one where `GROUP BY ALL` and `AVG` over integers produce on SQL Server and PostgreSQL exactly the 1.5 and 3.5 DuckDB computes (an integer-averaging engine says 1 and 3).
- **Tests changed because behavior improved**: eight unit tests that used `GROUP BY 1`, `GROUP BY ALL` and undeclared tables to provoke findings now use a construct lowering cannot fix on that engine (`REGEXP_MATCHES` on SQL Server 16) and declare their sources; they assert that lowering-off still reports the old finding.
- Unit tests 873, real-engine tests 69, all passing.

## 21. Subqueries are lowered

Operator decision: correlated subqueries are a hard requirement.

- **Method**: DuckDB's binder decorrelates every subquery (plan shapes in `docs/research/duckdb-plan-lowering/subq_probe.py`), so the lowerer re-correlates: a `DELIM_GET` column is the outer expression that feeds it (written as a `\u0002`-marked reference that is always qualified, whatever the outer block's own naming), the join back to the duplicate-eliminated values and the `GROUP BY` on them are dropped as constant per outer row, and the right side is printed as a nested query inside the outer block. `MARK` becomes `EXISTS (...)` or `x IN (...)` (null semantics preserved, so `NOT IN` against a set with NULLs behaves as in DuckDB), `SINGLE` becomes a scalar subquery, `INNER`/`LEFT` delim joins become `CROSS JOIN LATERAL` / `LEFT JOIN LATERAL ... ON TRUE`. DuckDB's guards are undone too: the single-row check of an uncorrelated scalar subquery, `count(*) = 1` over `LIMIT 1` for an uncorrelated `EXISTS`, the NULL guard around a correlated `count(*)`, and `row_number() <= k` partitioned by correlated values back into `ORDER BY ... LIMIT k`.
- **Scope rules**: the outer block is wrapped first when it cannot hold a subquery (DISTINCT, LIMIT, a set operation, a window), or when the subquery is correlated on an aggregate; an aggregate over an aggregate is wrapped.
- **Refused by name**: `x op ANY/ALL (...)`, row-value `IN`, a correlated subquery over a set operation, a window partitioned by a correlated value, a correlated `LIMIT` with an offset. A lowering whose text DuckDB itself would reject would be worse than a refusal, so every shape added was checked by executing the lowered text.
- **Tests**: 53 subquery forms (and the spike corpus, now with its lateral, `EXISTS` and `IN` cases lowering) lower to queries with the same rows and output names in DuckDB, including `NOT IN` with NULLs, zero-row scalars, nesting three deep, correlation through joins, `HAVING`, `ORDER BY`, `ON`, window `ORDER BY`, and a lowered text that reads like the original. Executed through polyglot on SQL Server 2022 and PostgreSQL 17: 51 and 52 of 53 match; the exceptions are SQL Server's rejection of an aggregate over a subquery and the spike database's case-insensitive collation. An end-to-end model with a correlated scalar subquery, a correlated `EXISTS` inside a `CASE` and a `NOT IN` is planned and applied on both engines with identical rows.
- **Found while doing it**: `BETWEEN` stays one node when its input is a subquery (now lowered); the virtual row-id column of a scan that selects no column; and the lowered text must qualify every inner column of a correlated block, because an unqualified `a` inside a subquery would silently bind to the inner table (a two-level case printed `(a = a)` before the fix).
- Unit tests 928, real-engine tests 71, all passing.

## 22. DISTINCT ON is lowered when its ordering decides the row

- `DISTINCT ON (k) ... ORDER BY k, o` becomes a derived table with `row_number() OVER (PARTITION BY k ORDER BY o) AS rn` filtered to `rn = 1`; the ON keys are removed from the window's ordering (they are constant within a partition). NULL keys form one group in both forms.
- **Why the guard**: DuckDB keeps an arbitrary row among ties and so does a window, so a tie would give different rows on different engines and could not be differentially tested. The ordering must therefore include the **declared grain of every table the query reads** (`ModelLowering` supplies the grain of each model and source). Refused, with the columns to add: an ORDER BY that omits part of the grain, no ORDER BY, an ORDER BY of only the ON columns, a derived table, a table without a declared grain. A join needs the grain of each side in the ordering.
- Found by the end-to-end test: when no projection sits at the top, the plan carries DuckDB's hidden trailing column (the DISTINCT ON key) and `DESCRIBE` names only the visible ones, so the lowered output is cut to the described columns (a regression test covers it).
- Tests: six deciding orderings (several keys, descending, NULLS FIRST, a filter, a join with both grains) give the same rows as DuckDB; six refusals; the lowered text; and a model planned and applied on SQL Server and PostgreSQL with identical rows (NULL as its own group, ties on the ON key resolved by the grain). Unit tests 942, real-engine tests 73.

## 23. Integer `generate_series` and `range` are lowered

- `generate_series(a, b[, s])`, `range(n)`, `range(a, b[, s])` over integer constants become `generate_series(start, stop[, step]) AS series(value)`; `range`'s exclusive end becomes an inclusive one (`range(2, 12, 5)` is `generate_series(2, 11, 5)`). The column is cast to BIGINT, DuckDB's type, because SQL Server's would be INT and arithmetic could overflow differently.
- **SQL Server quirk found on the real engine**: `GENERATE_SERIES` exists from SQL Server 2022 (version 16) but takes **no column alias list** (`AS series(s)` fails with "not a recognized function name"), and names its column `value`. The lowered query always names the column `value` (the author's name is the select alias), and the renderer drops `(value)` after `GENERATE_SERIES(...)` for the T-SQL dialects. PostgreSQL and DuckDB accept the list as written.
- Matrix: new row `fn.generate_series` (SQL Server `min_version: 16`, PostgreSQL native, Fabric unverified), with a spike corpus case. The conformance test is the evidence for the SQL Server and PostgreSQL claims; the spike has not been re-run with the new case.
- Refused (DDB-324, with the reason): date and timestamp series, a zero step, non-constant or NULL arguments, HUGEINT, other table functions, and `UNNEST` (lists have no equal on SQL Server; PostgreSQL's `unnest` would need list literals, which stay refused).
- Tests: 11 series forms (negative and zero-length series, a filter, a join to a table, `SELECT *`) give the same rows and names as DuckDB; the lowered text; refusals; a model with `range(0, 4)` left-joined to a table, planned and applied on SQL Server and PostgreSQL with identical rows. Unit tests 956, real-engine tests 75. Golden render files carry the new matrix hash.

