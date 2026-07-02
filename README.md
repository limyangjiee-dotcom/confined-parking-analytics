# Confined Parking Data Analytics System

A Business-Intelligence platform for confined (multi-storey) parking data: real-time
monitoring, historical analytics, machine-learning demand forecasting, driver-behaviour
segmentation, and a connector that integrates with an external parking system via its
REST API.

> **FYP T711641 — "Building Data Analytics for Confined Parking Data using BI Tools."**
> Virtual target: a ~11,000-bay confined carpark. Data is **synthetic**, calibrated to a
> real Malaysian tariff (free first 15 min; weekday RM2/3h, +RM1 hr 3–4, +RM2.50/h after;
> flat RM2 on Fri/weekend/public holiday).

---

## What it does

A **9-page web dashboard** (served by the API at `http://localhost:5000`):

| Page | Shows |
|---|---|
| **Overview** | KPIs, monthly vehicles/revenue, live occupancy, trends, top days |
| **Real-Time Status** | Live entries/exits (per-minute), occupancy gauge, by level, recent activity (5 s refresh) |
| **Occupancy** | Day×hour heatmap, occupancy by hour/day-type, peak periods, **entries vs exits by hour** |
| **Vehicle Analysis** | Vehicle-type & payment mix, duration distribution, stay/fee by type |
| **Driver Behaviour** | Classifies sessions into **resident / worker / visitor** from stay patterns |
| **Events Impact** | Event-day vs normal-day uplift, by category, event log, upcoming events |
| **Revenue** | Monthly/daily/cumulative revenue, by daypart & level |
| **Predictive Analytics** | **7-day ML forecast** of vehicles/revenue/occupancy with day-type & time-type detection, event boost, and confidence scores |
| **Data Source** | Connect to an external parking system's **REST API**, map fields, sync — imported data flows into every page and the forecast |

Every analytics page has a **date-range filter**.

## Architecture (in one line)
This is the **analytics layer** — it does not own the gate/barrier/ANPR hardware.
It connects to the operator's parking system (its REST API), pulls the session data
in, rebuilds its summary tables, and runs analytics + ML forecasting on top.

## Tech stack
- **API + web**: ASP.NET Core (.NET 8), serves a vanilla-JS + Chart.js dashboard (`api/wwwroot`).
- **Database**: PostgreSQL 16.
- **ML**: Python (scikit-learn HistGradientBoosting) — see `ml/`.
- **BI**: Power BI dashboard (`powerbi/`).

## Repository structure
```
api/            ASP.NET Core API + web dashboard (wwwroot)
ml/             ML forecast scripts (forecast_v2, forecast_hourly_v2, backtests)
scripts/        rollup_live.py, forecast_retrain.py (live pipeline)
connector_demo/ mock external parking-system REST API to demo the connector
database/       parking_db.dump  +  restore scripts   <-- the data the app needs
docs/           project status & design notes
powerbi/        Power BI dashboard (.pbix)
```

---

## Setup (get it running)

### 1. Prerequisites
- **.NET 8 SDK** — https://dotnet.microsoft.com/download/dotnet/8.0
- **PostgreSQL 16** — https://www.postgresql.org/download/  (during install, **set the `postgres` password to `parking123`** to match the defaults below; or use your own and edit the config in step 3)
- **Python 3.10+** (only for the ML/forecast and connector-demo scripts) — `pip install pandas numpy scikit-learn sqlalchemy psycopg2-binary holidays requests`

### 2. Restore the database (this is the data the dashboards read)
The analytics live in PostgreSQL, so restore the bundled dump (this is the **seed
dataset** so the system works out of the box; in a real deployment the data instead
comes from the operator via the Data Source connector):

**Windows (PowerShell):**
```powershell
cd database
./restore_database.ps1
```
**Linux / macOS:**
```bash
cd database
chmod +x restore_database.sh && ./restore_database.sh
```
Or manually:
```bash
createdb -U postgres parking_db
pg_restore -U postgres -d parking_db --no-owner --clean --if-exists database/parking_db.dump
```

### 3. Configure the connection (only if you didn't use password `parking123`)
- **API**: edit `api/appsettings.json` → `ConnectionStrings:Default`
  (or set env var `ConnectionStrings__Default="Host=localhost;Port=5432;Database=parking_db;Username=postgres;Password=YOURPASS"`).
- **Python scripts**: each has a `PASSWORD`/`DB` constant near the top — change it if needed.

### 4. Run the API + dashboard
```bash
cd api
dotnet run --urls http://localhost:5000
```
Then open **http://localhost:5000** in a browser. (Open via the URL, **not** the HTML file.)

That's it — the dashboards and the 7-day forecast work immediately on the restored historical data.

---

## Optional extras

### External-system connector demo (Data Source page)
Demonstrates connecting to a third-party parking system and pulling its data in:
```bash
python connector_demo/mock_parking_api.py     # serves a mock vendor API on :8900
```
Then in the app → **Data Source**: URL `http://localhost:8900/api/sessions`, method `GET`,
records path `data`, auth header `X-Api-Key` / `parking-vendor-key` → **Discover** →
map → **Save** → **Sync**. The imported sessions rebuild the summaries and refresh the
forecast automatically. See `connector_demo/README_CONNECTOR.md`.

The same page also has an **Event calendar feed (iCal)** card: paste an `.ics` URL
(e.g. a public Google Calendar, or the mock's `http://localhost:8900/calendar.ics`)
→ **Test** → **Import**. Planned events are written to `Event_Calendar`, so the ML
forecast becomes aware of upcoming event days.

### Live real-time demo
```bash
cd api && dotnet run --urls http://localhost:5000      # terminal 1
python api/event_simulator.py                          # terminal 2 — generates live traffic
python scripts/rollup_live.py                          # terminal 3 (repeat) — folds live data into analytics
```
The Real-Time page then animates; analytics pages filtered to today show live data.

### Re-train / regenerate the ML forecast
```bash
cd ml
python weather_seed.py           # (optional) seed a weather series -> Weather_Daily
python forecast_v2.py            # daily forecast  -> Forecast_Daily_V2 (uses weather if seeded)
python forecast_hourly_v2.py     # hourly forecast -> Forecast_Hourly_V2
python backtest_v2.py            # accuracy proof  -> Model_Comparison_V2
```
The daily forecast supports a **weather feature**: run `weather_seed.py` once and the model learns a
"rain → more mall traffic" effect (the Predictive Analytics page then shows a ☀️/🌧️ badge per day).
It's optional — without the `Weather_Daily` table the forecast simply runs without weather.

### Power BI
Open `powerbi/Parking_Dashboard.pbix` in Power BI Desktop (DirectQuery on the same PostgreSQL `parking_db`).

---

## Configuration reference
- **DB connection**: `api/appsettings.json` (or env var `ConnectionStrings__Default`).
- **API key** (dashboard ↔ API): `appsettings.json` → `ApiKey` (default `fyp-demo-key-2025`).
- **Capacity & auto-sync interval**: configurable in-app on the **Data Source** page (stored in `App_Settings`).

## Troubleshooting
- *Dashboard shows "API unreachable"* → the API isn't running, or PostgreSQL is down / wrong password.
- *`password authentication failed`* → fix the password in `appsettings.json` (and the Python scripts).
- *Pages load but charts are empty* → the database wasn't restored (step 2).
- *Open the dashboard at `http://localhost:5000`*, never as a `file://` path.

## Note (academic prototype)
This is an FYP proof-of-concept on synthetic data. It is **not** production-hardened:
no user authentication/HTTPS, a single demo API key, and the connector's auth value is
stored in plaintext config. Productionising it would require auth + TLS, secrets management,
PDPA/GDPR handling for plate data, automated tests, and deployment/perf work.
