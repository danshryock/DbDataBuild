# starter: a small cafe

A tiny project to try dbdatabuild without a database: three source tables (`raw.customers`, `raw.orders`,
`raw.payments`), a staging layer that tidies them, and two marts (`marts.orders`, `marts.customers`). The data is
made up by SQL in `seeds/`, so every command below runs on your own machine with nothing else installed.

```
dbdatabuild validate                   # check the project: config, models, sources, how each query lowers
dbdatabuild graph --columns            # what reads what, and where each column comes from
dbdatabuild seed                       # generate the source data into .dbdatabuild/seed.duckdb (inspect it with any DuckDB tool)
dbdatabuild sample marts.orders        # run a model on that data and look at the rows
dbdatabuild test                       # the project's tests: metadata rules and model tests
dbdatabuild render --write             # write the load scripts for SQL Server (rendered/), to read and commit
```

## What is where

| Folder | What |
|---|---|
| `sources/` | one file per table the project reads but does not build: its columns and types |
| `seeds/` | one DuckDB query per source that generates its rows from a `seed` and a `scale` (`dbdatabuild seed --seed 7 --scale 500`) |
| `models/staging/` | views: names made consistent, a status mapped to a group, cents turned into dollars |
| `models/marts/` | tables: `orders` pivots payments into one column per method; `customers` aggregates orders |
| `tests/metadata/` | rules over what the project *is* (staging only reads sources, every model has a grain, ...) |
| `tests/models/` | cases for a model's logic: given rows, expected rows |

## Things to try

- `dbdatabuild graph +marts.customers` and `dbdatabuild graph --column raw.payments.amount_cents`.
- Change `seeds/macros.sql` (`scale`) or run `seed --scale 1000`, then `sample marts.customers --limit 10`.
- Break something on purpose: rename a column in `models/marts/orders.sql` and run `validate`, then `test`.
- Add a mart of your own, for example revenue per month, and write a model test for it.

## Running it on a real database

The sources are tables in your database (the seed queries show what they hold), and the project is set up for SQL Server (see the
comment at the top of `dbdatabuild.yml` for PostgreSQL). `dbdatabuild plan` reads the database with a read-only login and writes a
plan you can read; `dbdatabuild apply <plan>` runs exactly that plan. See the main documentation for logins and the plan files.
