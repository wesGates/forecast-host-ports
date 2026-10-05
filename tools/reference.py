"""The Python reference for the fallback forecast, which the RPG and C# results must equal.

    python tools/reference.py [--data data] [--out results/REFERENCE.csv]

Computes "this day last week" for every series in data/SALES.csv and every origin
of the test year, and writes the rows as CSV for the C# reconciliation test. The
pytest reconciliation imports the same functions.
"""

import argparse
from pathlib import Path

import numpy as np
import pandas as pd

# The origins as set in ibmi/cl/runchain.clle.
FIRST_ORIGIN = pd.Timestamp("2015-05-24")
LAST_ORIGIN = pd.Timestamp("2016-05-15")
SEASON = HORIZON = 7
KEY = ["as_of", "id", "target_date"]


def seasonal_naive(y: np.ndarray, season: int = SEASON, horizon: int = HORIZON):
    """The study's bench_seasonal_naive: y holds sales up to and including the origin."""
    idx = [-season + ((h - 1) % season) for h in range(1, horizon + 1)]
    return y[idx]


def reference(sales: pd.DataFrame) -> pd.DataFrame:
    """Seasonal naive rows for every series and origin, computed from the sales."""
    rows = []
    for sid, s in sales.sort_values("date").groupby("id"):
        dates, units = s.date.to_numpy(), s.units.to_numpy()
        for t in np.flatnonzero((dates >= FIRST_ORIGIN) & (dates <= LAST_ORIGIN)):
            as_of = pd.Timestamp(dates[t])
            for h, f in enumerate(seasonal_naive(units[: t + 1]), start=1):
                rows.append((as_of, sid, as_of + pd.Timedelta(days=h), h, float(f)))
    return pd.DataFrame(rows, columns=[*KEY, "horizon", "forecast"])


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--data", type=Path, default=Path("data"))
    parser.add_argument("--out", type=Path, default=Path("results/REFERENCE.csv"))
    args = parser.parse_args()

    ref = reference(pd.read_csv(args.data / "SALES.csv", parse_dates=["date"]))
    args.out.parent.mkdir(parents=True, exist_ok=True)
    ref.to_csv(args.out, index=False, date_format="%Y-%m-%d")
    print(f"{args.out}: {len(ref)} rows")


if __name__ == "__main__":
    main()
