# retail: an online and in-store shop

A larger project to explore what dbdatabuild does with real-looking transforms. Twelve source tables (`shop.*` for the
operational system, `ref.*` for lookups, `finance.budgets`), a staging layer, and a star schema with facts, dimensions,
aggregates and a report. The data is made up by SQL in `seeds/` (about 300 customers and 2,000 order lines by default), so every command below
runs on your own machine.

```
dbdatabuild project compile                         # check config, models and sources, see how each query lowers, and write the load scripts (rendered/) to read and commit
dbdatabuild project show graph --columns                # what reads what, and where each column comes from
dbdatabuild project seed --scale 1000              # generate the source data into .dbdatabuild/seed.duckdb
dbdatabuild project sample marts.fct_sales_lines   # run a model on that data and look at the rows
dbdatabuild project tests run                           # metadata rules and model tests
```

## The transforms worth reading

| Model | What it shows |
|---|---|
| `staging.customers` | **splitting** one name column into first and last name (`van Dijk` stays whole) and an email into its domain |
| `staging.orders` | **mapping** a two-letter status code to words; a month key cut from a timestamp |
| `marts.dim_store`, `marts.dim_product` | joins to the reference tables, banding (`size_band`, margin band), a default for the unknown |
| `marts.dim_date` | a calendar of every day something happened, with weekday and month names that do not depend on the engine's language |
| `marts.fct_sales_lines` | **allocating** an order's discount and shipping fee to its lines in proportion to their value, in whole cents, the last line taking the remainder so the pieces always add up; incrementally loaded by `order_id, line_no` |
| `marts.fct_shipment_packages` | **splitting** a shipment into one row per package (a join to a series of numbers), weight and freight split evenly with the remainder on the last package |
| `marts.fct_returns` | a lookup with a default (`other`) for an unmapped reason code |
| `marts.agg_daily_sales` | **aggregating** lines to a day, a region and a channel group, with a running total per region |
| `marts.agg_customer_value` | **aggregating** to a customer: orders, spend, first and last order, a value segment |
| `marts.fct_budget_allocation` | **allocating** a regional budget to its shops by floor area, to the cent |
| `marts.rpt_budget_vs_actual` | the allocated budget against actual net sales per shop and month |

## Tests

`tests/metadata/` holds rules over what the project is (every model has a grain, staging reads only sources, marts never read sources).
`tests/models/` holds cases for the logic that is easy to get wrong: allocations that must add up exactly, the shipment split, the name split, the status mapping.
Try breaking one: change `ELSE order_discount_cents - ...` in `fct_sales_lines.sql` and run `dbdatabuild project tests run`.

## Things to try

- `dbdatabuild project show graph --column marts.agg_daily_sales.net_amount` follows a number back to the source columns.
- `dbdatabuild project show graph +marts.rpt_budget_vs_actual` and `dbdatabuild connection deploy --write-plan marts.fct_sales_lines+` (a selector) to see what a change touches.
- Add a mart (revenue per product category per month) and a test for it.

## Trying it on a sandbox database

The source tables are what `seeds/` generates, and `load-seeds` can put them in an empty SQL Server (or PostgreSQL) database you do not mind filling. The logins are environment variables
(`DBDATABUILD_SQLSERVER_WRITE` for what writes, `DBDATABUILD_SQLSERVER_READ` for what reads; see the main documentation):

```
dbdatabuild connection seed                 # shows what it would create and fill; connects to nothing
dbdatabuild connection seed --apply         # creates the source tables and loads them (--replace drops and recreates tables that exist)
dbdatabuild connection init --apply               # the tracking tables
dbdatabuild project compile --check     # the rendered files are checked in against the models
dbdatabuild connection deploy --write-plan --accept-inferred   # reads the database, writes a plan you can read
dbdatabuild connection deploy --apply-plan plans/sqlserver/<the plan>.plan.yml
```

Then compare: `dbdatabuild project sample <model> --limit 5` shows what DuckDB computes for the same seeds, and the tables in the database hold the same rows.

## PostgreSQL

The project is set for SQL Server (see the comment at the top of `dbdatabuild.yml` for PostgreSQL). Everything but `fct_shipment_packages` and the calendar renders for both.
