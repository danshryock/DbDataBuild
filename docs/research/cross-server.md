# Running queries on one server and writing the results to another

Status: investigation, with experiments on real engines (entry 62 of `docs/progress/state-and-apply.md`), and the owner's direction (entry 63). Nothing in the tool changes yet. The aim is to support these
scenarios; this note says what each needs, what was measured, and how to build it in steps. **Read "Direction" first: it supersedes the order and parts of the design further down.**

## Direction (the owner's decisions)

- **No linked servers or foreign servers** (steps A and B below are out): privileged, server-to-server, credentials on the server, and they differ per engine.
- **No DuckDB in the middle** as a load path.
- **The tool moves the data**: read on a source connection, bulk write on a target connection, through the gate (step D).
- **Same-server cross-database is not a feature**: views or synonyms solve it outside the tool.
- **Generalised connections** (step C), and above them a unit for composing flows, here called a **domain** (draft below).

The order becomes: connections, then `transfer`, then domains and the edges between them, then compute at the source.

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


## Domains (draft, after the owner's direction)

An idea to test, not a decision. Several self-contained units in one repository, each with its own sources, models and targets, joined only at **edges** where the persisted output of one is the source of
another.

**Words.** *Connection*: a named endpoint, an engine and a login (`DBDATABUILD_<NAME>_<READ|WRITE>`). *Domain*: a folder with its own models, sources, tests, rendered files and plans, bound to
connections; the unit of ownership and of planning. *Source*: an inbound table, on a connection. *Target*: a connection where a domain's models are stored (and computed). *Flow*: one explicit pairing
of a source with a target. *Publish*: an output offered to other domains; the consumer's *import* is a source whose descriptor is generated from the producer's declared columns. (Other names for a
domain: *context* (domain-driven design: a bounded context), *zone* (lakehouse: landing, curated), *stage* (if the flows are linear), *module*. Not *project*, which already means the whole repository
here and in dbt, and not *workspace* or *pipeline*, which already mean other things.)

```yaml
# dbdatabuild.yml at the repository root: the connections, once, and the domains
connections:
  crm:       { engine: postgres }       # source-only: no write login is ever read for it
  erp:       { engine: sqlserver }
  warehouse: { engine: sqlserver }
  lake:      { engine: postgres }
domains:
  ingest:    { path: domains/ingest }
  analytics: { path: domains/analytics }

# domains/ingest/domain.yml
targets: [warehouse]                    # where this domain stores (and computes) its models; the only connection it may write
sources:
  crm: { connection: crm }              # tables described under domains/ingest/sources/crm/
  erp: { connection: erp }
# domains/ingest/sources/crm/customers.yml
land: { into: warehouse, as: raw.customers, strategy: full_replace }      # this source goes to this target, nothing implicit
# domains/ingest/models/marts/dim_customer.yml
publish: [ { to: lake, as: curated.dim_customer } ]                       # compute once on the target, copy the result to the lake

# domains/analytics/domain.yml
targets: [lake]
sources:
  ingest: { from: domain }              # its sources are what `ingest` publishes: descriptors generated, not written
```

**Pairing is by declaration, never by product.** A source says where it lands (`land`), a model says where it is copied (`publish`) and where it is computed (`targets`, as today); a connection-level
default (`lands_in:`) is shorthand for a long list, not a rule. With sources S1, S2 and targets T1, T2 the tool does nothing for S1-T2 until someone writes it. `validate` reports a source that lands
nowhere and a published model with no consumer as notes.

**Two different "multiple targets".** `targets: [sqlserver, postgres]` on a model means *computed natively on each engine* (portability, the matrix, results that can differ by engine). `publish: [to: lake]`
means *computed once, then copied*: the data is identical on both sides. The first is what the tool does today; the second is fan-out. Both are needed and they must not be confused.

**An edge is a contract.** A published model is persisted by definition (a table kind; a view cannot cross a connection), its declared columns are the contract, and the consumer's source descriptor is
generated from them, so `validate` across the repository catches a producer's column change in the consumer **offline**, before any plan. `publish` could be required for any output that crosses a
domain (a persisted raw copy of a landed source included): that is how raw data fans out and how a consumer is protected from a producer that is mid-rebuild.

**What the draft must still answer** (my reading of the hard parts):

1. *Names.* Models are `schema.table` per project today. A domain gives a third position: DuckDB already parses `catalog.schema.table`, so a domain can be a DuckDB catalog (`ingest.raw.customers`), the offline binder attaches an in-memory catalog per domain, and the lowering (which refuses catalog-qualified names today, DDB-324) learns to map catalog to domain. Physical names on a target must be unique across the domains that write to it: `validate` checks ownership.
2. *Tracking tables.* One tracking schema per target connection records objects by name; with several domains writing to one connection the records need a domain (a column, or a schema each). A choice with consequences for `report`, `check` and drift.
3. *Plans and order.* A plan is per domain and target; an edge makes plans depend on each other. The consumer's plan records the producer's published shape (a stale edge is a stale plan), and `run` over several domains orders them; a failed producer stops its consumers. Plans stay individually hashed and individually approved.
4. *Credentials and least privilege.* Connections are defined once at the root; each domain lists the only connections it may write (`targets`) and may read (`sources`). The environment variable is shared, the permission to use it is not.
5. *Incremental movement.* The first version copies in full. Incremental extraction (a watermark on the source query) comes after, and an edge between domains can use the producer's own key and time column.
6. *Partial fan-out.* Publishing to two targets is two independent transfers; one can fail. Each edge's last good run is recorded in the tracking tables, a consumer's plan reads it, and `diff` between the published table and its copy (row counts, key-based differences) is the reconciliation.
7. *Compatibility.* A repository with no `domains:` is one implicit domain at the root; every command that takes a project works unchanged, and `--domain` selects one otherwise (all by default).

**Order of building**, each step useful alone: (C) connections and source-only connections; (D) `transfer` with the gate's bulk copy, first as `land:` on a source; domains as folders with `--domain`; `publish`
and generated imports with offline validation across domains; run order and edge status; incremental extraction; compute at the source.
