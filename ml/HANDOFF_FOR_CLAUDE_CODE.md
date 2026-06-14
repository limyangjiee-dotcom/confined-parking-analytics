# Handoff — continue FYP prediction upgrade (2026-06-12)

Read PROJECT_STATUS.md (project root) for full system context first.
This file covers the new work done in Cowork since then.

## What was just built (folder `prediction_v2`, copy lives in C:\FYP\prediction_v2\)
- `01_create_event_calendar.sql` — `Event_Calendar` table (future events). DONE, run in pgAdmin.
- `forecast_v2.py` — daily forecast: day-type features (dow/weekend/MY-KUL holiday/event)
  + lags (lag-7, mean of last 4 same weekdays, last 3 event days), HGB model
  + supervisor's literal baseline. Writes `Forecast_Daily_V2`.
  STATUS: ran successfully; table already added to the Power BI model.
- `backtest_v2.py` — 60-day walk-forward, MAPE per day type, ML vs baseline → `Model_Comparison_V2`.
- `forecast_hourly_v2.py` — hourly + event-NAME-aware forecast → `Forecast_Hourly_V2`
  (one row per date+hour, 30 days; Day_Type, Time_Band office/after hours,
  Event_Name, Prediction_Basis). `python forecast_hourly_v2.py backtest` for eval.
  STATUS: not yet run against the real DB. Latest fix: reads event names from
  `Event_Log_Table.Event_Category` (there is NO Event_Name col in that table).

## Verified facts about the real DB/model (queried via Power BI 2026-06-12)
- `Daily_Summary`: date col is `Entry_Date`; has `Total_Vehicles`, `Total_Revenue`,
  `Event_Flag`, `Event_Status`, `Occupancy_Rate_%`, `Is_Weekend`, etc.
- `Hourly_Summary`: `Entry_Date`, `Entry_Hour`, `Vehicle_Count`, `Revenue`, `Peak_Period`.
- `Hourly_Occupancy`: `Entry_Date`, `Entry_Hour`, `Occupancy_Rate_%`, `Concurrent_Vehicles`.
- `Event_Log_Table`: `Entry_Date`, `Event_Status` (="Event Day"), `Event_Category`,
  `Vehicles`, `Revenue_RM`. Event types & day counts:
  MegaSale 15, Expo 14, CareerFair 11, RoadShow 8, Concert 5.
- Event names also in `Transactions_Cleaned.Event_Name` and `Live_Parking.Event_Name`.
- DB password: in `forecast_v2.py` CONFIG (user edited it there; hourly script imports it).

## Immediate next steps — ALL DONE 2026-06-13 (see PROJECT_STATUS.md for details)
1. DONE — latest script in place (Event_Category fix confirmed).
2. DONE — Event_Calendar: 'Mega Sale…' → 'MegaSale' (other 2 events intentionally
   unmatched to demo the fallback basis).
3. DONE — `Forecast_Hourly_V2` written (720 rows); MegaSale rows show
   'avg of last 3 "MegaSale" days at HH:00'. Also added: both loaders now
   auto-exclude partial/live days (<50% of median traffic — live-rollup days
   were poisoning lags + backtest) and forecast from TODAY.
4. DONE — `Model_Comparison_V2` (20 rows) + new `Model_Comparison_Hourly_V2`
   (32 rows, hourly backtest now also writes to DB); console output saved to
   backtest_daily_results.txt / backtest_hourly_results.txt.
5. DONE — task "Parking Forecast V2 Daily" (02:10, pythonw, StartWhenAvailable)
   runs run_forecasts_v2.py (both forecasts). Existing "Parking Forecast Retrain"
   (user repointed to forecast_v2.py, python.exe) is permission-locked; left as-is.
6. PARTLY DONE — model tables `Forecast_Hourly_V2`, `Model_Comparison_V2`,
   `Model_Comparison_Hourly_V2` added via DirectQuery + 4 "Tomorrow …" measures
   on Forecast_Daily_V2. REMAINING (manual): place slicers/Tomorrow card visuals,
   then SAVE the pbix ("DASGBOARD_original（copy）" was the open working model).

## Pending bigger task (supervisor request) — DONE 2026-06-13
Barrier-gate integration built into the existing ASP.NET API: gate endpoints
(entry/exit/pay/status/log), per-device API keys (`Gate_Devices` + X-Device-Key),
unpaid-exit/duplicate/misread handling, tariff in code, EasyOCR/webcam demo in
C:\FYP\gate_demo\. 18/18 end-to-end tests pass (test_gate_api.py).
See C:\FYP\gate_demo\README_GATE.md and PROJECT_STATUS.md.
