# What the sample projects found

Every template under `templates/` is built on SQL Server 2022 and PostgreSQL 17 by `TemplateConformanceTests` and its marts are compared, row by row, with the same
models run on the same seeds in DuckDB. Writing the templates is how a construct meets an engine, so each problem they hit is written down here with what was done:

- **fixed**: the tool now produces the right query (a lowering change or a target rule, with a unit test and, where an engine is involved, a probe in `EngineDifferenceProbes` or the template test).
- **refused**: the tool says so at `validate` or `render` (a matrix row with `unsupported`, or a lowering refusal DDB-324), and the template writes the transform another way.
- **cataloged**: a difference that is real and understood, with its row in `matrix/constructs.yml` or `docs/research/engine-differences/README.md`; the engine's answer or error is known.

The older findings (from the probes, before the templates) are in `docs/research/engine-differences/README.md`.

## chinook

| # | Construct | What happened | Disposition |
|---|---|---|---|
| C1 | a project with `sources/` but no `models/` | the sources were not loaded, so `seed` said the seed was for a source that did not exist | **fixed**: `ProjectValidator` loads the sources and reports the missing `models/` directory as the error |
| C2 | `a // b` in an authored query | the parser that reads authored queries (lineage, nullability, the hash) cannot read `//` (DDB-306), although DuckDB can; found in `retail` and again in `chinook` | **fixed**: `SqlParseHints.ForParser` gives the parser `a / b` (same length, strings and comments untouched). Only lineage and the hash come from that parse, and they are the same for both operators; the meaning comes from DuckDB's plan, which lowers `//` itself |
| C3 | `lpad(s, n, p)`, `rpad` | SQL Server has neither (before 2025); the probes had them as `unsupported` | **fixed** for a literal length up to 4000 and a literal non-empty pad: target rule `pad-to-length` (`LEFT(REPLICATE(p, n), n - LEN(s)) + s`, `LEFT(s, n)` when too long); any other use is still refused. Remaining difference: a character outside the BMP counts as two on SQL Server |
| C4 | `split_part(s, sep, n)` | DuckDB expands the macro into `array_extract(string_split(s, sep), n)`, which neither engine has: both rendered invalid SQL and the linter only warned (DDB-305) | **fixed** on PostgreSQL (target rule `split-part`, its own `split_part`); **refused** on SQL Server (matrix row `fn.split_part`: STRING_SPLIT is a table function). The template splits with a recursive query |
| C5 | `WITH RECURSIVE` | lowering refused it (DDB-324) | **fixed**: `LOGICAL_RECURSIVE_CTE` is lowered (anchor and recursive part, text and decimal columns cast to one type in both parts, as SQL Server requires). **cataloged** in `syntax.recursive_cte`: SQL Server stops at 100 levels (error 530), allows only `UNION ALL`, and cannot take `OPTION (MAXRECURSION)` in a view |
| C6 | `generate_series(0, n)` where `n` comes from another table | lowering said "0 argument(s)" | **refused** with a message that says why (a series per row is a `CROSS APPLY` on SQL Server); the template joins a series of constants and filters it |
| C7 | `date + 3`, `date - n`, `n + date` | SQL Server: "Operand type clash: date is incompatible with int"; PostgreSQL accepts it | **fixed**: lowering marks it (`CAST(d AS DATE) + CAST(n AS INTEGER)`) and the target rule `date-plus-days` writes an interval of days on SQL Server |
| C8 | `string_agg(x, sep ORDER BY ...)` on PostgreSQL | polyglot writes `LISTAGG`, which PostgreSQL does not have; `render` refused it | **fixed**: target rule `string-agg-array` (`array_to_string(array_agg(x ORDER BY ...), sep)`) |
| C9 | `strpos(a \|\| b, 'x')`, `position(... IN a \|\| b)` | the transpile writes `+` for `\|\|` everywhere except inside the arguments of functions it rewrites into another shape (`CHARINDEX` with swapped arguments, `starts_with`): `CHARINDEX('x', a \|\| b)` is a syntax error on SQL Server | **fixed**: target rule `concat-plus` writes every `\|\|` as `+` before the transpile (the lowered query casts non-text operands itself). The upstream bug is in polyglot |
| C10 | `CAST(x AS VARCHAR)`, `LEFT`, `REPEAT` | not in `matrix/covered.yml`, so `validate` warned (DDB-305) | **fixed**: covered with the probe cases that already compare them on both engines |
| C11 | constants in a recursive anchor (`1 AS n`, `'a' AS s`) | the lowerer had no type for a constant, so the cast that keeps both parts the same type was missing | **fixed**: a constant's type is read from its value |

## adventureworks

| # | Construct | What happened | Disposition |
|---|---|---|---|
| A1 | `lead`, `ntile`, `percent_rank`, `cume_dist`, `first_value`, `last_value` | not in `matrix/covered.yml`, so `validate` warned (DDB-305), and no probe compared them | **fixed**: 36 window cases (frames, ties, NULL ordering, defaults) are now probes in `EngineDifferenceProbes`; every one agrees with DuckDB on both engines, and the nodes are covered |
| A2 | `count(*) OVER (...)` | the plan has a `count` with no argument and the lowerer wrote `count() OVER ()`: SQL Server "COUNT_BIG requires 1 argument", PostgreSQL "count(*) must be used" | **fixed** (the window count is written `count(*)`) |
| A3 | `nth_value(x, n) OVER (...)` | the lowerer does not know the window operator; SQL Server has no NTH_VALUE either | **refused** (DDB-324, "the window function WINDOW_NTH_VALUE"), listed here |
| A4 | `median(x)`, `quantile_cont(x, q)` | the fraction lives outside the aggregate's parameters in the plan; SQL Server has only an analytic PERCENTILE_CONT with an OVER clause | **refused** (DDB-324), listed here |
| A5 | `greatest`, `least` | not covered (DDB-305), and a SQL Server 2022 function | **fixed**: matrix row `fn.greatest_least` (SQL Server version 16; both engines skip NULLs like DuckDB, probed) |
| A6 | `date_diff('year' \| 'month' \| 'quarter', a, b)` on PostgreSQL | polyglot writes `AGE()`, which counts whole elapsed years and months; DuckDB counts boundaries crossed (tenure of someone hired 2016-06-30, on 2025-01-01: 9 vs 8) | **fixed**: target rule `date-diff-boundaries` (the difference of the calendar fields) |
| A7 | `date_diff('week', a, b)` | SQL Server counts week boundaries from `@@DATEFIRST`; PostgreSQL's cast to an integer rounds (8.86 weeks is 9) where DuckDB counts whole periods of seven days toward zero | **fixed** on both: target rule `date-diff-weeks` (the days between over seven, toward zero) |
| A8 | a product of several decimals | DuckDB casts the operands to DECIMAL(38, s) when the result would be wider than 38 digits and multiplies exactly; SQL Server cuts the scale of every product to 6 and rounds there. Price times discount times exchange rate differed in the fourth decimal in about one line in a hundred | **cataloged**: matrix row `type.decimal_product_wide` (the linter notes any product with a DECIMAL(38, s) operand), a probe with a case that shows it, and the template works the chain in DOUBLE and rounds |
| A9 | `x + 1`, `d - n` where `d` is a DATE (the previous period ends the day before the next one starts) | see C7 | **fixed** (C7) |
| A10 | as-of lookups: a correlated `ORDER BY ... LIMIT 1` subquery, and a join on a date range | lowered and agree with DuckDB on both engines | verified, nothing to do |
| A11 | a recursive query with decimal quantities multiplied down the levels and a path | lowered with the casts of C5; agrees on both engines | verified |
| A12 | a backslash in a string literal (`strpos(login, '\')`) | read the same by the parser, DuckDB and both engines | verified |

## Not covered by a template yet

`median` and `quantile_cont` (A4), `nth_value` (A3), `split_part` on SQL Server (C4), `PIVOT` and `UNPIVOT`, `QUALIFY`, `LATERAL`, `UNNEST` and list columns, JSON, `regexp_*`: the matrix says which of these
are refused today; a template that needs one is how it gets built.

