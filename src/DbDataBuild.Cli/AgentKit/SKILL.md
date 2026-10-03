---
name: dbdatabuild
description: Work in a dbdatabuild project (it has a dbdatabuild.yml): write or change models, sources, loads, indexes and hooks, validate and render them, preview them on sample data, and plan changes for SQL Server, PostgreSQL or Fabric. Use it for any task that touches models/, sources/, rendered/ or plans/, or that runs the dbdatabuild command.
---

# Working in a dbdatabuild project

dbdatabuild builds analytics tables and views on SQL Server, PostgreSQL and Fabric from SQL written in **DuckDB's dialect**. It is plan-then-apply: nothing reaches a database except through a plan a person has read. Your job is to write models and run the safe commands; a person decides what gets applied.

## Rules that are never negotiable

1. **Never run a command that changes a database or its tracking tables without the person's explicit go-ahead in this conversation**: `apply`, `run`, `ack`, `init --apply`, `publish-metadata`. Everything else is safe to run. `plan` writes only a plan file; `render --write` and `define --write` write only repository files.
2. **Never edit a plan file** (`plans/**/*.plan.yml`). It carries a hash of its own content and `apply` refuses an edited one. Make a new plan instead.
3. **Never edit `rendered/`** (the generated scripts and `rendered/lowered/*`). Change the model and run `dbdatabuild render --write`; commit the result. `render --check` is the CI test that they are current.
4. **Never answer a question about data on the person's behalf**: whether a new column's history was backfilled, whether a missing column is a rename, whether to drop something. Show the question (`plan --format json` lists it under `data.open_questions` with its options and the tool's proposal) and ask. Only after the person decides, write the answers file (`--answers`, format in `schemas/answers.schema.json` next to this file) with exactly their choices.
5. **Never set or print the write login** (`DBDATABUILD_<TARGET>_WRITE`) and never put a connection string in a file. Read-only commands use `DBDATABUILD_<TARGET>_READ`.
6. When something fails, run `dbdatabuild explain DDB-nnn` for the code and read the diagnostic's `fix` before changing anything. Do not work around a refusal by changing what it protects.

## How to run it

Always add `--format json`. Standard output is exactly one document (`schemas/output.schema.json` next to this file): `exit_code`, `ok`, `data` (a closed shape per command), `diagnostics` (each with `code`, `severity`, `location`, `found`, `supported`, `fix`) and the human text in `messages`. Exit codes: 0 ok, 1 findings (read the diagnostics), 2 usage, 70 a tool bug (report it). Null values are omitted from the document.

| Command | Effect | Use it to |
|---|---|---|
| `validate` | offline | check config, models and sources; lowers every query with DuckDB, lints it per target; `data.models` has full metadata |
| `sample [models]` | offline | run models on generated or supplied rows and see the result (`--rows --seed --limit --data <dir> --sources`) |
| `test [names] [--tag t] [--strict]` | offline | run the project's tests: metadata rules in `tests/metadata/*.sql` (DuckDB SELECTs over the `metadata_*` views that return violations); exit 1 if an error-severity rule returns rows |
| `graph [selectors] [--columns \| --column m.c \| --diagram dot\|mermaid]` | offline | the dependency graph and column lineage; what a change to a model or a column reaches |
| `new [template] [dir]` | repo files | list the built-in project templates, or create a ready-to-run example project (sources, seeds, staging, marts, tests) |
| `load-seeds [--target t --scale n --replace --apply]` | target writes | create the seeded source tables on a sandbox target and fill them from the seeds (prints the plan unless `--apply`; write login) |
| `seed [--seed n --scale n]` | repo files | run `seeds/` (DuckDB queries that generate the source data) into `.dbdatabuild/seed.duckdb`; `sample` uses the same seeds |
| `metadata`, `loads`, `matrix`, `explain <code>` | offline | what the tool knows: types per target, load operations, what differs per engine, a code's meaning |
| `define [paths] --check` / `--write --answers f` | repo files | keep the `.yml` definition in sync with the query; `--check` writes nothing |
| `render [--write \| --check]` | repo files | regenerate (or verify) `rendered/` |
| `import-sources [schema.table ...] [--write \| --check]` | database, read-only (files only with `--write`) | export tables and views from the target as `sources/` descriptors; with no arguments refresh the existing ones; the default shows a diff and writes nothing |
| `diff <schema.table> --against <schema.table> \| --against-schema <schema> [--key a,b] [--show-values]` | database, read-only | compare the data of two tables of one target: schema, row counts and a key-based row diff done in the engine; counts only unless `--show-values` (ask the person before using it: it prints real data) |
| `check`, `plan`, `report` | database, read-only | drift and blocks; write a plan file; history |
| `apply <plan>`, `run`, `ack`, `init --apply`, `publish-metadata` | **changes the database** | only with the person's go-ahead; `apply --dry-run` changes nothing |

## Choosing models

Every command that takes models takes selectors: `+model` (it and what it reads), `model+` (it and what depends on it), `2+model`, `@model`, `kind:full`, `target:postgres`, `path:models/marts`, `changed:origin/main` (what differs from a git ref; add `+` for what depends on it), `a,b` for an intersection, `exclude:<selector>`. `graph --column m.c` shows what a column change reaches. After editing a model, `plan changed:HEAD+` plans it and everything downstream.

## The loop for a model change

1. Edit `models/<schema>/<name>.sql` (and `.yml`). The model name is the path: `models/marts/fct_orders.sql` is `marts.fct_orders`.
2. `validate` until it is clean. Read warnings too: DDB-302 (approximated on an engine), DDB-304 (unverified, Fabric), DDB-223 (a key-based load has no index on its key), DDB-225 (a load slices by a column the engine cannot filter early: an aggregate or window output, a LIMIT). DDB-225 is advice about cost: tell the person and let them choose another column or strategy; do not rewrite their query to make it go away, and do not silence it yourself.
3. `sample <model>` and look at the rows. This is the fastest way to check logic; it runs in DuckDB only.
4. `define --check`; if the declared columns are out of sync (DDB-420), `define --write` with answers, or edit the `columns:` by hand.
5. `render --write`, then commit `rendered/`.
6. `plan --format json`. If it has `open_questions`, stop and ask (a parameter of a reload or backfill can be given as `--param model.operation.parameter=value` once the person has chosen the value). Otherwise give the person the plan path and a summary of the steps (`data.plan.steps`: type, risk, description). Risky and destructive steps need their own flags at apply time, and that is the person's call.

## Writing a model

A model is a single `SELECT` in DuckDB's dialect, plus a YAML definition. `columns:` is **required** and is the declared output: name, type, `nullable: false` where it can never be NULL.

```yaml
name: marts.fct_orders
kind:
  type: incremental_by_unique_key      # view | full | incremental_by_unique_key | incremental_by_time_range
  unique_key: [order_id]               # must equal grain for incremental_by_unique_key
grain: [order_id]                      # the columns that identify one row; required for incremental kinds
targets: [sqlserver, postgres]         # optional; default_targets in dbdatabuild.yml otherwise
columns:
  - {name: order_id, type: BIGINT, nullable: false}
  - {name: amount, type: "DECIMAL(14, 2)"}
indexes:                               # only what you declare is created; never inferred from unique_key
  - {name: ux_fct_orders_order_id, columns: [order_id], unique: true}
```

- **Sources** (tables the tool does not build) are described in `sources/<schema>/<table>.yml` with `name`, `columns`, optional `grain`. A query can only read declared sources and other models (DDB-218 otherwise). Do not type descriptors out by hand when the table exists: `import-sources` exports the real columns, types and nullability with the read login (preview first, then `--write`), keeps a `grain` you wrote, and maps unlimited text (`varchar(max)`, `text`) to a bare `VARCHAR` and xml/json to `VARCHAR` too, and leaves out only a column with no representation at all (DDB-226; declare it yourself). It also exports the table's `indexes:` and `foreign_keys:` (read-only facts: never write them to change the database). `import-sources --check` finds descriptors that have gone stale (DDB-227).
- **Kinds**: `view` is DDL only; `full` reloads everything; `incremental_by_unique_key` upserts by key; `incremental_by_time_range` loads slices after `MAX(time_column)` minus `lookback` (needs `time_column`, a DATE or TIMESTAMP). Other load shapes (a backfill, a merge) are named operations under `loads:`; see `schemas/model.schema.json` next to this file.
- **Types**: declare exact types. Types with no faithful equivalent are refused (HUGEINT, unsigned integers, structs, lists). A bare `VARCHAR` is unlimited text (`nvarchar(max)` on SQL Server, `text` on PostgreSQL) and stays unlimited from source to output unless a length is declared or written (`CAST(x AS VARCHAR(n))`). `sum(int)` is HUGEINT in DuckDB, so write `CAST(sum(x) AS BIGINT)`.
- **Strings** follow the project's profile (`string_semantics` in `dbdatabuild.yml`): by default case-insensitive, accent-sensitive, trailing spaces ignored. Every engine in use needs a collation in config (DDB-312). Do not rely on a comparison that the profile does not promise.
- **What cannot be written** (DDB-324, the message names the construct): `UNNEST`, list and struct constructors, `USING SAMPLE`, `x op ANY/ALL (subquery)`, row-value `IN`, a window partitioned by a correlated value, date or timestamp series. A `DISTINCT ON` is accepted only when its ORDER BY includes the declared grain of every table it reads. `GROUP BY 1`, `GROUP BY ALL`, `USING`, `NATURAL JOIN`, `SELECT *` and macros are fine: DuckDB expands them before the tool looks at the query. `generate_series` and `range` work over integers only.
- **The tool rewrites a few things for the engines** (`length` trailing spaces, `round` of a double, `TRY_CAST` of a string, integer `sum`): you write the DuckDB meaning and the rendered scripts compute the same. `dbdatabuild matrix` lists what still differs (for example `TRY_CAST('12.7' AS INTEGER)` is 13 in DuckDB and NULL on the engines).
- **Indexes and keys**: a merge on a key never needs an index or constraint, but a key without an index scans the table on every load (DDB-223). Whether a key is enforced as unique is the person's choice; declare `unique: true` only if they want it. `lint_ignore: [DDB-223]` silences advice for a model.
- **Hooks** (`hooks:` per model, `hook_groups:` in config) are native SQL files run before or after plan steps (`pre_`/`post_` `create`, `alter`, `load`, `backfill`). They run exactly as committed, so treat a new one as risky.

## Project tests

`tests/metadata/<name>.sql` is a rule: one DuckDB SELECT over the `metadata_*` views that returns the violations (no rows = pass). Put settings in comments at the top: `-- description: ...`, `-- severity: error|warning`, `-- tags: a, b`. Views: `metadata_columns` (model and source columns, native types per target), `metadata_models`, `metadata_sources`, `metadata_upstream`, `metadata_lineage`, `metadata_native_types`, `metadata_indexes`, `metadata_source_indexes`, `metadata_source_foreign_keys`, `metadata_loads`, `metadata_hooks`, `metadata_index_advice`, `metadata_current` (the raw JSON documents). Run `test --format json`; a failing error-severity rule is DDB-601, a rule that cannot run DDB-602. Do not weaken or delete a rule to make a model pass without asking the person.

`tests/models/<model>.yml` tests a model's logic on rows you give it (the file name is the model name; settings are `# description:`, `# severity:`, `# tags:` comments at the top):

```yaml
cases:
  - name: keeps null amounts
    given:
      staging.orders:                 # each table the query reads (sources and models); unlisted tables are empty
        - {order_id: 1, amount: 10.00}
        - {order_id: 2, amount: null}   # a plain null is NULL; "null" in quotes is text; a column left out is NULL
    expect:                           # rows the query must return (cast to the declared types); {rows: [...], ordered: true} to check order
      - {order_id: 1, amount: 10.00}
      - {order_id: 2, amount: null}
    assert: SELECT * FROM result WHERE amount < 0    # optional: violations over the table `result`
```

`test --kind model` runs them in DuckDB (the query as written, no target). A failure shows `_diff` rows (`missing` or `unexpected`). Write the expected rows from what the model *should* do, not by copying its output: a test copied from the output cannot fail.

## Reading a plan

`plan --format json` returns `data.plan.steps`. Each step has `type` (`ddl`, `load`, `backfill`, `hook`, `track`), `risk` (`safe`, `risky`, `destructive`), `reasons`, the exact `text`, and for loads the `parameters` and resolver result. Summarize for the person: what is created, altered or dropped, what loads, and every risky or destructive step with its reason. `apply` needs `--allow-risky` for risky steps and `--allow-destructive <object>` for each object with a destructive step; never add those flags yourself.

## Facts to keep straight

- DuckDB SQL is what you write; the target SQL is generated. Never write T-SQL or PostgreSQL syntax in a model.
- A change to an incremental model's query blocks `run` (DDB-431) until a person plans again or accepts it with `ack definition`. A change someone made in the database blocks that object (DDB-430) until `ack drift` or a restore.
- `lowering: { enabled: false }` and `lint: { indexes: false }` in `dbdatabuild.yml` switch features off for the whole project: ask before changing config.
- The human interface is `dbdatabuild tui`; it runs the same commands. You do not need it.
- Anything not covered here: the `schemas/` folder next to this file has the exact shape of every file and every command's output, and `dbdatabuild explain <code>` explains every diagnostic.
