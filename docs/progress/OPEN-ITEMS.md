# Open items and status

Written 2026-10-02, after the lowering order (subqueries, `DISTINCT ON`, integer series) was finished. Unit tests 956, real-engine tests 75 (SQL Server 2022, PostgreSQL 17). Fabric has never been run against a real engine.

## Where the milestones stand (DESIGN.md section 16)

| # | Milestone | State |
|---|---|---|
| 1-3 | Spike, foundations, test infrastructure | Done |
| 4 | State and safety core | Done (gate, logins, statement log, lock, invariant test) |
| 5-6 | Planning, apply | Done, with risk classes, resume, hooks, indexes, backfill, `ack`, `report` |
| 7 | Incremental kinds, loads, `run` | Done |
| 8 | PostgreSQL target | Done and verified. **Fabric target: written, unverified.** Operations guidance for SQL Server Audit: not written |
| 9 | Hardening | **Mostly done** (entry 25): seeded fuzzing of config, source, model, SQL, answers and plan files; the real-engine error-scrub test; linux-x64 single-file publish; operations guide (`docs/operations.md`). Open: Windows publish, fuzzing of the interactive question flow and of hook scripts, docs generation, pre-1.0 polyglot upgrade policy |
| extra | Plan lowering | Built (section 7.6) |

## A. Lowering: open items (the ones you asked to have written down)

1. **Target-specific rules: built** (DESIGN.md 7.6.1, entry 24): `length`, `round` of a double, `TRY_CAST` of a string. Remaining: no rule yet for `TRY_CAST` to a date on PostgreSQL, and for the `'12.7'`/`'1e3'` string-to-integer differences; other behavior differences will be found by more probes (for example string functions on non-BMP characters, `substr` negative positions, `%` on negatives, division by zero).
2. **`sum` widening: built** (lowering rule `sum-widen`). `sum` of DECIMAL needs no rule on SQL Server (both give DECIMAL(38, s)); PostgreSQL gives NUMERIC without a scale limit, which a declared column then constrains. Not yet probed: `avg` and `sum` over very large DECIMALs.
3. **`x op ANY/ALL (subquery)` and row-value `IN`.** Refused today. Both can be written with `EXISTS`/`NOT EXISTS` plus null handling; the risk is three-valued logic, so each needs differential tests including NULLs.
4. **Correlated subquery over `UNION`/`INTERSECT`/`EXCEPT`, a window partitioned by a correlated value, a correlated `LIMIT` with an offset.** Refused; no plan to build unless a real model needs them.
5. **Author table aliases are lost** (DuckDB's plan does not carry them), so lowered sources are named after their tables (`orders`, `orders_2`). Cosmetic, but it makes lowered queries less similar to the source than you asked for. Possible fix: recover aliases by matching the source text to the plan, or rename on a per-query basis from the parsed AST.
6. **Date/timestamp series, `UNNEST`, list/struct constructors, `USING SAMPLE`, `LIMIT ... PERCENT`.** Refused by decision. A date series could be an integer series plus `DATEADD`/interval arithmetic; I left it out because every interval unit needs its own differential check.
7. **Engine limit, not a lowering gap**: SQL Server rejects an aggregate over a subquery (`sum((SELECT ...))`). Could be worked around by lifting the subquery into a join; not done.
8. **Plan JSON is DuckDB-internal.** A DuckDB upgrade can change plans, which shows as a stale lowered artifact in `render --check`. The header records the DuckDB version; there is no tooling to explain *why* an artifact changed.
9. **Evidence gaps**: the `fn.generate_series` matrix row cites a spike case that has not been run (the conformance test is the real evidence). Re-run the spike when convenient.

## B. Features designed or reserved but not built

- **Hook events for drops and other reserved kinds** (`HookEvents` marks them `Fires = false`; the model loader rejects them with a message). Only the events the planner actually performs are accepted.
- **`define` asking about extra loads** (section 6.5): not built.
- **Index lint and generation: built** (entry 26). Not built: adding indexes when `define` updates an existing definition (the surgical editor has no block insertion), a `drop`/exclusive setting for undeclared indexes (the planner still never drops one), and lint for the partial or filtered indexes the model syntax cannot express yet.
- **JSON Schemas for each command's `data` and the metadata documents: built** (entry 27). Not done: marking keys as required per outcome (the schemas only close the key sets and fix types), and a published JSON Schema for the plan YAML's embedded JSON beyond `plan.schema.json`.
- **History consistency as a *blocking* condition** (section 12.3: "may be configured as a warning or as a block for downstream models, using lineage"): only the warning and the acknowledgement exist. The `report` doc comment still says the per-column report is not built; the code reads history, so the comment is stale.
- **Redaction** of logged parameter values and resolver results (decision 6 says it can be added later).

## C. Unverified or risky areas

- **Fabric**: every Fabric matrix row is `unverified`. Needs a real Fabric Warehouse to confirm MERGE/ALTER/TRUNCATE/rename, `nvarchar(max)` and constraints in the tracking tables, `sp_describe_first_result_set`, trailing-space and `LEN` behavior, collations, and the `GENERATE_SERIES` form. The draft upstream issue notes exist but you have not decided to submit them.
- **Lock, resume and failure paths** are tested on SQL Server and PostgreSQL, but only on single local containers: no concurrency between two real `apply` processes under load, no network failures mid-step.
- **Native dependency**: polyglot-sql 0.13.1 is pinned and built from source by `scripts/build-polyglot.sh`; pre-1.0 API churn, a Windows build and a distribution plan are open (section 17).
- **Windows**: builds from Linux and runs under Wine (entry 34): unit suite 1,087 of 1,087, the SQL Server real-engine tests pass. Not done: running on real Windows, the single-file executable (cannot start under Wine 9), the terminal interface on a Windows console, PostgreSQL (Wine lacks the PBKDF2 Npgsql's SCRAM login needs), code signing and an installer, and a win-arm64 build.
- **Licenses**: audited 2026-10-02 (all permissive; see `THIRD-PARTY-NOTICES.md`); re-check when dependencies change.
- **Collation**: chained collations under `GROUP BY`/`DISTINCT`/joins/windows on DuckDB and the engines are only partly verified (section 17). Live collation checks exist for SQL Server and PostgreSQL.
- **Error scrubbing and fuzzing**: done for the file inputs (entry 25). Not covered: fuzzing the interactive answer flow, hook scripts' content, and the resolver query results.

## D. Documentation debt

- DESIGN.md section 17 said plan lowering was "researched twice, not built"; corrected in this commit.
- Operations guide written (`docs/operations.md`; Fabric parts say "not checked"). Not written: user-facing getting-started docs, a generated command reference.
- REVIEW.md accumulates dated updates; it needs a consolidated rewrite before anyone else reads it.

## E. Suggested order (for you to change)

1. Target-specific rules and `sum` widening (they change query results, so they matter most for correctness), starting with the design question in A1.
2. Milestone 9 hardening: error-scrub fuzzing, single-file publish on linux and Windows, operations guide.
3. `ANY`/`ALL`, row-value `IN`, index lint/generation, per-command JSON Schemas, as demand appears.
4. Fabric verification (moved to the back by your decision, 2026-10-02): needs a real Fabric instance; otherwise Fabric stays unverified for the first release.

## F. Terminal interface and agents (added 2026-10-02)

- **TUI gaps**: progress and stop-between-steps are built (entry 33); a stop cannot interrupt a long single statement (by design: a started statement is never abandoned), and a `plan` or `sample` that takes minutes cannot be cancelled; forms do not scroll on a terminal shorter than the longest form (`plan`, 9 fields); no menu bar or mouse testing; Windows terminals not tried; view code is covered only by the pty walk-through (`scripts/tui_drive.py`), not by unit tests; the target chosen in the TUI is not shown in the title until the next screen change; `define` is reachable but its interactive prompts are answered through dialogs only for open questions, not for the accept/inferred flow.
- **Sample data gaps**: no `--target` emulation (it runs in DuckDB only, so string-semantics emulation of a target is not applied); a source whose type has no generator needs a CSV; no PIVOT/seed values yet; generated values do not respect CHECK-like rules that are not declared.
- **Agents**: no MCP server (a thin wrapper over the command layer would add typed tools and schema resources; `apply` and the other writers would stay off by default); the skill has not been tried by a real agent on a real task (the tests keep it true, not useful, so it needs a trial run and revision from what an agent gets wrong); no per-engine variants of the skill (it says Fabric is unverified).

## G. After the slice and parameter work (2026-10-02)

- Predicate pushdown and runtime parameters are no longer open questions; what remains: a first-class **custom slicing operation** (author-written filter with a declared parameter; allowed today by writing it in the query and using `full_replace` or a key load, but not modelled), **seeded parameter values for `sample`** so the strategies can be previewed, and a check that a parameter's type is one of date, timestamp, integer or short text (everything is logged).
- The slice lint follows plain column lineage; it does not judge sargability of an expression slice column (`CAST(ts AS DATE)`) or whether the slice column is indexed on the source.

## I. Sources and metadata (2026-10-02)

- **`import-sources`** (entry 36) is built and verified on SQL Server 2022 and PostgreSQL 17; Fabric is unverified (the catalog queries are the SQL Server ones). Open: collation is not exported; indexes and foreign keys of sources are not exported; a refresh rewrites a descriptor without its comments; `--check` needs a database login in CI.
- **Metadata** now covers the project, sources and models. Not yet in metadata: the diagnostics catalog and the support matrix (both are printed by `explain` and `matrix`, and the matrix hash is in the project document), observed live shapes of sources (`import-sources --format json` has them), and the tool's own tracking tables.

## H. DuckDB 2.0 and the repository (2026-10-02)

- **DuckDB 2.0 adoption** (see `docs/research/duckdb-2.0/README.md`): lowering work is done (all unit and real-engine tests pass on the alpha). Open: it depends on the deprecated `delim_join_as_cte` setting (if it is removed, write the inverse decorrelation: 16 forms); wait for a DuckDB.NET release built for 2.0; then regenerate committed lowered artifacts (their headers carry the DuckDB version) and make 2.0 the default. A CI job running `scripts/test-duckdb-preview.sh` weekly would show convergence.
- **GitHub**: the repository is private; no CI workflow yet (a workflow needs the polyglot library: building it takes a Rust toolchain and a few minutes, so cache `native/`); no branch protection, issue templates or release process; commit author is `dlshryoc` with no address (commits will not link to the GitHub account until the author identity is set for future commits).

