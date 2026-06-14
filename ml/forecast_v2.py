"""
forecast_v2.py — Day-type-aware daily parking forecast (FYP upgrade)
=====================================================================
What's new vs forecast_model.py / forecast_retrain.py:
  1. Day-type features: day-of-week, weekend, Malaysian public holiday,
     event day (past events from Event_Log_Table, future from Event_Calendar).
  2. Lag features: same weekday last week (lag-7), average of the last 4
     same weekdays, average of the last 3 event days.
     -> "predict Saturday from previous Saturdays, event days from
        previous event days" — exactly what the supervisor asked.
  3. Supervisor's method implemented literally as a BASELINE, so the ML
     model can be compared against it (see backtest_v2.py).
  4. Output table Forecast_Daily_V2 includes Day_Type and
     Prediction_Basis (explains what each prediction was based on).

Run:  python forecast_v2.py
Deps: pip install pandas scikit-learn sqlalchemy psycopg2-binary holidays
"""

import sys
import datetime as dt
import numpy as np
import pandas as pd

# ----------------------------------------------------------------------
# CONFIG — edit the password; everything else should match your setup
# ----------------------------------------------------------------------
DB = dict(
    host="localhost",
    port=5432,
    dbname="parking_db",
    user="postgres",
    password="parking123",   # <-- EDIT (same as forecast_retrain.py)
)

DAILY_TABLE     = "Daily_Summary"
EVENT_LOG_TABLE = "Event_Log_Table"
EVENT_CAL_TABLE = "Event_Calendar"
OUTPUT_TABLE    = "Forecast_Daily_V2"   # new table; old Forecast_30Days untouched

HORIZON_DAYS = 30

# Candidate column names — the script auto-detects which one your table
# actually uses. If detection fails it prints the real columns so you
# can add the right name here.
DATE_COLS     = ["Entry_Date", "Date", "date", "Summary_Date", "Day", "Txn_Date"]
VEHICLE_COLS  = ["Total_Vehicles", "Vehicles", "Vehicle_Count",
                 "Total_Entries", "Total_Transactions", "Vehicle_Volume"]
REVENUE_COLS  = ["Total_Revenue", "Revenue", "Daily_Revenue", "Total_Fee"]
EVT_DATE_COLS = ["Date", "Event_Date", "date", "Log_Date", "Entry_Date"]

DOW_NAMES = ["Monday", "Tuesday", "Wednesday", "Thursday",
             "Friday", "Saturday", "Sunday"]


# ----------------------------------------------------------------------
# Helpers
# ----------------------------------------------------------------------
def find_col(df: pd.DataFrame, candidates, what, table):
    for c in candidates:
        if c in df.columns:
            return c
    sys.exit(f"ERROR: could not find the {what} column in {table}.\n"
             f"Columns found: {list(df.columns)}\n"
             f"-> Add the correct name to the candidate list in CONFIG.")


def get_holidays(years):
    """Malaysian public holidays (Kuala Lumpur)."""
    import holidays as hol
    try:
        return set(hol.Malaysia(subdiv="KUL", years=years).keys())
    except Exception:
        return set(hol.Malaysia(years=years).keys())


def load_data():
    from sqlalchemy import create_engine
    from sqlalchemy.engine import URL
    url = URL.create("postgresql+psycopg2",
                     username=DB["user"], password=DB["password"],
                     host=DB["host"], port=DB["port"], database=DB["dbname"])
    eng = create_engine(url)

    raw = pd.read_sql(f'SELECT * FROM "{DAILY_TABLE}"', eng)
    dcol = find_col(raw, DATE_COLS, "date", DAILY_TABLE)
    vcol = find_col(raw, VEHICLE_COLS, "vehicle-count", DAILY_TABLE)
    rcol = find_col(raw, REVENUE_COLS, "revenue", DAILY_TABLE)
    daily = (raw[[dcol, vcol, rcol]]
             .rename(columns={dcol: "date", vcol: "vehicles", rcol: "revenue"}))
    daily["date"] = pd.to_datetime(daily["date"]).dt.normalize()
    daily = daily.groupby("date", as_index=False).sum().sort_values("date")

    # Drop partial days (live-rollup rows still filling in during the demo):
    # a half-recorded day must never be used as a lag value or training row.
    med = daily["vehicles"].median()
    partial = daily["vehicles"] < 0.5 * med
    if partial.any():
        days = ", ".join(str(d.date()) for d in daily.loc[partial, "date"])
        print(f"Excluding {int(partial.sum())} partial/live day(s) "
              f"(<50% of median traffic): {days}")
        daily = daily[~partial].copy()

    event_dates = set()
    # Past event days directly from Daily_Summary's Event_Flag (most reliable)
    if "Event_Flag" in raw.columns:
        flag = raw["Event_Flag"]
        mask = (flag.astype(str).str.strip().str.lower()
                .isin(["1", "true", "yes", "y", "event"])) | (
                pd.to_numeric(flag, errors="coerce").fillna(0) > 0)
        event_dates |= set(pd.to_datetime(raw.loc[mask, dcol]).dt.normalize())
    for tbl in (EVENT_LOG_TABLE, EVENT_CAL_TABLE):
        try:
            ev = pd.read_sql(f'SELECT * FROM "{tbl}"', eng)
            ecol = find_col(ev, EVT_DATE_COLS, "event-date", tbl)
            event_dates |= set(pd.to_datetime(ev[ecol]).dt.normalize())
        except SystemExit:
            raise
        except Exception as e:
            print(f"WARNING: could not read {tbl} ({e}) — continuing without it.")
    return eng, daily, event_dates


# ----------------------------------------------------------------------
# Feature engineering  (works on a per-target value series)
# ----------------------------------------------------------------------
def make_features(dates, values_history, event_dates, holiday_dates):
    """
    Build the feature row for each date in `dates`, using only values
    strictly BEFORE that date (no leakage).
    `values_history` : dict {Timestamp: value} of known/predicted values.
    Returns DataFrame of features (may contain NaN for early dates).
    """
    rows = []
    # pre-sort event-day history for "last 3 event days" lookups
    for d in dates:
        dow = d.dayofweek
        # lag-7 and same-weekday averages
        sdow_vals = []
        for k in range(1, 5):                       # last 4 same weekdays
            prev = d - pd.Timedelta(days=7 * k)
            if prev in values_history:
                sdow_vals.append(values_history[prev])
        lag7 = sdow_vals[0] if sdow_vals else np.nan

        # last 3 event days strictly before d
        past_events = sorted(e for e in event_dates if e < d and e in values_history)
        ev_vals = [values_history[e] for e in past_events[-3:]]

        rows.append(dict(
            date=d,
            dow=dow,
            is_weekend=int(dow >= 5),
            is_holiday=int(d.date() in holiday_dates or d in holiday_dates),
            is_event=int(d in event_dates),
            month=d.month,
            day_of_month=d.day,
            lag7=lag7,
            sdow_mean4=np.mean(sdow_vals) if sdow_vals else np.nan,
            event_mean3=np.mean(ev_vals) if ev_vals else np.nan,
        ))
    return pd.DataFrame(rows)


FEATURES = ["dow", "is_weekend", "is_holiday", "is_event",
            "month", "day_of_month", "lag7", "sdow_mean4", "event_mean3"]


def train_model(daily, target, event_dates, holiday_dates):
    """Train HistGradientBoosting on history for one target column."""
    from sklearn.ensemble import HistGradientBoostingRegressor
    hist = dict(zip(daily["date"], daily[target]))
    X = make_features(list(daily["date"]), hist, event_dates, holiday_dates)
    y = daily[target].values
    ok = X["sdow_mean4"].notna()                    # skip first ~4 weeks
    model = HistGradientBoostingRegressor(random_state=42)
    model.fit(X.loc[ok, FEATURES], y[ok.values])
    return model, hist


def baseline_predict(feat_row, global_event_uplift):
    """
    The supervisor's method, literally:
      normal day -> average of the last 4 same weekdays
      event day  -> average of the last 3 previous event days
                    (fallback: same-weekday average x typical event uplift)
    """
    if feat_row["is_event"]:
        if not np.isnan(feat_row["event_mean3"]):
            return feat_row["event_mean3"], "avg of last 3 event days"
        return (feat_row["sdow_mean4"] * global_event_uplift,
                f"avg of last 4 {DOW_NAMES[int(feat_row['dow'])]}s x event uplift")
    return (feat_row["sdow_mean4"],
            f"avg of last 4 {DOW_NAMES[int(feat_row['dow'])]}s")


def event_uplift(daily, event_dates):
    """Typical ratio of event-day traffic vs same-weekday normal traffic."""
    df = daily.copy()
    df["is_event"] = df["date"].isin(event_dates)
    df["dow"] = df["date"].dt.dayofweek
    norm = df[~df["is_event"]].groupby("dow")["vehicles"].mean()
    ev = df[df["is_event"]]
    if ev.empty:
        return 1.15
    ratios = ev["vehicles"] / ev["dow"].map(norm)
    return float(np.clip(ratios.mean(), 1.0, 2.0))


def day_type(row, holiday_dates):
    if row["is_event"]:
        return "Event"
    if row["is_holiday"]:
        return "Public Holiday"
    return "Weekend" if row["is_weekend"] else "Weekday"


# ----------------------------------------------------------------------
# Recursive 30-day forecast (later days reuse earlier predictions
# so the lag features always exist)
# ----------------------------------------------------------------------
def forecast(daily, event_dates, horizon=HORIZON_DAYS):
    years = list(range(daily["date"].min().year,
                       daily["date"].max().year + 2))
    holiday_dates = get_holidays(years)
    uplift = event_uplift(daily, event_dates)

    models, hists = {}, {}
    for tgt in ("vehicles", "revenue"):
        models[tgt], hists[tgt] = train_model(daily, tgt, event_dates, holiday_dates)

    # Forecast from today even if the last complete day is older
    # (partial live days get excluded in load_data).
    start = max(daily["date"].max() + pd.Timedelta(days=1),
                pd.Timestamp.now().normalize())
    future = pd.date_range(start, periods=horizon, freq="D")

    out = []
    for d in future:
        rec = dict(Date=d.date(), Day_Name=DOW_NAMES[d.dayofweek])
        for tgt in ("vehicles", "revenue"):
            feat = make_features([d], hists[tgt], event_dates,
                                 holiday_dates).iloc[0]
            ml = float(models[tgt].predict(
                feat[FEATURES].to_frame().T.astype(float))[0])
            base, basis = baseline_predict(feat, uplift)
            hists[tgt][d] = ml                     # recursive: feed prediction back
            rec[f"Predicted_{tgt.capitalize()}"] = round(max(ml, 0))
            rec[f"Baseline_{tgt.capitalize()}"] = round(max(float(base), 0)) \
                if not np.isnan(base) else None
            if tgt == "vehicles":
                rec["Day_Type"] = day_type(feat, holiday_dates)
                rec["Prediction_Basis"] = basis
        out.append(rec)
    return pd.DataFrame(out)[["Date", "Day_Name", "Day_Type",
                              "Predicted_Vehicles", "Baseline_Vehicles",
                              "Predicted_Revenue", "Baseline_Revenue",
                              "Prediction_Basis"]]


def main():
    eng, daily, event_dates = load_data()
    print(f"Loaded {len(daily)} days of history "
          f"({daily['date'].min().date()} -> {daily['date'].max().date()}), "
          f"{len(event_dates)} event days known.")
    fc = forecast(daily, event_dates)
    fc.to_sql(OUTPUT_TABLE, eng, if_exists="replace", index=False)
    print(f"\nWrote {len(fc)} rows to \"{OUTPUT_TABLE}\".")
    print(fc.head(10).to_string(index=False))


if __name__ == "__main__":
    main()
