# FYP Project Status — Confined Parking Data Analytics

## Project
- **FYP T711641** — "Building Data Analytics for Confined Parking Data using BI Tools."
- **Virtual target:** a ~**11,000-bay** confined (multi-storey) carpark.
- **Tariff the synthetic data is calibrated to (real):** free first 15 min; weekday RM2 first 3 hrs, +RM1 hr 3–4, +RM2.50/hr after; flat **RM2** on Fri/weekend/public holiday.
- **Deliverables:** ML forecast model, BI reports, web prototype, real-time pipeline, external-system integration — all built.

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
  (`Payment_Type='Imported'`, deduped on plate+entry-time) — scales to hundreds of thousands of rows.
- **Auto-aggregation** (`Services/AggregationService.cs`): after each sync, rebuilds the date-grained
  summary tables (`Daily_Summary`, `Hourly_Summary`, `Hourly_Occupancy`, `Event_Log_Table`,
  `Transactions_Cleaned`) from `Live_Parking` with set-based SQL — surgical (only the imported dates).
- **Auto-forecast** (`Services/ForecastService.cs`): after each sync it runs `ml/run_forecasts_v2.py`
  in the background; also the **"Run forecast now"** button (`POST /api/forecast/run`, `GET /api/forecast/run-status`).
- So: **connect → sync → aggregate → dashboards & forecast update automatically**, no manual Python step.
- **Platform settings** (`Services/SettingsService.cs`, `App_Settings`): configurable **capacity**
  (flows into `/api/occupancy`) and **auto-sync interval** (drives `Services/SyncBackgroundService.cs`).

## ML forecast (V2)
- `ml/forecast_v2.py` (daily, day-type + lag features + supervisor baseline → `Forecast_Daily_V2`),
  `ml/forecast_hourly_v2.py` (hourly, event-name aware → `Forecast_Hourly_V2`),
  `ml/run_forecasts_v2.py` (runs both), `ml/backtest_v2.py` (walk-forward → `Model_Comparison_V2`).
- Both loaders **exclude partial/live days** (<50% of median daily traffic) and forecast from **today**.
- Backtest headline: ML beats the supervisor baseline on 9/10 daily segments (vehicles MAPE 5.3% vs 7.5%,
  revenue 6.7% vs 11.9%, event days 7.3% vs 17.9%; baseline wins only on public-holiday revenue, N=3).
- Web **confidence** is derived from the backtest MAPE per day type (Weekday≈96%, Event≈93%, PH≈90%).

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
- After demo syncs, `connector_demo/_cleanup_imported.py` removes `Payment_Type='Imported'` rows; a full
  reset is `database/restore_database.ps1` (or `scripts/reset_demo.ps1 -Mode baseline`).
