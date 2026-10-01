# Review summary: milestones 4 to 6 (state, safety, planning, apply)

Written for you to review. The decision log with the detail and evidence is `docs/progress/state-and-apply.md` (entries 1 to 9, one per commit). The research you asked for first is in `docs/research/duckdb-plan-lowering/README.md`.

## Where things stand

The command surface in DESIGN.md 9.1 is complete: all thirteen commands exist and work against real SQL Server 2022 and PostgreSQL 17.

| Command | Effect class | What it does now |
|---|---|---|
| `init` | tracking tables only | prints the idempotent tracking-table script; `--apply` runs it through the gate |
| `check` | target read-only | object states (missing, in sync, not tracked, changed outside the tool), what a plan would do |
| `plan` | target read-only (writes plan files) | refuses a project that does not validate, asks every open question, writes `plans/<target>/<id>.plan.yml` and `.plan.md` |
| `apply` | target writes | verifies the plan against the live target, takes the application lock, executes exactly the plan's statements, records everything |
| `run` | target data writes | plan and apply for routine loads only; refuses anything else |
| `ack` | tracking tables only | records a person's acknowledgement of one specific drift or definition change |
| `report` | target read-only | applied plans, DDL and load history, recorded objects, what needs attention |

Nine commits since the render and loads work (`1d9ebc5` is the research, then `b8703ac` to `4c2c26d`) plus this summary. Source is about 8,900 lines and tests about 7,800. **743 unit tests and 53 conformance tests pass.** The conformance tests run on SQL Server 2022 and PostgreSQL 17 in local throwaway containers (`scripts/test-engines.sh up`, then `eval "$(scripts/test-engines.sh env)"`); without the environment variables they are reported as skipped, never silently passed.

To try it by hand: set `DBDATABUILD_SQLSERVER_READ` and `DBDATABUILD_SQLSERVER_WRITE` (or `_POSTGRES_`) to connection strings, then `init --apply`, `render --write`, `plan`, `apply plans/<target>/<id>.plan.yml --dry-run`, `apply ...`.

## What was built

- **Hashes and tracking tables** (`DbDataBuild.State`): shape, physical and script hashes over a canonical text; the seven tracking tables as one logical definition with per-engine DDL.
- **Logins, the mutation gate, the statement log** (`DbDataBuild.Execution`, the only project that references a database driver): credentials from environment variables with no fallback between read and write; every write goes through one gate that checks the statement's effect class, logs before executing, and does not execute if it cannot log. A source-scan test enforces that nothing else touches a driver.
- **Catalog reader, drift classification, DDL generation**: live shapes on both engines; a type table whose predicted shapes were checked against the real catalogs attribute by attribute for 20 columns covering every mapped type.
- **The planner and the decision table**: a pure function (no database needed to test it) with the decision table as data in `matrix/decision-table.yml` (32 rows; a test requires each planner row to cite a real test).
- **Plan files**: YAML with a SHA-256 content hash; `apply` refuses anything that is not byte-for-byte what `plan` wrote.
- **Apply**: allowances, base-hash and resolver staleness checks, the application lock, per-step logging into `ddl_log`, `run_log`, `schema_version` and `migration_log`, failure handling, and `--resume`.

## Decisions I made that you should look at

1. **Tracking-table layout differs from the illustrative DDL in DESIGN.md 12.** Log ids are tool-generated GUIDs instead of `IDENTITY`; there is an added `tracking_version` table; every table has a primary key; Fabric uses `varchar` because it has no `nvarchar`. Reason in each case is in log entry 1.
2. **Logical-to-native type table** (log entry 4): TINYINT maps to `smallint` everywhere (T-SQL `tinyint` is unsigned), `VARCHAR(n)` maps to `nvarchar(n)` on SQL Server (`nvarchar(max)` above 4000), and unsigned integers, HUGEINT, an unsized VARCHAR and anything above `DECIMAL(38)` are refused with a new code (DDB-321) rather than guessed. The design had no table for this.
3. **Text columns always carry the profile collation** from `string_semantics.collations`, never the database default, so the expected shape is known offline. A missing collation entry for a target is DDB-312.
4. **A plan with blocks is still written** for the models that are not blocked; blocks and skips are printed and the exit code is 1. The alternative (refuse any plan while any block exists) is a one-line change in `PlanCommand`.
5. **Acknowledgements are keyed by the exact hash** (`DDB-430|object|shape hash`), so a later, different change blocks again, and a repeated `ack` is a no-op. `--reason` is required.
6. **Views are tracked by the hash of the applied `CREATE OR ALTER VIEW` text** in `ddl_log`, and a view step has no predicted shape hash (column types are derived by the engine). After a view step the check is that the view exists, not its column names.
7. **`run` cannot ask questions.** A load with an unanswered runtime parameter is not routine, so `run` refuses and points to `plan`.
8. **Planning loads only the default operation.** There is no `--operation` flag yet.
9. **Dry run needs the read login but not the write login**, and a dirty working tree only warns on a dry run but refuses a real apply (`--allow-dirty` records it).
10. **The read guard is deliberately conservative** (single SELECT or WITH, no data-changing keywords, no dollar-quoting). It can refuse a harmless query; the read login's permissions are the real enforcement. The tool's own queries pass it.

## Things that went wrong or surprised me (all fixed, all with tests)

- The hash canonical text collided for a null and the literal text `~` (found by a test before anything was recorded).
- My first read guard hid a statement behind a backtick on PostgreSQL; found while writing the guard's bypass tests and fixed.
- PostgreSQL leaves a failed transaction aborted; without a rollback after a failed load the failure could not be recorded. A mutation test (removing the rollback) confirmed the scenario test catches it.
- Npgsql refuses UTC-kind timestamps for zone-less columns; tracking timestamps are UTC wall time with Kind Unspecified, strictly increasing at millisecond precision.
- The PostgreSQL test image (alpine) has no `en_US.utf8`, so its tests use the `C` and `POSIX` collations.

## Known gaps and risks (not hidden)

- **Not built**: hook steps, the downstream warn-or-block policy for history inconsistencies (the report itself exists), creating unique constraints and indexes from the model (`unique_key` does not create a constraint), the live-catalog collation check in `check`, JSON Schemas for plan files (they are verified by their own parser and hash).
- **Fabric is unverified throughout**: no Fabric engine has been available. Its init script, type table and plan statements are generated and parse, but have never run.
- **Never tested on Windows or against a managed instance**; integrated security is wired but untested. SQL Server 2022 only (not 2019 or 2025); PostgreSQL 17 only.
- **The invariant test is a source scan, not a call-graph analysis**; a determined reflection call would not be seen.
- **`apply` is single-connection and sequential.** There is no parallelism, and a connection that dies mid-load leaves the plan resumable only if the live state matches the recorded intermediate state.
- **Resolver parameters assume one resolver per load**, and resolver values are compared as text.
- The `report` and `check` commands print tables for people; they have no machine-readable output yet.
- DuckDB plan lowering (your first request) is researched and documented but **not built**; the recommendation and open questions are in the research README.

## Suggested next steps

1. Review decisions 1 to 10 above and tell me which to change.
2. Constraint and index creation from the model (needs a design decision: should `unique_key` create a unique constraint?). Backfill, `--op` and the column-history report were added after this summary was first written (log entries 10 and 11).
3. A JSON Schema for plan files and a machine-readable output mode for `check` and `report`.
4. If you want the DuckDB plan-lowering idea pursued, start with enum-seeded PIVOT and macros as the research recommends.
5. A Windows build of the FFI library and single-file publish (still open from the spike).
