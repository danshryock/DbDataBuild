# chinook: a digital music store

A project after the shape of the Chinook sample database (artists, albums, tracks, playlists, customers, employees, invoices), with every row
made up by SQL in `seeds/`: the names, tracks and invoices are generated, so nothing here is copied from the original data. Eleven source tables
in the `chinook` schema, a staging layer, and a star schema with a calendar, dimensions, a fact and some reports.

```
dbdatabuild validate                       # config, models, sources and how each query lowers
dbdatabuild graph --columns                # what reads what, and where each column comes from
dbdatabuild seed --scale 500               # generate the source data (the scale is the number of customers)
dbdatabuild sample marts.dim_employee      # run a model on that data and look at the rows
dbdatabuild test                           # metadata rules and model tests
dbdatabuild render --write                 # the load scripts for SQL Server (rendered/)
```

## The transforms worth reading

| Model | What it shows |
|---|---|
| `marts.dim_employee` | a **hierarchy**: the management chain walked from the general manager down with a recursive query (`WITH RECURSIVE`), a level and the path of names |
| `marts.fct_track_composer` | **splitting** one text column (`Ana Reyes, Ben Okafor` or `A / B / C`) into a row per name with its position, by a recursive query that peels the first name off the rest |
| `marts.dim_track` | joins that keep tracks without an album or a genre, `lpad` to write a length as `mm:ss`, a length band |
| `marts.dim_date` | a calendar counted forward from the first invoice (a series of numbers added to a date) |
| `marts.fct_invoice_lines` | an incremental fact, loaded by the line number |
| `marts.agg_genre_revenue` | **aggregating** by genre and year with the share of the year, a rank, and a running total over the years (a window with a frame) |
| `marts.agg_top_tracks` | the best three tracks of every genre (`row_number`), and units by year as columns (a pivot written with `CASE`) |
| `marts.agg_customer_cohorts` | customers grouped by the month of their first purchase and how many came back, month by month |
| `marts.rpt_playlists` | a many-to-many table, hours of music, and the most common genres of a playlist as one text (`string_agg`) |
| `marts.rpt_sales_by_manager` | revenue **allocated up the hierarchy**: every manager's figure includes the people below them |

## Tests

`tests/metadata/` holds rules over what the project is (every model has a grain, staging reads only sources, dimensions are keyed). `tests/models/` holds cases for the
logic that is easy to get wrong: the split of the composer text, the management chain, the roll-up of revenue, the cohort counts, the `mm:ss` text.

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

- On SQL Server a recursive query stops at 100 levels (error 530) where DuckDB goes on; this hierarchy is three levels deep.
- The project is set for SQL Server (see the comment at the top of `dbdatabuild.yml` for PostgreSQL); it asks for SQL Server 2022 (`targets: sqlserver: {version: 16}`) because of `generate_series`.
- The names of the artists, the composers and the customers are invented; the shape of the data follows the Chinook sample database (MIT licence).
