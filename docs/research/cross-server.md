# Running queries on one server and writing the results to another

Status: investigation, with experiments on real engines (entry 62 of `docs/progress/state-and-apply.md`), and the design the owner and I settled on (entries 63 and 64). Nothing in the tool
changes yet. The aim is to support these scenarios. Sections: the direction, what the code has, what was measured, the scenarios, then the **terms and design** (from "Terms" down), which is the current
proposal; where the scenario table below speaks of links or DuckDB, the direction above already ruled them out.

## Direction (the owner's decisions)

- **No linked servers or foreign servers.** **No DuckDB in the middle** as a load path. **Same-server cross-database queries are not a feature** (views or synonyms, outside the tool).
- **The tool moves the data**: read on one connection, bulk write on another, through the gate.
- **A clean break**: no compatibility with the current layout; the schemas, keys and directories below replace today's.
- **One root, no grouping above it**: the **project** stays the unit; there is no domain, context or workspace layer.
- **Reusable, general terms** over special ones (a fan-in is not a special feature; see Parameters).

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

Scenarios 1 to 3 and 6 are out by the owner's direction (links, same-server cross-database, DuckDB in the middle). 4 and 5 are what the design below builds (5 as an ordinary model on the origin connection, then a copy); 7 stays a documentation note.

| # | Scenario | Mechanism | Fit today | What it needs |
|---|---|---|---|---|
| 1 | Sources in another database or schema of the same server | the engine's own cross-database name | none (the names are refused) | a source location (`database`), a physical name at render time; SQL Server only (PostgreSQL needs 2) |
| 2 | Sources on another server of the same engine, joined in the destination's script (**pull**) | linked server (SQL Server), `postgres_fdw` (PostgreSQL) | none | as 1, plus a prerequisite the tool checks but does not create (a link is privileged and holds a login) |
| 3 | Compute on the source server, store the result on another (**push**) | a link, written from the source | none | works on PostgreSQL; **not** on SQL Server inside a transaction: do it as a pull from the destination, or move the rows with 4 |
| 4 | Any engine to any engine, or servers that cannot see each other | the tool moves rows: read on the source connection, bulk write on the destination | none | named connections, a `transfer` step, a bulk path in the gate (below) |
| 5 | Compute on the source engine (a big aggregate there, only the result travels) | 4 with the model's query rendered for the **source** engine | none | the model says where it computes (`compute`) and where it is stored; types are mapped for the store |
| 6 | DuckDB computes, reading sources through its extensions | DuckDB attaches the sources, the tool writes the result | partly (`sample` runs DuckDB; `load-seeds` writes DuckDB rows through the gate) | not recommended as the main path (below) |
| 7 | Read replica for sources, primary for the result | two logins | already two logins, but plan, drift and `diff` read the *target's own* state through the read login | not a data-movement feature; a replica as the read login makes drift checks lag; say so in the documentation |

## Terms

| Term | Means |
|---|---|
| **Connection** | A named database endpoint: an engine and a login (`DBDATABUILD_<NAME>_<READ\|WRITE>`). Data lives only on connections. A connection with no write login can only be read. |
| **Engine** | The SQL dialect of a connection (`sqlserver`, `postgres`, `fabric`). The matrix, the rules and the type mapping are per engine. This is what the old word "target" mostly meant. |
| **Model** | One named thing in the project, with declared columns, that lives on a connection. Every kind below is a model. |
| **Kinds that build from SQL** | `view`, `full`, `incremental_by_unique_key`, `incremental_by_time_range`, as today: SQL in DuckDB's dialect, run on the model's connection. |
| **`mapped`** | A model with no body: it **maps** an existing physical table into the project's namespace. The tool never creates or alters it; it declares the columns, optionally the physical name, keys, indexes and tests, and the live table is checked against it. What the old "source" was. A `sources/` folder is only a place to keep them. |
| **`copy`** | A model with no SQL: `from: <model>`, the connection it lives on, and a strategy. Its columns come from the model it copies. The copy is always persisted (a table). |
| **Project** | The folder with `dbdatabuild.yml`: its models, tests, rendered files and plans. Unchanged. |
| **Parameter** | A named value. The same word at every level; the scope says whose it is. Declared under `parameters:` in the place it belongs (the project, a connection, a model or folder default, an operation); referenced with its scope as a prefix: `${connection.store_id}`, `${project.region}`, `${model.system}`. A load operation's runtime parameters (`@watermark`) are the same concept at the operation scope and keep their SQL form (below). |
| **Connection group** | A named set of connections that run the same application (below). |

**The rule behind all of it: a query runs against one connection.** SQL in a model reads only models that live on the model's own connection; a reference to a model on another connection is refused,
by name, with the fix (copy it first). Moving data between connections is always an explicit `copy`. That removes the question of joins across servers and of "sources times targets": a copy names
one origin and one connection, and nothing flows that is not written. A transformation happens at the origin (an ordinary model on that connection, then copied) or at the destination (an ordinary
model reading the copy); a copy itself has no SQL.

**Landing raw data and sending on a result are both copies.** Whether the origin is a `mapped` table or a model the project builds is a property of the origin. Fan-out is several copies of the same model, one per destination connection.

**`connections` on a model** replaces `targets`. On a built model it still means "built natively on each of these" (portability: the engine's rules apply, results can differ by engine). On a `copy` it
means "copied to each": nothing is computed, so the data is the same on all. (One copy per destination is also fine and is the explicit form.)

## Configuration and inheritance

Every setting that is a default for models can be given at three levels, **nearest wins**: `dbdatabuild.yml` at the root (`defaults:`), `_dbdatabuild.yml` in any folder (applies to every model beneath it),
and the model's own file.

```
dbdatabuild.yml                       # connections, defaults, policy, string_semantics, rewrites
models/
  crm/
    _dbdatabuild.yml                  # connection: crm_pg ; kind: mapped ; schema: crm
    customers.yml                     # a mapped model: columns, grain, physical name
    orders.yml
  warehouse/
    _dbdatabuild.yml                  # connection: warehouse ; schema: raw
    customers.yml                     # kind: copy ; from: crm.customers ; strategy: full_replace
    marts/
      _dbdatabuild.yml                # kind: full ; rewrites: { fidelity: native }
      dim_customer.sql
      dim_customer.yml
```

- **Merge rules** (one table, tested): a scalar is replaced; a map is merged by key; a list is replaced, unless the key ends in `+` (`tags+:`), which appends.
- **Inheritable**: `connection(s)`, `kind` and its settings (strategy, key, time column), `schema`, `rewrites`, `lint`, `policy`, `tags`, `hooks` (additive by default). **Not inheritable**: `name`, `columns`, `grain`, `from`.
- **Provenance is never hidden**: `validate` prints each model's effective settings and the file each came from (the project already prints its effective configuration), and the metadata JSON carries the same.
  Action at a distance is the risk of any inheritance; this is the control.
- **Names**: the `schema` part of a model's name is the `schema:` setting (inheritable; by default the first folder under `models/`), so folders can be organised by system, layer or anything else.
  Two models cannot have one name; a mapped model's physical name may differ from its project name (`physical: {schema: dbo, table: CUSTOMER_MST}`).
- The underscore file is not a model: the loader and the orphan check skip it.

## Parameters

One concept, four scopes. The key is `parameters:` wherever it is declared; where it is declared decides whose it is.

| Scope | Declared in | Referenced as | Used for |
|---|---|---|---|
| project | `dbdatabuild.yml` | `${project.name}` | values the whole project shares |
| connection | the connection's entry | `${connection.name}` | values that differ per connection (a store id, a region): fan-in |
| model | a model file, a `_dbdatabuild.yml` or the root `defaults:` (so it inherits like any setting) | `${model.name}` | values that differ per model or folder |
| operation | a load operation (as today) | `@name` in the operation's SQL | runtime values: a watermark, a backfill start |

- **A reference carries its scope, so nothing shadows anything**: `${connection.region}` and `${model.region}` are two values.
- **Two ways a value is used, and they are not the same**: a project, connection or model parameter is substituted **into configuration** (a column added by a copy, the value of a slice, a schema name) when the project
  loads, and `validate` shows the result. An operation parameter is **bound** by the driver at run time and never written into statement text. A value that reaches a statement (a slice's value in the
  `DELETE` and the `INSERT`) is bound, not concatenated: the principle that statement text never contains values holds for every scope.
- Model SQL stays plain DuckDB: a parameter is not interpolated into a query (my default; see the open points). The seeds' `scale` and `seed` are DuckDB variables of the seed queries and stay as they are.

## Fan-in: one application, many deployments

A **connection group** names the connections that run the same application, with each member's **parameters**:

```yaml
connections:
  store_017: { engine: postgres, parameters: { store_id: "017", region: eu } }
  store_018: { engine: postgres, parameters: { store_id: "018", region: eu } }
  warehouse: { engine: sqlserver }
groups:
  stores: [store_017, store_018]            # or a pattern: store_*
```

```yaml
# models/stores/orders.yml          a mapped model on a group: the same table exists on every member
connection: stores
kind: mapped
columns: [ ... ]
# models/warehouse/orders_all.yml   one copy per member, into one table
connection: warehouse
kind: copy
from: stores.orders
strategy: full_replace
slice: { column: store_id, value: "${connection.store_id}" }     # which rows are this member's; the column is added if the data does not have it
```

- One declaration and one dimension of expansion (the group's members) into one destination table: the only product there is, and the intended one.
- **Parameters are general**: a value per connection that a copy can add as a column (`add: { region: "${connection.region}" }`), use as the slice of a replacement, and, later, any other place a setting differs per connection
  (a schema name, a hook argument). **No new column is dictated**: if the systems already carry a distinguishing column (a site code in the data), the slice names it and the parameter only says
  which value is this member's; if they do not, the slice adds one from a parameter. The destination key must include the slice column (`validate` checks).
- **Isolation**: a member's run replaces only its slice (delete where the slice column equals the value, then insert), so one failing or offline member leaves the others' data untouched. The check
  that every row of a member's extract carries that member's value is made before the swap: a member cannot write into another's slice.
- **Version skew**: `plan` compares each member's live table with the shared declaration and reports every member that differs by name; `on_mismatch: fail | skip` (default `fail`) decides, never silently.
  Each member's last good run is recorded in the tracking tables, and a skipped or failed member shows in `report`.
- Credentials are per connection (`DBDATABUILD_STORE_017_READ`), which is fine for tens of members; hundreds is a later problem.

## Execution (unchanged from the earlier proposal)

A copy plans as a `transfer` step: the plan records the origin connection, the exact read (hashed like any statement), the staging and destination DDL, and the origin's declared and live shape (a
changed origin is a stale plan). The rows are read through `ReadSession` on the origin connection, converted **by declared logical type** (not the driver's: Npgsql's `DateOnly`, .NET's 28-digit
`decimal`; a value that does not fit is an error naming the column), and written to a staging table on the destination through a new gate statement, `GateStatement.BulkCopy`, implemented with
`SqlBulkCopy` and PostgreSQL's binary `COPY` (190k rows/s measured, against 10k rows/s for the gate's parameterised inserts). The log records the destination, the hash of the read and a row count, never
values. The swap or merge from staging into the destination table is then the existing strategy (`full_replace`, the incremental ones) inside one destination transaction, with indexes, drift and `ack` as
today. The read of the origin is one query on one connection (a snapshot where the engine offers one). Staging has a deterministic name, so an interrupted run is run again. `GateInvariantTests` is
extended to the new statement; there is still no other write path.

## What the clean break changes

| Today | Becomes |
|---|---|
| `targets:`, `default_targets:` | `connection(s):` on a model; `defaults: { connection: ... }` |
| target = engine kind; one read and one write login per engine | named connections, each with its engine; `DBDATABUILD_<CONNECTION>_<READ\|WRITE>` |
| `sources/` directory, source descriptors, `import-sources` | `mapped` models anywhere under `models/`; `import` generates mapped models from a connection's catalog |
| `--target`, `rendered/<engine>/`, `plans/<engine>/` | `--connection`, `rendered/<connection>/`, `plans/<connection>/` |
| the matrix per target | the matrix per engine (unchanged), looked up through the connection's engine |
| name = path under `models/` (`schema.table`) | `schema` from the setting (default: first folder); name unique in the project |
| `string_semantics`, `rewrites`, `policy` at the project only | defaults that any folder or model can override |

Everything that reads "target" in the code, the schemas, the documentation and the skill changes; this is a major version (a new release line), not a patch.

## Order

1. **Connections and the rename** (`target` to `connection` and `engine`), model kinds `mapped`, inheritance with `_dbdatabuild.yml` and provenance, `import`. Larger than it sounds: it touches every place that treats a target as an engine. No data movement yet.
2. **`copy` between two connections**: `BulkCopy` in the gate, the transfer step in plan and apply, value conversion, staging and swap, origin shape in the plan; real-engine tests (SQL Server and PostgreSQL both ways: nulls, text, dates, decimals up to 38, large rows).
3. **Connection groups, connection parameters and slices**: fan-in with per-member replacement, version-skew reporting.
4. **Incremental extraction** (a watermark on the origin read); **compute at the origin** (a model that lives on the origin connection, then copied, already covers it: this is only convenience).

## Still open

1. Whether a `copy` may select columns or filter rows (my default: no; do it at the origin with a model).
2. Whether a project, connection or model parameter may be used inside a model's SQL (my default: no; the SQL stays plain DuckDB, so a per-connection value goes in through a copy).
3. Whether `mapped` models are checked against the live table at every `plan` (my default: yes, as drift is now) or only by `import --check`.
4. Names for the commands (`import`, `copy`) and for `slice`.
