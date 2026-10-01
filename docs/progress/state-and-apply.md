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
