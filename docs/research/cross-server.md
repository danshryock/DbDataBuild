# Running queries on one server and writing the results to another

Status: investigation, with experiments on real engines (entry 62 of `docs/progress/state-and-apply.md`). Nothing in the tool changes yet. The aim is to support these scenarios; this note says
what each needs, what was measured, and proposes how to build it in steps.

## Words

Today three things are the same place, the **target**: where a model's query *runs* (compute), where its result *lives* (store), and where its inputs live (sources). A target is an engine kind
(`sqlserver`, `postgres`, `fabric`) with one read login and one write login in the environment (`DBDATABUILD_<TARGET>_<READ|WRITE>`), so two servers of one engine cannot even be told apart.
Cross-server work pulls those three apart.

## What the code already has, and what stands in the way

| | State |
|---|---|
| Read path | `ReadSession` over a read login, with `ReadGuard` (SELECT only). Returns rows as objects; it is built for catalog reads and `diff`, not for streaming a table. |
| Write path | `MutationGate` is the only way to send a statement to a target (`GateInvariantTests` scans the source). It takes **text statements**; the one bulk form is `GateStatement.BulkInsert`, a multi-row `INSERT` with a parameter per value (used by `load-seeds`). The log records hashes and counts, never values. |
| Targets and logins | by engine kind; no named connections; a source-only connection (read, never written) has no place. |
| Sources | `sources/<schema>/<table>.yml` has no location: a source is a table of the target. Descriptors are read from the target by `import-sources`. |
| Names in a model | `schema.table`. A reference with a catalog (`crm.public.customers`) is refused by the lowering (DDB-324, checked), and a four-part SQL Server name does not parse in DuckDB at all. A logical-to-physical name mapping at render time does not exist. |
| Plan and apply | one target per plan; the plan holds the exact statements and the shapes it assumed of the target's objects (drift is checked against that one target). |
| Offline binding | DuckDB binds every query against declared columns only, so remote tables need only descriptors, not access. |

## What was measured

All on local containers (SQL Server 2022 and 2025, PostgreSQL 17), 1,000,000 rows of four columns unless stated. Local numbers say what the mechanism costs, not what a network does.

| Mechanism | Result |
|---|---|
| **SQL Server, other database on the same instance** (three-part name), inside `BEGIN TRANSACTION` with `XACT_ABORT ON` | works, pull and push |
| **PostgreSQL, other database**, plain `SELECT * FROM otherdb.s.t` | `cross-database references are not implemented`: needs `postgres_fdw` or a schema |
| **SQL Server linked server, pull** (the script runs on the destination and reads `LINK.db.dbo.t`), inside a transaction | works; 1M rows in **8.5 s** |
| **SQL Server linked server, push** (the script runs on the source and writes `LINK.db.dbo.t`) | works outside a transaction; **inside one it fails** (`Msg 7391 ... unable to begin a distributed transaction`): the rendered load scripts are transactional, and a Linux SQL Server has no MSDTC (a Windows one might, configured) |
| **PostgreSQL `postgres_fdw`** (stock image; `CREATE EXTENSION`, `IMPORT FOREIGN SCHEMA`), pull and push inside transactions | both work; a rolled-back push leaves the remote table untouched; there is no two-phase commit, so a failed commit after the remote one cannot be undone |
| **DuckDB in the middle** (`ATTACH` PostgreSQL with the official extension, SQL Server with the community `mssql` extension, `CREATE TABLE ... AS SELECT` into the other server) | works: PostgreSQL to SQL Server, 1M rows in **5.1 s**; SQL Server to SQL Server **7.2 s**; the query is DuckDB's, so no engine rewrites apply; text became `nvarchar(max)`, `numeric(10,2)` became `decimal(10,2)` |
| **Client copy, reading PostgreSQL** with Npgsql | 200k rows into memory in 0.4 s |
| **Client copy, writing SQL Server with `SqlBulkCopy`** | 200k rows in **1.0 s** (about 190k rows/s) |
| **Client copy, writing with parameterised multi-row `INSERT` batches** (what `GateStatement.BulkInsert` is) | 50k rows in **4.8 s** (about 10k rows/s): **twenty times slower**, so a transfer needs a real bulk API, not `BulkInsert` |
| Driver types | Npgsql returns `date` as `DateOnly`, SQL Server's driver wants `DateTime`: the values need converting by declared type, and .NET `decimal` holds 28 digits while SQL Server and PostgreSQL go to 38 |

Reproduce: `scripts/test-engines.sh up mssql mssql2025 pg`; a linked server is `sp_addlinkedserver ... @provider='MSOLEDBSQL', @datasrc='<container ip>'` plus `sp_addlinkedsrvlogin`; DuckDB is
`INSTALL postgres; INSTALL mssql FROM community; ATTACH '...' AS src (TYPE postgres); ATTACH '...;Encrypt=true;TrustServerCertificate=true' AS ms (TYPE mssql);`.

## The scenarios, and what each needs

| # | Scenario | Mechanism | Fit today | What it needs |
|---|---|---|---|---|
| 1 | Sources in another database or schema of the same server | the engine's own cross-database name | none (the names are refused) | a source location (`database`), a physical name at render time; SQL Server only (PostgreSQL needs 2) |
| 2 | Sources on another server of the same engine, joined in the destination's script (**pull**) | linked server (SQL Server), `postgres_fdw` (PostgreSQL) | none | as 1, plus a prerequisite the tool checks but does not create (a link is privileged and holds a login) |
| 3 | Compute on the source server, store the result on another (**push**) | a link, written from the source | none | works on PostgreSQL; **not** on SQL Server inside a transaction: do it as a pull from the destination, or move the rows with 4 |
| 4 | Any engine to any engine, or servers that cannot see each other | the tool moves rows: read on the source connection, bulk write on the destination | none | named connections, a `transfer` step, a bulk path in the gate (below) |
| 5 | Compute on the source engine (a big aggregate there, only the result travels) | 4 with the model's query rendered for the **source** engine | none | the model says where it computes (`compute`) and where it is stored; types are mapped for the store |
| 6 | DuckDB computes, reading sources through its extensions | DuckDB attaches the sources, the tool writes the result | partly (`sample` runs DuckDB; `load-seeds` writes DuckDB rows through the gate) | not recommended as the main path (below) |
| 7 | Read replica for sources, primary for the result | two logins | already two logins, but plan, drift and `diff` read the *target's own* state through the read login | not a data-movement feature; a replica as the read login makes drift checks lag; say so in the documentation |

## Design

**1. Named connections.** `connections:` in `dbdatabuild.yml` gives names to connections and says which engine each is; `sqlserver`, `postgres` and `fabric` stay as implicit connections of
themselves, so every existing project is unchanged:

```yaml
connections:
  crm:       { engine: postgres }     # DBDATABUILD_CRM_READ; no write login: a source-only connection
  warehouse: { engine: sqlserver }    # DBDATABUILD_WAREHOUSE_READ and _WRITE
default_targets: [warehouse]
```

The login variable is already `DBDATABUILD_<NAME>_<READ|WRITE>`, so it is the same rule with a longer list of names. The matrix stays per **engine**. A connection with no write login can be read and
never written, and the gate refuses it (the existing refusal, not a new one).

**2. Where a source lives.** A source descriptor gets `connection:` (default: the model's own target). With two kinds of access, chosen per source or per connection pair:

- **`link`** (scenarios 1, 2): the rendered script reads the remote table by a physical name the project gives (`physical: {sqlserver: "[SRC].[src].[dbo].[orders]"}`, or a PostgreSQL foreign
  schema). The lowering maps the logical name (`crm.orders`) to the physical one at render time; the tool never creates a linked server or a foreign server. `plan` and `validate` check that the name
  answers (`SELECT 1 FROM <name> WHERE 1 = 0` through the read login) and report a missing or unreachable link by name. A matrix row says what holds: SQL Server pull works in a transaction, push does
  not; PostgreSQL both; Fabric unverified. Filters on a linked-server table travel poorly (the whole table can cross); the documentation says so and shows `OPENQUERY` as the way to push a filter.
- **`copy`** (scenarios 4, 5): see 3.

**3. A `transfer` step.** The one new mechanism. It runs a query on connection X and lands its rows in a staging table on connection Y, then the **existing load strategies** (`full_replace`,
`incremental_by_unique_key`, `incremental_by_time_range`) take the staging table into the real one inside a destination transaction, so atomicity, keys, indexes, drift and `ack` are all as today.
A model gets `kind: {type: transfer, connection: crm}`: its SQL is in DuckDB's dialect over **crm's** source descriptors, it is lowered and rendered for crm's *engine* (compute there), and its
declared columns decide the destination table (types mapped for the store, as now). A plain copy of a table is `SELECT * FROM crm.public.customers`. Pieces:

- *Plan*: the step records the source connection, the exact query text (hashed like every statement), the staging and destination DDL, and the source tables' shape hashes, so a source that changed
  is a stale plan. DDL is planned and reviewed as always; the rows move at apply.
- *Gate*: `GateStatement.BulkCopy` (a data statement), implemented with `SqlBulkCopy` and PostgreSQL's binary `COPY`; the log records the destination table, the hash of the source query and a row
  count, never a value (`BulkValues` already exists for this). Reads on the source connection go through `ReadSession`; `GateInvariantTests` extends to both. No other write path.
- *Values*: rows are converted by the **declared** logical type, not the driver's (the `DateOnly` and 28-digit decimal findings), and a value that does not fit is an error naming the column, not a
  truncation. Text, binary and time zones need an explicit table, as `SourceTypes` has for catalogs.
- *Consistency*: the destination is atomic (staging, then swap or merge in one transaction); the read of the source is one query on one connection, on a snapshot where the engine offers one
  (`READ COMMITTED SNAPSHOT`, a repeatable-read transaction on PostgreSQL). Staging has a deterministic name, so an interrupted run is simply run again.
- *Size*: rows flow through the machine running the tool. For the volumes dbdatabuild is for that is fine (about 190k rows/s measured with bulk APIs), and where it is not, scenario 5 (compute at
  the source, ship the result) or scenario 2 (the servers talk to each other) is the answer.

**4. DuckDB in the middle is not the main path** even though it works and is fast: the writes would bypass `MutationGate` (its own writer), it depends on a community extension downloaded at run time
and pinned to a DuckDB version, and it moves the *inputs* of a query to the client. It stays a possible later `sample --from` for developers with explicit consent (the principle that an agent works
without data holds), not a load path.

**5. What does not change**: lowering, the rules, the matrix per engine, the plan hash and the approval flow, `MutationGate` as the only writer, the statement log without values, the agent
seeing no row values.

## Order

| Step | What | Size | Risk |
|---|---|---|---|
| A | Same-server other database (SQL Server three-part names): a source location, physical names at render time, a matrix row, a check that the name answers, a real-engine test on SQL Server 2022 and 2025 | small | low: no new execution path |
| B | `link` sources: linked server and `postgres_fdw` physical names, the transaction note, the same check, real-engine tests (the experiments above, as tests) | small to medium | the setup is the user's; push on SQL Server is refused with the reason |
| C | Named connections (config, logins, source-only connections), `connection:` on sources, `import-sources --connection` | medium | touches every place that treats target as engine; mostly mechanical, covered by the existing suites |
| D | `transfer`: `BulkCopy` in the gate (both engines), the step in plan and apply, the model kind, value conversion, staging and swap, source shape in the plan | large | the gate invariant, value fidelity, and what a half-finished run leaves; the biggest test surface (real engines, large rows, nulls, text, dates, decimals up to 38) |
| E | Compute at the source (`compute` separate from `store`) | medium on top of D | type mapping per engine pair |

A and B are the smallest steps and give same-engine users most of what they ask for. C and D are the real cross-engine feature. I would not start with D.

## Decisions for the owner

1. **Which scenarios are wanted first.** My guess is 1 and 2 on SQL Server and 4 for PostgreSQL to SQL Server; correct this if the real case is different (for instance results pushed to a PostgreSQL destination from SQL Server sources, which is 4 or 5, not 2).
2. **May rows pass through the machine that runs the tool?** If not (data residency, volume), only the link scenarios (1, 2, 3) and compute-at-the-source with a link are available.
3. **Fabric.** Cross-warehouse queries and shortcuts exist, but Fabric has never been run, so nothing here can be promised for it.
4. **Naming.** `connections:` as above, or `targets:` extended (the section that already carries `version`). The first keeps "target" meaning an engine for the matrix; the second is one concept fewer. I prefer the first.
5. **Who creates links.** The tool checks them and documents the setup; it should not create linked servers or foreign servers (privileged, hold credentials). Say if a managed `link` step is wanted.
