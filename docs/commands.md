# Command reference

Generated from the command tree of `dbdatabuild` (`UPDATE_GOLDEN=1 dotnet test tests/DbDataBuild.Tests.Unit` rewrites it; a test fails when it is out of date). Every command takes `--format json` to give one JSON document on standard output (see `schemas/output.schema.json`). The effect class says what a command may touch: offline only, repository files only, a target read-only, the tracking tables only, or a target's data or definitions (DESIGN.md section 9.1).

| Command | Effect | Purpose |
|---|---|---|
| [`validate`](#validate) | Offline only | Validate config and models (offline) |
| [`render`](#render) | Repo files only (no target connection) | Render load operations and resolvers per target |
| [`loads`](#loads) | Offline only | Print the model x target x operation pairing table |
| [`matrix`](#matrix) | Offline only | Print the support matrix and portability report |
| [`explain`](#explain) | Offline only | Long-form explanation of a diagnostic code |
| [`agent-kit`](#agent-kit) | Repo files only (no target connection) | Install the skill and JSON Schemas an AI coding agent needs to work in a project (lists them unless --write) |
| [`tui`](#tui) | Offline only | Interactive terminal interface: choose, plan and run operations (each action it runs declares its own effect) |
| [`mcp`](#mcp) | Offline only | Model Context Protocol server on standard input and output: the commands as tools for an AI agent, the agent kit as resources (each tool it offers declares its own effect; commands that change a target are offered only with --allow-writes) |
| [`web`](#web) | Offline only | Read-only web interface on this machine's loopback address: project health, lineage, models and their rendered scripts, tests, the support matrix (it runs only commands that read the project) |
| [`new`](#new) | Repo files only (no target connection) | List the project templates built in, or create a ready-to-run project from one |
| [`seed`](#seed) | Repo files only (no target connection) | Run the seeds (DuckDB queries that generate the source data) into a DuckDB file |
| [`load-seeds`](#load-seeds) | Target writes (DDL and/or data, as the plan states) | Create the seeded source tables on a target and fill them from the seeds (prints what it would do unless --apply) |
| [`sample`](#sample) | Offline only | Run models on generated or supplied sample data, offline |
| [`metadata`](#metadata) | Offline only | Print everything the tool knows about the project and its models (use --format json) |
| [`define`](#define) | Repo files only (no target connection) | Generate or update model definition files |
| [`diff`](#diff) | Target read-only | Compare the data of two tables, of one connection or across connections and engines (by digests): schemas, row counts and a key-based row diff (values are read only with --show-values) |
| [`graph`](#graph) | Offline only | Show the dependency graph (which table each model reads), column lineage, or a diagram of it; selectors pick the part to show |
| [`import`](#import) | Target read-only | Export tables and views from the target as mapped models (models/), so models over them bind offline (writes files only with --write) |
| [`test`](#test) | Offline only | Run the project's tests: metadata rules (DuckDB SQL over the metadata views) in tests/metadata/ and model tests (given rows, expected rows) in tests/models/ |
| [`check`](#check) | Target read-only | Preflight findings: drift, blocks, what a plan would do |
| [`plan`](#plan) | Target read-only | Guided planning (writes plan files locally) |
| [`review`](#review) | Offline only | Read plan files (offline, nothing applied): list a project's plans, or show one with its steps, risk, exact statements, report and what applying it would need to be allowed |
| [`publish-metadata`](#publish-metadata) | Tracking tables only | Store the project and model metadata as JSON in the target for introspection |
| [`report`](#report) | Target read-only | Applied plans, DDL and load history, recorded shapes, and what needs attention |
| [`apply`](#apply) | Target writes (DDL and/or data, as the plan states) | Execute exactly the plan's recorded statements |
| [`run`](#run) | Target writes (data only) | Plan + apply for routine loads only (refuses anything else) |
| [`ack`](#ack) | Tracking tables only | Record a human decision (drift, definition change) |
| [`init`](#init) | Tracking tables only | Create the tracking tables and their schema name (prints the script; --apply runs it) |

## validate

Validate config and models (offline).

Effect: Offline only.

| Option | Meaning |
|---|---|
| `--project` `<path>` | Project root (contains models/) |

## render

Render load operations and resolvers per target.

Effect: Repo files only (no target connection).

| Argument | Meaning |
|---|---|
| `models` (several) | Model names (marts.fct_orders), model files, or directories (default: every model) |

| Option | Meaning |
|---|---|
| `--check` | CI: fail if the committed rendered/ files differ from a fresh render; writes nothing |
| `--connection` `<text ...>` | Only these connections |
| `--content` | With --format json: put each rendered file's text in the document (files[].content), so a client can show the lowered and rendered scripts without writing them |
| `--project` `<path>` | Project root (contains models/) |
| `--write` | Write the committed rendered/ files (and remove stale generated ones) |

## loads

Print the model x target x operation pairing table.

Effect: Offline only.

| Option | Meaning |
|---|---|
| `--project` `<path>` | Project root (contains models/) |

## matrix

Print the support matrix and portability report.

Effect: Offline only.

| Option | Meaning |
|---|---|
| `--rewrites` | List the rewrites that make the engines give DuckDB's answers (what each does, where it is required, and what the engine does without it); `rewrites:` in dbdatabuild.yml or a model turns the optional ones off |

## explain

Long-form explanation of a diagnostic code.

Effect: Offline only.

| Argument | Meaning |
|---|---|
| `code` | Diagnostic code, e.g. DDB-214 |

## agent-kit

Install the skill and JSON Schemas an AI coding agent needs to work in a project (lists them unless --write).

Effect: Repo files only (no target connection).

| Option | Meaning |
|---|---|
| `--check` | CI: fail if the installed kit differs from this version's; writes nothing |
| `--dir` `<text>` | Where to install the kit, relative to the project (default: .claude/skills/dbdatabuild) |
| `--mcp` | Also add the dbdatabuild MCP server to the project's .mcp.json (Claude Code starts it: `dbdatabuild mcp --project .`, read-only; other servers in the file are kept). With --write it writes the file, with --check it checks it, otherwise it only says what it would do. |
| `--project` `<path>` | Project root |
| `--write` | Install the files (default: list them and write nothing) |

## tui

Interactive terminal interface: choose, plan and run operations (each action it runs declares its own effect).

Effect: Offline only.

| Option | Meaning |
|---|---|
| `--connection` `<text>` | Connection to work on (default: the project's only default connection) |
| `--project` `<path>` | Project root |

## mcp

Model Context Protocol server on standard input and output: the commands as tools for an AI agent, the agent kit as resources (each tool it offers declares its own effect; commands that change a target are offered only with --allow-writes).

Effect: Offline only.

| Option | Meaning |
|---|---|
| `--allow-apply` | Let an MCP app (a page the host shows the person) apply plans: the person confirms in the app by typing the plan's target; the tools it calls are hidden from the model. Needs the write login in this environment. Independent of --allow-writes, which offers the commands to the model (each run then needs the person's approval). |
| `--allow-writes` | Also offer the commands that change a target or its tracking tables (apply, run, load-seeds, init, ack, publish-metadata). Off by default: a person runs those. |
| `--project` `<path>` | Project root the tools work on |

## web

Read-only web interface on this machine's loopback address: project health, lineage, models and their rendered scripts, tests, the support matrix (it runs only commands that read the project).

Effect: Offline only.

| Option | Meaning |
|---|---|
| `--allow-apply` | Let the page apply plans (a person confirms each one by typing the plan's id and connection; needs the write login in this environment). Off by default: the page reads and plans only. |
| `--port` `<number>` | Port on the loopback address (default: a free one) |
| `--project` `<path>` | Project root to show |

## new

List the project templates built in, or create a ready-to-run project from one.

Effect: Repo files only (no target connection).

| Argument | Meaning |
|---|---|
| `template` | The template to create (omit to list them) |
| `directory` | Where to write it (default: a directory named after the template) |

## seed

Run the seeds (DuckDB queries that generate the source data) into a DuckDB file.

Effect: Repo files only (no target connection).

| Option | Meaning |
|---|---|
| `--out` `<file>` | The DuckDB file to write (default: .dbdatabuild/seed.duckdb) |
| `--project` `<path>` | Project root |
| `--scale` `<nullable`1>` | The variable `scale` the seeds read, usually how many of the main entity (default: the project's own) |
| `--seed` `<number>` | The variable `seed` the seeds read: the same seed and scale give the same rows |

## load-seeds

Create the seeded source tables on a target and fill them from the seeds (prints what it would do unless --apply).

Effect: Target writes (DDL and/or data, as the plan states).

| Option | Meaning |
|---|---|
| `--apply` | Create and fill the tables on the write login (default: print what would happen and connect to nothing) |
| `--connection` `<text>` | Connection to load (default: the project's only default connection) |
| `--project` `<path>` | Project root |
| `--replace` | Drop and recreate a source table that already exists (default: stop at the first table that exists) |
| `--scale` `<nullable`1>` | The variable `scale` the seeds read, usually how many of the main entity (default: the project's own) |
| `--seed` `<number>` | The variable `seed` the seeds read: the same seed and scale give the same rows |

## sample

Run models on generated or supplied sample data, offline.

Effect: Offline only.

| Argument | Meaning |
|---|---|
| `models` (several) | Model names (marts.fct_orders), model files, or directories (default: every model) |

| Option | Meaning |
|---|---|
| `--data` `<path>` | A directory of CSV files named after sources (staging.orders.csv) to use instead of generated rows |
| `--limit` `<number>` | Rows of each result to show (the row count is always complete) |
| `--project` `<path>` | Project root (contains models/) |
| `--rows` `<number>` | Rows generated for each source table |
| `--scale` `<nullable`1>` | For sources that have a seed (seeds/): the scale the seed reads with getvariable('scale'), usually how many of the main entity (default: the project's own) |
| `--seed` `<number>` | Seed of the generator: the same seed gives the same rows |
| `--sources` | Also show the source tables the models read |

## metadata

Print everything the tool knows about the project and its models (use --format json).

Effect: Offline only.

| Argument | Meaning |
|---|---|
| `models` (several) | Model selectors: names, files, directories, `+model`, `model+`, `@model`, `kind:`, `changed:<git ref>`, `exclude:...` (default: every model) |

| Option | Meaning |
|---|---|
| `--project` `<path>` | Project root |

## define

Generate or update model definition files.

Effect: Repo files only (no target connection).

| Argument | Meaning |
|---|---|
| `paths` (several) | Model .sql or .yml files, or directories under models/ (default: every model) |

| Option | Meaning |
|---|---|
| `--accept-inferred` | Accept inferred proposals marked high certainty (names from paths, types from DuckDB, nullability from lineage) |
| `--answers` `<file>` | Answers file for the questions (see schemas/answers.schema.json) |
| `--check` | CI: fail if any definition is out of sync with its query; asks nothing, writes nothing |
| `--project` `<path>` | Project root (contains models/) |
| `--write` | Non-interactive: write the definitions without asking (needs --answers for any open questions) |

## diff

Compare the data of two tables, of one connection or across connections and engines (by digests): schemas, row counts and a key-based row diff (values are read only with --show-values).

Effect: Target read-only.

| Argument | Meaning |
|---|---|
| `table` | The table or view to compare, as schema_name.table_name (a model's table, for example) |

| Option | Meaning |
|---|---|
| `--against` `<text>` | The table or view to compare it with, as schema_name.table_name |
| `--against-connection` `<text>` | Compare with the table on this connection (the same table name unless --against or --against-schema says another); the connections may be on different engines, and only digests of the values are compared |
| `--against-schema` `<text>` | Compare with the table of the same name under this schema name (a development copy, for example) |
| `--columns` `<text ...>` | Compare only these columns (and the key) |
| `--connection` `<text>` | Connection (default: the project's only default connection) |
| `--exclude-columns` `<text ...>` | Leave these columns out of the comparison |
| `--key` `<text ...>` | Columns that identify a row (default: the model's grain or unique key, or a source's grain) |
| `--limit` `<number>` | With --show-values, how many sample rows of each kind of difference |
| `--project` `<path>` | Project root |
| `--show-values` | Read and show values: the smallest and largest of each column and sample rows of each difference (without it only counts are read) |

## graph

Show the dependency graph (which table each model reads), column lineage, or a diagram of it; selectors pick the part to show.

Effect: Offline only.

| Argument | Meaning |
|---|---|
| `models` (several) | Selectors (default: every model): names, paths, `+model`, `model+`, `2+model`, `@model`, `kind:`, `connection:`, `path:`, `changed:<git ref>`, `exclude:...` |

| Option | Meaning |
|---|---|
| `--column` `<text>` | Follow one column (model.column) up to the columns it comes from and down to the columns built from it |
| `--columns` | Also show which column of which table each output column comes from |
| `--diagram` `<text>` | Print a diagram instead of the list: dot (Graphviz) or mermaid |
| `--project` `<path>` | Project root |

## import

Export tables and views from the target as mapped models (models/), so models over them bind offline (writes files only with --write).

Effect: Target read-only.

| Argument | Meaning |
|---|---|
| `tables` (several) | Tables or views as schema_name.table_name, with * and ? as wildcards (default: refresh the source descriptors the project already has) |

| Option | Meaning |
|---|---|
| `--check` | CI: fail if a descriptor differs from the table it describes; writes nothing |
| `--connection` `<text>` | Connection to read (default: the project's only default connection) |
| `--project` `<path>` | Project root |
| `--write` | Write the new and changed mapped models under models/ (without it the command only shows the diff) |

## test

Run the project's tests: metadata rules (DuckDB SQL over the metadata views) in tests/metadata/ and model tests (given rows, expected rows) in tests/models/.

Effect: Offline only.

| Argument | Meaning |
|---|---|
| `tests` (several) | Test names (tests/metadata/naming/x.sql is naming.x) or files (default: every test) |

| Option | Meaning |
|---|---|
| `--kind` `<text>` | Run only tests of this kind: metadata (tests/metadata) or model (tests/models) |
| `--limit` `<number>` | How many violating rows to show per test |
| `--project` `<path>` | Project root |
| `--strict` | Fail on warning-severity tests too |
| `--tag` `<text ...>` | Run only tests with this tag (repeat for several: any of them) |

## check

Preflight findings: drift, blocks, what a plan would do.

Effect: Target read-only.

| Argument | Meaning |
|---|---|
| `models` (several) | Model selectors: names, files, directories, `+model`, `model+`, `@model`, `kind:`, `changed:<git ref>`, `exclude:...` (default: every model that declares the connection) |

| Option | Meaning |
|---|---|
| `--connection` `<text>` | Connection to check (default: the project's only default connection) |
| `--project` `<path>` | Project root |

## plan

Guided planning (writes plan files locally).

Effect: Target read-only.

| Argument | Meaning |
|---|---|
| `models` (several) | Model names, files or directories to plan (default: every model that declares the connection) |

| Option | Meaning |
|---|---|
| `--accept-inferred` | Accept inferred proposals marked high certainty |
| `--answers` `<file>` | Answers file for the questions (see schemas/answers.schema.json) |
| `--backfill` `<text ...>` | model=operation: plan that operation as a backfill (risky; needs --allow-risky at apply); the model has no routine load in this plan (repeatable) |
| `--connection` `<text>` | Connection to plan for (default: the project's only default connection) |
| `--full-refresh` `<text ...>` | model: read an incremental copy's origins from the start instead of from the newest value the destination holds (the merge by unique key makes it safe to repeat; it does not see rows deleted at the origin; repeatable) |
| `--op` `<text ...>` | model=operation: load this model with a non-default operation (repeatable) |
| `--output` `<path>` | Where to write the plan files (default: plans/<connection>/) |
| `--param` `<text ...>` | model.operation.parameter=value: the value of a runtime parameter of a load operation, instead of an answers file (repeatable) |
| `--project` `<path>` | Project root |

## review

Read plan files (offline, nothing applied): list a project's plans, or show one with its steps, risk, exact statements, report and what applying it would need to be allowed.

Effect: Offline only.

| Argument | Meaning |
|---|---|
| `plan` | A plan file (plans/<connection>/<id>.plan.yml); omit to list the project's plans |

| Option | Meaning |
|---|---|
| `--connection` `<text>` | When listing, only the plans of this connection |
| `--project` `<path>` | Project root |

## publish-metadata

Store the project and model metadata as JSON in the target for introspection.

Effect: Tracking tables only.

| Argument | Meaning |
|---|---|
| `models` (several) | Model selectors: names, files, directories, `+model`, `model+`, `@model`, `kind:`, `changed:<git ref>`, `exclude:...` (default: every model) |

| Option | Meaning |
|---|---|
| `--connection` `<text>` | Connection (default: the project's only default connection) |
| `--project` `<path>` | Project root |

## report

Applied plans, DDL and load history, recorded shapes, and what needs attention.

Effect: Target read-only.

| Option | Meaning |
|---|---|
| `--connection` `<text>` | Connection (default: the project's only default connection) |
| `--last` `<number>` | How many recent rows of each history to show |
| `--project` `<path>` | Project root |

## apply

Execute exactly the plan's recorded statements.

Effect: Target writes (DDL and/or data, as the plan states).

| Argument | Meaning |
|---|---|
| `plan` | Plan file written by `plan` (plans/<connection>/<id>.plan.yml) |

| Option | Meaning |
|---|---|
| `--allow-destructive` `<text ...>` | Object (marts.fct) whose destructive steps are allowed; repeat for several objects |
| `--allow-dirty` | Apply from a working tree with uncommitted changes (recorded) |
| `--allow-risky` | Allow the plan's risky steps |
| `--dry-run` | Run every check and print every statement; execute nothing |
| `--project` `<path>` | Project root |
| `--resume` | Continue a plan that stopped part-way, if the live objects are exactly in the recorded intermediate state |

## run

Plan + apply for routine loads only (refuses anything else).

Effect: Target writes (data only).

| Argument | Meaning |
|---|---|
| `models` (several) | Model selectors: names, files, directories, `+model`, `model+`, `@model`, `kind:`, `changed:<git ref>`, `exclude:...` (default: every model that declares the connection) |

| Option | Meaning |
|---|---|
| `--allow-dirty` | Run from a working tree with uncommitted changes (recorded) |
| `--connection` `<text>` | Connection (default: the project's only default connection) |
| `--project` `<path>` | Project root |

## ack

Record a human decision (drift, definition change).

Effect: Tracking tables only.

| Argument | Meaning |
|---|---|
| `kind` | drift (an object changed outside the tool), definition (an incremental model's query changed), or history (a recorded backfill that never happened; name is model.column) |
| `name` | The object (marts.fct) or model name |

| Option | Meaning |
|---|---|
| `--connection` `<text>` | Connection (default: the project's only default connection) |
| `--project` `<path>` | Project root |
| `--reason` `<text>` | Why the change is accepted (required; recorded with your login) |

## init

Create the tracking tables and their schema name (prints the script; --apply runs it).

Effect: Tracking tables only.

| Option | Meaning |
|---|---|
| `--apply` | Run the script on the write login (default: print it for review and connect to nothing) |
| `--connection` `<text>` | Connection to initialize (default: the project's only default connection) |
| `--project` `<path>` | Project root (contains dbdatabuild.yml) |
| `--upgrade` | Bring tracking tables of an older layout (before 4) to this one first: adds the `connection` column to each record table (every existing row gets the connection being initialized), puts it in the primary keys and drops the old views; the script is printed for review like the rest |
