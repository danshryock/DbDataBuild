# adventureworks: a bicycle maker

A project after the shape of the AdventureWorks sample database (products and their price history, bills of materials, work orders, sales in several currencies, purchasing,
people), with every row made up by SQL in `seeds/`: nothing is copied from the original data. Twenty-three source tables in five schemas
(`production`, `sales`, `purchasing`, `humanresources`), a staging layer of views, and a warehouse of dimensions, facts, aggregates and reports.

```
dbdatabuild validate                       # config, models, sources and how each query lowers
dbdatabuild graph --columns                # what reads what, and where each column comes from
dbdatabuild seed --scale 800               # generate the source data (the scale is the number of customers; four orders each)
dbdatabuild sample marts.fct_bom_explosion # run a model on that data and look at the rows
dbdatabuild test                           # metadata rules and model tests
dbdatabuild render --write                 # the load scripts for SQL Server (rendered/)
```

## The transforms worth reading

| Model | What it shows |
|---|---|
| `marts.dim_product_history` | a **slowly changing dimension** built from two histories (cost and list price): the periods start at every change of either, and each ends the day before the next |
| `marts.fct_sales_lines` | **as-of lookups**: the latest exchange rate on or before the order date (a correlated query with `ORDER BY ... LIMIT 1`, a weekend order takes Friday's rate) and the product cost in force on that day (a join on a date range); a fact loaded incrementally; currency conversion worked in DOUBLE and rounded |
| `marts.fct_bom_explosion` | a **parts explosion**: a recursive query from the assemblies nothing else uses, multiplying the quantity per assembly down the levels and keeping the path |
| `marts.agg_bom_rolled_cost` | what a finished product costs from its leaf parts against the cost it carries |
| `marts.agg_sales_territory_quarter` | **aggregating** to a quarter, the running total of the year (a window frame), and the same quarter of the year before with the growth |
| `marts.agg_product_abc` | a Pareto classification: cumulative share of revenue (window over the whole table) and a class A, B or C |
| `marts.rpt_customer_rfm` | recency, frequency and spend cut into five groups (`ntile`) and a segment name written as a **mapping** of the three scores |
| `marts.rpt_sales_person_quota` | quota attainment per quarter, a rank and a percentile (`percent_rank`) |
| `marts.fct_inventory_snapshot` | stock next to sales, the average of the last three months (a rolling frame) and the months of supply |
| `marts.fct_purchase_lines`, `marts.rpt_vendor_scorecard` | lead time, share received and rejected, shipped on time (`count(*) FILTER (...)`), vendors ranked by spend |
| `marts.rpt_revenue_by_color_year` | `PIVOT`: the years become columns |
| `marts.rpt_quota_long` | `UNPIVOT`: revenue and quota columns become one row per measure |
| `marts.rpt_customer_last_order` | `QUALIFY`: the latest order of every customer |
| `marts.rpt_territory_top_products` | `LATERAL`: the three best products of each territory, a subquery that reads the row it is joined to (`CROSS APPLY` on SQL Server) |
| `marts.rpt_product_revenue_spread` | `median`, `quantile_cont` and `quantile_disc`, ranked inside each group because SQL Server has no aggregate for them |
| `marts.dim_product_attributes` | **JSON**: `json_extract_string` reads the frame, the gears and the first tag out of the document each product carries |
| `marts.fct_work_orders` | scrap rate, days late, and a status mapped from dates (an order with no end is in progress) |
| `marts.dim_employee` | the department they are in now (the history row with no end), the pay rate in force (`row_number`), the years with the company |
| `marts.dim_date` | a calendar from a series of numbers with a fiscal year that starts in July |
| `staging.employees` | **splitting** a login (`domain\name`) at the backslash, codes mapped to words |

## Tests

`tests/metadata/` holds rules over what the project is (every model has a grain, staging reads only sources, marts are named dim, fct, agg or rpt, incremental models declare their key,
money is a decimal). `tests/models/` holds cases for the logic that is easy to get wrong: the explosion, the cost and price periods, the exchange rate of a weekend, the cohorts of
quintiles, the year-on-year comparison, the on-time flags.

## Trying it on a sandbox database

`load-seeds` puts the source tables in an empty SQL Server (or PostgreSQL) database you do not mind filling. The logins are environment variables
(`DBDATABUILD_SQLSERVER_WRITE` for what writes, `DBDATABUILD_SQLSERVER_READ` for what reads; see the main documentation):

```
dbdatabuild load-seeds --apply         # creates the source tables and loads them (--replace drops and recreates tables that exist)
dbdatabuild init --apply               # the tracking tables
dbdatabuild render --write             # the rendered files are checked in against the models
dbdatabuild plan --accept-inferred     # reads the database, writes a plan you can read
dbdatabuild apply plans/sqlserver/<the plan>.plan.yml
```

## Notes

- Money here is `DECIMAL(19, 4)`. DuckDB multiplies decimals exactly; SQL Server rounds each product to six decimals when the result would be wider than 38 digits, so a chain of
  products (price, discount, exchange rate) can differ in the last place. `marts.fct_sales_lines` works the discount and the conversion in DOUBLE and rounds to four places, which both engines do alike
  (the support matrix row `type.decimal_product_wide` says more).
- The project is set for SQL Server (see the comment at the top of `dbdatabuild.yml` for PostgreSQL); it asks for SQL Server 2022 (`connections: sqlserver: {version: 16}`) because of `generate_series` and `greatest`.
- The shape of the data follows the AdventureWorks sample database (Microsoft Public License); the rows are invented.
