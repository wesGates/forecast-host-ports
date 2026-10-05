# Schema design

Two files create the tables in the project library.

- `forecast_tables.sql` holds `SALES`, `FORECAST` and `SUGGESTED`. It is
  generated from the forecasting study's table definition, so the study can
  push its rows here with a straight copy. Do not edit it by hand.
- `reference_tables.sql` holds the reference data the facts point at:
  stores, items, series and the calendar.

## Reference tables

| table | key | one row per | why it exists |
|---|---|---|---|
| `STORE` | `store_id` | store | the store and its state |
| `DEPT` | `dept_id` | department | a department belongs to one category |
| `ITEM` | `item_id` | item | the item and its department |
| `SERIES` | `id` | item at a store | M5's series id, the key `SALES` and `FORECAST` use; unique on `(item_id, store_id)` |
| `CALENDAR` | `date` | day | M5's day label `d` and the Walmart week `wm_yr_wk` |
| `EVENT` | `event_name` | event | an event has one type |
| `CAL_EVENT` | `(date, event_name)` | event on a day | a day can carry two events |
| `SNAP_DAY` | `(state_id, date)` | SNAP day in a state | SNAP days differ by state |

M5 ships these facts flattened into two files. Each table above removes
one dependency from that layout.

- `cat_id` depends on `dept_id`, so it lives in `DEPT`, not in `ITEM`.
- `event_type` depends on `event_name`, so it lives in `EVENT`.
- M5's calendar has `event_name_1` and `event_name_2`. That repeating
  group becomes rows in `CAL_EVENT`.
- M5's calendar has `snap_CA`, `snap_TX` and `snap_WI`. Those become rows
  in `SNAP_DAY`, one per state and day with SNAP. A day with no row has no
  SNAP.
- `weekday`, `wday`, `month` and `year` follow from the date. DB2 computes
  them (`DAYOFWEEK`, `MONTH`, `YEAR`), so they are not stored.

There is no `STATE` or `CATEGORY` table because the data has no attribute
of a state or a category beyond its code.

## The fact tables

`SALES` has key `(id, date)`. `FORECAST` has `(as_of, id, method,
target_date)` and `SUGGESTED` has `(as_of, id, target_date)`. All three
also carry `item_id` and `store_id`, which `SERIES` determines from `id`.
That is the one place the schema is not in third normal form. The columns
mirror the study's own tables so the push and the reconciliation are plain
copies and joins. The two columns cost little at this size.

The fact tables have no foreign keys. They come from the generated file,
and the study pushes into `FORECAST` from outside the host. The
reconciliation test checks that every `id` and date resolves.

## Host constraints

- PUB400 user libraries have no journal. Foreign keys with `RESTRICT`
  rules need none, so every rule here is `RESTRICT`. Inserts must run
  without commitment control.
- Table and column names are ten characters or fewer, so the system names
  match the SQL names.
