# DuckDB 2.0 (Cyanoptera): what it changes for dbdatabuild

Investigated 2026-10-02 against **2.0.0-alpha44073** (the `v2.0-cyanoptera` branch, built the same day). DuckDB says it releases 2.0 "in the second half of October"; the branch is feature-frozen and now takes only fixes. We run **DuckDB 1.5.4 (CLI) and 1.5.6 (through DuckDB.NET 1.5.6)** today.

## Bottom line

- **Update 2026-10-02 (later the same day): the whole suite passes on the 2.0 alpha.** Unit tests 1,084 of 1,084 and the real-engine suite 83 of 83 (SQL Server 2022 and PostgreSQL 17, with the alpha `libduckdb` swapped in), same as on 1.5.6. Across 207 lowering queries the lowered text differs on only three cosmetic points (below), so committed artifacts hardly change on upgrade.
- **Nothing in dbdatabuild's SQL semantics breaks.** On the 94-construct corpus (`spike/constructs.yml` on the seeded data) 1.5 and 2.0 return the same rows, errors and types. The script reports 4 differences: one is a column name, and three are noise (the CSV header printed on an error path, and a random `USING SAMPLE`). The query-level breaking changes in DuckDB 2.0 (integer `//` by zero raises, `~` matches like PostgreSQL, the lambda arrow errors) do not touch anything dbdatabuild generates or accepts.
- **One real incompatibility, now handled: the serialized plan.** DuckDB 2.0 restructured `json_serialize_plan`, which the lowerer reads. `PlanNormalizer` rewrites a 2.0 plan into the 1.x shape (identity on 1.x). Correlated subqueries needed one more thing: DuckDB 2.0 decorrelates them into materialized CTEs and joins to grouped derived tables, which cannot be turned back into the author's subquery without pattern-matching every rewrite. DuckDB's setting **`delim_join_as_cte = false`** brings the old delim-join form back, and `QueryDescriber.PreparePlanConnection` sets it whenever it exists. **That setting is marked deprecated** ("will be removed in a future release"). Without it 16 of the 1,084 tests fail (correlated subqueries again: see "If the setting goes away").
- **DuckDB.NET has no 2.0 build yet** (latest 1.5.6; its `develop` branch is active and a `nightly-builds` branch exists). The *old* C API still works against the 2.0 library, which is why the experiment could run with only the native library swapped.
- **Recommendation (revised):** 2.0 can now be adopted as soon as 2.0.0 and a matching DuckDB.NET exist: no lowering work stands in the way. Until then 1.5.6 stays the default. Run `scripts/test-duckdb-preview.sh` (and with `TEST_PROJECT=tests/DbDataBuild.Tests.Conformance` against the engines) on each alpha or release candidate; if the deprecated setting disappears before or at 2.0.0, do the inverse decorrelation described below.

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

### If the setting goes away: correlated scalar subqueries

Without `delim_join_as_cte = false` the alpha plans correlated scalar subqueries as a `SINGLE` or `LEFT` join to a grouped derived table, a `count(*)` additionally gets a CASE and an empty-result branch (the COUNT-bug guard) wrapped in materialized CTEs (`LOGICAL_MATERIALIZED_CTE` / `LOGICAL_CTE_REF`), and a `SEMI` join appears. 16 test forms fail in that configuration. Turning it back into the author's subquery is a pattern match:

```sql
-- the author wrote                                   -- the 2.0 plan (setting off the old shape) describes
SELECT id, (SELECT max(b) FROM u WHERE u.a = t.a)     SELECT t.id, g.m FROM t LEFT JOIN
FROM t                                                  (SELECT a, max(b) AS m FROM u WHERE a IS NOT NULL GROUP BY a) g ON t.a = g.a
```

Both are correct; the first is what we promised ("reasonably similar to the source"). The pieces already written help: `TryCountPatch` recognizes the COUNT-bug guard, and `MarkJoin` already reads the 2.0 MARK join for EXISTS and IN.

### What the adaptation changed in the lowerer

| Where | Change |
|---|---|
| `PlanNormalizer` | names from `qname`; comparison, cast and BETWEEN functions back to their node kinds; `NOT` over `IN` is `NOT IN`; `NOT` over a comparison is the opposite comparison (2.0 no longer folds it); `lead`/`lag` arguments, with an offset of 1 and a NULL default dropped as 1.x did |
| `MarkJoin` | EXISTS and IN arrive as MARK joins with the correlation in the join conditions; EQUAL is an IN, the rest go back into the subquery's WHERE (its own column first: `u.a = t.a`), and the `IS NOT NULL` guard that makes null-safe equality an equality is removed again |
| `TryCountPatch` | the COUNT-bug guard shape is just the grouped count |
| right projection map | 2.0 lists the right-side columns a join outputs (`right_projection_map`); absent means all, as in 1.x |
| single-row cross product | an ungrouped aggregate next to another relation is a scalar subquery again: `WHERE a > (SELECT min(a) FROM u)` instead of a CROSS JOIN |
| `QueryDescriber.PreparePlanConnection` | sets `delim_join_as_cte = false` when the setting exists |

The three remaining text differences are cosmetic and equivalent: `SELECT NULL AS x` is no longer cast to INTEGER (the column is untyped NULL), `date_part('year', d)` is `year(d)`, and an unaliased `trim(s)` column is named `"trim"(s)` instead of `main."trim"(s)`.

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

- **Now:** nothing blocks the move except the two packages: run `scripts/test-duckdb-preview.sh` (unit) and `TEST_PROJECT=tests/DbDataBuild.Tests.Conformance scripts/test-duckdb-preview.sh` (real engines) on each alpha or release candidate, watching for the deprecated setting.
- **When 2.0.0 and a DuckDB.NET release for it exist:** bump `DuckDB.NET.Data.Full` and `Bindings.Full`, regenerate the committed lowered artifacts (their headers carry the DuckDB version), run both suites, and make 2.0 the default. Keep the 1.x path for one release: DuckDB 1.4 is an LTS line and 1.5 stays supported for a while, and the normalizer costs nothing.
- **If `delim_join_as_cte` is removed first:** write the inverse decorrelation described above (a day or two, with the 16 forms as the test).

## Update 2026-10-03: ANY / ALL / row-value IN

The lowering of `x op ANY/ALL (subquery)` and row-value `IN` (progress entry 39) was written and tested against 1.5.x plans. On the 2.0 alpha these plans are different (a count-based `CASE` over a scalar subquery, and an `EMPTY_RESULT` operator for an empty subquery), 9 of the new unit tests fail there (6 value-use refusals that are not refused, and 3 filter cases: two refused with "the plan operator EMPTY_RESULT" and one with **wrong rows**). The default engine is unaffected. Rework them when 2.0 is adopted; the differential tests will show every gap.
