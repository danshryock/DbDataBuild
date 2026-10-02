# DESIGN.md: DbDataBuild (explicit SQL transformation tool)

Status: design baseline for the first build. Written to be read by Claude Code and by humans. Items marked **[VERIFY]** are assumptions that must be checked against current docs or tests before code depends on them. Items marked **[DECIDE]** are open decisions for the owner (none are open at the moment; see section 17).

---

## 1. Purpose

A .NET command-line tool that builds and maintains analytics tables and views on **SQL Server Enterprise** (primary) and **Microsoft Fabric Warehouse** (secondary), from SQL model files written in **DuckDB dialect** and transpiled per target.

Model definitions follow a minimal subset of SQLMesh's style. Execution is **explicit, plan-then-apply, and predictable**. A person must always be able to tell, before anything runs, exactly which scripts will execute against a target and why.

## 2. Non-negotiable principles

These are invariants. Each one must have automated tests (section 15).

1. **Nothing touches a target without an explicit request.** No startup migrations, no auto-heal, no implicit operations, no side effects from read-only commands.
2. **State informs reports and blocking. It never triggers or selects actions.** Tracking tables are read for visibility and for preflight blocks only.
3. **Plan, review, apply.** `plan` is read-only toward the target. It asks open questions, then produces a complete plan. `apply` executes exactly what the plan records, nothing more.
4. **Every statement that will run is shown in full, with the reason it is needed.** Plans are human-readable documents first.
5. **Stateless by default.** What to do is derived from the git repo and the target's current catalog and data. Watermarks come from target data. Tracking tables are append-only records, not control inputs.
6. **Configuration errors explain themselves.** Stable diagnostic codes, file and line, what was found, what is supported, suggested fix. Never a stack trace as primary output.
7. **Supported states are enumerated data.** The support matrix and the planning decision table are tested data, not hidden branching logic.
8. **Row data does not leave the target through tool output.** Diagnostics, logs, and plans contain schema, SQL, hashes, and counts only, never row values (see section 14). The one accepted exception is declared runtime parameter values and watermark resolver results, which are logged (section 6.6).
9. **No virtual environments, no implicit backfills, no automatic change categorization.**

## 3. Non-goals (first version)

- Python models, SCD Type 2 snapshots, virtual environments, automatic breaking-change categorization, a docs website, a package ecosystem, cross-project meshes, and a built-in scheduler (SQL Agent or CI invokes the tool).
- Schema names and object names as parameters (environment-dependent identifiers). Rendered SQL uses each model's fixed, declared names, and the environment is selected by the target connection. To be revisited in a later design.
- Engines other than SQL Server, Fabric, and PostgreSQL. PostgreSQL is included early, as a deliberately non-T-SQL target, to expose multi-target pitfalls in the shared code (section 8). The design must *allow* further targets.

## 4. Development ground rules (for Claude Code)

- **Schema-only development.** The dev environment has no network path to production. Never request, log, or commit production data. Fixtures are synthetic.
- **Never connect to any host except the configured local or ephemeral test engine.**
- Write tests first for each invariant and diagnostic. Run the full test suite before proposing a change.
- Keep commands, diagnostics, matrix entries, and decision-table rows as **data** with tests enumerating them.
- Any new code path that can execute a non-query statement must go through the mutation gate (section 9.3). A test enforces this.
- Prefer small, reviewable changes. Update this document when a decision changes.
- Do not add dependencies without noting them in section 5 and verifying the license.

## 5. Technology

| Concern | Choice | Notes |
|---|---|---|
| Language/runtime | C#, .NET 10 (LTS; moved from .NET 8, which leaves support in November 2026, so that Terminal.Gui 2.x can be used) | Single-file, self-contained publish for win-x64 and linux-x64 |
| CLI | `System.CommandLine` | Interactive prompts via a small abstraction (Spectre.Console is acceptable) |
| SQL parse/transpile | `polyglot-sql` (Rust, MIT) via its C FFI library (`polyglot-sql-ffi`) | Bound with P/Invoke (`DbDataBuild.Sql`). Native library built from the pinned commit by `scripts/build-polyglot.sh` (verified in the milestone 1 spike, `spike/RESULTS.md`). The FFI crate is not on crates.io, so it is built from the repo. Dialects are `tsql`, `fabric`, `postgresql`, `duckdb`; SQL Server maps to `tsql` |
| Offline DuckDB | `DuckDB.NET.Data` (MIT, verified) | Synthetic data, local execution, result typing |
| T-SQL syntax check | `Microsoft.SqlServer.TransactSql.ScriptDom` (MIT, verified) | Per-version parsers **[VERIFY]** Fabric coverage. Syntax only: the spike showed it accepts `GROUP BY 1` and `[1,2,3]`, which SQL Server then rejects |
| Database access | `Microsoft.Data.SqlClient` (MIT, verified) | Integrated and Entra auth; `SqlBulkCopy` for test loads. Server is given as `host,port` |
| PostgreSQL access | `Npgsql` (PostgreSQL license, verified permissive) | Binary `COPY` for test loads **[VERIFY]** |
| YAML | `YamlDotNet` | Plus published JSON Schemas for editor validation |
| Tests | xUnit, a snapshot library (e.g., Verify) for golden files (plain golden files with `UPDATE_GOLDEN=1` are used so far) | `JsonSchema.Net` **7.0.4** (MIT) for schema conformance tests only. The conformance project (`tests/DbDataBuild.Tests.Conformance`) also uses `Microsoft.Data.SqlClient` (MIT), `Npgsql` (PostgreSQL license), `DuckDB.NET.Data.Full` (MIT) and `Xunit.SkippableFact` (MS-PL), and only there: product code must not reference a database driver until the mutation gate exists (a test checks it). Do not upgrade to 9.x without review: it ships under the Open Source Maintenance Fee EULA, not a plain open-source license |
| Containers | Optional only | Must work with no container runtime (Windows dev has no nested virtualization) |

### Naming

The product is **DbDataBuild**, a sibling of DbDataSync. As with `dbdatasync`, the CLI is the full lowercase product name.

| Item | Name |
|---|---|
| CLI | `dbdatabuild` |
| Config file | `dbdatabuild.yml` |
| Tracking schema (default) | `dbdatabuild` |
| Diagnostic prefix | `DDB-` (short form, easier to read in error messages) |
| .NET namespaces / projects | `DbDataBuild.*` |

All of these derive from a single `ProductInfo` constant in the code base, so a later rename is mechanical. **[VERIFY]** the name for collisions (NuGet, GitHub, trademark) before the first public release.

### Solution layout

```
src/
  DbDataBuild.Cli/                            # command definitions, effect-class headers, prompts
  DbDataBuild.Core/                           # project model, plan model, questions, diagnostics
  DbDataBuild.Models/                         # model definition (YAML) loader and validation, model graph
  DbDataBuild.Define/                         # `define`: inference from a query, questions, definition writer and splice editor, diff
  DbDataBuild.Sql/                            # polyglot FFI binding, AST helpers, hashing, matrix linter
  DbDataBuild.Targets/                        # ITarget + SqlServer, Fabric and PostgreSQL implementations: strategy loaders, offline validators, renderer
  DbDataBuild.Targets.DuckDb/                 # canonical dialect engine, synthetic data runner
  DbDataBuild.State/                          # tracking tables access, schema/physical/definition hashes
  DbDataBuild.Testing/                        # ephemeral database manager, differential runner
tests/
  DbDataBuild.Tests.Unit/
  DbDataBuild.Tests.Conformance/              # DuckDB vs target differential tests (needs a SQL Server test instance)
  DbDataBuild.Tests.Golden/                   # plan documents, dry-run output, diagnostics
matrix/                 # support matrix data (YAML)
sources/                # committed schema exports of upstream tables that are not models (section 6.5)
schemas/                # JSON Schemas for config, models, sources, answers, plans
docs/diagnostics/       # generated diagnostic catalog
rendered/               # committed, rendered load operations per target (section 6.6)
```

## 6. Model definitions

SQLMesh-style concepts (kinds, grain, declared columns), with **no custom grammar**. A model is a pair of files with the same base name:

- `<name>.sql`: a single SELECT in **DuckDB dialect**. Plain SQL with no header, no macros, and no templating. It runs as-is in DuckDB.
- `<name>.yml`: the model definition, written in a strict YAML subset (section 6.3).

```yaml
# models/marts/fct_orders.yml
name: marts.fct_orders
kind:
  type: incremental_by_unique_key
  unique_key: [order_id]
grain: [order_id]
targets: [sqlserver, fabric]
columns:
  - name: order_id
    type: BIGINT
    nullable: false
  - name: customer_id
    type: BIGINT
  - name: order_date
    type: DATE
  - name: amount
    type: DECIMAL(14, 2)
  - name: discount_code
    type: VARCHAR(20)
```

```sql
-- models/marts/fct_orders.sql
SELECT
  o.order_id,
  o.customer_id,
  o.order_date,
  o.amount,
  o.discount_code
FROM staging.orders AS o
```

Upstream references are plain table names, resolved against the model graph and the committed source descriptors. There is no `ref()`.

### 6.1 Supported kinds (first milestone)

| `kind.type` | Meaning | Load behavior |
|---|---|---|
| `view` | View over upstream | DDL only (create/alter via plan). `apply` issues no data statements |
| `full` | Table fully reloaded | Transactional delete+insert of contents (table swap can be added later as a named load operation, section 6.6) |
| `incremental_by_unique_key` | Upsert by key | Watermark-free: rows selected per model query, applied via delete+insert or MERGE per target. Requires `unique_key` |
| `incremental_by_time_range` | Time-sliced incremental | Range derived from `MAX(time_column)` in the target minus `lookback`, never from a state table. Requires `time_column` |

Kinds define table semantics and supply the **default** load operation. Explicit, additional load operations are declared with `loads` (section 6.6).

### 6.2 Definition rules

- `columns` is **required for all models**. It is the declared output schema. It drives the shape hash, the plan's DDL, and offline validation with no database connection.
- `targets` lists which engines the model must be valid for. The project default applies if omitted. The linter evaluates the matrix against these targets.
- `grain` is required for incremental kinds and is checked against `unique_key`.
- `name` is explicit in the file and must match the path convention (`models/marts/fct_orders.yml` is `marts.fct_orders`). A mismatch is a diagnostic.
- Every `.sql` needs a `.yml` and vice versa. An orphan of either kind is a diagnostic (DDB-1xx) that points to `dbdatabuild define`.
- Unknown keys are errors (DDB-1xx), not ignored.
- Renames are never inferred. They are declared explicitly in the definition:

  ```yaml
  renames:
    - from: cust_nm
      to: customer_name
  ```

### 6.3 Strict YAML subset

YAML has well-known pitfalls (for example, unquoted `no`, `on`, or `2026-10-12` being coerced to booleans or dates by some parsers). The loader therefore applies a strict subset:

- **All scalars are read as strings** and validated against the JSON Schemas in `schemas/`. Nothing is coerced by YAML type rules.
- **Unknown keys and duplicate keys are errors.** Anchors, aliases, merge keys, and custom tags are not allowed.
- Enumerated values are lowercase snake_case. SQL types are written as plain strings (`DECIMAL(14, 2)`).
- The JSON Schemas (`schemas/model.schema.json`, `schemas/config.schema.json`) are associated with the files by glob (`models/**/*.yml`, `dbdatabuild.yml`; see `.vscode/settings.json`) so editors, and Claude Code, validate definitions as they are written. The C# loaders are authoritative because they produce the diagnostics, and they also check what a schema cannot (name against path, grain against `unique_key`, column references). A conformance corpus runs through both, so the schemas and loaders cannot drift apart: structural errors must fail in both, semantic-only errors must pass the schema and fail the loader. Editors read unquoted `false` or `16` as typed values, so the schemas accept both forms where the loader reads strings. `schemas/answers.schema.json` covers answers files (section 10.1); `schemas/plan.schema.json` covers plan files (section 10.2); it cannot verify the content hash.
- Every error carries file, line, and column, and uses the diagnostic format in section 14.

**Bodies have no macros or templating.** Load strategies (section 6.6) wrap the body, for example `SELECT * FROM (<body>) AS b WHERE b.order_date >= @start`. Predicate pushdown into bodies with aggregates or windows is not rewritten for the author: slicing the finished query is always correct, and where an engine cannot apply the filter early the tool says so (DDB-225, section 6.6.2). If wrapping proves too costly for specific models, revisit with standard bind-parameter placeholders declared in the YAML, still without custom syntax.

### 6.4 Parsing strategy

- The definition is loaded with `YamlDotNet` under the strict rules above, then validated against its JSON Schema. The tool has **no custom grammar parser**.
- The body is parsed with polyglot as DuckDB dialect into an AST. **The AST is used for analysis (dependencies, lineage, lint, hashing) and for transpilation.** For any emitted SQL that is not produced by transpilation, prefer minimal text substitution over regeneration, to preserve comments and hints. **[VERIFY]** round-trip behavior.
- If polyglot fails to parse a file, report a diagnostic. Do not fall back to silent best-effort.

### 6.5 Definition generation and maintenance (`dbdatabuild define`)

The definition file can be generated, or updated, from the query body plus a guided walkthrough. The command is **offline and repo-only**: it never connects to a target, and it writes only the definition (`.yml`) files it is pointed at. It never modifies a `.sql` file.

**Usage**

```
dbdatabuild define models/marts/fct_orders.sql            # walkthrough, shows diff, asks before writing
dbdatabuild define models/marts --answers define-answers.yml --write   # non-interactive
dbdatabuild define --check                                # CI: exit non-zero if any definition is out of sync with its body; writes nothing
```

**Inputs:** the `.sql` body, the existing `.yml` if any, the project model graph, source descriptors (committed schema exports), and the project profile (string semantics, default targets).

**What it infers** (offline, no target connection). Every inferred value is a *proposal with evidence*. Accepting it is an explicit answer (a keypress interactively, `accept: inferred` in an answers file, or `--accept-inferred`, which is recorded in the diff output). `--accept-inferred` covers only high-certainty proposals (names from paths, types resolved by the DuckDB describe, nullability from lineage); kind, grain and unique key, targets, time column, renames, and load operations always need an explicit answer. There are no silent defaults.

| Definition field | How it is proposed | Question if unclear |
|---|---|---|
| `name` | Path convention (`models/marts/fct_orders.sql` becomes `marts.fct_orders`) | Always confirmed |
| `columns` (names, types) | The body is resolved by asking DuckDB to describe the query (`LIMIT 0`) against an empty schema built from upstream declared columns and source descriptors. Names and DuckDB types are mapped to logical types | If a type can't be resolved, or a mapping is lossy for a target, ask |
| Nullability | Proposed from lineage (for example, a column passed straight through from a `NOT NULL` upstream column by an inner join) | Otherwise ask per column |
| `grain` / `unique_key` | Candidates from upstream keys, `GROUP BY` columns, and `DISTINCT` usage | Choose one (required for incremental kinds) |
| `kind` | Never inferred | Options listed with one-line consequences |
| `time_column` (time-range kinds) | Candidates among `DATE`/`TIMESTAMP` output columns | Choose one, plus lookback |
| `targets` | Project default | Confirm. The matrix linter runs during the walkthrough and shows which constructs limit which targets |
| Column collations | Project profile default | Ask only for exceptions |
| `renames` | A missing declared column plus a new column of the same type and position | Question: rename or drop-and-add |
| `loads` | The kind supplies a default operation | Ask whether to declare additional operations (section 6.6) |

**Questions** use the same mechanism as planning: stable IDs (`Q-define-<model>-<field>`), context, explicit options, an answers file, and in non-interactive mode all unanswered questions are listed at once with the YAML to add.

**Generate and replace semantics**

- **No definition present:** a new `<name>.yml` is created beside the `.sql` file, in canonical formatting with a fixed key order.
- **Definition present:** the command computes a field-level diff and edits the file by **minimal text splices** located with the YAML parser's source positions, so comments, key order, and the author's own formatting survive. It does not re-serialize the whole file. **[VERIFY]** `YamlDotNet` does not round-trip comments when re-serializing, which is why splices are used. Fields are treated by class:
  - *Human-authored* (`name`, `kind`, `grain`, `unique_key`, `targets`, `renames`, `loads`, comments) are never changed without a question showing old and new values.
  - *Derived* (`columns`) are updated per column through questions (added, removed, type changed, nullability changed). New entries use canonical formatting.
  - *Unknown keys* are errors, never dropped silently.
- **Idempotent:** a second run on an in-sync definition produces no diff.
- **The `.sql` body is never opened for writing.** A hash check confirms it is unchanged after the command.
- **Nothing is written before confirmation.** The unified diff is shown first. Interactive mode asks for confirmation; non-interactive mode requires `--write`.
- **Safe write:** the file's hash is checked against what was read, the write goes to a temp file then an atomic rename, and the command refuses if the file changed in between.

**Relationship to planning:** `dbdatabuild plan` detects a definition that no longer matches its body (declared `columns` versus resolved query output) and refuses to plan that model, pointing to `dbdatabuild define`. Planning never proceeds from a stale declared schema. History dispositions (whether a new column's history was backfilled) are **not** definition questions, since they depend on target state. They remain planning questions.

**Effect class:** repo files only. Tests assert zero database connections, `.sql` byte-equality, and idempotency (section 15.5).

### 6.5.1 How `define` works (as built)

The text above is the design. This records what the implementation does, including the decisions the design left open.

**Source descriptors.** "Committed schema exports" are `sources/<schema>/<table>.yml`, one per upstream table that is not a model, in the same column shape as a model definition. `name` must equal the path under `sources/` with `/` replaced by `.` (as for models), a name cannot be both a source and a model, and an optional `grain` feeds grain candidates. The schema is `schemas/source.schema.json`; `validate` checks them. `import-sources` exports them from a target (below); they can still be written by hand.

**Source descriptors**

```yaml
# sources/staging/orders.yml
name: staging.orders
grain: [order_id]
columns:
  - name: order_id
    type: BIGINT
    nullable: false
  - name: amount
    type: DECIMAL(14, 2)
```

**Importing sources** (`import-sources`, effect: target read-only; with `--write` it also writes `sources/` files). `import-sources [schema.table ...]` reads the columns, native types, nullability and primary key of the tables and views the patterns match (`*` and `?` are wildcards, case-insensitive; no pattern refreshes every descriptor the project has) through the read login: the same catalog queries planning uses, plus the schema list and primary keys. It runs no query against the data, needs no tracking tables, and changes nothing in the target. Native types map to logical types through an enumerated table (`SourceTypes`), with a fit for each: *exact* (`int` is `INTEGER`, `numeric(14,2)` is `DECIMAL(14, 2)`, `varchar(n)` and `nvarchar(n)` are `VARCHAR(n)`), *widened* (`tinyint` is `SMALLINT`, `money` is `DECIMAL(19, 4)`, `char(n)` is `VARCHAR(n)`, `binary(n)` is `BLOB`, `datetime` is `TIMESTAMP`) and *lossy* (`datetime2(7)`, which keeps one digit more than `TIMESTAMP`). Non-exact fits are printed per column and appear in the JSON. A column with **no honest logical type** (`text`, `varchar(max)`, an unconstrained `numeric`, `xml`, `jsonb`, arrays, user types) is left out of a generated descriptor and reported (DDB-226): a guessed length would flow into every model that selects the column. A column the committed descriptor already declares is kept as written. The live table wins for columns, types and nullability (a descriptor is an export); a committed `grain` is kept and a primary key only seeds a new descriptor's grain. Output is a diff per descriptor, `new`, `changed`, `unchanged`, `stale` (a descriptor for a table the target does not show: DDB-228, never deleted) or `invalid` (a file that does not load is never overwritten). Without `--write` nothing is written; `--write` writes through `DefinitionFile` (hash-checked, atomic); `--check` writes nothing and fails with DDB-227 per descriptor that differs, for CI. A table that is a model, the tracking schema, and names that cannot be a path are skipped and listed. Descriptors are generated with fixed key order and no comments, so a hand-written comment in a refreshed file is lost; use the diff before `--write`.

**Inference** (offline). For each model the query's tables are resolved against models and sources (DDB-218 if one is missing), an empty in-memory DuckDB schema is built from their declared columns, and DuckDB *describes* the query (it is never run; external access is disabled first, so a query cannot read files or the network while it is bound). Lineage and nullability come from polyglot's `analyze_query` with the same schema. Type proposals: a column passed straight through keeps its upstream declared type, including a `VARCHAR` length, which DuckDB alone cannot report; a written `CAST(x AS VARCHAR(n))` states its length; otherwise DuckDB's type is mapped through an enumerated table (`LogicalTypes`). Clean, target-neutral types (`BIGINT`, `INTEGER`, `SMALLINT`, `DOUBLE`, `BOOLEAN`, `DATE`, `TIMESTAMP`, `TIME`, `DECIMAL(p, s)`) are high certainty; types lossy for some target (`TINYINT`, `FLOAT`, unsigned integers, `TIMESTAMP WITH TIME ZONE`, `BLOB`, `UUID`) are normal certainty; a bare `VARCHAR`, `HUGEINT`, `INTERVAL`, `JSON` and nested types get no proposal and are asked. Nullability is proposed from lineage (`non_null`, `nullable`) and asked when lineage cannot tell. An expression without an alias, or a duplicate column name, is an error (DDB-220).

**Questions** (ids are `Q-define-<model>-<field>`, with `columns.<column>.type|nullable|remove` for columns and `Q-rename-<model>.<column>` for renames; unusual characters in names become `_u<hex>_`). They come in rounds because later questions depend on earlier answers: for a new definition, round 1 is `name`, `kind`, `targets` and every column's `type` and `nullable`, and round 2 (once the kind is answered) is `grain` and `unique_key`, or `time_column` and `lookback`. In non-interactive mode the open questions of the round reached are listed at once, with a note that more follow. Each question's escape hatch is `skip_model`. Grain candidates come from GROUP BY columns that appear in the output, SELECT DISTINCT, and an upstream key that passes through a one-to-one query; time column candidates are the DATE and TIMESTAMP outputs.

**Existing definitions.** Only `columns` (and `renames`) are touched, by minimal text splices located with the YAML parser's offsets (block and flow item styles, CRLF, and files without a final newline are handled; a `columns` list written as a flow list is reported, DDB-422, not rewritten). Columns match by name, case-insensitively, and declared order is not compared. A difference is a declared column the query no longer returns, a returned column that is not declared, or a type that is not equivalent (synonyms match, a bare DuckDB `VARCHAR` matches any `VARCHAR(n)`). A removed declared column and an added one of the same type at the same ordinal position are asked first as a possible rename; a rename updates the column, records `renames:`, and carries its references in `grain`, `unique_key` and `time_column` (the question shows the old and new values). **Decision:** declared nullability on an existing column is human knowledge: a declared NOT NULL over a column that lineage cannot prove non-null (a LEFT JOIN that always matches, for example) is a note, never a difference, or `--check` would fail forever. Lineage proposes nullability only for new columns. A person may answer `keep_declared`; that is recorded in the output and `--check` keeps reporting it.

**Writing.** Nothing is written before the diff is shown, and (interactively) confirmed. Writes are **all or nothing across models**: if any selected model still needs attention, nothing is written. Each file is written through `DefinitionFile`, which accepts only `.yml` paths, checks the file's hash against what was read (twice, the second time immediately before a rename), writes a temp file and renames it; a changed file is refused (DDB-421). After writing, every query file is re-hashed against what was read. After editing, the new text is loaded and compared with the inferred columns: anything still different must be exactly what the person chose to keep, otherwise `define` stops with an internal error and writes nothing.

**Modes.** `--check` asks nothing and writes nothing, and fails (DDB-420 per difference, with the definition line) if a definition is missing or out of sync; it cannot be combined with `--write`, `--answers` or `--accept-inferred`. `--write` is non-interactive and needs `--answers` for any open question. Without either, `define` is interactive and refuses to run without a terminal. `--accept-inferred` accepts only high-certainty proposals and each acceptance is printed with the diff. Models are processed in dependency order, so a model sees the columns an upstream model has just been given; a model whose upstream is not defined is skipped with a note, and a dependency cycle is DDB-221.

### 6.6 Load operations: paired with targets, committed, parameterized

A **load operation** is a named way to load one model on one target. A model can have several operations per target (for example a routine watermark load, a period reload, and a keyed merge). The SQL for every operation is **rendered to disk and committed**. At execution time the only unresolved things are the operation's declared runtime parameters.

**Declaration**

```yaml
# models/marts/fct_orders.yml (excerpt)
kind:
  type: incremental_by_time_range
  time_column: order_date
# ... name, grain, targets as before; columns include order_id, order_date (DATE) and modified_at (TIMESTAMP)
loads:
  daily:
    default: true
    strategy: watermark_append
    watermark:
      column: modified_at
      resolver: target_max        # how the value is determined; rendered and committed
      lookback: 3 days
      on_null: require_param      # an empty target never silently means "load everything"
  reload_period:
    strategy: delete_insert_by_range
    params:
      start: DATE
      end: DATE
    max_span: 400 days
  by_key:
    strategy: merge_by_key
    key: [order_id]              # defaults to kind.unique_key, which this kind does not have
    targets: [sqlserver]
```

**Pairing rules**

- An operation's identity is `(model, target, operation name)`. `targets: [...]` restricts an operation; without it the operation applies to every target the model declares.
- Every declared pair must render, or fail with a diagnostic (for example, the strategy is `unsupported` on that target). There are no silent gaps. `dbdatabuild loads` prints the model-by-target-by-operation table with each pair's matrix status.
- At most one operation per `(model, target)` is marked `default`. `dbdatabuild run` without `--op` uses it. With none marked, `--op` is required.
- If a model declares no `loads`, its `kind` supplies a single operation named `default`. It is still rendered and committed explicitly, and `dbdatabuild define` asks about it during the walkthrough.
- Strategies are a closed library: `watermark_append`, `delete_insert_by_range`, `delete_insert_by_key`, `merge_by_key`, `full_replace`. Each strategy has a per-target implementation behind `ILoader`, matrix rows (for example, `merge_by_key` is `emulated` on Fabric **[VERIFY]**), and a **DuckDB reference implementation** used as the test oracle (section 15.5).

**Committed artifacts**

```
rendered/
  sqlserver/
    marts.fct_orders/
      load.daily.sql               # value parameter: @watermark
      load.daily.resolve.sql       # resolver: the committed query that determines @watermark
      load.reload_period.sql       # value parameters: @start, @end
      load.by_key.sql
      manifest.yml                 # operations, parameters, sources, hashes, matrix status
  postgres/
    marts.fct_orders/ ...
```

- Files are generated by `dbdatabuild render --write`. Output is deterministic with **no timestamps**. Each file begins with a comment holding the model definition hash, strategy, target, matrix version, and tool version.
- `dbdatabuild render --check` (for CI) fails if the committed files differ from a fresh render, and writes nothing.
- The rendered files cover **load operations and resolvers only**. Create/alter DDL depends on target state, so its text lives in committed plans (section 10.5).
- Each rendered T-SQL file is parsed by ScriptDOM as part of validation. **[VERIFY]** handling of parameterized scripts, and equivalent checks for other targets.

**Parameters and placeholders**

Each operation declares its parameters: name, logical type, source (`runtime` for user-supplied, or `resolver`), and constraints (type, range, `max_span`). Only **value parameters** (`@name`) may appear as placeholders in a rendered file. They are bound through the database driver and never interpolated into the text, so the executed SQL text is **exactly** the committed text. A lint rejects any other placeholder.

**Object names are fixed.** Schema and object names in rendered files are exactly the model's declared names. Which environment a run targets is determined by the target's connection (server and database), not by the SQL text. A value such as an environment name may still be passed as a bound parameter (for example, for logging), but it cannot change identifiers. Making schema and object names parameters is deliberately **out of scope** for this design and will be revisited (section 3).

**How a watermark is determined** is written down and committed, never implied:

- The resolver is a committed SQL file: a single `SELECT` returning one row and one column of the parameter's declared type. `target_max` produces a query such as `SELECT MAX(modified_at) FROM <target table>`, less the declared lookback.
- It executes at plan time under the read-only login. The plan shows the result and the resolver's hash, and `apply` binds that value.
- **`apply` re-runs the resolver and compares it with the plan.** If the value moved between plan and apply, the plan is stale and `apply` refuses, consistent with the stale-plan rule.
- A NULL result is handled by the declared `on_null`: `require_param` (a question, or `--param` in an answers file) or a declared, committed initial literal. Nothing defaults to "from the beginning of time".
- A resolver may be `overridable`. An explicit `--param watermark=...` is then recorded in the plan as an override.

**Runtime parameter handling**

- Runtime parameters without a value become questions (`Q-param-<model>-<op>-<param>`) using the same question and answers-file mechanism. Constraints such as `max_span` are enforced at plan time.
- **All parameter values and resolver results are logged**: in the target's `run_log`, in plans shown or written by the tool, and in the tool's logs. This is a deliberate, accepted choice for now. Resolvers are expected to return watermark-like values (timestamps, dates, sequence keys); a resolver that returns a business measure needs review. Redaction can be scoped and added later if a need appears.
- Plans containing load steps (and therefore resolved values) are routine artifacts and are **not committed to the repo**. Only DDL plans for shared targets are committed (section 10.5). This also keeps resolved values away from the schema-only development environment.

**What a plan shows for a load step:** the operation name, the committed file path and hash, the resolver file and hash, the parameter values (on screen), and the full resolved script. `apply` verifies the committed file's hash against the plan before executing.

### 6.6.1 How render and loads work (as built)

**Declaring operations.** The `loads:` block is validated by the loader and by `schemas/model.schema.json`. Keys: `default`, `strategy`, `targets`, and per strategy `key` (the key strategies; defaults to the kind's `unique_key`), `column` and `params` and `max_span` (`delete_insert_by_range`; the column defaults to the kind's `time_column`, `params` to the column's own type), and `watermark` (`watermark_append`: `column`, `resolver: target_max`, `lookback`, `on_null: require_param | initial`, `initial`, `overridable`). Durations are `<n> minute|hour|day|week|month` and must fit the column (a DATE takes days, weeks and months). The watermark column and range column must be DATE, TIMESTAMP or an integer type, and an `initial` literal must be valid for it. A view takes no loads. At most one operation is the default for a target.

**What a kind supplies when `loads:` is absent.** `view`: nothing (DDL only). `full`: `full_replace`. `incremental_by_unique_key`: `delete_insert_by_key` on the unique key (a `merge_by_key` operation can be declared; `delete_insert_by_key` is the default because `MERGE` has concurrency pitfalls on SQL Server and is new in PostgreSQL 15). `incremental_by_time_range`: `watermark_append` on the time column with the kind's `lookback` and `on_null: require_param`. Declared operations replace the implicit one.

**Decision: lookback on `watermark_append`.** The example in 6.6 pairs `watermark_append` with a `lookback`, but appending the rows of a lookback window would duplicate them. So with a `lookback` the window from the watermark is *replaced* (rows at or after the watermark are deleted from the target, and the query's rows at or after it are inserted); without one, only rows strictly newer than the watermark are appended. The `watermark` parameter is the resolver's value: `MAX(column)` in the target, less the lookback, `COALESCE`d with the typed initial literal when `on_null: initial`. With `on_null: require_param` an empty target returns NULL and planning asks.

**Script shape.** Every script stages the query result once into a temporary table (so the body runs once, and its result cannot change between the delete and the insert), then applies it inside one transaction that the script opens and closes itself, so it is atomic wherever it is run. T-SQL: `SET XACT_ABORT ON`, `SELECT ... INTO #ddb_stage`, delete, insert (or `MERGE`), drop, commit. PostgreSQL: `BEGIN`, `CREATE TEMP TABLE ddb_stage AS`, delete, insert (or `MERGE`, version 15 or later), drop, commit. The model body is wrapped as a CTE named `ddb_body`, transpiled by polyglot, and the loader's own statements are assembled around it as text, so nothing depends on guessing polyglot's output; a body's own CTEs are hoisted for T-SQL and an unbounded `ORDER BY` gets `OFFSET 0 ROWS`. Polyglot's `unsupportedLevel: raise` is not used: it misses constructs and also rejects supported ones (`REGEXP_LIKE` on SQL Server 2025), so the support matrix decides what may render. Parameters appear only as `@name`; the placeholder lint rejects anything else (DDB-319). Key columns that are nullable are warned about (DDB-320).

**Validation.** Every rendered script and resolver is parsed offline: ScriptDOM for T-SQL (the grammar follows `targets.sqlserver.version`: 14, 15, 16, or 17 and later, and the newest when no version is configured; Fabric uses the newest), polyglot for PostgreSQL. ScriptDOM accepts parameterized scripts (verified). `validate` renders in memory, so an unrenderable model x target x operation pair is reported by name (DDB-317), and so is a script a parser rejects (DDB-318).

**Files.** `render --write` writes `rendered/<target>/<model>/load.<op>.sql`, `load.<op>.resolve.sql` (when the operation has a resolver) and `manifest.yml`, deterministically and without timestamps. Each file starts with a comment holding the model, operation, target, strategy, definition hash (the hash of the normalized body AST, section 12.1), matrix version (a hash of the matrix data) and tool version. The manifest lists the operations with their parameters, sources, matrix status (the worst status among the strategy and the constructs used) and the SHA-256 of each committed file, so `apply` can verify the text it executes. `rendered/` is pinned to LF by `.gitattributes`: the hashes are of the committed text. `--write` is all or nothing: it writes nothing if any pair fails, writes only files whose text changed (atomically), and removes stale generated files (`load.*.sql`, `manifest.yml`) in the directories it owns, including those of models that no longer exist, leaving anything else alone. `--check` writes nothing and reports each missing, differing or no-longer-rendered file as DDB-424. `loads` prints the model x target x operation table with the matrix status.

**Conformance.** `matrix/strategies.yml` has one row per strategy and target. The `translated` entries cite `conformance/<strategy>`, cases in `tests/DbDataBuild.Tests.Conformance`. That suite runs the rendered scripts, exactly as committed with only the parameters bound, on real SQL Server and PostgreSQL (`scripts/test-engines.sh up` starts throwaway containers; the suite refuses non-loopback hosts and is reported as skipped when no engine is configured), and compares the result with a DuckDB reference implementation of each strategy's semantics on synthetic data: a first run, a rerun, late-arriving rows, a failure part-way (the target must be unchanged and no temp table left), bound range parameters, and the resolvers' values for timestamp, date and integer watermarks. Fabric entries are `unverified`: no Fabric engine was available.

**Since built** (sections 6.6.2 and 10.4.1): resolvers run at plan time and again at apply, scripts are bound and run at apply, and the executor ends a failed PostgreSQL transaction block with `ROLLBACK`. **Not built:** `define` does not yet ask about declaring additional operations.

### 6.6.2 Slices and runtime parameters (as built)

**The slice is applied to the finished query.** `watermark_append` and `delete_insert_by_range` select `FROM (<your query>) WHERE slice_column >= @watermark`. That is always correct, including for aggregates and windows (pushing the filter into a windowed query would change the numbers). Whether it is also cheap depends on whether the engine can apply the filter before the expensive part. The tool does not rewrite the query to force that. It **advises** (DDB-225, a warning, never blocking): it follows the slice column down through the query's derived tables, CTEs and set operations and reports what stands in the way: the column is the output of an aggregate or a window function, a window is not partitioned by it, or the query has a LIMIT or DISTINCT ON. A column it cannot trace (a star, an ambiguous name) gives no finding. The message carries the way out: slice by a grouping or partition column, or choose a strategy that does not slice the finished result. The author may do their own slicing inside the query and use `full_replace` or a key-based load; the tool does not generate that, and a first-class custom slicing operation is not built. `lint_ignore: [DDB-225]` (per model) and `lint: { slices: false }` (per project) silence it.

**Runtime parameters** are questions (`Q-param-<model>-<operation>-<parameter>`, option `provide` with a value, or `skip_load`). Besides an answers file, `plan --param model.operation.parameter=value` (repeatable) answers them on the command line; the model name may contain dots because the last two parts before `=` are the operation and the parameter. A parameter named twice, in `--param` and in the answers file or twice in `--param`, is refused. `run` cannot take parameters (it refuses open questions), which is intended: a runtime range is a person's decision. Values are checked against their type and `max_span`, and a range that is **empty or backwards** (the end is exclusive) is refused (DDB-412) whether or not there is a `max_span`. For a range load the plan reads the smallest and largest value of the range column on the target (a read-only `SELECT MIN, MAX` through the read session) and **notes**, without refusing, a range the data does not overlap (the DELETE removes nothing; likely a typo of a year) and a range into an empty table (a first load). All parameter values and resolver results are still logged.

## 7. Support matrix

The matrix is **data**, stored under `matrix/`, and every non-`native` row is backed by a conformance test.

### 7.1 Status tiers

| Status | Meaning |
|---|---|
| `native` | Same syntax and semantics |
| `translated` | Rewritten; conformance test passes |
| `approximated` | Rewritten with a documented semantic difference (warning) |
| `emulated` | Multi-statement or helper rewrite (note atomicity) |
| `unsupported` | Build fails if the model declares this target |
| `unverified` | Rewrite exists, no passing conformance test yet (warning) |

### 7.2 Format

```yaml
- id: fn.date_trunc.month
  duckdb: native
  tsql:
    sqlserver: { status: translated, min_version: 16, test: conf/date_trunc_month }
    fabric:    { status: translated, test: conf/date_trunc_month }
- id: op.int_division
  tsql:
    any: { status: approximated, note: "DuckDB `/` yields decimal; rewritten with cast", test: conf/int_div }
- id: syntax.qualify
  tsql:
    any: { status: emulated, note: "Rewritten to subquery with row_number filter" }
- id: type.list
  tsql:
    any: { status: unsupported }
```

A row also carries `detect:` (how the linter finds the construct: `node:<polyglot Expression tag>`, `fn:<FUNCTION>`, or `detector:<name>` for a coded predicate such as `qualify`), and each non-native, non-unverified entry names a `test:` (currently `spike/<case id>`, replaced by conformance test ids in milestone 3). `matrix/covered.yml` lists the nodes, functions and cast data types verified as plain SQL, each with the cases that show it. The matrix files are embedded in the tool.

Keys include engine version or compatibility level where behavior differs. Per-target keys are `sqlserver`, `fabric`, and `postgres`; a `tsql` group is shorthand for both T-SQL targets. A construct that is `native` or `translated` on the T-SQL targets is **not** assumed to behave the same on Postgres, so each target has its own row and conformance test.

### 7.3 Use

- The linter maps each AST node to construct IDs and reports per declared target. Default severities: `unsupported` is an error (DDB-301), `approximated` and `unverified` are warnings (DDB-302, DDB-304), `emulated` is a note (DDB-303), and a `min_version` is a warning until target versions are configured (DDB-308). Policy config will set severity per status.
- **Nothing is assumed safe.** An AST node, function, cast data type, or select clause that is neither matched by a matrix row nor listed in `covered.yml` is reported as DDB-305. This is deliberate: the spike showed polyglot silently passes unsupported constructs through, and its `unsupportedLevel: raise` option catches only a few.
- A `dbdatabuild matrix` command prints the matrix, and a per-model portability report lists exactly which constructs limit which targets.
- **Semantic profile rules** (string comparison/collation, trailing-space handling, integer division, cast lengths, Unicode literals, NULL ordering, boolean/bit handling, timestamp and decimal precision) are matrix rows tied to per-target profiles. Seed list in section 15.4.

### 7.4 String comparison semantics

String comparison is the highest-risk semantic divergence between the canonical dialect and the targets. It produces different results with no error on either side, so it gets its own rules.

**Facts to design around** (**[VERIFY]** each in the conformance suite):

| | DuckDB | SQL Server | Fabric Warehouse |
|---|---|---|---|
| Case sensitivity | Case-sensitive by default. Collations `NOCASE`, `NOACCENT`, `NFC` are built in; ICU collations (e.g., `DE`) need the ICU extension. Collation applies per expression (`x COLLATE NOCASE`), per column (`VARCHAR COLLATE NOCASE`), or globally (`SET default_collation = NOCASE`) | Governed by the collation of the column, database, or expression. Common defaults are case-insensitive | Default collation may be case-sensitive, with case-insensitive available as an option **[VERIFY]** |
| Trailing spaces in `=` | **Significant.** No collation or setting makes DuckDB ignore them | Typically ignored in `=`, `IN`, joins, `GROUP BY` (ANSI padding), but significant in `LIKE` | **[VERIFY]** |
| `LENGTH`/`LEN` | `length()` counts trailing spaces | `LEN()` excludes trailing spaces; `DATALENGTH()` counts bytes | **[VERIFY]** |

DuckDB's docs warn that its collation support has known limitations, and collations can't be mixed freely (comparing columns with different collations is an error), so every rule below needs a test, not an assumption.

**Comparison profile.** The default profile is SQL Server-style: **case-insensitive, accent-sensitive, trailing spaces ignored**. It is written into `dbdatabuild.yml` by `dbdatabuild init`, and the effective profile is printed in every command header, so it is never hidden. A project that wants different semantics changes the file.

```yaml
# dbdatabuild.yml
string_semantics:
  case: insensitive          # sensitive | insensitive
  accent: sensitive          # sensitive | insensitive
  trailing_space: ignored    # significant | ignored
  collations:                # logical name -> per-engine name
    default:
      duckdb: NOCASE
      sqlserver: Latin1_General_100_CI_AS
      fabric: Latin1_General_100_CI_AS_KS_WS_SC_UTF8   # [VERIFY]
```

In DuckDB terms the default profile is `NOCASE` alone: it ignores case but stays accent-sensitive, so do not chain `NOACCENT`. On SQL Server it corresponds to a `_CI_AS` collation.

A target whose configured collation cannot satisfy the project's declared profile is reported at `validate` time, before any connection is needed, and again in `check` against the live catalog.

**How the profile is applied:**

1. **Declared column collations.** `columns` entries in the model definition may specify `collation: <logical name>`; the default comes from the profile. Generated DDL always states collations explicitly, so objects never depend on a database default. The collation is part of `shape_hash`.
2. **Offline DuckDB execution emulates the target.** Offline runs set `default_collation` and use per-column collations from the profile. Where the profile says trailing spaces are ignored, a **DuckDB-side emulation rewrite** wraps string operands of `=`, `IN`, join keys, `GROUP BY`, `DISTINCT`, set operations, and window `PARTITION BY` in `rtrim()`. This rewrite applies **only when executing in DuckDB**. The SQL emitted for targets is unchanged where the target already has those semantics, which also avoids hurting index seeks.
3. **Transpiled output follows the target's collation rules.** Emit `COLLATE` clauses only where the matrix row requires one (for example, a comparison between columns with different declared collations).
4. **Collation-sensitive operations are lint targets.** The linter identifies, from the AST, every equality or `IN` predicate, join key, `GROUP BY`, `DISTINCT`, `UNION`, window `PARTITION BY`/`ORDER BY`, `ORDER BY`, `MIN`/`MAX`, and `LIKE` over string expressions, and checks each against the declared profile and the operands' declared collations. Comparing operands with different collations is an error offline (SQL Server would reject it at run time with a collation conflict).
5. **Differential comparison normalizes per profile.** When the profile says trailing spaces are ignored, output string columns are `rtrim`-normalized before comparison, because SQL Server may return either representative of rows grouped together. Record this normalization in the test report so it can't quietly mask real differences.

**Matrix rows** (each has a conformance test; status shown is the expected starting point):

| ID | Expected status (SQL Server) | Notes |
|---|---|---|
| `str.eq.case` | translated | Driven by profile and column collation |
| `str.eq.trailing_space` | native on target; emulated in DuckDB | Emulation rewrite above |
| `str.in_list` / `str.join_key` | as above | Same semantics as `=` |
| `str.group_by` / `str.distinct` / `str.union_dedupe` / `str.window_partition` | as above | Row counts differ if semantics differ |
| `str.like.case` | translated | DuckDB `LIKE` is case-sensitive; `ILIKE` is always case-insensitive. Target `LIKE` follows collation |
| `str.like.trailing_space` | approximated | Significant in SQL Server `LIKE` even when `=` ignores them |
| `str.len` | approximated | `length()` vs `LEN()` differ on trailing spaces |
| `str.order_by` / `str.min_max` / `str.window_order` | approximated | Sort order follows collation rules, so ranks and `ROW_NUMBER` ties can differ. Lint warns on ordering by strings without an explicit tiebreaker |
| `str.concat_null` | translated | DuckDB `||` with NULL yields NULL; SQL Server `+` also yields NULL under default settings, but `CONCAT()` treats NULL as empty. Choose one rewrite deliberately |
| `str.unicode_literal` | translated | Emit `N'...'` and `nvarchar` for non-ASCII content |
| `str.cast_no_length` | translated | `CAST(x AS VARCHAR)` needs an explicit length or `max` on SQL Server |
| `str.case_fold` | approximated | `lower()`/`upper()` Unicode behavior may differ by collation |

**Conformance cases for this section** (run under both profile settings: case-insensitive/trailing-ignored and case-sensitive/trailing-significant, using the ephemeral database collation and DuckDB `default_collation` to match):

- `'abc' = 'ABC'`, `'abc' = 'abc   '`, and `'abc' = ' abc'` in `WHERE`, `CASE`, join keys, and `IN`
- `GROUP BY` and `DISTINCT` over values that differ only by case or trailing spaces (row counts and representative values)
- `UNION` versus `UNION ALL` de-duplication of the same values
- `ROW_NUMBER() OVER (PARTITION BY s ORDER BY s)` over mixed-case values
- `LIKE 'abc%'`, `LIKE 'abc'` against `'abc  '`, and `ILIKE`
- `length(s)` versus `LEN(s)` for values with trailing and leading spaces
- `MIN`/`MAX` over mixed-case strings; `ORDER BY` over mixed-case and accented strings
- Accent handling: `'é' = 'e'` and `'é' = 'É'` under the default profile (accent-sensitive), and under an accent-insensitive profile (`NOACCENT` on DuckDB)
- Column-to-column comparison with different declared collations (expected diagnostic, not a run-time failure)
- Empty string versus NULL; non-ASCII values round-tripped through `nvarchar`
- `CAST(x AS VARCHAR)` on long values (truncation must not occur silently)

The synthetic-data edge-case pack (section 15.2) includes every value these cases need.

### 7.6 Lowering (as built)

Every model query is bound by DuckDB and **lowered** to one explicit query before the matrix lint and the transpile (research: `docs/research/duckdb-plan-lowering/README.md`).

- **Flow**: author's SQL, then DuckDB's bound, unoptimized plan (against an empty schema built from the declared columns of every other model and source), then one readable query in DuckDB's dialect with `*`, `USING`, `NATURAL JOIN`, `GROUP BY ALL` and ordinals, macros and implicit casts expanded, then lint and polyglot. The lint and the loads see the lowered query; findings point at its committed artifact.
- **Committed artifact**: `rendered/lowered/<model>/lowered.sql`, with a header naming the model, the source file and its hash, the DuckDB version, the tool version, the output columns with their resolved DuckDB types and the type rules that changed the text. It is written by `render --write`, checked by `render --check` and by `plan` (a stale or missing file is DDB-424), so a reviewer sees exactly what runs, a DuckDB upgrade shows up as a diff, and the pinned engine version is visible.
- **Plan format and DuckDB versions.** The serialized plan is DuckDB's internal format and DuckDB 2.0 restructured it. `PlanNormalizer` rewrites a 2.0 plan into the 1.x shape the lowerer reads (names from `qname.path`, comparison and cast functions back to comparisons and casts, NOT over IN back to NOT IN, lead/lag arguments), and is the identity on a 1.x plan, so one lowerer serves both. The whole suite passes on the 2.0 alpha. Correlated subqueries rely on DuckDB's `delim_join_as_cte = false` (set by `QueryDescriber.PreparePlanConnection` when the setting exists), which DuckDB has marked deprecated; without it the plan holds materialized CTEs and joins to grouped tables and 16 forms would need an inverse decorrelation. See `docs/research/duckdb-2.0/README.md`, which also records the plan to move. `scripts/test-duckdb-preview.sh` runs the unit tests against DuckDB's preview library.
- **Subqueries are lowered back to subqueries.** DuckDB's binder has already decorrelated them (an `EXISTS` is a `MARK` delim join, a scalar subquery a `SINGLE` join, `LATERAL` an inner or left one, `IN` a `MARK` join), so the lowerer undoes that: each correlated reference is written as the outer column, always qualified (`o.customer_id`) inside the subquery, the joins back to the outer values and the `GROUP BY` on them are dropped, and the right side is printed as a nested query. Supported: `EXISTS` and `NOT EXISTS`, `IN` and `NOT IN` (with DuckDB's null semantics), scalar subqueries including aggregates and `count(*)`, `LIMIT k` with an order inside a correlated subquery, subqueries in the select list, `WHERE`, `HAVING`, `ON`, `ORDER BY` and `CASE`, nesting several levels deep, `LATERAL` and `LEFT JOIN LATERAL`, and uncorrelated scalar, `EXISTS` and `IN` subqueries. Refused by name: `x op ANY/ALL (...)`, row-value `IN`, a correlated subquery over `UNION`/`INTERSECT`/`EXCEPT`, a window partitioned by a correlated value, and a correlated `LIMIT` with an offset. Table aliases the author wrote are not in the plan, so each subquery source is named after its table (`orders`, `orders_2`).
- **`DISTINCT ON`** lowers to `row_number() OVER (PARTITION BY the ON keys ORDER BY the rest of the ordering) = 1` over a derived table, but only when the ordering **decides** the row: DuckDB (like a window) keeps an arbitrary row among ties, so the result would differ between engines. The ordering must include the declared grain of every table the query reads (a source's or model's `grain:`); otherwise it is refused and the message names the columns to add. No ORDER BY, an ORDER BY of only the ON columns, a derived table or a table without a grain are refused.
- **Integer series**: `generate_series` and `range` over integer constants lower to `generate_series(start, stop[, step]) AS series(value)` (`range`'s exclusive end made inclusive, the column cast to BIGINT). SQL Server 2022 (version 16) has the function but takes no column list, so the renderer drops `(value)` for the T-SQL dialects. Date series are refused.
- **A query that cannot be lowered is an error** (DDB-324), not a fallback: `UNNEST` and other table functions, `USING SAMPLE`, `LIMIT ... PERCENT`, list and struct constructors, and the subquery shapes above. A DuckDB parse error is still DDB-306 and an undeclared upstream table DDB-218.
- **Type rules** (so far): `avg` of a non-DOUBLE argument is written `avg(CAST(x AS DOUBLE))`, and a DATE widened to TIMESTAMP by `date_trunc` or interval arithmetic is written with an explicit cast. Division needs none: the binder already casts both operands to DOUBLE. Null ordering is always written explicitly. Output column names are applied from `DESCRIBE` by position, and a name DuckDB repeats gets a `_2` suffix.
- **Binder rewrites are accepted** in the lowered query: `BETWEEN` becomes two comparisons, `NOT (a > 1)` becomes `a <= 1`, `NULLIF` becomes `CASE`, constants are folded, an unused non-materialized CTE is inlined.
- `lowering: { enabled: false }` in `dbdatabuild.yml` turns the stage off for a project (the author's text goes straight to the lint and transpile, as before). There is no per-model fallback.
- Metadata (`metadata`, `publish-metadata`) records each column's resolved DuckDB type, the lowered artifact's hash and the rules fired.
- Still open: target-specific string-comparison, `LIKE`, `REPLACE` and `LENGTH` rules through identity marker macros, `sum` widening, decimal precision pinning, and lowering for the constructs above. SQL Server rejects an aggregate over a subquery (`sum((SELECT ...))`), which lowering faithfully preserves; that is a matrix finding, not a lowering gap.


#### 7.6.1 Target rules (as built)

The lowered query is in DuckDB's dialect and identical for every target. A few functions behave differently on an engine, and polyglot can only translate the name, so a **target rule** step (`DbDataBuild.Targets/Rules/TargetRules.cs`) sits between the lowered query and the transpile. It parses the DuckDB-dialect query, rewrites the recognised shapes into equivalent DuckDB-dialect expressions built from functions that translate cleanly, and hands the result to the transpile. The rewritten text appears only in the rendered scripts under `rendered/<target>/`, which name the rules that fired in a `-- target rules:` header line; `rendered/lowered/` stays target-neutral. A query no rule applies to is passed through byte for byte. The matrix lint and `validate` run on the rewritten query, per target.

The step does not guess types. The lowerer, which has them, **marks** the expressions that need a rule by writing an explicit cast the engines would not need, and the rule removes the mark:

| Rule | Mark in the lowered query | Targets | What it does |
|---|---|---|---|
| `length-trailing-spaces` | none (`length(x)`) | SQL Server, Fabric | `LEN` ignores trailing spaces, DuckDB counts them: `length(x \|\| 'x') - 1` |
| `round-double` | `round(CAST(x AS DOUBLE), n)` with constant `n` (`\|n\| <= 15`) | SQL Server, PostgreSQL | DuckDB rounds the scaled double half away from zero (`round(2.675, 2)` is 2.68, `round(0.285, 2)` is 0.28); SQL Server rounds the decimal text (2.67) and PostgreSQL has no `round(double precision, integer)`: `sign(x*p) * floor(abs(x*p) + 0.5) / p` |
| `try-cast-parse` | `TRY_CAST(CAST(s AS VARCHAR) AS <integer, decimal, double, date, timestamp>)` | SQL Server, Fabric, PostgreSQL | `''` is NULL in DuckDB, 0 on SQL Server, an error on PostgreSQL. SQL Server: `TRY_CAST(NULLIF(TRIM(s), '') AS ...)`. PostgreSQL: a `CASE` that tests the text with a regular expression and the range before casting (integers, decimals and doubles; dates cannot be tested without raising, so they are left as they are and still reported) |
| `sum-widen` | `sum(CAST(x AS BIGINT))` (or `DECIMAL(38, 0)` for a BIGINT) | all | not a target rule but a lowering rule: DuckDB sums integers into a HUGEINT, SQL Server's `SUM` of an `INT` raises an overflow error past 2^31. Written into the lowered query itself, so it is visible in the committed artifact |

Known differences the rules do not remove: a string DuckDB reads as a number that an engine refuses (`'12.7'` as INTEGER is 13 and `'1e3'` is 1000 in DuckDB, NULL on the engines); a PostgreSQL `TRY_CAST` of a string longer than 38 digits raises; `round` of a double beyond 1e15 digits or scaled past the double range. A column declared HUGEINT is still refused, so a model writes `CAST(sum(n) AS BIGINT)`.

## 8. Targets

```csharp
public interface ITarget
{
    string Name { get; }
    TargetProfile Profile { get; }        // collation, default string type, precision rules, version
    string Render(Expression ast, RenderContext ctx);   // transpile + per-target post-rewrites
    IReadOnlyList<Diagnostic> Validate(string sql);     // offline validators (e.g., ScriptDOM)
    ILoader Loader { get; }               // engine-specific load statements per model kind
    IDdlGenerator Ddl { get; }            // create/alter statements from shape diffs
    IHooks Hooks { get; }                 // app locks, statistics, indexes, grants (engine-specific, explicit)
}
```

- **SQL Server**: T-SQL via ScriptDOM-validated output, transactional DDL, `sp_getapplock` for mutual exclusion.
- **Fabric Warehouse**: T-SQL surface is narrower. Per Microsoft's T-SQL surface area page (checked 2026-09-30): `MERGE`, `TRUNCATE TABLE`, `QUALIFY`, `GROUP BY ALL`/`ORDER BY ALL`, and column rename via `sp_rename` are supported; `ALTER TABLE` is limited (add nullable columns, drop column, constraints `NOT ENFORCED`; `ALTER COLUMN` is preview); recursive queries, triggers, synonyms and materialized views are not supported; nested CTEs are preview. Docs say what is supported, not how it behaves, so every Fabric matrix entry needs a test on a Fabric engine. None has been available, so Fabric entries are `unverified`. Operations with no native form are `emulated` with explicit atomicity notes.
- **PostgreSQL** (added early as a non-T-SQL target): it differs from the T-SQL targets in identifier case-folding and quoting, case-sensitive string comparison with `varchar` trailing spaces significant, integer division that truncates, first-class booleans, transactional DDL, advisory locks instead of `sp_getapplock`, and `MERGE` only from newer versions **[VERIFY]**. It is close to DuckDB's dialect, so its value is less about hard transpilation and more about exposing T-SQL assumptions baked into shared code (app locks, `nvarchar`, ScriptDOM-only validation, tracking-table DDL, bracket quoting).
- **State and safety as built** (`DbDataBuild.State`, `DbDataBuild.Execution`; see `docs/progress/state-and-apply.md`): logins come from `DBDATABUILD_<TARGET>_<READ|WRITE>` environment variables with no fallback (DDB-501); the mutation gate checks effect class (DDB-502), logs before executing and refuses to execute if it cannot log (DDB-503); read queries pass a guard (DDB-504). Tracking-table log ids are GUIDs, not IDENTITY, and a `tracking_version` table records the layout.
- Engine-specific hooks and load logic are written in native target SQL, are exempt from DuckDB-based offline tests, and must be verified on the test engine.

## 9. Commands and effect classes

### 9.1 Command surface

Every command declares one **effect class**, printed in `--help` and in a header at the start of each run (command, effect class, target, login in use, objects that may be touched).

| Command | Effect class | Purpose |
|---|---|---|
| `dbdatabuild validate` | Offline only | Validate config, models, matrix lint, ScriptDOM parse. No target connection |
| `dbdatabuild render [<model>]` | Repo files only (no target connection) | Render load operations and resolvers per target. Prints by default; `--write` writes the committed `rendered/` files; `--check` fails if committed files differ from a fresh render and writes nothing |
| `dbdatabuild loads` | Offline only | Print the model x target x operation pairing table with matrix status |
| `dbdatabuild sample [<model>...]` | Offline only | Run models on generated or supplied sample data in an in-memory DuckDB and show the rows (section 15.2). Connects to nothing, writes nothing |
| `dbdatabuild agent-kit` | Repo files only (lists unless `--write`) | Install the agent skill and JSON Schemas (section 9.7) |
| `dbdatabuild tui` | Offline only itself; each action it runs declares its own effect | Interactive terminal interface (section 9.6) |
| `dbdatabuild matrix` | Offline only | Print matrix and portability report |
| `dbdatabuild explain <code>` | Offline only | Long-form diagnostic explanation |
| `dbdatabuild define <path>` | Repo files only (no target connection) | Generate or update model definition files (YAML) from the query plus a guided walkthrough (section 6.5). `--check` writes nothing |
| `dbdatabuild check` | Target read-only | Preflight findings: drift, blocks, history report inputs |
| `dbdatabuild plan` | Target read-only (writes plan files locally) | Guided planning: discover, ask, generate plan |
| `dbdatabuild report` | Target read-only | History consistency, drift, run and DDL history |
| `dbdatabuild apply <plan>` | **Target writes** (DDL and/or data, as the plan states) | Execute exactly the plan's recorded statements |
| `dbdatabuild run <selector>` | **Target writes (data only)** | Shorthand: plan + apply, allowed only when the plan contains routine load steps and no questions or DDL. Otherwise refuses and points to `plan` |
| `dbdatabuild ack ...` | Tracking tables only | Records a human decision (drift, history). Changes no user data |
| `dbdatabuild init` | Tracking tables only | Creates tracking schema/tables. Prints the reviewable, idempotent script by default and connects to nothing; `--apply` runs it on the write login through the mutation gate |

No flag changes a command's effect class. Backfills are requested through `plan` (scope option), not a separate mutating command.

### 9.2 Logins

- **Read login**: catalog and tracking-table read only. Used by all read-only commands.
- **Write login**: DDL/DML rights scoped to managed schemas plus insert on tracking tables. Used only by `apply`, `run`, `ack`, `init`.
- Credentials come from the runtime environment (integrated/Entra auth preferred). Never stored in the repo or config files.

### 9.3 The mutation gate

Only code paths within mutating command handlers can obtain a write-capable connection, by way of a single `MutationGate` type that:

- Accepts only statements that originate from a plan step (or a tracking-table writer).
- Records every executed statement (hash, text, step id) to the statement log before execution.
- Verifies the statement's effect class is permitted for the current command.

A test enumerates call paths to `ExecuteNonQuery` and fails on any path not routed through the gate.

### 9.4 Project configuration (`dbdatabuild.yml`)

Offline settings only. Credentials never live here (section 9.2). Every key is optional; an absent key takes the built-in default, and **every command prints the effective settings in its header**, so a default is never hidden. A missing file yields the defaults and a warning (DDB-109). The schema is `schemas/config.schema.json`.

```yaml
default_targets: [sqlserver]      # targets for models without `targets:`. Default: [sqlserver]
targets:                          # per-target settings used by offline checks
  sqlserver: { version: 16 }      # the engine's major version; resolves matrix `min_version` rows
  postgres:  { version: 17 }
tracking_schema: dbdatabuild      # default: dbdatabuild
string_semantics:                 # section 7.4. Defaults shown
  case: insensitive               # sensitive | insensitive
  accent: sensitive               # sensitive | insensitive
  trailing_space: ignored         # significant | ignored
  collations:
    default: { duckdb: NOCASE, sqlserver: Latin1_General_100_CI_AS }
policy:
  severity:                       # severity of matrix findings: error | warning | note
    approximated: warning         # DDB-302
    emulated: note                # DDB-303
    unverified: warning           # DDB-304
    not_covered: warning          # DDB-305
```

- **Target versions** settle `min_version` matrix rows: at or above the minimum there is no finding, below it the construct is an error (DDB-301), and with no version configured it stays a warning (DDB-308).
- **Policy** can raise or lower a finding's severity but never hides it. `unsupported` findings are always errors and are not configurable.
- **Collation check (offline, `validate`).** For every engine in use (each model's `targets`, the default targets when a model relies on them or there are no models, and always DuckDB) `string_semantics.collations.default` must have an entry, and its name is read for case, accent and trailing-space behavior and compared with the profile. A collation known to contradict the profile is an error (DDB-310); one whose behavior the name cannot settle is a warning (DDB-311) and is never assumed to match; a missing entry, a `collation:` on a column that is not defined, or a defined collation missing an engine the model targets is an error (DDB-312). Only `default` must satisfy the profile: other logical names are declared exceptions. Name rules: SQL Server and Fabric read `_CI_`/`_CS_`/`_AI_`/`_AS_`/`_BIN2` tokens (SQL Server always ignores trailing spaces in `=`; Fabric's is unverified, so it is reported as DDB-311); DuckDB reads `NOCASE`, `NOACCENT`, `NFC`, locale names and `.` chains, and meets an `ignored` trailing-space profile through the offline `rtrim()` rewrite; PostgreSQL reads libc/C locales and ICU `-u-ks-level1/2` names, and cannot ignore trailing spaces natively. The same check runs against the live catalog in `check`: text columns the model leaves on the default collation must have a live collation that satisfies the profile (DDB-310), a column on the database default is DDB-311 because its behavior cannot be verified, and declared exceptions and columns the model does not declare are not held to the profile.

### 9.5 Machine-readable output and its schemas (as built)

Every command takes `--format json` and then prints exactly one JSON document on standard output (`dbdatabuild.output/1`): `command`, `tool_version`, `exit_code`, `ok`, `data`, `diagnostics` (the structured form of the text-mode diagnostics), and `messages` and `errors` (the human text). Exit codes are the same as in text mode. The shapes are **schemas, checked by tests**:

- `schemas/output.schema.json` holds the envelope and one `data` schema per command (`$defs.data_<command>`, selected by `command`). A test requires every command in the command table to have one. Each `data` object is closed (`additionalProperties: false`), so a renamed, retyped or unannounced key fails the build; keys are present when the command got far enough to produce them (a command refused early has few or none), and null values are omitted rather than written as null (the exceptions, `login` and `statement_log`, are typed as nullable).
- `schemas/metadata.schema.json` (`urn:dbdatabuild:schemas:metadata`) defines the **metadata documents**, one per project (`dbdatabuild.project/1`), one per source descriptor (`dbdatabuild.source/1`: columns, grain, and the models that read it) and one per model (`dbdatabuild.model/1`): declared, DuckDB and native types per target, lineage, lowered artifact and rules, rendered loads with hashes and matrix status, indexes, resolved hooks, expected shape hashes, and the index advice. The same documents are what `validate` and `metadata` print and what `publish-metadata` stores in the tracking schema, so one schema covers both. A new optional key is not a version change; a removed or retyped key is.
- The unit tests validate every offline command's document; the real-engine suite validates every document it sees from `init`, `check`, `plan`, `apply` (dry run, applied and refused), `run`, `report`, `ack` and `publish-metadata` against the schema, on both engines.
- `define` reports its mode, what it checked or wrote, and each model's status with any open questions (as data, with options and proposals) so a pipeline can answer them and run again.

### 9.6 The terminal interface (as built)

`dbdatabuild tui` (Terminal.Gui 2.5, which needs .NET 10, so the solution moved from .NET 8, whose support ends in November 2026) lets a person choose an operation, plan it, read the plan and run it, without remembering options. **It has no logic of its own.** It is a client of the machine interface of section 9.5: every action runs a command in process with `--format json` and shows that document, and anything a command could not do it cannot do either. The consequences, all tested:

- **Forms come from the command definitions.** The catalog is read from the real command tree (every option and argument, its type, default and description), and a form has one field per option or argument. A new option appears in the TUI without writing anything; a test requires every option of every command to be in the catalog, and that the arguments every form can produce are accepted by the real parser. The form shows the exact command line it stands for.
- **Confirmation is by effect.** An action that changes something (data, the target, tracking tables, files) asks first, unless the form as filled in does not change anything (`render` without `--write`, `apply --dry-run`). A project with several default targets is asked once which to work on, instead of letting the command refuse.
- **Questions become dialogs.** When `plan` or `define` stops with open questions, the TUI shows each with its context, options and the tool's proposal (never taken without an explicit choice), writes the answers as an answers file under `.dbdatabuild/`, and runs the command again with `--answers`, the way a pipeline would.
- **Plans are read before they run.** The plan browser lists every step with its risk, the reasons, parameters, resolver result and the exact script, shows the report, and offers a dry run or apply with the flags the plan needs (`--allow-risky`, `--allow-destructive <objects>`) already filled in. A plan that was edited by hand cannot be opened as valid.
- **Models** are browsed from the metadata document (columns with native types per target, loads, indexes, index advice, the lowered query) with actions on the selected model: sample data, render, plan, check.
- **Progress and stop.** A command runs on its own thread; if it takes longer than 0.4 seconds a progress window shows the elapsed time and what the command reports as it goes (for `apply`, each step as it starts and how long it took). For `apply` and `run` a **Stop after this step** button asks the apply to stop between two steps: a statement that has started is never abandoned, the migration is recorded as failed (DDB-445), and `apply --resume` continues from the next step. The live side is `CommandContext` (`CommandHooks`: a progress callback and a stop question, carried with the command), separate from the command's JSON document, which is still delivered at the end.
- **Sample data**: results of `sample` show as tables, one per model, with warnings (declared columns the query does not return) and errors.

Keys: Enter opens, Tab moves, Esc closes a screen, F1 help, F2 models, F3 plans, F4 target, F5 last result, Ctrl+Q quits. `tui` refuses `--format json` and a redirected terminal. Terminal.Gui has no headless driver in its package, so the screens are checked by `scripts/tui_drive.py`, which runs the real app in a pseudo-terminal and prints the screen; the parts that decide behaviour (forms, results, questions, plans) are unit tested without a terminal.

### 9.7 AI agents (as built)

An AI coding agent works through the CLI with `--format json` (the machine interface of section 9.5); an HTTP service is not wanted (a daemon, authentication and a network surface for no new capability), and an MCP server over the same command layer is a possible convenience, not built (`docs/agents.md` has the reasoning and a permissions example that follows the effect classes). What an agent cannot guess is shipped with the tool: `dbdatabuild agent-kit` lists, and `--write` installs, a skill (`SKILL.md`: the rules that are never negotiable, the effect of every command, the loop for a model change, how to write a model, what cannot be written and why, how to read a plan) and the JSON Schemas, embedded in the executable so they match its version. `--check` fails when an installed copy differs. Tests keep the skill true: every command, option, diagnostic code and schema it names must exist, its example model must load, and the schemas it ships must be the repository's. The repository's own `CLAUDE.md` is the equivalent for someone changing the tool.

## 10. Planning and applying

### 10.1 `dbdatabuild plan` flow

1. **Discover**: read repo (models, intents, answers), target catalog, and tracking tables.
2. **Compute findings**: shape/physical/definition hash comparisons, drift, required operations by applying the decision table (section 11).
3. **Ask**: collect every open decision into a list of **questions** with stable IDs, context, and explicit options. No silent defaults.
4. **Answer**: interactively, or from `--answers answers.yml`. In non-interactive mode, unanswered questions fail the run and **all are listed at once**, each with the exact YAML to add.
5. **Generate**: produce the plan only when nothing is open. Answers are embedded in the plan.

Question IDs are deterministic (same scenario, same ID) so answer files are reusable and testable.

```yaml
# answers.yml
answers:
  - id: Q-history-marts.fct_orders.discount_code
    choice: not_backfilled
    note: "No history exists in source."
  - id: Q-rename-marts.dim_customer.cust_nm
    choice: rename_to
    value: customer_name
  - id: Q-define-marts.fct_orders-type-amount
    accept: inferred
```

**Questions** (`DbDataBuild.Core.Questions`). A question has a stable id `Q-<area>-<subject>`, a prompt, context lines, and at least two explicit options, each with a lowercase snake_case key, a description, an optional consequence, and optionally a value it takes (`rename_to` takes the new column name). No option is a default. Areas and their id shapes: `define` (`Q-define-<model>-<field>`), `history` (`Q-history-<model>.<column>`), `rename` (`Q-rename-<model>.<column>`), `param` (`Q-param-<model>-<operation>-<parameter>`), `adopt` (`Q-adopt-<object>`). Ids are built only by `QuestionIds`, so a scenario always yields the same id.

**Answers.** Each answer is explicit: `choice` (one of the question's option keys, plus `value:` when that option takes one, and an optional `note:` that is kept with the answer in the plan), or `accept: inferred` to accept the question's inferred proposal. The schema is `schemas/answers.schema.json`. A question may carry a **proposal** (an inferred option and value, with evidence and a certainty). Accepting one is always an explicit answer: `accept: inferred`, the accept key at a prompt, or `--accept-inferred`, which accepts only proposals marked high certainty and is recorded as such (`AcceptedProposalByFlag`).

**Resolution** (`QuestionResolver`). Questions are processed in id order, so the outcome does not depend on input order. A file answer is validated against the question: an unknown choice is DDB-411, a value that does not fit the option is DDB-412, `accept: inferred` without a proposal is DDB-413, and an invalid file answer is an error that is never silently replaced by a prompt. An answer for a question that was not asked is only a warning (DDB-410), because answer files are reused across runs. Without a prompter, every unanswered question is reported at once as DDB-414 with the question's text and paste-ready YAML for each option. With a prompter, only questions the file does not answer are asked. Resolved answers record how each was answered (`File`, `Interactive`, `AcceptedProposal`, `AcceptedProposalByFlag`) and serialize back to the same file format (`AnswerSerializer`), which is how a plan embeds them.

### 10.2 The plan document

Two renderings of one object: a readable Markdown report, and a machine-readable YAML that `apply` consumes. Each plan records: target, git commit, git-dirty flag, tool version, base hashes, answers, steps, and "noticed but not done" items.

Each **step** has: ordinal, type (`ddl` | `load` | `backfill` | `hook`), the **full script text**, a **reason chain** (model change, answer, drift, or request), risk class, expected effect (objects and expected result hash), and lock/transaction notes.

```
PLAN 2026-10-12-fct_orders-01     target: sqlserver-prod     commit: 4af31c2
Summary: 1 DDL step, 1 load step. Nothing else will run.

1. [ddl, safe] ALTER TABLE [marts].[fct_orders] ADD [discount_code] varchar(20) NULL;
   Why: model marts.fct_orders (commit 4af31c2) declares discount_code; target lacks it.
   Decided: answers.yml Q-history-marts.fct_orders.discount_code = not_backfilled ("No history exists in source.")
   Lock: brief schema-modification lock.

2. [load] MERGE ...   (full script)
   Why: routine incremental load; watermark 2026-10-11 read from target.

Noticed but NOT done:
  - Rows loaded before 2026-10-12 will have NULL discount_code.
  - Index IX_fct_orders_date differs from model definition. No change planned.
```

### 10.3 `dbdatabuild apply` semantics

- Verifies plan integrity (content hash). A hand-edited plan is refused unless regenerated through the plan command.
- Verifies **base hashes** against the live target. A mismatch is a **stale plan**: refuse, never guess.
- Refuses from a dirty working tree unless explicitly allowed. Records git commit and plan hash.
- Acquires the application lock. Applies steps in order, recording per-step status.
- After DDL steps, recomputes the shape hash and compares to the plan's expected result. Mismatch is logged and blocks dependents.
- **Partial failure**: per-step status is recorded. `dbdatabuild apply --resume` continues only if the live hashes match the recorded intermediate state. Otherwise stop and require a new plan. Nothing resumes automatically.
- `--dry-run` prints every statement and runs all preflight checks using the same code path as a real apply.

### 10.4 Risk classes

| Class | Examples | Requires |
|---|---|---|
| safe | add nullable column, widen type, add index | plain `apply` |
| risky | add NOT NULL column, collation change, size-of-data operations on large tables | `--allow-risky`; plan shows estimated rows from catalog metadata only |
| destructive | drop column/table, narrow type, truncate | `--allow-destructive <object list>`; objects must be named explicitly |

Renames are never inferred. An undeclared rename plans as a destructive drop plus an add, and raises a question.

### 10.4.1 Planning and applying as built

`plan`, `check`, `apply` and `ack` exist (see `docs/progress/state-and-apply.md` entries 5 to 8 for the decisions and the evidence). Points where the build settles or differs from the text above:

- The decision table is `matrix/decision-table.yml`: 32 rows, each with an owner (`planner`, `command` or `apply`), and a test for every planner row. Planner steps name their row id first in their reason chain.
- A plan with blocks is still written for the models that are not blocked; blocks and skips are listed in the report and the command exits non-zero.
- Plan files are YAML with a SHA-256 over their content. `apply` refuses any plan that is not byte-for-byte what `plan` wrote (DDB-435), including cosmetic edits.
- `apply` takes `--allow-risky`, `--allow-destructive <object>` (repeatable, naming each object), `--resume`, `--dry-run` and `--allow-dirty`. A plan is applied once (DDB-438); a plan that stopped part-way is resumed only from the exact intermediate state it recorded.
- `ack drift` and `ack definition` record a person's acknowledgement of one specific hash in `block_log`; they never change user data.
- Views are tracked by the hash of the applied `CREATE OR ALTER VIEW` text (in `ddl_log`); their column types are derived by the engine, so a view step has no predicted shape hash.
- `run` plans and applies only routine loads and refuses anything else, pointing to `plan`. `report` lists applied plans, DDL and load history, recorded objects and what needs attention.
- `plan --op model=operation` and `plan --backfill model=operation` choose operations; a backfill is a risky step recorded in `operation_interval`.
- `report` includes the per-column history report (12.3) built from the answers in applied plans; its configurable warning-or-block policy for downstream models is not built.
- Indexes are declared in the model (`indexes:`), never implied by `unique_key`; the planner creates missing ones, rebuilds changed ones (risky), and never drops undeclared ones. Hooks (`hooks:` in a model, `hook_groups:` in `dbdatabuild.yml`) are ordered, named, native-SQL scripts attached to events (`pre_`/`post_` create, alter, load, backfill; drop is reserved) per engine, and run as `hook` steps in plans.
- **Index lint and generation** (`IndexAdvisor`): the planner still creates only declared indexes, but `validate` and `plan` now advise. A key-based load (`merge_by_key`, `delete_insert_by_key`, or the implicit one of `incremental_by_unique_key`) whose key no declared index leads with (as a set of columns, on every target that runs the load) is **DDB-223, a warning**: each load scans the table. A key that is indexed but not declared `unique: true`, and the watermark, time and range columns of `watermark_append` and `delete_insert_by_range`, are **DDB-224, a note**. The message carries the exact line to paste under `indexes:` (`ux_<table>_<columns>` for a unique key, `ix_` otherwise, at most 60 characters). Advice never blocks. An operator silences a code for one model with `lint_ignore: [DDB-223]`, or all index advice with `lint: { indexes: false }` in `dbdatabuild.yml`. `define` asks, for a new model, whether to declare the suggested indexes (a normal-certainty proposal, so `--accept-inferred` never takes it); the existing-definition update path does not add indexes.

### 10.5 Plans live in the repo

Plans for shared targets are committed under `plans/<target>/` with their answers and intents. Dev and ephemeral plans are not committed, and neither are plans containing load steps with resolved parameter values (section 6.6). `migration_log` in the target records plan hash, plan text, git commit, and before/after hashes, so the target's audit trail is self-contained.

## 11. Planning decision table

Rows are (model kind x target condition). Every cell is an operation, a question, or an explicit refusal with a diagnostic code. A test enumerates the cross-product and fails on any undefined cell.

| Model kind | Target condition | Result |
|---|---|---|
| any | object missing | DDL: create |
| any | object exists, no tool record | **Question**: adopt existing object, or stop |
| any | shape hash differs from last recorded (out-of-band change) | **Block** until `ack drift` or re-plan |
| table kinds | model adds column | DDL add column + **Question** (history disposition) |
| table kinds | model removes column | destructive DDL (requires explicit allow) |
| table kinds | type widening / narrowing | safe / destructive |
| view | definition hash differs | DDL: alter view |
| incremental_* | definition changed since last load | **Block**; report differences; require `ack` or explicit backfill |
| incremental_by_unique_key | unique key missing in the model definition | **Unsupported**: DDB-2xx |
| any | model definition out of sync with its body (declared `columns` differ from resolved query output) | **Block planning** for that model: DDB-4xx, run `dbdatabuild define` |
| any | committed load file or resolver differs from a fresh render of the current model | **Block planning** for that model: DDB-4xx, run `dbdatabuild render --write` and commit |
| any | declared model x target x operation pair has no supported rendering | **Unsupported**: DDB-3xx, named pair in the message |
| any | required runtime parameter has no value | **Question** (`Q-param-...`), answered interactively or via answers file |
| any | watermark resolver returns NULL | Per declared `on_null`: question, or the committed initial literal |
| any | resolver returns other than one row and one column, or the wrong type | **Unsupported**: DDB-2xx |
| any | resolver value at `apply` differs from the plan | **Stale plan**: refuse |
| any | upstream blocked or failed | skip downstream, log as skipped |

Populate the full table during implementation and keep it in `matrix/decision-table.yml`.

## 12. Tracking tables

A dedicated schema (default `dbdatabuild`). Append-only by convention and permission. **[VERIFY]** each construct on Fabric. The T-SQL below is illustrative: each target generates its tracking-table DDL from one logical definition (column names, logical types, keys), since `nvarchar(max)`, `IDENTITY`, and `datetime2` have different equivalents on PostgreSQL.

```sql
CREATE TABLE dbdatabuild.schema_version (
  object_name      nvarchar(512) NOT NULL,
  shape_hash       char(64)      NOT NULL,
  physical_hash    char(64)      NULL,
  first_seen_utc   datetime2(3)  NOT NULL,
  source           varchar(32)   NOT NULL,   -- tool | out_of_band
  plan_id          varchar(128)  NULL,
  git_commit       varchar(64)   NULL
);
CREATE TABLE dbdatabuild.ddl_log (
  ddl_id bigint IDENTITY PRIMARY KEY, object_name nvarchar(512), statement_hash char(64),
  statement_text nvarchar(max), hash_before char(64), hash_after char(64),
  invoker nvarchar(256), plan_id varchar(128), git_commit varchar(64), executed_utc datetime2(3), status varchar(16)
);
CREATE TABLE dbdatabuild.run_log (
  run_id uniqueidentifier, step_id varchar(64), model nvarchar(512), operation varchar(32),
  rows_affected bigint, status varchar(16), plan_id varchar(128), git_commit varchar(64),
  definition_hash char(64), shape_hash_start char(64), shape_hash_end char(64),
  load_name varchar(128), load_file_hash char(64), resolver_file_hash char(64),
  parameters nvarchar(max),   -- runtime parameter values (all logged, section 6.6)
  watermark_used nvarchar(128), started_utc datetime2(3), ended_utc datetime2(3)
);
CREATE TABLE dbdatabuild.operation_interval (
  model nvarchar(512), run_id uniqueidentifier, range_start nvarchar(128), range_end nvarchar(128),
  shape_hash char(64), operation varchar(32)     -- load | backfill
);
CREATE TABLE dbdatabuild.block_log (
  block_id bigint IDENTITY PRIMARY KEY, model nvarchar(512), code varchar(16), detail nvarchar(max),
  created_utc datetime2(3), ack_by nvarchar(256) NULL, ack_reason nvarchar(max) NULL, ack_utc datetime2(3) NULL
);
CREATE TABLE dbdatabuild.migration_log (
  plan_id varchar(128), plan_hash char(64), plan_text nvarchar(max), git_commit varchar(64),
  applied_by nvarchar(256), applied_utc datetime2(3), hash_before char(64), hash_after char(64), status varchar(16)
);
```

### 12.1 Hashes

- **`shape_hash`**: column names, types, lengths, precision, scale, nullability, collation, computed definitions. Sorted by name; ordinals stored separately (the tool always emits explicit column lists).
- **`physical_hash`**: indexes, partitioning, compression. Tracked separately from shape.
- **`definition_hash`**: hash of the normalized DuckDB-dialect AST of the model body, so comments and formatting changes don't register. Also store per-target rendered-SQL hashes.

### 12.2 Capturing out-of-band DDL

- Tool-issued DDL: logged before execution, updated with outcome and resulting hash.
- Out-of-band changes (all targets) are **detected by catalog hashing**. Every `plan` and `apply` takes a catalog snapshot, computes the shape and physical hashes, and compares them with the last recorded `schema_version`. A mismatch is recorded as `out_of_band` and blocks per the decision table. The tool does **not** install DDL triggers or Extended Events sessions.
- Who changed what, with statement text, is a database-audit concern. For SQL Server, SQL Server Audit configured by DBAs is the authoritative record (documented in the operations guide). **[VERIFY]** equivalents on Fabric and PostgreSQL.

### 12.3 History consistency report

Joining `schema_version` to `operation_interval` and recorded history dispositions yields per-column reports such as: "added in schema version 7; intervals before it loaded with NULL; not backfilled; acknowledged by X: <note>". An unacknowledged inconsistency may be configured as a warning or as a block for downstream models, using lineage.

## 13. Validation layers

Cheapest to strongest:

1. **Config and model validation** (JSON Schema plus semantic checks). Reports all problems in one pass.
2. **Matrix lint** per declared target.
3. **ScriptDOM parse** of rendered T-SQL (syntax only).
4. **Schema-only compile** on the test engine: deploy empty tables from the declared schema, then run `sp_describe_first_result_set` on each rendered SELECT and compare result column names/types with the model's declared `columns` and with DuckDB's result types. Treat "cannot describe" results as *not checkable*, not as errors **[VERIFY]** on Fabric.
5. **Differential data test**: run the same synthetic data through DuckDB and through the target test engine, compare results (section 15.3).

## 14. Diagnostics

### 14.1 Format

```
error DDB-214  marts/fct_orders.yml:3
  Kind incremental_by_unique_key requires a unique_key, but none is set.
  Supported: unique_key: [<column>, ...]
  Fix: add under `kind:`  unique_key: [order_id]
  Docs: dbdatabuild explain DDB-214
```

- Stable codes (`DDB-1xx` config/model definition, `DDB-2xx` model semantics, `DDB-3xx` matrix/portability, `DDB-4xx` planning/decision table, `DDB-5xx` apply/runtime, `DDB-9xx` internal).
- Each diagnostic: code, location, what was found, what is supported, suggested fix, docs pointer.
- `docs/diagnostics/` is generated from the same metadata the validator uses.

### 14.2 Internal errors and data safety

- Unhandled exceptions are caught at the top level and reported as **internal error (tool bug)**: what the command was doing, which statements already ran (from the statement log), and what state the target is in. A stack trace is never the primary output.
- Full detail goes to a local log file **with values scrubbed**.
- **SQL Server error messages contain data values** (conversion failures, duplicate keys). Runtime diagnostics must keep error numbers, states, and object names, and **strip message text that may contain values**. A test feeds known data-bearing errors and asserts the values never appear in output or logs.
- Never log row values, statistics histograms, or execution plan literals. Declared runtime parameter values and resolver results are the one accepted exception (section 6.6).

## 15. Testing

### 15.1 Test engines

- The tool creates an **ephemeral database per run** (`CREATE DATABASE tmp_<runid>`, marker written so the tool can identify databases it created, with the target collation; partial containment where it avoids tempdb collation conflicts **[VERIFY]**) and drops it afterwards.
- `ITestEngine` implementations: existing instance via connection string (Developer edition installed natively on Windows or Linux), LocalDB (quick checks; Windows only; Express-engine limits), and optional Testcontainers. **Containers must never be required.** PostgreSQL follows the same pattern: a natively installed server, an ephemeral database per run (`CREATE DATABASE`), accessed via Npgsql.

### 15.2 Synthetic data

- DuckDB SQL scripts generate data: deterministic (fixed seed), committed, diff-friendly.
- A single schema source (the models' declared `columns` and source descriptors) generates both DuckDB DDL and target DDL. The type mapping lives in the matrix.
- **Edge-case packs**: trailing spaces, mixed case, empty string vs NULL, non-ASCII text, max-precision decimals, boundary dates, duplicate keys, late-arriving rows.
- Loading into the test engine: read from DuckDB and `SqlBulkCopy` into ephemeral tables.
- **`dbdatabuild sample` (as built)** uses the same idea for people: it fills every source a model reads from its declared columns (`SampleGenerator`: deterministic from a seed; NULLs in nullable columns; strings that differ only by case, accent or trailing space, empty strings, quotes; decimals on rounding boundaries; doubles with inexact text; month-end dates; unique grain columns, with a composite grain unique as a combination and its first columns repeating so joins and GROUP BYs find matches), or from `<schema.table>.csv` files in `--data <dir>`, then runs the selected models and everything they read in dependency order as `CREATE TABLE ... AS <query>` in an in-memory DuckDB with external access off, so a downstream model reads what its upstream produced. A type with no generator is refused with the way out (supply rows). It answers what a query returns on data like this, offline; it is not a differential test and says nothing about a target engine.

### 15.3 Differential comparison rules

Canonical row ordering or set hashing; exact decimal comparison; timestamps truncated to target precision; explicit NULL handling; collation-sensitive tests run under both case-sensitive and case-insensitive settings.

### 15.4 Seed conformance constructs

String comparison semantics (full case list in section 7.4), integer vs decimal division, `CAST` to VARCHAR without length, Unicode literals, NULL ordering, boolean-as-predicate vs `bit`, `DATE`/`TIMESTAMP` precision, `DECIMAL` result precision, `LIMIT`/`OFFSET` vs `TOP`/`OFFSET FETCH`, `QUALIFY`, `DATE_TRUNC`, `LIST`/`STRUCT`/`UNNEST` (expected unsupported), `COALESCE`/`IFNULL`, string functions (`LENGTH`, `SUBSTR`, `||`), `REGEXP`, window functions, `GROUP BY` ordinal, `ILIKE`, date arithmetic, integer overflow behavior. Target about 40 first.

### 15.5 Invariant and behavior tests

- **Zero writes**: every read-only command against a deliberately drifted database performs no writes.
- **Mutation gate**: enumerate call paths to `ExecuteNonQuery`; all must route through the gate.
- **Effect scope**: each command's recorded statements stay within its declared class and object scope.
- **Plan golden files** for every decision-table cell; **dry-run golden files** per command.
- **Stale plan** rejected; **hand-edited plan** rejected; **plan idempotency** (a second plan after apply is empty).
- **Out-of-band drift** blocks; **concurrent run** blocked by the application lock; **partial failure + resume** behaves as specified.
- **Diagnostics**: one fixture per code asserting message, location, and fix; a malformed-config fuzz set asserts no unhandled exception.
- **Questions**: same scenario yields the same question IDs; an answers file fully resolves them.
- **Definition generation** (`dbdatabuild define`): golden files for generated definitions; idempotency (second run yields no diff); comments, key order, and hand formatting preserved on update (minimal splices); the `.sql` file byte-identical before and after; unknown keys, duplicate keys, anchors/aliases, and custom tags rejected; orphan `.sql` or `.yml` files reported; stale-file write refused; `--check` writes nothing and fails on out-of-sync definitions; zero database connections during the command.
- **Load operations**: golden files for every rendered operation and resolver; `dbdatabuild render --check` fails on any difference and writes nothing; **the executed statement text equals the committed file text** (parameters bound, never interpolated; asserted from the statement log); only declared value parameters appear as placeholders (no identifier substitution of any kind); resolver contract violations (multiple rows, wrong type, NULL) produce the declared outcome; resolver value changing between plan and apply is refused; `max_span` and type constraints enforced; each strategy's result on the test engine is compared with its **DuckDB reference implementation** on synthetic data, including rerun idempotency and late-arriving rows.
- **Data safety**: error-message scrubbing test (section 14.2).

## 16. Milestones

1. **Spike (time-boxed)**: C# harness binding polyglot via FFI. Run about 40 constructs DuckDB to T-SQL through the conformance runner against a native test instance. Include PostgreSQL as a second render and differential target, since it is cheap at this stage and shows which matrix rows and assumptions are really T-SQL-specific. Outcome: confirm DuckDB-canonical is viable, seed the matrix, record polyglot gaps.
2. **Foundations**: project model, strict YAML loader (duplicate-key detection, comment-preserving edit support), JSON Schemas, diagnostics framework (`validate`, `explain`), matrix loader and linter, `render`, ScriptDOM check, the question/answer framework core, and `dbdatabuild define` (which needs DuckDB-based query description, so it may follow milestone 3's DuckDB runner).
3. **Test infrastructure**: ephemeral database manager, synthetic data runner, differential runner, schema-only compile (`sp_describe_first_result_set`).
4. **State and safety core**: tracking tables (`init`), hashes, read/write login split, mutation gate, statement log, invariant tests.
5. **Planning**: plan-specific questions (building on the question/answer framework from milestone 2), decision table, plan document and file format, `plan`, `check`, stale-plan and integrity checks.
6. **Apply**: `apply` (with dry-run, resume, risk classes), VIEW and FULL kinds, `ack`, `report`.
7. **Incremental kinds and load operations** (section 6.6, including `dbdatabuild render`, `dbdatabuild loads`, resolvers, and parameters): INCREMENTAL_BY_UNIQUE_KEY, then INCREMENTAL_BY_TIME_RANGE, backfill scope in `plan`, `run` shorthand.
8. **PostgreSQL target** (DDL, loads, apply, tracking tables), started right after milestone 6 if capacity allows because it exposes T-SQL assumptions in shared code; then the **Fabric target**, matrix expansion, and operations guidance for out-of-band DDL statement capture with SQL Server Audit (the tool itself detects drift by catalog hash).
9. **Hardening**: fuzzing, error-scrub tests, packaging (single-file publish), docs generation, SQL Agent invocation guide.

Each milestone ends with its tests green and this document updated.

## 17. Open decisions and verifications

**Decisions made** (reflected in the sections noted)

1. `dbdatabuild run` shorthand is kept: plan + apply, only for routine loads, refusing if the plan contains DDL or open questions (section 9.1).
2. FULL kind loads use transactional delete+insert. Table swap can be added later as a named load operation with its own step type and risk class (sections 6.1 and 6.6).
3. Out-of-band DDL is detected by catalog hashing on every target. The tool does not install triggers or Extended Events sessions. Statement-level capture is SQL Server Audit, configured by DBAs (section 12.2).
4. `columns` is required for all models (section 6.2).
5. Default string-comparison profile: case-insensitive, accent-sensitive, trailing spaces ignored (section 7.4).
6. All runtime parameter values and resolver results are logged. Redaction can be scoped and added later (section 6.6).
7. `dbdatabuild define --accept-inferred` exists, limited to high-certainty proposals (section 6.5).
8. The DuckDB-side `rtrim()` emulation rewrite is used for offline runs (section 7.4).
9. Model definitions are YAML files beside plain `.sql` bodies, with no custom grammar and no macros. Load strategies wrap the body (section 6).

**[DECIDE]** None open at the moment.

**[VERIFY]** (do before depending on them)
- polyglot-sql: **spike done for 47 constructs (`spike/RESULTS.md`)**: FFI binding and pinned build work; fidelity gaps found, including silent wrong rewrites, `fabric` dialect gaps, and an incomplete `unsupportedLevel: raise`, so the matrix linter must walk the AST itself. Still open: round-trip stability for comment-preserving substitution, pre-1.0 API churn, Windows build and single-file publish, native distribution plan.
- DuckDB plan lowering: **built** (section 7.6; research in `docs/research/duckdb-plan-lowering/README.md`). Open: target-specific rules and `sum` widening, listed in `docs/progress/OPEN-ITEMS.md`.
- ScriptDOM: available parser versions; Fabric-specific syntax coverage.
- Fabric Warehouse: MERGE/ALTER/TRUNCATE/rename support, trigger support, extended properties, query history retention.
- `sp_describe_first_result_set`: limits (temp tables, dynamic SQL) and Fabric availability.
- Tracking-table constructs on Fabric (identity columns, constraints, `nvarchar(max)`).
- Collation behavior (section 7.4): DuckDB `NOCASE` under `GROUP BY`, `DISTINCT`, joins, and window operations; behavior of chained collations under `GROUP BY`, `DISTINCT`, joins and windows (equality chaining `NOCASE.NOACCENT` is verified on DuckDB 1.5.4 in either order); Fabric Warehouse default and available collations, and its trailing-space and `LEN` semantics.
- PostgreSQL target: polyglot DuckDB-to-Postgres fidelity; offline syntax validation options (a libpg_query binding for .NET, or polyglot validation); schema-only compile via prepare/describe; advisory-lock and tracking-table DDL equivalents; `MERGE` availability by version; identifier case-folding and quoting.
- Load operations: `@name` placeholders are bound by SqlClient and Npgsql, and ScriptDOM parses parameterized scripts (both verified by the conformance suite and unit tests). Still open: Fabric support for the staging, delete+insert and merge templates inside transactions. Predicate pushdown is decided (advice, never a rewrite): section 6.6.2.
- YAML handling: `YamlDotNet` duplicate-key detection and parser source marks for comment-preserving splice edits; editor association of the JSON Schemas by file glob.
- Licenses of all native and managed dependencies.
