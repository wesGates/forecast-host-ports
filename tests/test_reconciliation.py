"""Reconcile the host's results with a Python reference computed from the same slice.

Reads the slice from data/ (tools/export_slice.py) and the rows pulled back from the
host from results/ (tools/deploy.sh). Skips when either is missing. Mismatches are
reported row by row; none are tolerated beyond decimal rounding.
"""

from pathlib import Path

import numpy as np
import pandas as pd
import pytest

ROOT = Path(__file__).resolve().parent.parent
DATA = ROOT / "data"
RESULTS = ROOT / "results"

# The origins and the preferred model, as set in ibmi/cl/runchain.clle.
FIRST_ORIGIN = pd.Timestamp("2015-05-24")
LAST_ORIGIN = pd.Timestamp("2016-05-15")
MODEL = "xgboost_rel@item_id"
FALLBACK = "SEASONAL_NAIVE"
SEASON = HORIZON = 7
TOLERANCE = 1e-6

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


def read_host_csv(path: Path) -> pd.DataFrame:
    """A CSV written by CPYTOIMPF: upper-case names, numbers padded with blanks."""
    df = pd.read_csv(path, dtype=str).rename(columns=str.lower)
    df = df.apply(lambda c: c.str.strip())
    for c in ("as_of", "target_date"):
        df[c] = pd.to_datetime(df[c])
    df["horizon"] = df.horizon.astype(int)
    df["forecast"] = df.forecast.astype(float)
    return df


def mismatches(left: pd.DataFrame, right: pd.DataFrame, what: str) -> list[str]:
    """Rows missing on either side, by key."""
    both = left[KEY].merge(right[KEY], on=KEY, how="outer", indicator=True)
    found = []
    for side, label in (("left_only", "host"), ("right_only", "reference")):
        missing = both[both._merge == side]
        if len(missing):
            found.append(
                f"{what}: {len(missing)} rows only in the {label}:\n"
                f"{missing.head().to_string(index=False)}"
            )
    return found


@pytest.fixture(scope="module")
def slice_data() -> dict[str, pd.DataFrame]:
    if not (DATA / "SALES.csv").exists():
        pytest.skip("no data slice; run tools/export_slice.py or tools/deploy.sh")
    sales = pd.read_csv(DATA / "SALES.csv", parse_dates=["date"])
    return {
        "sales": sales,
        "series": pd.read_csv(DATA / "SERIES.csv"),
        "calendar": pd.read_csv(DATA / "CALENDAR.csv", parse_dates=["date"]),
    }


@pytest.fixture(scope="module")
def host() -> dict[str, pd.DataFrame]:
    if not (RESULTS / "FORECAST.csv").exists():
        pytest.skip("no host results; run tools/deploy.sh")
    return {
        t: read_host_csv(RESULTS / f"{t.upper()}.csv") for t in ("forecast", "suggested")
    }


def test_reference_on_a_known_series():
    y = np.arange(1, 22)  # 21 days of sales, origin on day 21
    assert seasonal_naive(y).tolist() == [15, 16, 17, 18, 19, 20, 21]


def test_seasonal_naive_equals_reference(slice_data, host):
    rpg = host["forecast"][host["forecast"].method == FALLBACK]
    ref = reference(slice_data["sales"])
    assert len(ref) == 10 * 358 * HORIZON

    problems = mismatches(rpg, ref, FALLBACK)
    joined = rpg.merge(ref, on=KEY, suffixes=("_host", "_ref"))
    off = joined[
        ((joined.forecast_host - joined.forecast_ref).abs() > TOLERANCE)
        | (joined.horizon_host != joined.horizon_ref)
    ]
    if len(off):
        problems.append(f"{len(off)} rows differ:\n{off.head().to_string(index=False)}")
    assert not problems, "\n".join(problems)

    assert (rpg.fallback == "0").all()
    series = slice_data["series"].set_index("id")
    assert (rpg.item_id.to_numpy() == series.loc[rpg.id, "item_id"].to_numpy()).all()
    assert (rpg.store_id.to_numpy() == series.loc[rpg.id, "store_id"].to_numpy()).all()


def test_suggested_takes_the_model_where_present(host):
    fc = host["forecast"]
    naive = fc[fc.method == FALLBACK]
    model = fc[fc.method == MODEL][[*KEY, "forecast", "method"]]
    expected = naive[KEY].merge(model, on=KEY, how="left")
    fallback = expected.forecast.isna()
    expected.loc[fallback, "forecast"] = (
        naive.set_index(KEY)
        .loc[pd.MultiIndex.from_frame(expected.loc[fallback, KEY]), "forecast"]
        .to_numpy()
    )
    expected.loc[fallback, "method"] = FALLBACK

    got = host["suggested"]
    problems = mismatches(got, expected, "SUGGESTED")
    joined = got.merge(expected, on=KEY)
    off = joined[
        ((joined.forecast_x - joined.forecast_y).abs() > TOLERANCE)
        | (joined.source != joined.method)
    ]
    if len(off):
        problems.append(f"{len(off)} rows differ:\n{off.head().to_string(index=False)}")
    assert not problems, "\n".join(problems)


def test_keys_resolve(slice_data, host):
    ids = set(slice_data["series"].id)
    days = set(slice_data["calendar"].date)
    for name, df, cols in (
        ("SALES", slice_data["sales"], ["date"]),
        ("FORECAST", host["forecast"], ["as_of", "target_date"]),
        ("SUGGESTED", host["suggested"], ["as_of", "target_date"]),
    ):
        assert set(df.id) <= ids, f"{name} has ids not in SERIES"
        for c in cols:
            assert set(df[c]) <= days, f"{name}.{c} has days not in CALENDAR"
