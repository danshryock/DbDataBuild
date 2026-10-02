# DuckDB 2.0 (Cyanoptera): what it changes for dbdatabuild

Investigated 2026-10-02 against **2.0.0-alpha44073** (the `v2.0-cyanoptera` branch, built the same day). DuckDB says it releases 2.0 "in the second half of October"; the branch is feature-frozen and now takes only fixes. We run **DuckDB 1.5.4 (CLI) and 1.5.6 (through DuckDB.NET 1.5.6)** today.

## Bottom line

- **Nothing in dbdatabuild's SQL semantics breaks.** On the 94-construct corpus (`spike/constructs.yml` on the seeded data) 1.5 and 2.0 return the same rows, errors and types. The script reports 4 differences: one is a column name (below), and three are noise (the CSV header printed on an error path, and a random `USING SAMPLE`). The query-level breaking changes in DuckDB 2.0 (integer `//` by zero raises, `~` matches like PostgreSQL, the lambda arrow errors) do not touch anything dbdatabuild generates or accepts.
- **One real incompatibility: the serialized plan.** DuckDB 2.0 restructured `json_serialize_plan`, which the lowerer reads. About **1,057 of 1,074 unit tests pass on 2.0 after a normalizer** (`PlanNormalizer`, committed, harmless on 1.x). The 17 that fail are all **correlated scalar subqueries that return an aggregate** (the decorrelation shape changed; details below). EXISTS, IN, NOT EXISTS, LATERAL-free forms, joins, windows, DISTINCT ON, integer series, CTEs, every target rule, `define`, `sample`, `render`, `plan`'s offline half all pass.
- **DuckDB.NET has no 2.0 build yet** (latest 1.5.6; its `develop` branch is active and a `nightly-builds` branch exists). The *old* C API still works against the 2.0 library, which is why the experiment could run with only the native library swapped.
- **Recommendation:** keep 1.5.6 as the default now. Land 2.0 support in this order: (1) the plan normalizer and a CI job that runs the suite against the preview (done: `scripts/test-duckdb-preview.sh`); (2) finish the correlated-scalar lowering once a **release candidate** exists, because the plan shape is the one thing still allowed to move; (3) switch the pinned version when DuckDB 2.0.0 and a matching DuckDB.NET are both out. Adopting the alpha as the default now would regress correlated scalar subqueries, which are a hard requirement.

## How it was tested

```
scripts/test-duckdb-preview.sh        # downloads the preview libduckdb, copies it over the one in the test output, runs the unit tests
python3 docs/research/duckdb-2.0/corpus_diff.py        # 94 constructs on two CLIs; DUCKDB_OLD / DUCKDB_NEW name the two binaries (the preview CLI is duckdb-cli-linux-amd64.tar.gz from https://artifacts.duckdb.org/v2.0-cyanoptera/)
python3 docs/research/duckdb-2.0/plan_kinds.py         # which plan node kinds and keys differ across 134 queries
duckdb -batch < docs/research/duckdb-2.0/breaking_changes_probe.sql    # the breaking-change probes, run on each CLI and diffed
```

## The serialized plan

DuckDB's plan JSON is internal, so a major version moving it is expected. The differences found by `plan_kinds.py` (134 queries, all of which plan on both):

| 1.5 | 2.0 | Handled by |
|---|---|---|
| `"name": "sum", "schema_name": "main", "catalog_name": "system"` on functions, aggregates, windows and scans | `"qname": {"path": ["system", "main", "sum"]}` | normalizer: last element is the name |
| comparisons: `BOUND_COMPARISON` with `left` and `right` | `BOUND_FUNCTION` of type `COMPARE_*` with `children: [l, r]` | normalizer |
| casts: `BOUND_CAST` with `child` and `try_cast` | `BOUND_FUNCTION` of type `OPERATOR_CAST`, `children: [x]`, `function_data.try_cast` | normalizer |
| `NOT IN`: `COMPARE_NOT_IN` | `OPERATOR_NOT` over `COMPARE_IN` | normalizer (so the lowered text is unchanged) |
| `lead`/`lag`: `offset_expr`, `default_expr` beside the argument | all in `children` | normalizer |
| `LOGICAL_GET` carries `name`, `catalog_name`, `schema_name` | `qname`, plus `projection_ids`, `table_filters`, `source_ordinality` and more (ignored) | normalizer |
| **correlated EXISTS / IN**: `LOGICAL_DELIM_JOIN` of `MARK` type, with `DELIM_GET` frames and the correlated filter inside the subquery | a plain `LOGICAL_COMPARISON_JOIN` of `MARK` type; the correlation is **join conditions** (`t.a IS NOT DISTINCT FROM u.a`) and the subquery side is just `u` with `a IS NOT NULL` | `MarkJoin` rewritten: EQUAL is an IN, other conditions go back into the subquery's WHERE; the `IS NOT NULL` guard is removed again so the text reads `u.a = t.a` |
| **correlated scalar subquery with an aggregate** | decorrelated into a **LEFT join to a grouped derived table** (`t LEFT JOIN (SELECT max(b), a FROM u WHERE a IS NOT NULL GROUP BY a) ON t.a = a`); `count(*)` additionally gets a CASE and an empty-result branch for the COUNT bug | **not done** |

```jsonc
// 1.5                                        // 2.0
{"type":"COMPARE_GREATERTHAN",                {"type":"COMPARE_GREATERTHAN",
 "expression_class":"BOUND_COMPARISON",        "expression_class":"BOUND_FUNCTION",
 "left":{...}, "right":{...}}                   "qname":{"path":[">"]}, "children":[{...},{...}]}
```

### What is left for 2.0: correlated scalar subqueries

17 test forms fail on the alpha, in three shapes: a `SINGLE` or `LEFT` join to a grouped derived table (`SELECT id, (SELECT max(b) FROM u WHERE u.a = t.a) FROM t`), the `count(*)` version with `LOGICAL_EMPTY_RESULT` and a CASE, and a `SEMI` join (a new join type in unoptimized plans). The lowering is a pattern match that turns the decorrelated join back into the subquery the author wrote:

```sql
-- the author wrote                                   -- the 2.0 plan describes
SELECT id, (SELECT max(b) FROM u WHERE u.a = t.a)     SELECT t.id, g.m FROM t LEFT JOIN
FROM t                                                  (SELECT a, max(b) AS m FROM u WHERE a IS NOT NULL GROUP BY a) g ON t.a = g.a
```

Both are correct; the first is what we promised ("reasonably similar to the source"). The inverse needs the shapes to be final. DuckDB has **no setting** to keep the old decorrelation (checked `duckdb_settings()`), so this cannot be avoided by configuration.

## Behavior changes found

| Change | Affects us? | Action |
|---|---|---|
| `SELECT NULL AS x` is typed `"NULL"` (SQLNULL), was INTEGER | **Yes: `define --check` reported a false type drift** (declared INTEGER, "resolves NULL"), and `define` would have proposed nothing useful | **Fixed** (version-agnostic): an untyped NULL fits any declared type and is never proposed; the question says to `CAST(NULL AS <type>)` |
| Output name of an unaliased function: `main.trim(s)` becomes `trim(s)` | Lowered artifacts that name such a column change on upgrade (the header already records the DuckDB version, so `render --check` shows it) | none: expected churn |
| `//` (integer division) by zero raises (was NULL); setting `error_on_division_by_zero` | No: `/` is float division and unchanged; the tool never writes `//` | note in the matrix if a model uses `//` |
| Regex operator `~` matches anywhere (PostgreSQL behavior), was a full match | No: the tool uses `regexp_matches`, whose behavior is unchanged | none |
| Lambda arrow `x -> x + 1` is an error by default | No: list functions are refused in lowering | none |
| Negating an unsigned integer errors | No | none |
| A scalar subquery that returns more than one row errors by default (`scalar_subquery_error_on_multiple_rows`) | Positive: now matches SQL Server and PostgreSQL, so the "first() with error guard" recognizer matches the engines | none |
| Transactions are invalidated by any error | No: DuckDB is used offline and in memory | none |
| ICU removed (timezone, calendar, collation code moved into the engine) | Collations used by the string profile (`NOCASE`, `NOACCENT`, `NFC`, chained) gave identical results, including under `GROUP BY` | none |
| PEG parser replaces the PostgreSQL-derived one | No difference on the corpus; plan JSON `query_location` values moved by a couple of characters | none |
| New C API (versioned, stable ABI); the old one remains | DuckDB.NET 1.5.6 runs unchanged against the 2.0 library | wait for a DuckDB.NET release built for 2.0 |
| CLI prints an "agent mode" notice on stderr when `AI_AGENT` is set (and stdout is not a terminal) | Only for people (or agents) running the DuckDB CLI by hand | none |

The matrix needed no change: it describes engines other than DuckDB, and DuckDB's side of every row (the oracle) behaved the same.

## When to move

- **Now:** nothing breaks on 1.5.6, and the normalizer is in. Run `scripts/test-duckdb-preview.sh` weekly (or in CI) to watch the alpha converge; each run says how many forms still fail.
- **When the first release candidate appears:** finish the correlated-scalar lowering against it (budget a day or two), and re-run the real-engine suite with the preview library, since lowered text feeds the target engines.
- **When 2.0.0 and a DuckDB.NET release for it exist:** bump `DuckDB.NET.Data.Full` and `Bindings.Full`, regenerate the committed lowered artifacts (their headers change), run both suites, and make 2.0 the default. Keep the 1.x path alive for one release: DuckDB 1.4 is an LTS line and 1.5 stays supported for a while, and the normalizer costs nothing.
