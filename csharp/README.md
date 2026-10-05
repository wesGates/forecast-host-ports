# The C# port

The host's fallback forecast and suggested-order rule, ported from the RPG
program `FCSTNAIVE` on IBM i to C# on .NET 10 and SQL Server. It is a full
port, data and logic: the same tables in SQL Server's `host` schema
(`host.sales`, `host.forecast`, `host.suggested`) and the same rule. That is
where a migration off the host would end up.

For every series and every origin day T, the port writes "this day last
week" into `host.forecast` under method `SEASONAL_NAIVE`; the forecast for
T+h is the sales on T+h−7. It then fills `host.suggested` with the model's
row where `host.forecast` has one and the `SEASONAL_NAIVE` row otherwise.
Both writes happen in one transaction and replace the rows for the origin
range, so a rerun gives the same rows.

It reads SQL Server and never the IBM i host. PUB400 makes no uptime
promise, and tests that depended on it would fail for reasons that have
nothing to do with the code.

## Projects

| path | what |
|---|---|
| `ForecastHost/` | the console app: `Rule.cs` (the rule, no database), `HostTables.cs` (SQL Server), `Chain.cs` (one run), `Demo.cs` (synthetic rows) |
| `ForecastHost.Api/` | a read-only JSON API over the same tables |
| `ForecastHost.Tests/` | xUnit v3: unit, integration, API and reconciliation tests |
| `Dockerfile`, `compose.yml` | the port in a container beside its own SQL Server |

## Running it

The connection comes from environment variables with the defaults of the
study's SQL Server container: `MSSQL_HOST` (localhost), `MSSQL_PORT` (1433),
`MSSQL_USER` (sa), `MSSQL_PASSWORD` (required), `MSSQL_DATABASE`
(forecasting).

```bash
cd csharp
dotnet run --project ForecastHost                  # the test year, as RUNCHAIN runs it
dotnet run --project ForecastHost -- --from 2015-06-01 --to 2015-06-30 --model ets
dotnet run --project ForecastHost -- --seed-demo   # synthetic rows in forecast_host_demo
dotnet test
```

`--seed-demo` writes into its own database, `forecast_host_demo`, and never
into `forecasting`. Three stores sell one item for 90 days. The demo model's
rows stop after 2024-03-15 and store `DEMO_3` never has any, so the
suggested rows come from both sources.

To run the port in a container beside a fresh SQL Server, on the demo rows:

```bash
MSSQL_PASSWORD='<a strong password>' docker compose -f compose.yml up --build --exit-code-from forecasthost
```

## Tests

- Unit tests run the rule on synthetic sales with no database.
- Integration tests run the port against SQL Server in a database of their
  own, `forecast_host_test`, with one series that has model rows and one
  that has none. They skip when `MSSQL_PASSWORD` is not set.
- API tests start the API on the demo database, one test per endpoint.
  They skip the same way.
- The reconciliation test runs the rule on the M5 slice in `data/SALES.csv`
  and compares every row with the Python reference in
  `results/REFERENCE.csv` (`python tools/reference.py`). Both files hold M5
  rows and stay off GitHub, so this test skips in CI.

CI (`.github/workflows/csharp.yml`) builds and runs the tests on every push,
with SQL Server as a service container.

## Results

On the fast-mover slice, origins 2015-05-24 to 2016-05-15:

- 25,060 `SEASONAL_NAIVE` rows, each equal to the RPG program's row on the
  host and to the Python reference.
- 25,060 suggested rows, all from `xgboost_rel@item_id`, which covers every
  store and origin in that window. Each is within 5e-7 of the host's row.
  The study's forecasts are `FLOAT` in SQL Server and `DECIMAL(15,6)` in
  DB2, so the host's copy is rounded to six places.

## The API

```bash
cd csharp
dotnet run --project ForecastHost.Api              # http://localhost:5023
curl localhost:5023/series
curl 'localhost:5023/series/FOODS_3_586_CA_1_evaluation/sales?from=2016-05-01&to=2016-05-22'
curl 'localhost:5023/series/FOODS_3_586_CA_1_evaluation/suggested?as_of=2016-05-15'
```

| endpoint | returns |
|---|---|
| `GET /series` | every series: `id`, `item_id`, `store_id` |
| `GET /series/{id}/sales?from=&to=` | daily `units` by `date`; both dates optional |
| `GET /series/{id}/suggested?as_of=` | the seven suggested days for one origin (the latest if `as_of` is left out), each with `source` `model` or `fallback` and the `method` behind it |

It is for a page that shows an item's sales at a store with the current
forecast and where that forecast came from. It is read-only, has no
authentication and listens on localhost only.

## A shop's version

A shop that keeps the data on IBM i would point the same code at DB2 for i
through IBM's ODBC driver (`System.Data.Odbc`) instead of SQL Server. The
queries are plain SQL. Three things would change:

- `SqlBulkCopy` becomes a blocked `INSERT ... VALUES`, at most 32,767 rows
  per execute.
- `TOP 1` becomes `FETCH FIRST 1 ROW ONLY`.
- On an unjournaled library, the writes run without commitment control, as
  `FCSTNAIVE` does, so they would lose the one transaction.
