# FYP Project Status — Confined Parking Data Analytics (Handoff)

## Project
- **FYP T711641** — "Building Data Analytics for Confined Parking Data using BI Tools."
- **Virtual target:** Mid Valley Megamall, Kuala Lumpur (**11,000 bays**).
- **Tariff calibration (real):** free first 15 min; weekday RM2 first 3 hrs, +RM1 hr 3-4, +RM2.50/hr after; flat **RM2** on Fri/weekend/public holiday.
- **Deliverables:** model, BI reports, web-based prototype, real-time, API — ALL built.

## Data (synthetic, calibrated to real references)
- **2025** full year: ~8.1M vehicles, ~RM23.7M revenue, weekend peak occupancy ~94%, weekday ~61%.
- **2026 Jan–May**: ~3.34M vehicles, ~RM9.9M revenue. Uses Malaysian plates (e.g. `SWJ2558`) + unique ticket IDs (e.g. `T5703E545`).
- Transactions are a **2.5% sample** (~202k for 2025, ~84k for 2026); summary tables are **full-population**. (Don't mix the two on one visual.)

## Database — PostgreSQL 16 (`parking_db`)
- Settled on **PostgreSQL 16** after SQL Server failed (NVMe 4KB-sector install bug) and PostgreSQL 18 failed (broken bundled tools). Service: `postgresql-x64-16`, user `postgres`.
- Tools: pgAdmin 4 (works) / DBeaver. (MySQL `fyp2_parking` from the original dashboard was abandoned.)
- **Tables:**
  - `Transactions_Cleaned` (26 cols incl `Ticket_ID`; 2025 + 2026; 2.5% sample)
  - `Daily_Summary`, `Hourly_Summary`, `Hourly_Occupancy` (date-grained, 2025 + 2026 Jan–May)
  - `Monthly_Summary`, `Level_Summary`, `Event_Summary` (all-time rollups, 2025 only)
  - `Forecast_30Days` (ML predictions), `Event_Log_Table`, `Model_Comparison`
  - `Live_Parking` (live simulator data; cols incl `Ticket_ID`, `Event_Status`, `Event_Name`)

## Power BI dashboard (DirectQuery on PostgreSQL — composite model)
- **7 pages:** Overview, Real-Time Status (Live Monitor), Occupancy, Vehicle, Events, Revenue, Forecast.
- **~23 measures** (Total Revenue, Total Vehicles, Occupancy Rate, Avg Fee, Car Dominant %, Cars In Now, etc.). % measures formatted `0.0%`, revenue formatted `"RM" #,##0`.
- **Date table** (CALENDAR 2025–2026, marked as date table) with **6 relationships** to date-grained tables. `Date[Year]` slicer toggles 2025/2026 (synced across pages; Forecast page intentionally NOT synced).
- Calculated columns: `Day_No`, `Day_Sorted`, `Duration_Band`, `Status`, `Zone Occupancy %`.
- **Known:** `Monthly_Summary`/`Level_Summary`/`Event_Summary` are NOT date-connected, so visuals on them don't filter by year — some were repointed to date-connected tables (Daily_Summary, Event_Log_Table, Transactions_Cleaned).

## ML forecast model
- `forecast_model.py` — Histogram Gradient Boosting. Hourly occupancy R²≈0.995, daily vehicles R²≈0.92, revenue R²≈0.82; beats a naive "last-week" baseline (proven in `Model_Comparison`).
- `forecast_retrain.py` — reads PostgreSQL, retrains, writes `Forecast_30Days`. **NO LONGER SCHEDULED** (task repointed to V2) — `Forecast_30Days` is now stale; Forecast page should move to the V2 tables.
- `FORECAST_METHODOLOGY.md` documents it.

## ML forecast V2 (prediction_v2, done 2026-06-13)
- `C:\FYP\prediction_v2\` — `forecast_v2.py` (daily, day-type + lag features + supervisor baseline → `Forecast_Daily_V2`), `forecast_hourly_v2.py` (hourly + event-NAME-aware → `Forecast_Hourly_V2`, 720 rows/30 days), `backtest_v2.py` (60-day walk-forward → `Model_Comparison_V2`), `forecast_hourly_v2.py backtest` (→ `Model_Comparison_Hourly_V2`); results also saved as `backtest_daily_results.txt` / `backtest_hourly_results.txt` for the report.
- Backtest headline: ML beats baseline on 9/10 daily segments (vehicles MAPE 5.3% vs 7.5%, revenue 6.7% vs 11.9%, event days 7.3% vs 17.9%). Baseline wins on public-holiday revenue (N=3) — say so honestly in the report.
- Both loaders **auto-exclude partial/live days** (<50% of median daily traffic, e.g. live-rollup days still filling in) and forecast from TODAY even when the last complete day is older.
- `Event_Calendar` holds future events; names must match `Event_Log_Table.Event_Category` (MegaSale/Expo/CareerFair/RoadShow/Concert) for name-aware prediction; unmatched names fall back to "avg of last event days" (stated in `Prediction_Basis`).
- `run_forecasts_v2.py` runs both forecasts; scheduled as **"Parking Forecast V2 Daily"** (02:10, pythonw, StartWhenAvailable, run-when-logged-on).
- Power BI model (open file "DASGBOARD_original（copy）") now has DirectQuery tables `Forecast_Daily_V2`, `Forecast_Hourly_V2`, `Model_Comparison_V2`, `Model_Comparison_Hourly_V2` + Tomorrow measures (`Tomorrow Predicted Vehicles/Revenue`, `Tomorrow Day Type`, `Tomorrow Prediction Basis`) on Forecast_Daily_V2. **Visuals (slicers + Tomorrow card) still to be placed manually; SAVE the pbix to persist.**

## ASP.NET Core Web API (.NET 8) + web prototype
- Project: `C:\FYP\ParkingApiPg_PostgreSQL\ParkingApiPg` (Npgsql/PostgreSQL). .NET 8 SDK 8.0.421.
- **Endpoints:** POST `/api/parking/entry`, `/exit`; GET `/api/occupancy`, `/levels`, `/daily`, `/forecast`, `/data/{table}`. Header: `X-Api-Key: fyp-demo-key-2025`.
- **Web prototype (REBUILT 2026-06-13):** 8-page dashboard "Confined Parking Analytics" at **http://localhost:5000** (MUST open via the URL, not the file): Overview, Real-Time (5s refresh), Occupancy (day×hour heatmap), Vehicle, Events, Revenue, Forecast, **Gate Monitor** (live audit log + simulate-a-plate-read demo buttons). Shared shell in `assets/app.js` + `assets/style.css` (dark BI theme, sidebar); Chart.js vendored locally (`assets/chart.umd.min.js`, works offline). Old/broken files archived in `wwwroot/_archive_20260613/`.
- **Driver Behaviour page (done 2026-06-13):** new `behaviour.html` + nav "Driver Behaviour" classifies every parking session into **Resident / Worker / Visitor** from its stay pattern (no ML — explainable heuristic): Resident = stay ≥12h (overnight/all-day); Worker = weekday, arrival 5–11am, 5–12h stay (commuter); Visitor = the rest. Endpoints `/api/dash/driver-segments` + `driver-segments-hourly` (`DashboardController`, `Seg` CASE on `Transactions_Cleaned`, date-filtered). Charts: arrivals-by-hour stacked per segment, segment-share donut, avg stay & fee by segment, profiles table; 5 KPIs. Verified on 2026: Visitor 96.9% (avg 3.1h, ~2:30pm), Worker 2.9% (avg 6.9h, ~8:54am, morning peak), Resident 0.15% (avg 13.2h). This closes the last open objective (driver behaviour). Assets bumped to `?v=9`.
- **User-requested revisions (same day):** every analytics page has a **date filter** (preset dropdown All/2025/2026/30d/90d + from/to date inputs, persisted; `/api/dash/*` rewritten to `?from&to`, monthly labels now "Mon YY" so cross-year ranges work). No "supervisor"/"Mid Valley" wording in the UI (virtual target only).
- **Design system v2 + Predictive Analytics rebuild (same day):** `style.css` overhauled (refined tokens, layered shadows/inset highlights, hover-lift cards, gradient active nav, fadeUp entrance, tabular-nums, scrollbar styling). A11y pass (web-interface-guidelines): `color-scheme:dark` on html, semantic `<h1>` page title, `aria-hidden` nav icons + `aria-current`, labelled gate input, `theme-color` meta, expanded `prefers-reduced-motion`, `touch-action:manipulation`. Asset cache-buster now `?v=6`.
- **Forecast page = "Predictive Analytics"**: method-chip row (day-type detection / time-type / pattern matching / event boost / confidence); 4 hero cards (tomorrow vehicles + **confidence bar**, predicted revenue, office-vs-after-hours split, event impact %); **7-day forecast strip** (one card/day: day-type badge, vehicles, revenue, confidence bar High/Med/Low); hourly forecast (next 7 days, office hours = darker bars); 7-day predicted revenue; 7-day detail table with confidence + "matched against" basis. **Confidence is derived from the walk-forward backtest MAPE per day type** (Model_Comparison_V2, ML rows) — Weekday≈96%, Event≈93%, Public Holiday≈90% — minus 8pts when an event has no same-name history. Backtest MAPE chart stays out of the web (Power BI/report only).
- **2026-06-13 bug fix:** all `/api/dash/*` endpoints were returning 500 (the 2026-06-12 month-filter refactor passed an untyped `DBNull` parameter — Npgsql needs `NpgsqlDbType.Integer`). Fixed in `DashboardController.Query`. `/api/data` allowlist extended with the V2 forecast/comparison tables, `Event_Calendar`, `live_vehicle_mix`, and `gate_devices` (keys excluded).
- `Live_Parking` table created manually (EnsureCreated skipped because DB already had tables).

## Barrier-gate integration (done 2026-06-13, supervisor request)
- **Endpoints:** POST `/api/gate/entry` & `/exit` → `{action: open/deny, ticketId, reason}`; POST `/api/gate/pay` (autopay), GET `/api/gate/status/{plate}`, GET `/api/gate/log` (audit). `GateController.cs` + `Models/Gate.cs` + `Models/Tariff.cs`.
- **Per-device API keys:** `Gate_Devices` table, gates send `X-Device-Key` (seeded: ENTRY-CAM-01/`gate-entry-01-key-7f3a`, EXIT-CAM-01/`gate-exit-01-key-9b2c`); master `X-Api-Key` still works. Tables `Gate_Devices`/`Gate_Log`/`Gate_Payments` auto-created + seeded on API startup (raw SQL, since EnsureCreated skips existing DBs).
- **Edge cases:** duplicate entry deny, misread/low-confidence (<0.40) deny, unpaid exit deny with RM due, pay → 15-min grace, no-ticket exit deny. Real tariff in `Tariff.cs` (free 15 min; weekday 2/3h +1 +2.50/h; Fri/weekend flat RM2; PH simplified to day-of-week). Entries auto-stamped with today's `Event_Calendar` event (verified: Event Day/MegaSale).
- **Plate reader demo:** `C:\FYP\gate_demo\plate_reader.py` — webcam (SPACE to read) / image / `--plate` manual mode (no OCR deps); EasyOCR optional (needs torch; if pip fails on Py3.14 use a 3.12 venv). `README_GATE.md` documents everything.
- **Tests:** `ParkingApiPg/test_gate_api.py` — 18 end-to-end checks, all passing (auth, entry, duplicate, misread, free exit, unpaid→pay→exit, log; self-cleaning).
- Scope kept single-carpark; multi-tenant = future work.

## Data-source connector (done 2026-06-13) — connect to an EXTERNAL parking system
- **Architecture clarified by user:** this platform is the **analytics layer**; the gate/barrier/ANPR camera + their database belong to a third-party parking system. The platform connects to that DB, discovers its schema, maps fields, and pulls data in for analysis (it does NOT own the hardware).
- `ConnectorController` (`/api/connector`): `POST /test`, `POST /discover` (information_schema), `GET`/`POST /config` (saved in `Data_Source_Config` table, password masked on read), `POST /sync` (reads mapped cols → inserts into `Live_Parking` as `Payment_Type='Imported'`, deduped on plate+entry-time), `GET /status`. PostgreSQL implemented; `engine` field lets MySQL/SQL Server be added via their ADO.NET provider. Identifiers validated against `^[A-Za-z_][A-Za-z0-9_]*$` (anti-injection).
- **Web page** `connect.html` ("Data Source" in nav): connection form → Test → Discover (auto-guesses mapping from column names) → Save → Sync; KPIs + imported-sessions preview.
- **Demo:** `C:\FYP\connector_demo\mock_parking_system.py` creates a separate DB `ext_parking_demo` with vendor-shaped table `anpr_sessions` (lpr_plate/gate_in_at/gate_out_at/paid_amount/deck_code/vehicle_class) + 120 sessions. Verified end-to-end: test→discover→auto-map→sync (120 imported)→re-sync dedup 0→Real-Time & /api/occupancy reflect it. `_cleanup_imported.py` removes imported rows. See `connector_demo\README_CONNECTOR.md`.
- **Note:** the earlier in-app gate API (GateController) is still valid as the *reference/simulated* gate for when no external system is connected; the connector is the path for integrating a real third-party system.
- **API-based integration + multi-engine DB (done 2026-06-13, per supervisor "usually communicate via API"):** the connector now supports TWO source types:
  - **REST API source** (the supervisor's recommended path): pull sessions from the parking system's HTTP endpoint — config = URL, method, optional auth header+value, records path (dotted, for nested JSON like `data`); discover returns the JSON keys; mapping maps JSON fields → canonical. Uses `IHttpClientFactory`. **Verified end-to-end** against `C:\FYP\connector_demo\mock_parking_api.py` (serves `GET /api/sessions`, header `X-Api-Key: parking-vendor-key`, body `{status,data:[...]}`): test→110 records, discover→keys, sync→imported 110 via API + aggregated. Also note: the *reverse* direction already works — their system can POST to our `/api/parking/entry` & `/api/gate/entry`.
  - **Multi-engine DB**: PostgreSQL + **MySQL** (`MySqlConnector`) + **SQL Server** (`Microsoft.Data.SqlClient`) — engine-specific connection/quoting/discovery in `Services/ConnectorDb.cs`; sync uses `System.Data.Common.DbConnection` so the flow is shared. Verified: postgres discover works; MySQL driver reached a live server (got auth-denied = functional); SQL Server failed gracefully (none installed). Couldn't fully live-test MySQL/SQL Server end-to-end (no seeded server), but the providers are wired and connect.
  - Refactor: `IngestionService` now has `FetchDbRows`/`FetchApiRows`/`IngestRows` (shared ingest+aggregate); `Test`/`Discover` moved into it and branch on source type; `ConnectorController` is a thin delegator. `Models/ConnectorModels.cs` gained `ApiSource` + `SourceType` + `SessionRow`. Data Source page has a **Source type** selector (REST API / Database) toggling field groups; assets `?v=10`.
  - **MySQL proven end-to-end 2026-06-14:** `connector_demo/mock_parking_mysql.py` seeds MySQL DB `ext_parking_mysql.car_movements` (root/parking123, port 3306); connector synced 100 sessions via mysql + aggregated 5 days, re-sync deduped to 0. So PostgreSQL + MySQL + REST API are all demonstrated; **SQL Server remains code-only (no server installed to test).**
- **Historical entries-vs-exits chart (2026-06-14):** new `/api/dash/entries-exits-hourly` (entries by Entry_Hour vs exits by Exit_Time hour, date-filtered) + "Entries vs Exits by Hour" card on the Occupancy page — closes the historical-exits gap (shows the daily rhythm: arrivals AM, departures PM).
- **"Configure the integration" (user picked this scope 2026-06-13, NOT writing into their system):**
  - **Scheduled sync** — sync logic extracted to `Services/IngestionService.cs`; `Services/SyncBackgroundService.cs` (hosted) runs it on the configured interval (checks every 15s). Verified: with interval=1 the log shows repeated "Scheduled sync: Imported N…" and `Last_Sync` updates.
  - **Platform settings** — `App_Settings` (key/value) + `Services/SettingsService.cs` (singleton, cached) + `/api/settings` GET/POST. Configurable **capacity** (flows into `/api/occupancy` live — verified 11000→8000 changed occupancy 29%→40%) and **tariff** (free mins, weekday base, hr3-4 add, after-4 rate, weekend flat) which `SettingsService` pushes into the now-mutable `Tariff` static used by the gate. `sync_interval_seconds` drives the scheduler.
  - **UI:** Data Source page card "3 · Integration settings" (auto-sync dropdown Manual/1m/5m/15m/hourly, capacity, tariff fields) — load/save verified end-to-end, persists. Connector models moved to `Models/ConnectorModels.cs`; `ConnectorController` slimmed to delegate to `IngestionService`. Assets `?v=8`.
- **Generalized aggregation — closes the connect→analyse loop (2026-06-13):** `Services/AggregationService.cs` rebuilds the date-grained summary tables (`Daily_Summary`, `Hourly_Summary`, `Hourly_Occupancy`, `Event_Log_Table`, `Transactions_Cleaned`) from `Live_Parking` with self-contained set-based SQL (temp tables `_sess`/`_conc`; real per-hour concurrency for occupancy). **Surgical**: only rebuilds dates present in `Live_Parking`, so curated history on other dates is untouched (does NOT touch the all-time `Level_Summary`/`Monthly_Summary` rollups). Uses the configurable capacity. **`IngestionService.Sync` calls it automatically after every sync** (manual button or scheduled), so a freshly-connected database flows end-to-end with no Python/Task Scheduler: connect → sync → aggregate → dashboards. Verified: sync reported "aggregated 4 day(s)"; `/api/dash/kpis` + `daily` + `vehicle-mix` for today now reflect the imported sessions (122 vehicles, 29.2% occ, their car/motorcycle/van/lorry classes). The ML forecast (Python, nightly) then picks up the rebuilt summaries automatically. **Sync is a FULL table read (no date filter), so it imports the operator's entire history**, aggregation builds summaries for every imported date, and the forecast trains on that full history from the first sync — it works immediately when the connected DB has history (the V2 forecast already trains on 17 months, which is the same pipeline). The "needs a few weeks" case applies ONLY to a true greenfield install with zero prior data (falls back gracefully). This C# aggregation supersedes `rollup_live.py` for the connector path (rollup remains for the simulator demo). Scale refinements (not yet done): incremental sync via an entry-time high-water mark; indexed/windowed rewrite of the occupancy-concurrency aggregation for very large histories.

## Real-time pipeline
- `event_simulator.py` — posts entries/exits with plates + ticket IDs + **event bursts** (occasional event-days with heavier traffic). Run: `python event_simulator.py`.
- `rollup_live.py` — aggregates `Live_Parking` into the summary tables as **today/June rows** so the analytical pages show live data when filtered to June. **Scheduled every 1 minute** (Task Scheduler). Only touches June-onward; history untouched.
- **Demo routine:** start API (`dotnet run`) → start simulator → (rollup + forecast retrain run automatically) → Power BI on June + auto page refresh shows live; Live Monitor page + web app are live.

## Windows Task Scheduler
- **Parking Forecast Retrain** — daily 02:00, now runs `prediction_v2\forecast_v2.py` (user repointed it; still python.exe — couldn't change to pythonw, task is permission-locked from non-elevated shells).
- **Parking Forecast V2 Daily** — daily 02:10, `prediction_v2\run_forecasts_v2.py` (both V2 forecasts), pythonw, StartWhenAvailable.
- **Parking Live Rollup** — repeat every 1 min.
- Both: use `pythonw.exe` (no popup window), **"Run only when user is logged on"** (avoids the Windows-password prompt — user forgot the password, doesn't need it).
- Python path: `C:\Users\End User\AppData\Local\Programs\Python\Python314\pythonw.exe`. Scripts in `C:\FYP\`.

## Environment specifics
- Laptop: ASUS TUF Gaming, Windows 11. Python 3.14. PostgreSQL 16. .NET 8.
- All working files in `C:\FYP\`.

## Key gotchas learned
- Don't open CSVs in Excel before importing (mangles timestamps → "invalid timestamp" errors).
- pgAdmin caches schema — disconnect/reconnect the SERVER to pick up new columns.
- `%` in PostgreSQL column names (e.g. `Occupancy_Rate_%`) must be escaped as `%%` in psycopg2 parameterized INSERTs.
- Paths with spaces ("End User") need quotes in Task Scheduler.
- Open the web app at `http://localhost:5000`, never as a `file://`.

## Outstanding / next
- **Power BI visuals for V2** (manual): Forecast page — Tomorrow card (drag the 4 Tomorrow measures), Forecast_Daily_V2 line/column by Day_Type, Prediction_Basis table; Forecast_Hourly_V2 slicers Day_Name/Hour/Time_Band/Event_Name. Then SAVE the pbix.
- **Repoint Forecast page off stale `Forecast_30Days`** onto `Forecast_Daily_V2`.
- ~~Barrier-gate integration~~ DONE 2026-06-13 (see section above). Optional polish: a `gate.html` live-monitor page on `/api/gate/log`; install easyocr for the webcam demo.
- ~~Web prototype redo~~ DONE 2026-06-13 — full rebuild, see API section above.
- Optional: extend forecast horizon to 90 days; run perf/postgres optimization reviews; report writing (methodology/system-design/results sections — material exists in FORECAST_METHODOLOGY.md, System_Architecture.svg, BUILD_GUIDE.md).
