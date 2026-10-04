# Oracle, Spark SQL and the BigQuery emulator: the first scoreboard

The three engines are run by the same 267 probes as SQL Server and PostgreSQL (`EngineDifferenceProbes`): a DuckDB-dialect query is lowered, transpiled by polyglot to the engine's dialect with **no
rewrite rules yet**, run on the engine over the same eight seeded rows, and compared with DuckDB's answer. So the numbers below are polyglot's own translation: every case that is not "same" is a
candidate for a target rule, a matrix row or a refusal, in the order the engine shows. They are kept as checked-in baselines (`tests/DbDataBuild.Tests.Conformance/Baselines/<engine>.txt` lists the
cases that agree); the test fails when a case that agreed stops agreeing, and `DDB_PROBE_BASELINE=update` rewrites the list when a rule makes more cases agree.

| Engine | How it runs | Same | Different answer | Engine rejects the query | Both reject | DuckDB rejects |
|---|---|---|---|---|---|---|
| Spark SQL 4.0 (ANSI on) | `apache/spark:4.0.0`, Thrift server in http mode, ADBC Spark driver | 225 | 21 | 14 | 6 | 1 |
| BigQuery emulator | `ghcr.io/goccy/bigquery-emulator`, REST `jobs.query` | 163 | 45 | 52 | 2 | 5 |
| Oracle 23ai Free | `gvenzl/oracle-free:23-slim`, Oracle.ManagedDataAccess | 113 | 76 | 71 | 4 | 3 |

(SQL Server and PostgreSQL are at 0 undocumented differences because their differences are rules, matrix rows or entries in `Known`.)

## What the differences are

**Spark SQL** is the closest to DuckDB. What is left: ANSI mode raises on a division by zero and an overflow where DuckDB gives NULL or infinity; `substr(s, 0, n)`, a negative `left`, `concat` with a
NULL (Spark gives NULL, DuckDB skips it); `upper('ß')` is `SS`; `date_diff` for years, months and quarters (the transpile writes a form that counts whole elapsed periods or days, not boundaries crossed); `dayofweek` counts from
Sunday as 1; `ISODOW` and `WEEK` do not exist as written; `split_part` arrives as `array_extract(string_split(...))`; `regexp_full_match` is the unanchored match; `regexp_replace` takes `$1` where DuckDB takes
`\1`, and the `'g'` flag is read as a start position; `ORDER BY ... DESC` puts NULLs first. The decimal product wider than 38 digits rounds at scale 6 (as on SQL Server).

**BigQuery emulator** (GoogleSQL; the emulator is a community project, so some of what fails may be the emulator and not BigQuery): no `%` (it is `MOD()`); no `BOOL_AND`/`BOOL_OR`/`mode`/`string_agg` as
written; date functions want the part as a keyword (`DATE_DIFF(d2, d1, DAY)`, polyglot writes `DATE_DIFF(DAY, d1, d2)`); `date_trunc` and `date_part` need BigQuery's part names; `strpos` and `position` reject
a position of 0; strings do not cast to integers with surrounding spaces; no `concat_ws`, `array_extract`, `json_array_length`, `json_valid`; `substr` with a negative length and `left` with a negative
count raise; integer division, `greatest` and `least` give other answers (the probes show the rows; BigQuery returns NULL from `greatest` if any argument is NULL, and a division by zero is an error).

**Oracle** is the furthest: no `LEFT`/`RIGHT`/`REPEAT`/`CONCAT_WS`/`STARTS_WITH`/`CONTAINS`/`LOG10`/`MAKE_DATE`/`REGEXP_EXTRACT`/`SPLIT_PART`; no `%` (it is `MOD`); a cast to text comes out as `CLOB`, which
Oracle will not use in a comparison, `LENGTH` or `GROUP BY`; booleans are not values in a select list (`IS DISTINCT FROM`, `bool_and`, a comparison as a column all need a `CASE`); the empty string is NULL (so
every empty-string probe differs: `length('')`, `s = ''`, `coalesce(s, '')`); `DATE` carries a time; `date_trunc` and `date_part` are `TRUNC` and `EXTRACT` with different part names; adding a month to the
31st raises; `DATEDIFF` as the transpile writes it is not an Oracle function. Nearly everything here is polyglot's dialect output, not an Oracle limitation: it is where the target rules start.

## What it costs

The three probe runs take about a minute in all (Oracle 1 s, BigQuery 10 s, Spark 20 s, plus container start: Oracle and the emulator in seconds, Spark in about 15 s). CI runs each in its own job.
The probes are the cheap half of a new target: the other half is the target itself (a DDL type table, the tracking tables, load strategies, a driver for `MutationGate`, and a matrix column), which
none of the three has yet.
