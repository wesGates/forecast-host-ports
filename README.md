# forecast-host-ports

The host side of a store replenishment forecast on IBM i, built on
[PUB400](https://pub400.com). A normalized DB2 for i schema holds stores,
items, the calendar and daily sales. An RPG program writes the host's own
fallback forecast, "this day last week", and fills a table of suggested
forecasts with the model's row where one exists and the fallback otherwise. A
CL program runs the load and the RPG program as one job. A deploy script does
everything over SSH, and a Python test proves the RPG results equal a plain
Python reference row for row.

The same tables and rule are also ported to C# on SQL Server, with tests, CI,
a container and a read-only JSON API. See [`csharp/README.md`](csharp/README.md).

The forecasts and the baseline come from a separate study,
[demand-forecasting](https://github.com/wesGates/demand-forecasting). It
trained gradient-boosted trees, ETS and ARIMA on the public M5 dataset of
Walmart daily sales and pushes its forecasts into this host's `FORECAST`
table.

## Why the host needs a fallback

In a shop the model runs beside the host. The host extracts sales nightly,
the model writes forecasts back into a host table, and the host turns them
into suggested orders. When the model's rows do not arrive (its server is
down, the extract failed, a new item has no history), the host still has to
order. This project builds that leg on the host and checks it against the
study's own definition of the baseline.

## What is here

| path | what |
|---|---|
| `ibmi/schema/forecast_tables.sql` | `SALES`, `FORECAST`, `SUGGESTED`, generated from the study's table definition |
| `ibmi/schema/reference_tables.sql` | stores, departments, items, series, calendar, events and SNAP days |
| `ibmi/schema/design.md` | the keys and why each table exists |
| `ibmi/cl/loadslice.clle` | `LOADSLICE`: replace the data slice from CSV files with `CPYFRMIMPF` |
| `ibmi/rpg/fcstnaive.sqlrpgle` | `FCSTNAIVE`: free-format ILE RPG with embedded SQL; the fallback forecast and the suggested rows |
| `ibmi/cl/runchain.clle` | `RUNCHAIN`: the batch job, `LOADSLICE` then `FCSTNAIVE`, stopping at the first failed step |
| `tools/export_slice.py` | writes the slice from the raw M5 files as one CSV per table |
| `tools/deploy.sh` | export, upload, compile, run, pull the results back |
| `tools/reference.py` | the Python reference: "this day last week" from the same slice |
| `tests/test_reconciliation.py` | the host's rows against the Python reference |
| `csharp/` | the C# port on SQL Server and its API |

## The rule

The data slice is one fast-moving item, `FOODS_3_586`, at the ten M5 stores,
with daily sales from 2011-01-29 to 2016-05-22. An origin is the last day of
sales a forecast sees. For every store and every origin day from 2015-05-24
to 2016-05-15, `FCSTNAIVE` writes seven rows under method `SEASONAL_NAIVE`.
The forecast for day T+h is the sales on day T+h−7, which is the study's
`bench_seasonal_naive`.

`SUGGESTED` then holds one row per store, origin and target day. It takes
the forecast of method `xgboost_rel@item_id` where `FORECAST` has that row
and the `SEASONAL_NAIVE` row otherwise. Its `source` column names the method
used.

## Running it

You need a user profile on an IBM i with SSH access by key, a library of your
own, and the M5 files `calendar.csv` and `sales_train_evaluation.csv` from the
[M5 forecasting competition](https://www.kaggle.com/competitions/m5-forecasting-accuracy/data).
The M5 data is not in this repository.

```bash
export IBMI_HOST=pub400.com IBMI_USER=<profile> IBMI_LIBRARY=<library>
python3 -m venv .venv && .venv/bin/pip install -r requirements.txt
tools/deploy.sh <directory with the M5 files>
.venv/bin/pytest
```

The SSH port defaults to 2222, PUB400's port; set `IBMI_PORT` for another
host. `SALES`, `FORECAST` and `SUGGESTED` must already exist in the library;
they are created from `ibmi/schema/forecast_tables.sql`, which the study
generates. The deploy script creates the reference tables when they are
missing. The slice goes to
`data/` and the pulled rows to `results/`, both ignored by Git.

## Results

From one run of `tools/deploy.sh` from a clean checkout, followed by the tests:

- The load put 19,410 sales rows, 1,969 calendar days and 10 series on the host.
- `FCSTNAIVE` wrote 25,060 `SEASONAL_NAIVE` rows (10 stores × 358 origins × 7 days).
  All of them equal the Python reference exactly.
- `SUGGESTED` has 25,060 rows. The study's model covers every store and
  origin in this window, so every row came from `xgboost_rel@item_id`.
- Run once with a model name that has no rows, `FCSTNAIVE` filled all
  25,060 rows from `SEASONAL_NAIVE`, each equal to its fallback forecast.
- Every series id and date in `SALES`, `FORECAST` and `SUGGESTED` exists in
  `SERIES` and `CALENDAR`.
- The deploy took about six minutes on PUB400, nearly all of it uploads,
  compiles and SSH round trips. `RUNCHAIN` itself takes about six seconds.

The Python reference uses the raw sales. The study replaces sales on store
closure days before forecasting, so its own cached baseline can differ from
these rows around Christmas.

## Notes on the host

- PUB400 user libraries have no journal. The programs run without commitment
  control, and the foreign keys use `RESTRICT` rules, which need no journal.
- `FCSTNAIVE` finds its tables through the library list. `RUNCHAIN` adds the
  project library before calling it.
- A CL `CALL` from a command line passes a literal at its own length. The
  deploy script pads the directory parameter to the 128 bytes `RUNCHAIN`
  declares.

## What it does not do

There is no buyer screen and no order quantity. A suggested row is a
forecast of units sold, with no stock on hand, pack sizes, lead times or
other business rules. The 28-day average baseline and a nightly
`ADDJOBSCDE` entry are not built.
