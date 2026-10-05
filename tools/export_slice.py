"""Export the host's data slice from the raw M5 files as CSV, one file per table.

    python tools/export_slice.py ~/path/to/m5/data [--out data] [--item FOODS_3_586]

Reads calendar.csv and sales_train_evaluation.csv from the M5 directory and writes
STORE.csv, DEPT.csv, ... SALES.csv, named after the host tables. Columns are in table
order, since CPYFRMIMPF maps fields by position. The files hold M5 rows, so they go to
data/, which is gitignored.
"""

import argparse
from pathlib import Path

import pandas as pd

STATES = ["CA", "TX", "WI"]


def calendar_tables(cal: pd.DataFrame) -> dict[str, pd.DataFrame]:
    calendar = cal[["date", "d", "wm_yr_wk"]]

    # event_name_1/_2 and event_type_1/_2 become one row per event on a day
    pairs = [
        cal[["date", f"event_name_{k}", f"event_type_{k}"]].set_axis(
            ["date", "event_name", "event_type"], axis=1
        )
        for k in (1, 2)
    ]
    events = pd.concat(pairs).dropna()
    event = (
        events[["event_name", "event_type"]].drop_duplicates().sort_values("event_name")
    )
    if event.event_name.duplicated().any():
        raise SystemExit("an event name has more than one type")
    cal_event = events[["date", "event_name"]].sort_values(["date", "event_name"])

    # snap_CA, snap_TX, snap_WI become one row per state and SNAP day
    snap_day = pd.concat(
        cal.loc[cal[f"snap_{s}"] == 1, ["date"]].assign(state_id=s) for s in STATES
    )[["state_id", "date"]].sort_values(["state_id", "date"])

    return {
        "calendar": calendar,
        "event": event,
        "cal_event": cal_event,
        "snap_day": snap_day,
    }


def sales_tables(wide: pd.DataFrame, cal: pd.DataFrame) -> dict[str, pd.DataFrame]:
    store = wide[["store_id", "state_id"]].drop_duplicates().sort_values("store_id")
    dept = wide[["dept_id", "cat_id"]].drop_duplicates().sort_values("dept_id")
    item = wide[["item_id", "dept_id"]].drop_duplicates().sort_values("item_id")
    series = wide[["id", "item_id", "store_id"]].sort_values("id")

    days = [c for c in wide.columns if c.startswith("d_")]
    long = wide.melt(
        id_vars=["id", "item_id", "store_id"],
        value_vars=days,
        var_name="d",
        value_name="units",
    )
    long = long.merge(cal[["d", "date"]], on="d", how="left", validate="many_to_one")
    if long.date.isna().any():
        raise SystemExit("a sales day is missing from the calendar")
    sales = long[["id", "item_id", "store_id", "date", "units"]].sort_values(
        ["id", "date"]
    )

    return {"store": store, "dept": dept, "item": item, "series": series, "sales": sales}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("m5_dir", type=Path, help="directory with the raw M5 CSV files")
    parser.add_argument("--out", type=Path, default=Path("data"))
    parser.add_argument("--item", default="FOODS_3_586")
    args = parser.parse_args()

    cal = pd.read_csv(args.m5_dir / "calendar.csv")
    wide = pd.read_csv(args.m5_dir / "sales_train_evaluation.csv")
    wide = wide[wide.item_id == args.item]
    if wide.empty:
        raise SystemExit(f"no series for item {args.item}")

    tables = calendar_tables(cal) | sales_tables(wide, cal)
    args.out.mkdir(parents=True, exist_ok=True)
    for name, frame in tables.items():
        frame.to_csv(args.out / f"{name.upper()}.csv", index=False)
        print(f"{name:10} {len(frame):6} rows")


if __name__ == "__main__":
    main()
