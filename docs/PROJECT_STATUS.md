# FYP Project Status — Confined Parking Data Analytics

## Project
- **FYP T711641** — "Building Data Analytics for Confined Parking Data using BI Tools."
- **Virtual target:** a ~**11,000-bay** confined (multi-storey) carpark.
- **Tariff the synthetic data is calibrated to (real):** free first 15 min; weekday RM2 first 3 hrs, +RM1 hr 3–4, +RM2.50/hr after; flat **RM2** on Fri/weekend/public holiday.
- **Deliverables:** ML forecast model, BI reports, web prototype, real-time pipeline, external-system integration — all built.

## Status
**Software COMPLETE as of 2026-06-16** (branch `main`, latest commit `95b0535`, not pushed to GitHub yet).
Builds clean (0 warnings); all features verified. Remaining work is the **written FYP report** + the user
pushing to GitHub. A pre-iCal backup copy is at `C:\FYP\parking-analytics-fyp_BACKUP_pre-ical_20260616`.

## Source of truth
Everything lives in **one git repo: `C:\FYP\parking-analytics-fyp\`** (branch `main`). Layout:
`api/` (ASP.NET Core 8 API + dashboard in `api/wwwroot`), `ml/` (Python forecast), `scripts/`
(rollup/retrain), `connector_demo/` (mock external REST API), `database/parking_db.dump` (+restore),
`docs/`, `powerbi/`, `README.md`.

## Architecture
This platform is the **analytics layer**. It does **not** own the gate / barrier / ANPR camera —
those belong to the operator's parking system. The platform **connects to that system's REST API**,
pulls the session data in, rebuilds its summary tables, and runs analytics + ML forecasting on top.

## Stack & how to run
- **API + web:** ASP.NET Core (.NET 8) serving a vanilla-JS + Chart.js dashboard.
- **Database:** PostgreSQL 16 (`parking_db`, user `postgres`, password `parking123`).
- **ML:** Python 3.10+ (scikit-learn HistGradientBoosting).
- **Run:** restore the dump (`database/restore_database.ps1`), then
  `cd api && dotnet run --urls http://localhost:5000` → open http://localhost:5000.
  Build gotcha: stop the running app first (`Get-Process ParkingApiPg | Stop-Process -Force`) — Windows locks the DLL **and** port 5000.
- **API key** (dashboard ↔ API): `appsettings.json` → `ApiKey` (default `fyp-demo-key-2025`, header `X-Api-Key`).

## Web dashboard — 9 pages
Overview · Real-Time Status (5 s) · Occupancy (heatmap + entries-vs-exits by hour) · Vehicle Analysis ·
Driver Behaviour (resident/worker/visitor heuristic) · Events Impact · Revenue · Predictive Analytics
(7-day forecast + confidence + "Run forecast now") · Data Source (connector). Every analytics page has a
date-range filter; all `/api/dash/*` endpoints take `?from=YYYY-MM-DD&to=YYYY-MM-DD`.

## External-system connector (REST API)
- `ConnectorController` (`/api/connector`) delegates to `Services/IngestionService.cs`:
  `POST /test`, `POST /discover` (returns the API's JSON fields), `GET`/`POST /config`
  (saved in `Data_Source_Config`, auth value masked on read), `POST /sync`, `GET /status`.
- **Flow:** Test → Discover → Map JSON fields → Save → Sync. Sync pulls sessions, **bulk-loads via
  binary COPY** into a temp table, then one set-based deduped insert into `Live_Parking`
  (ticket prefix `IMP…` marks imported rows; deduped on plate+entry-time) — scales to hundreds of thousands of rows.
- **Auto-aggregation** (`Services/AggregationService.cs`): after each sync, rebuilds the date-grained
  summary tables (`Daily_Summary`, `Hourly_Summary`, `Hourly_Occupancy`, `Event_Log_Table`,
  `Transactions_Cleaned`) from `Live_Parking` with set-based SQL — surgical (only the imported dates).
- **Auto-forecast** (`Services/ForecastService.cs`): after each sync it runs `ml/run_forecasts_v2.py`
  in the background; also the **"Run forecast now"** button (`POST /api/forecast/run`, `GET /api/forecast/run-status`).
- So: **connect → sync → aggregate → dashboards & forecast update automatically**, no manual Python step.
- **Schema-agnostic:** the Discover+Map step means the connector is NOT tied to the mock's field names —
  any session-level JSON API works (proven against a second mock shape `/api/altsessions`: different field
  names `vehicle_plate/arrival/...` nested under `result.records`, no auth — just re-mapped).
- (A CSV file-import card existed briefly and was removed by user decision on 2026-07-04 — the connector
  is REST-API-only. `IngestionService.ImportSessions` remains as the shared ingest path used by Sync.)
- **Payment method mapping** (2026-07-05): the mapping has an optional **Payment** field — the vendor's
  payment method (e.g. mock's `pay_method`) flows into `Live_Parking.Payment_Type`, so the payment-mix
  chart shows real values (TnG/Card/Cash/Autopay). **Provenance marker changed**: imported rows are now
  identified by `Ticket_ID LIKE 'IMP%'` (not `Payment_Type='Imported'`, which is only the fallback when
  no payment mapping is set). Status counter, `_cleanup_imported.py`, and `reset_demo.ps1` all updated.
- **Event-stamping on aggregation** (2026-07-05): `AggregationService` LEFT JOINs `Event_Calendar` by date,
  so imported sessions on calendar-event days get `Event Day` status + the event name — Events Impact page
  and `event_days`/`Event_Flag` now work for connector-imported data (previously always 'Non-Event Day').
- **Revenue by level fallback**: `/api/dash/levels-alltime` prefers the curated `Level_Summary` rollup and
  falls back to computing per-level from `Transactions_Cleaned` when the rollup is empty (fresh installs).
- **Mock driver populations** (2026-07-05): mock history now mixes ~87% visitors, ~12% weekday workers
  (7–10am, 7–10h stays) and ~1% residents (12–18h), so Driver Behaviour segmentation shows all 3 segments
  on imported data. Demo note: set **capacity ≈2200 BEFORE Sync** (occupancy % bakes in at aggregation).
- **Platform settings** (`Services/SettingsService.cs`, `App_Settings`): configurable **capacity**
  (flows into `/api/occupancy`) and **auto-sync interval** (drives `Services/SyncBackgroundService.cs`).
- **AI Insights (supervisor request, post-presentation)** (`Services/AiService.cs` + `Controllers/AiController.cs`):
  a ✨ AI button on every page opens a panel with **"Summarize this page"** (per-page focus; the forecast
  page adds operational recommendations) and an **"Ask about your data"** Q&A box. Uses the **Google Gemini
  API** (free tier; model configurable, currently `gemini-flash-lite-latest`). Privacy by design: only a
  compact AGGREGATED context (KPIs, day-of-week averages, busiest hours, event lift, mixes, segments,
  7-day forecast) is sent — never raw sessions or plates; the AI answers from that context only. Key lives
  in `api/appsettings.Local.json` (gitignored) or env `GEMINI_API_KEY`; degrades gracefully without it.
- **Event calendar feed (iCal)** (`Services/EventFeedService.cs` + `Controllers/EventsController.cs`,
  `/api/events/feed`): imports a public `.ics` feed (e.g. a Google Calendar) into `Event_Calendar` via a
  self-contained VEVENT parser, so the forecast becomes event-aware. **Connected by default + auto-imports:**
  a default `ical_url` (the mock feed) is seeded on startup and `Services/EventFeedBackgroundService.cs`
  re-imports it ~hourly (`ical_refresh_seconds`) — it's a first-class always-on integration, not optional.
  The Data Source page also has a manual Test / "Import now". The mock serves a sample feed at `:8900/calendar.ics`.
  (This realizes the FYP1 "optional Google Calendar iCal feed", upgraded to always-on.)

## ML forecast (V2)
- `ml/forecast_v2.py` (daily, day-type + lag features + supervisor baseline → `Forecast_Daily_V2`),
  `ml/forecast_hourly_v2.py` (hourly, event-name aware → `Forecast_Hourly_V2`),
  `ml/run_forecasts_v2.py` (runs both), `ml/backtest_v2.py` (walk-forward → `Model_Comparison_V2`).
- Both loaders **exclude partial/live days** (<50% of median daily traffic) and forecast from **today**.
- Backtest headline: ML beats the supervisor baseline on 9/10 daily segments (vehicles MAPE 5.3% vs 7.5%,
  revenue 6.7% vs 11.9%, event days 7.3% vs 17.9%; baseline wins only on public-holiday revenue, N=3).
- Web **confidence** is derived from the backtest MAPE per day type (Weekday≈96%, Event≈93%, PH≈90%).
- **Weather feature** (`ml/weather_seed.py` + `forecast_v2.py`): a `Weather_Daily` table holds a synthetic
  rain series; the daily forecast adds `is_rainy` as a predictor and learns a "rain → more mall traffic"
  effect (rainy days forecast ~+10% weekday / ~+20% event). The Predictive Analytics page shows a
  ☀️/🌧️ badge per day. It's **optional & graceful**: if `Weather_Daily` is absent the forecast runs
  without weather. On real operator data the correlation is inherent; the seed only simulates it because
  the base synthetic parking data has no weather signal. Enable with `python ml/weather_seed.py`.
- **Needs the full history to look right.** The model learns from the last 4 same-weekdays, so it needs
  months of data. On the **seed dump it forecasts correctly** (varied per day type, events boosted). If the
  DB is in the empty→4-week-mock demo state it has too little history and outputs a near-**constant** value
  for every day — that is expected (insufficient data), NOT a bug. Demo the forecast on the baseline dataset
  (`scripts/reset_demo.ps1 -Mode baseline`); use the mock connector to demo the integration pipeline.

## Database — PostgreSQL 16 (`parking_db`)
- `Transactions_Cleaned` (2.5% sample, 2025 + 2026), `Daily_Summary`, `Hourly_Summary`, `Hourly_Occupancy`,
  `Monthly_Summary`, `Level_Summary`, `Event_Summary`, `Event_Log_Table`, `Event_Calendar`,
  `Forecast_Daily_V2`, `Forecast_Hourly_V2`, `Model_Comparison_V2`, `Model_Comparison_Hourly_V2`,
  `Live_Parking`, `Data_Source_Config`, `App_Settings`.
- **Don't mix** the 2.5% `Transactions_Cleaned` sample with the full-population summary tables on one visual.

## Data
- **2025** full year + **2026 Jan–May** synthetic, calibrated to real references; vehicle types are **Car + Motorcycle** only.
- The committed `database/parking_db.dump` is the **seed dataset** so the system runs out of the box. In a
  real deployment the data instead comes from the operator via the connector.

## Power BI (separate BI deliverable)
- `powerbi/Parking_Dashboard.pbix` — DirectQuery on the same PostgreSQL. Outstanding (legacy track):
  place the V2 forecast visuals + repoint the Forecast page off the stale `Forecast_30Days` onto
  `Forecast_Daily_V2`, then save the pbix. (The **web** dashboard already uses the V2 tables.)

## Removed (history)
The following were built earlier and then **removed** to keep the system a focused analytics layer:
the **barrier-gate** feature (gate API/controller, tariff, Gate Monitor page, plate-reader demo),
the **multi-engine DB connector** (PostgreSQL/MySQL/SQL Server direct-DB source — replaced by REST-API-only),
and the topbar **admin sign-in** block.

## Outstanding / next
- **Push to GitHub** (user does this): create empty repo → `git remote add origin <url>` → `git push -u origin main`.
- **Written FYP report** — separate from the software.
- Power BI V2 visuals (above). Optional: extend forecast horizon to 90 days; perf review for very large histories.

## Dev-only helpers (kept locally, gitignored — not in the upload)
- `connector_demo/mock_parking_api.py` IS committed (the connector test harness).
- `scripts/reset_demo.ps1` (baseline/empty demo reset) and `DEMO_GUIDE.md` are kept local only.
- After demo syncs, `connector_demo/_cleanup_imported.py` removes imported (`IMP…`-ticket) rows; a full
  reset is `database/restore_database.ps1` (or `scripts/reset_demo.ps1 -Mode baseline`).
