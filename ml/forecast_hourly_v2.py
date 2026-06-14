"""
forecast_hourly_v2.py — Hour-level, event-NAME-aware parking forecast
======================================================================
Answers questions like:
  "What will parking look like next Tuesday at 2pm?"
     -> predicted from previous Tuesdays at 2pm (same weekday + same hour)
  "What will the MegaSale event traffic look like?"
     -> predicted from previous MegaSale days at the same hour
        (event matched BY NAME, not just 'some event')

Output table: Forecast_Hourly_V2 — one row per (date, hour) for the next
30 days, with Day_Type, Time_Band (Office Hours / After Hours),
Event_Name and Prediction_Basis explaining what each number is based on.

Uses the SAME password as forecast_v2.py (edit it there only).

Run forecast:  python forecast_hourly_v2.py
Run backtest:  python forecast_hourly_v2.py backtest
"""

import sys
import numpy as np
import pandas as pd

from forecast_v2 import (DB, get_holidays, DOW_NAMES, find_col,
                         EVENT_LOG_TABLE, EVENT_CAL_TABLE)

# ----------------------------------------------------------------------
# CONFIG
# ----------------------------------------------------------------------
HOURLY_TABLE   = "Hourly_Summary"
OCC_TABLE      = "Hourly_Occupancy"     # optional; merged if readable
OUTPUT_TABLE   = "Forecast_Hourly_V2"
HORIZON_DAYS   = 30
OFFICE_HOURS   = (8, 18)                # 08:00–17:59 = "Office Hours"
BACKTEST_DAYS  = 14

DATE_COLS    = ["Entry_Date", "Date", "date", "Summary_Date", "Day"]
HOUR_COLS    = ["Hour", "Entry_Hour", "Hour_Of_Day", "hour", "Hr",
                "Hour_24", "Time_Hour"]
VEHICLE_COLS = ["Total_Vehicles", "Vehicles", "Vehicle_Count",
                "Total_Entries", "Entries", "Total_Transactions"]
OCC_COLS     = ["Occupancy_Rate_%", "Occupancy_Rate", "Occupancy",
                "Avg_Occupancy_%", "Occupied_Bays", "Occupancy_Percent"]
EVT_DATE_COLS = ["Date", "Event_Date", "date", "Log_Date", "Entry_Date"]
EVT_NAME_COLS = ["Event_Name", "Event_Category", "Name", "Event", "Event_Title"]

FEATURES = ["hour", "dow", "is_weekend", "is_holiday", "is_event",
            "office_hour", "month", "lag_w", "sdow_mean4",
            "event_mean3", "same_event_mean"]


# ----------------------------------------------------------------------
# Event-name matching ("Mega Sale Carnival" matches "MegaSale Carnival")
# ----------------------------------------------------------------------
def norm_name(s):
    return "".join(ch for ch in str(s).lower() if ch.isalnum())


def same_event(a, b):
    na, nb = norm_name(a), norm_name(b)
    return bool(na) and bool(nb) and (na == nb or na in nb or nb in na)


# ----------------------------------------------------------------------
# Data loading
# ----------------------------------------------------------------------
def load_hourly():
    from sqlalchemy import create_engine
    from sqlalchemy.engine import URL
    url = URL.create("postgresql+psycopg2",
                     username=DB["user"], password=DB["password"],
                     host=DB["host"], port=DB["port"], database=DB["dbname"])
    eng = create_engine(url)

    df = pd.read_sql(f'SELECT * FROM "{HOURLY_TABLE}"', eng)
    dcol = find_col(df, DATE_COLS, "date", HOURLY_TABLE)
    hcol = find_col(df, HOUR_COLS, "hour", HOURLY_TABLE)
    vcol = find_col(df, VEHICLE_COLS, "vehicle-count", HOURLY_TABLE)
    df = df[[dcol, hcol, vcol]].rename(
        columns={dcol: "date", hcol: "hour", vcol: "vehicles"})
    df["date"] = pd.to_datetime(df["date"]).dt.normalize()
    df["hour"] = pd.to_numeric(df["hour"], errors="coerce").astype(int)
    df = df.groupby(["date", "hour"], as_index=False).sum()

    # Drop partial days (live-rollup rows still filling in during the demo)
    day_tot = df.groupby("date")["vehicles"].sum()
    partial = day_tot[day_tot < 0.5 * day_tot.median()].index
    if len(partial):
        print(f"Excluding {len(partial)} partial/live day(s) "
              f"(<50% of median traffic): "
              + ", ".join(str(d.date()) for d in partial))
        df = df[~df["date"].isin(partial)]

    # optional occupancy merge
    targets = ["vehicles"]
    try:
        oc = pd.read_sql(f'SELECT * FROM "{OCC_TABLE}"', eng)
        odc = find_col(oc, DATE_COLS, "date", OCC_TABLE)
        ohc = find_col(oc, HOUR_COLS, "hour", OCC_TABLE)
        occ = find_col(oc, OCC_COLS, "occupancy", OCC_TABLE)
        oc = oc[[odc, ohc, occ]].rename(
            columns={odc: "date", ohc: "hour", occ: "occupancy"})
        oc["date"] = pd.to_datetime(oc["date"]).dt.normalize()
        oc["hour"] = pd.to_numeric(oc["hour"], errors="coerce").astype(int)
        oc = oc.groupby(["date", "hour"], as_index=False).mean()
        df = df.merge(oc, on=["date", "hour"], how="left")
        if df["occupancy"].notna().mean() > 0.8:
            targets.append("occupancy")
        else:
            df = df.drop(columns=["occupancy"])
    except SystemExit:
        raise
    except Exception as e:
        print(f"WARNING: no occupancy data merged ({e}).")

    # events: {date -> event name}
    events = {}
    for tbl in (EVENT_LOG_TABLE, EVENT_CAL_TABLE):
        try:
            ev = pd.read_sql(f'SELECT * FROM "{tbl}"', eng)
            ecol = find_col(ev, EVT_DATE_COLS, "event-date", tbl)
            ncol = next((c for c in EVT_NAME_COLS if c in ev.columns), None)
            for _, r in ev.iterrows():
                d = pd.to_datetime(r[ecol]).normalize()
                events[d] = str(r[ncol]) if ncol else "Event"
        except SystemExit:
            raise
        except Exception as e:
            print(f"WARNING: could not read {tbl} ({e}).")
    return eng, df.sort_values(["date", "hour"]), events


# ----------------------------------------------------------------------
# Features for one (date, hour), using only earlier values (no leakage)
# ----------------------------------------------------------------------
def make_features_h(pairs, hist, events, holiday_dates):
    ev_dates = sorted(events)
    rows = []
    for d, h in pairs:
        dow = d.dayofweek
        sdow = []
        for k in range(1, 5):                          # last 4 same weekday@hour
            prev = (d - pd.Timedelta(days=7 * k), h)
            if prev in hist:
                sdow.append(hist[prev])

        past_ev = [e for e in ev_dates if e < d and (e, h) in hist]
        ev_vals = [hist[(e, h)] for e in past_ev[-3:]]

        name = events.get(d)
        same_vals = []
        if name:
            same_vals = [hist[(e, h)] for e in past_ev
                         if same_event(events[e], name)][-3:]

        rows.append(dict(
            date=d, hour=h, dow=dow,
            is_weekend=int(dow >= 5),
            is_holiday=int(d.date() in holiday_dates or d in holiday_dates),
            is_event=int(d in events),
            office_hour=int(OFFICE_HOURS[0] <= h < OFFICE_HOURS[1]),
            month=d.month,
            lag_w=sdow[0] if sdow else np.nan,
            sdow_mean4=np.mean(sdow) if sdow else np.nan,
            event_mean3=np.mean(ev_vals) if ev_vals else np.nan,
            same_event_mean=np.mean(same_vals) if same_vals
                            else (np.mean(ev_vals) if ev_vals else np.nan),
            _n_same=len(same_vals),
        ))
    return pd.DataFrame(rows)


def baseline_predict_h(f, events):
    """Supervisor's method at hour level."""
    d = f["date"]
    name = events.get(d)
    if f["is_event"] and name:
        if f["_n_same"] > 0:
            return f["same_event_mean"], \
                   f'avg of last {int(f["_n_same"])} "{name}" days at {int(f["hour"]):02d}:00'
        if not np.isnan(f["event_mean3"]):
            return f["event_mean3"], \
                   f"avg of last event days at {int(f['hour']):02d}:00 (no '{name}' history)"
    return f["sdow_mean4"], \
           f"avg of last 4 {DOW_NAMES[int(f['dow'])]}s at {int(f['hour']):02d}:00"


def day_type_h(f):
    if f["is_event"]:
        return "Event"
    if f["is_holiday"]:
        return "Public Holiday"
    return "Weekend" if f["is_weekend"] else "Weekday"


def train(df, target, events, holiday_dates):
    from sklearn.ensemble import HistGradientBoostingRegressor
    hist = {(d, h): v for d, h, v in
            zip(df["date"], df["hour"], df[target])}
    X = make_features_h(list(zip(df["date"], df["hour"])), hist,
                        events, holiday_dates)
    ok = X["sdow_mean4"].notna()
    model = HistGradientBoostingRegressor(random_state=42)
    model.fit(X.loc[ok, FEATURES], df[target].values[ok.values])
    return model, hist


# ----------------------------------------------------------------------
# Recursive hourly forecast
# ----------------------------------------------------------------------
def forecast(df, events, targets, horizon=HORIZON_DAYS):
    years = list(range(df["date"].min().year, df["date"].max().year + 2))
    hols = get_holidays(years)

    models, hists = {}, {}
    for t in targets:
        models[t], hists[t] = train(df, t, events, hols)

    # Forecast from today even if the last complete day is older
    # (partial live days get excluded in load_hourly).
    start = max(df["date"].max() + pd.Timedelta(days=1),
                pd.Timestamp.now().normalize())
    out = []
    for d in pd.date_range(start, periods=horizon, freq="D"):
        for h in range(24):
            rec = dict(Date=d.date(), Hour=h,
                       Day_Name=DOW_NAMES[d.dayofweek],
                       Time_Band="Office Hours"
                       if OFFICE_HOURS[0] <= h < OFFICE_HOURS[1]
                       else "After Hours",
                       Event_Name=events.get(d, ""))
            for t in targets:
                f = make_features_h([(d, h)], hists[t], events, hols).iloc[0]
                ml = float(models[t].predict(
                    f[FEATURES].to_frame().T.astype(float))[0])
                base, basis = baseline_predict_h(f, events)
                hists[t][(d, h)] = ml              # recursive feed-back
                nd = 1 if t == "occupancy" else 0
                rec[f"Predicted_{t.capitalize()}"] = round(max(ml, 0), nd)
                rec[f"Baseline_{t.capitalize()}"] = \
                    round(max(float(base), 0), nd) if not np.isnan(base) else None
                if t == targets[0]:
                    rec["Day_Type"] = day_type_h(f)
                    rec["Prediction_Basis"] = basis
            out.append(rec)
    return pd.DataFrame(out)


# ----------------------------------------------------------------------
# Backtest: one-step-ahead over the last BACKTEST_DAYS days
# ----------------------------------------------------------------------
COMPARISON_TABLE = "Model_Comparison_Hourly_V2"


def backtest(eng, df, events, targets):
    years = list(range(df["date"].min().year, df["date"].max().year + 2))
    hols = get_holidays(years)
    cutoff = df["date"].max() - pd.Timedelta(days=BACKTEST_DAYS)
    tr, te = df[df["date"] <= cutoff], df[df["date"] > cutoff]
    print(f"Train <= {cutoff.date()} ({len(tr)} rows); "
          f"test {len(te)} hourly rows.")
    res = []
    for t in targets:
        model, _ = train(tr, t, events, hols)
        full = {(d, h): v for d, h, v in zip(df["date"], df["hour"], df[t])}
        for _, r in te.iterrows():
            d, h = r["date"], r["hour"]
            hist = {k: v for k, v in full.items() if k[0] < d}
            f = make_features_h([(d, h)], hist, events, hols).iloc[0]
            if np.isnan(f["sdow_mean4"]):
                continue
            ml = float(model.predict(
                f[FEATURES].to_frame().T.astype(float))[0])
            base, _ = baseline_predict_h(f, events)
            res.append(dict(target=t, day_type=day_type_h(f),
                            band="Office Hours"
                            if OFFICE_HOURS[0] <= h < OFFICE_HOURS[1]
                            else "After Hours",
                            actual=r[t], ml=ml, baseline=float(base)))
    res = pd.DataFrame(res)
    rows = []
    for keys, g in res.groupby(["target", "day_type", "band"]):
        for mname, col in (("ML", "ml"), ("Baseline", "baseline")):
            mape = ((g[col] - g["actual"]).abs()
                    / g["actual"].clip(lower=1)).mean() * 100
            rows.append(dict(Target=keys[0], Day_Type=keys[1],
                             Time_Band=keys[2], Model=mname,
                             N=len(g), MAPE_Percent=round(mape, 2)))
    comp = pd.DataFrame(rows)
    comp.to_sql(COMPARISON_TABLE, eng, if_exists="replace", index=False)
    print(f'Wrote {len(comp)} rows to "{COMPARISON_TABLE}".')
    print(comp.to_string(index=False))


def main():
    eng, df, events = load_hourly()
    targets = [c for c in ("vehicles", "occupancy") if c in df.columns]
    print(f"Loaded {len(df)} hourly rows "
          f"({df['date'].min().date()} -> {df['date'].max().date()}), "
          f"{len(events)} event days, targets: {targets}")
    if len(sys.argv) > 1 and sys.argv[1] == "backtest":
        backtest(eng, df, events, targets)
        return
    fc = forecast(df, events, targets)
    fc.to_sql(OUTPUT_TABLE, eng, if_exists="replace", index=False)
    print(f'\nWrote {len(fc)} rows to "{OUTPUT_TABLE}".')
    print(fc[fc["Hour"] == 14].head(8).to_string(index=False))


if __name__ == "__main__":
    main()
