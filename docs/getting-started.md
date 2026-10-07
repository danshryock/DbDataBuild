# Getting started

This walks from nothing to a model built on a database. The first part needs only the executable; the second needs an empty SQL Server or PostgreSQL database you do not mind filling. Every command is listed with its options in `docs/commands.md`; running it against real databases, scheduling and recovery are in `docs/operations.md`.

## 1. Look at a project offline

```
dbdatabuild new starter cafe       # 28 files: a small cafe, sources through staging to two marts (also: retail, chinook, adventureworks)
cd cafe
dbdatabuild validate               # config, models, how each query lowers; "OK: 5 model(s) valid"
dbdatabuild graph --columns        # what reads what, and where each column comes from
dbdatabuild seed                   # generate the source data into .dbdatabuild/seed.duckdb
dbdatabuild sample marts.orders    # run a model on that data and see the rows
dbdatabuild test                   # the project's tests: metadata rules and model tests
dbdatabuild render --write         # write the load scripts for the target (rendered/), to read and commit
dbdatabuild loads                  # each model on each connection: the strategy, and how well the engine supports its query
```

Nothing here opens a database. `validate` prints notes where a construct behaves slightly differently on the engine than in DuckDB (DDB-302); `dbdatabuild explain DDB-302` says what and why.

## 2. What a project is

```
dbdatabuild.yml        connections, defaults, where tracking lives, the string profile
models/**/*.sql        one SELECT per model (DuckDB dialect), optionally with a head: CREATE TABLE schema_name.table_name WITH (...) AS
models/**/*.yml        the definition: name, kind, grain, columns (the contract), indexes, tests
seeds/                 DuckDB queries that make sample source data
tests/                 metadata rules and model tests
rendered/              generated load scripts: commit them
plans/                 plan files from `plan`: read them, apply them
```

A model is a query and a declaration of the columns it returns. `dbdatabuild define models/marts/orders.sql` proposes the declaration from the query and asks before writing; `define --check` fails in CI when they disagree. A table the tool does not build, but your models read, is a **mapped** model (`kind: {type: mapped}`); `dbdatabuild import staging.*` writes them from the database's catalog.

## 3. Build on a database

The tool uses two logins per connection, named by environment variable, and nothing falls back from one to the other: `DBDATABUILD_<CONNECTION>_READ` (plans, diffs, imports) and `DBDATABUILD_<CONNECTION>_WRITE` (apply). The connection name is the one in `dbdatabuild.yml` (`sqlserver` and `postgres` exist without being declared). A login is a connection string for the engine.

```
export DBDATABUILD_SQLSERVER_READ='Server=localhost,1433;User Id=reader;Password=...;TrustServerCertificate=true'
export DBDATABUILD_SQLSERVER_WRITE='Server=localhost,1433;User Id=builder;Password=...;TrustServerCertificate=true'

dbdatabuild load-seeds --apply                    # optional: put the seeded source tables into the database (without --apply it only shows what it would do)
dbdatabuild init --connection sqlserver --apply   # create the tracking tables (without --apply it shows the statements)
dbdatabuild plan --connection sqlserver           # read the database, write plans/sqlserver/<id>.plan.yml
dbdatabuild apply plans/sqlserver/<id>.plan.yml --dry-run
dbdatabuild apply plans/sqlserver/<id>.plan.yml
```

The plan lists every statement, its risk and the reasons, and asks, in the file, any question it cannot answer from the project (a renamed column, a load that needs a decision); answers go in an answers file (`plan --answers`). `apply` runs exactly what the plan recorded and refuses a plan that was edited, is out of date, or no longer matches the database. After the first build, `dbdatabuild run` plans and applies the incremental loads of models that have them. `dbdatabuild report` shows what happened, `diff` compares a table with another (even on another engine), and `ack` records a decision about drift or a changed definition.

## 4. Next

- Several connections, copies between them, parameters per connection, native queries and macros: `DESIGN.md` section 6.5 and `docs/research/cross-server.md`.
- Working with an AI agent: `docs/agents.md`; `dbdatabuild agent-kit --write` installs the skill and schemas in the project.
- The terminal and web interfaces: `docs/interfaces.md` (`dbdatabuild tui`, `dbdatabuild web`).
