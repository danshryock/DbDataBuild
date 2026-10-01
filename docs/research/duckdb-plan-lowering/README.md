# Research: lowering DuckDB model SQL through DuckDB's binder (2026-09-30)

**Question.** Can DuckDB expand things that need its own binder (macros, PIVOT with runtime values, `SELECT *`, `COLUMNS()`) before we transpile through polyglot, so we gain those features without implementing or gating each one?

**Status.** Research only. Nothing in `src/` uses this. The code here is the prototype, kept as evidence. DuckDB CLI 1.5.4, polyglot 0.13.1.

## Findings

| Hook | Result |
|---|---|
| `json_serialize_sql` / `json_deserialize_sql` | Parser AST only, SELECT only. Macros are not expanded and PIVOT is not desugared. Round-tripping gains nothing. |
| `json_serialize_plan(sql, optimize := false)` | Bound logical plan with macros, defaults, PIVOT, `*` and implicit casts expanded. Serialize-only: DuckDB has no plan-to-SQL function. All 93 spike queries bind and serialize. |
| Macros | Expanded only at bind. The catalog (`duckdb_functions()`) holds body text and parameter names, but not defaults. |
| Dynamic PIVOT | Needs data at bind time. On an empty table it silently yields no pivot columns (dangerous for our empty-table describe sessions). The statement form (`PIVOT t ON ...`) failed in `json_serialize_plan` (not investigated); polyglot passes it verbatim to T-SQL, which is wrong. |
| Seeding PIVOT values | `getvariable()` cannot feed `PIVOT ... IN`. An **enum type** works: `IN yr` with `CREATE TYPE yr AS ENUM (...)` needs no data. `query()` also does bind-time dynamic SQL. |
| Plan to SQL (prototype) | `unparse.py` turns the unoptimized plan into core SQL. Result-equal to DuckDB on the 6 simple cases tried, and on enum-seeded PIVOT. Handles `LOGICAL_GET`, `PROJECTION`, `FILTER`, `AGGREGATE_AND_GROUP_BY` only. |

Operators across the 93 spike cases (`surface.py`; the numbers differ by one for PROJECTION and GET from an earlier hand run): PROJECTION 103, GET 98, FILTER 19, DUMMY_SCAN 9, COMPARISON_JOIN 8, AGGREGATE_AND_GROUP_BY 6, WINDOW 5, DISTINCT 5, LIMIT 3, ORDER_BY 3, MATERIALIZED_CTE 3, CTE_REF 3, CROSS_PRODUCT 3, UNION 2, DELIM_JOIN 2, DELIM_GET 2, UNNEST/SAMPLE/INTERSECT/EXCEPT 1 each. A full unparser needs about 20 operator kinds. Correlated subqueries (DELIM_JOIN), CTE references and windows are the hard ones.

## Risks

- The plan JSON is internal. I found no documented stability guarantee (the docs fetch returned only a redirect page, so absence is unconfirmed). Pin DuckDB and test the unparser against it.
- Internal function names leak into the plan (`count_star`, `~~`, `sum_no_overflow`, `list_value`, `struct_pack`, `to_days`) and need mapping back to surface SQL.
- Output is noisy (explicit casts, `IS NOT DISTINCT FROM` filters) and harder to review than the author's SQL.
- The optimized plan must not be used: it reorders and rewrites.
- If the lowered SQL replaced the author's SQL, linter diagnostics would lose source spans.

## Proposed design (not adopted)

1. Author's SQL stays the source of truth; the matrix linter runs on it as today.
2. Optional lowering (`DuckDB bind, then unparse to core SQL`) only for constructs the matrix marks `lowerable`: macros, table macros, PIVOT/UNPIVOT, star variants, `COLUMNS()`. An unsupported operator raises a diagnostic and falls back to current behavior.
3. PIVOT requires seeded values (enum type or values declared in model YAML). Unseeded dynamic PIVOT is a hard error, never an empty result.
4. Macros come from project-supplied macro files loaded into the describe session, which also supplies defaults.
5. Lowered SQL is shown in `explain` and written to `rendered/`.
6. A differential test: original and lowered queries return equal results on DuckDB.

Suggested order: enum-seeded PIVOT and macros first, then joins, windows and delim joins.

## Files

- `setup.sql`: schema `staging` (`cities`, `wide`), scalar macro `net(a, tax := 0.2)`, table macro `big_cities(min_pop)`, enum `yr`.
- `unparse.py`: plan-to-SQL prototype (`python3 unparse.py`, needs the `duckdb` CLI).
- `tree.py`: prints a plan tree.
- `surface.py`: operator, expression and function tally over `spike/constructs.yml`.
