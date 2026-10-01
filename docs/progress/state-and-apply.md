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
