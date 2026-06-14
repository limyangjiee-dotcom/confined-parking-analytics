# Prediction Upgrade V2 — Step-by-Step Guide

Your supervisor asked: predict by day type (weekday / weekend / event day),
base each day's prediction on previous similar days (Saturday from past
Saturdays, event days from past event days), updated daily.
**You do NOT redo anything** — these 3 files extend your current system.

## What each file does

| File | Purpose |
|---|---|
| `01_create_event_calendar.sql` | New `Event_Calendar` table for FUTURE planned events (the model can't know tomorrow has an event unless you tell it) |
| `forecast_v2.py` | New forecast: ML model with day-type + lag features, PLUS your supervisor's method as a baseline. Writes `Forecast_Daily_V2` |
| `backtest_v2.py` | Proof it works: predicts the last 60 days one-day-ahead, reports error per day type, ML vs baseline. Writes `Model_Comparison_V2` |

Your old `Forecast_30Days` table and Power BI page are NOT touched, so
nothing breaks while you test.

## Step 1 — Install packages (once)

Open Command Prompt:
```
pip install pandas scikit-learn sqlalchemy psycopg2-binary holidays
```
(`holidays` gives real Malaysian/KL public holidays automatically.)

## Step 2 — Create the Event_Calendar table

1. Copy this whole `prediction_v2` folder into `C:\FYP\`.
2. Open pgAdmin 4 → `parking_db` → Query Tool.
3. Open and run `01_create_event_calendar.sql`.
4. **Edit the sample events** — put events on dates near your demo/presentation
   so the forecast visibly reacts to them.

## Step 3 — Configure and run the forecast

1. Open `forecast_v2.py`, set your postgres password in `DB` (top of file,
   same as in `forecast_retrain.py`).
2. Run: `python C:\FYP\prediction_v2\forecast_v2.py`
3. It auto-detects your column names. If it prints
   `ERROR: could not find the ... column`, it also lists the real column
   names — add the right one to the candidate list at the top and rerun.
4. Check pgAdmin: new table `Forecast_Daily_V2` with columns
   `Date, Day_Name, Day_Type, Predicted_Vehicles, Baseline_Vehicles,
   Predicted_Revenue, Baseline_Revenue, Prediction_Basis`.

## Step 4 — Run the backtest (your report's key evidence)

```
python C:\FYP\prediction_v2\backtest_v2.py
```
This simulates "today is Friday, predict Saturday" for every day in the
last 60 days and prints MAE/MAPE per day type for both models.
Screenshot the table; it also lands in `Model_Comparison_V2` for Power BI.
**Expected result:** ML beats the baseline overall and especially on
event days. If baseline wins somewhere, say so honestly in the report —
examiners like that.

## Step 5 — Schedule it daily

Windows Task Scheduler → edit your existing "Forecast retrain" daily task
(or clone it) → change the script argument to
`C:\FYP\prediction_v2\forecast_v2.py`. Keep `pythonw.exe`, "Run only when
user is logged on", and quotes around paths (your usual gotchas).
Now every day the forecast refreshes from the latest data — including the
live simulator data rolled up by `rollup_live.py`.

## Step 6 — Update the Forecast page (Power BI + web app)

Power BI: Get Data → PostgreSQL → add `Forecast_Daily_V2` (DirectQuery).
Suggested visuals:
- **Card: "Tomorrow"** — Predicted_Vehicles + Day_Type (supervisor asked
  for daily prediction; make tomorrow prominent, not just a 30-day line).
- Line/column chart of Predicted_Vehicles colored by `Day_Type`
  (event days visibly spike = exactly what he wants to see).
- Table with `Prediction_Basis` — shows each prediction is explainable
  ("avg of last 4 Saturdays", "avg of last 3 event days").
- Optional: clustered columns Predicted vs Baseline.

Web app: add a fetch of `/api/data/Forecast_Daily_V2` (your generic
`/data/{table}` endpoint already serves it — zero API changes needed).

## How to explain it to your supervisor (and in the report)

1. "I implemented your method exactly as a **baseline**: Saturday is
   predicted from the average of the last 4 Saturdays; an event day from
   the average of the last 3 event days."
2. "Then I gave the ML model the same knowledge as **features** (day of
   week, weekend, public holiday, event flag, same-weekday lags,
   event-day lags), so it learns those patterns plus interactions."
3. "A 60-day walk-forward backtest shows MAPE per day type; the ML model
   improves on the baseline by X%." (fill X from Step 4)
4. "Future events come from an `Event_Calendar` table — realistic, since
   malls plan events in advance. Past events come from `Event_Log_Table`."
5. "It retrains daily via Task Scheduler, so tomorrow's prediction always
   uses the newest data."

## Step 7 — Hourly + event-name forecast (`forecast_hourly_v2.py`)

Answers "next Tuesday 2pm?" and "how will MegaSale traffic look?".
Uses your `Hourly_Summary` (+ `Hourly_Occupancy` if readable) tables.
Password is taken from `forecast_v2.py` — no second edit needed.

```
python C:\FYP\prediction_v2\forecast_hourly_v2.py            (forecast)
python C:\FYP\prediction_v2\forecast_hourly_v2.py backtest   (proof)
```

Writes `Forecast_Hourly_V2`: one row per (date, hour) for 30 days with
`Day_Type`, `Time_Band` (Office Hours 08–18 / After Hours), `Event_Name`,
predicted + baseline vehicles (and occupancy), and `Prediction_Basis`.

How predictions are based on similar history:
- Normal hour → last 4 same weekday at the same hour
  ("next Tuesday 2pm" ← previous Tuesdays 2pm).
- Event day → previous days of the SAME event name at the same hour
  ("MegaSale" ← past MegaSale days; name matching is fuzzy, so
  "Mega Sale Carnival" matches "MegaSale").
- New event with no history → average of recent event days (fallback,
  stated in `Prediction_Basis`).

**For event forecasts to appear, the event must be in `Event_Calendar`**
with the same (or similar) name as its past occurrences in
`Event_Log_Table`.

Example queries:

```sql
-- next Tuesday 2pm
SELECT * FROM "Forecast_Hourly_V2"
WHERE "Day_Name"='Tuesday' AND "Hour"=14 ORDER BY "Date" LIMIT 1;

-- MegaSale traffic by hour
SELECT "Date","Hour","Predicted_Vehicles","Prediction_Basis"
FROM "Forecast_Hourly_V2"
WHERE "Event_Name" ILIKE '%mega%sale%' ORDER BY "Date","Hour";

-- office vs after hours comparison
SELECT "Time_Band", AVG("Predicted_Vehicles")
FROM "Forecast_Hourly_V2" GROUP BY "Time_Band";
```

Power BI: add `Forecast_Hourly_V2`, then slicers on `Day_Name`, `Hour`,
`Time_Band`, `Event_Name` — that IS the "pick a specific day and time"
feature. Web app: `/api/data/Forecast_Hourly_V2` already serves it.

## Troubleshooting

- `could not find the ... column` → add your real column name to the
  candidate lists in `forecast_v2.py` CONFIG.
- `password authentication failed` → wrong password in `DB`.
- `No module named holidays` → rerun Step 1.
- Event days not spiking in forecast → no future events in
  `Event_Calendar` within the next 30 days. Add some (Step 2.4).
- Backtest "Event" rows missing → no event days fell in the last 60 days
  of history; raise `TEST_DAYS` in `backtest_v2.py` (e.g. 120).
