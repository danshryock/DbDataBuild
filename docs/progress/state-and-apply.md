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
