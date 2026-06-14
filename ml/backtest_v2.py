"""
backtest_v2.py — Proof that the new forecast works
===================================================
Walk-forward backtest: for each day in the test window we pretend we
only know the data BEFORE that day, predict it one day ahead (exactly
the "today is Friday, predict Saturday" scenario), then compare with
what actually happened.

Reports error (MAE / MAPE) separately for Weekday / Weekend / Event /
Public Holiday, for BOTH models:
  - ML  (HistGradientBoosting with day-type + lag features)
  - Baseline (supervisor's method: average of last 4 same weekdays;
    event days from last 3 event days)

Writes results to Model_Comparison_V2 and prints a table you can
screenshot for your report.

Run:  python backtest_v2.py     (after editing CONFIG in forecast_v2.py)
"""

import numpy as np
import pandas as pd

from forecast_v2 import (load_data, make_features, baseline_predict,
                         event_uplift, get_holidays, day_type,
                         train_model, FEATURES)

TEST_DAYS = 60          # backtest window = last 60 days of history
OUTPUT_TABLE = "Model_Comparison_V2"


def backtest():
    eng, daily, event_dates = load_data()
    years = list(range(daily["date"].min().year,
                       daily["date"].max().year + 2))
    holiday_dates = get_holidays(years)

    cutoff = daily["date"].max() - pd.Timedelta(days=TEST_DAYS)
    train_df = daily[daily["date"] <= cutoff]
    test_df = daily[daily["date"] > cutoff]
    print(f"Training on {len(train_df)} days (<= {cutoff.date()}), "
          f"testing on {len(test_df)} days.")

    uplift = event_uplift(train_df, event_dates)
    rows = []
    for tgt in ("vehicles", "revenue"):
        model, _ = train_model(train_df, tgt, event_dates, holiday_dates)
        # one-step-ahead: lags use TRUE history (it's known by then)
        full_hist = dict(zip(daily["date"], daily[tgt]))
        for _, r in test_df.iterrows():
            d = r["date"]
            hist = {k: v for k, v in full_hist.items() if k < d}
            feat = make_features([d], hist, event_dates, holiday_dates).iloc[0]
            if np.isnan(feat["sdow_mean4"]):
                continue
            ml = float(model.predict(
                feat[FEATURES].to_frame().T.astype(float))[0])
            base, _ = baseline_predict(feat, uplift)
            rows.append(dict(date=d, target=tgt, actual=r[tgt],
                             ml=ml, baseline=float(base),
                             day_type=day_type(feat, holiday_dates)))

    res = pd.DataFrame(rows)
    if res.empty:
        raise SystemExit("No backtest rows — is Daily_Summary long enough?")

    def metrics(g, col):
        err = g[col] - g["actual"]
        mape = (err.abs() / g["actual"].clip(lower=1)).mean() * 100
        return pd.Series({"MAE": err.abs().mean(), "MAPE_%": mape})

    out = []
    for (tgt, dtp), g in res.groupby(["target", "day_type"]):
        for model_name, col in (("ML (HGB + day-type features)", "ml"),
                                ("Baseline (supervisor method)", "baseline")):
            m = metrics(g, col)
            out.append(dict(Target=tgt, Day_Type=dtp, Model=model_name,
                            N_Days=len(g), MAE=round(m["MAE"], 1),
                            MAPE_Percent=round(m["MAPE_%"], 2)))
    # overall rows
    for tgt, g in res.groupby("target"):
        for model_name, col in (("ML (HGB + day-type features)", "ml"),
                                ("Baseline (supervisor method)", "baseline")):
            m = metrics(g, col)
            out.append(dict(Target=tgt, Day_Type="ALL", Model=model_name,
                            N_Days=len(g), MAE=round(m["MAE"], 1),
                            MAPE_Percent=round(m["MAPE_%"], 2)))

    comp = pd.DataFrame(out).sort_values(["Target", "Day_Type", "Model"])
    comp.to_sql(OUTPUT_TABLE, eng, if_exists="replace", index=False)
    print(f'\nWrote {len(comp)} rows to "{OUTPUT_TABLE}".\n')
    print(comp.to_string(index=False))
    print("\nLower MAPE = better. If ML beats the baseline on most rows, "
          "that is your report's key result.")


if __name__ == "__main__":
    backtest()
