# Confined Parking Data Analytics System

A Business-Intelligence platform for confined (multi-storey) parking data: real-time
monitoring, historical analytics, machine-learning demand forecasting, driver-behaviour
segmentation, a barrier-gate API, and a connector that integrates with an external
parking system (via its REST API or database).

> **FYP T711641 — "Building Data Analytics for Confined Parking Data using BI Tools."**
> Virtual target: a ~11,000-bay confined carpark. Data is **synthetic**, calibrated to a
> real Malaysian tariff (free first 15 min; weekday RM2/3h, +RM1 hr 3–4, +RM2.50/h after;
> flat RM2 on Fri/weekend/public holiday).

---

## What it does

A **10-page web dashboard** (served by the API at `http://localhost:5000`):

| Page | Shows |
|---|---|
| **Overview** | KPIs, monthly vehicles/revenue, live occupancy, trends, top days |
| **Real-Time Status** | Live entries/exits (per-minute), occupancy gauge, by level, recent activity, gate decisions (5 s refresh) |
| **Occupancy** | Day×hour heatmap, occupancy by hour/day-type, peak periods, **entries vs exits by hour** |
| **Vehicle Analysis** | Vehicle-type & payment mix, duration distribution, stay/fee by type |
| **Driver Behaviour** | Classifies sessions into **resident / worker / visitor** from stay patterns |
| **Events Impact** | Event-day vs normal-day uplift, by category, event log, upcoming events |
| **Revenue** | Monthly/daily/cumulative revenue, by daypart & level |
| **Predictive Analytics** | **7-day ML forecast** of vehicles/revenue/occupancy with day-type & time-type detection, event boost, and confidence scores |
| **Gate Monitor** | Barrier-gate audit log + simulate-a-plate-read demo |
| **Data Source** | Connect to an external parking system (**REST API** or **PostgreSQL/MySQL/SQL Server**), map fields, sync |

Every analytics page has a **date-range filter**.

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
connector_demo/ mock external parking systems (DB + REST API) to demo the connector
gate_demo/      webcam / EasyOCR plate-reader client for the gate API
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
The analytics live in PostgreSQL, so restore the bundled dump:

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
python forecast_v2.py            # daily forecast  -> Forecast_Daily_V2
python forecast_hourly_v2.py     # hourly forecast -> Forecast_Hourly_V2
python backtest_v2.py            # accuracy proof  -> Model_Comparison_V2
```

### External-system connector demo (Data Source page)
- **REST API source**: `python connector_demo/mock_parking_api.py` → in the app: Source type = REST API, URL `http://localhost:8900/api/sessions`, header `X-Api-Key: parking-vendor-key`, records path `data`, discover → map → sync.
- **PostgreSQL source**: `python connector_demo/mock_parking_system.py` (creates `ext_parking_demo`).
- **MySQL source**: `python connector_demo/mock_parking_mysql.py` (needs MySQL + `pip install pymysql`; creates `ext_parking_mysql`).

### Barrier-gate
The gate API (`/api/gate/entry|exit|pay|status|log`) is documented in `gate_demo/README_GATE.md`. Test it with `api/test_gate_api.py` or the Gate Monitor page.

### Power BI
Open `powerbi/Parking_Dashboard.pbix` in Power BI Desktop (DirectQuery on the same PostgreSQL `parking_db`).

---

## Configuration reference
- **DB connection**: `api/appsettings.json` (or env var `ConnectionStrings__Default`).
- **API key** (dashboard ↔ API): `appsettings.json` → `ApiKey` (default `fyp-demo-key-2025`).
- **Capacity & tariff & auto-sync**: configurable in-app on the **Data Source** page (stored in `App_Settings`).

## Troubleshooting
- *Dashboard shows "API unreachable"* → the API isn't running, or PostgreSQL is down / wrong password.
- *`password authentication failed`* → fix the password in `appsettings.json` (and the Python scripts).
- *Pages load but charts are empty* → the database wasn't restored (step 2).
- *Open the dashboard at `http://localhost:5000`*, never as a `file://` path.

## Note (academic prototype)
This is an FYP proof-of-concept on synthetic data. It is **not** production-hardened:
no user authentication/HTTPS, a single demo API key, and connection passwords are stored
in plaintext config. Productionising it would require auth + TLS, secrets management,
PDPA/GDPR handling for plate data, automated tests, and deployment/perf work.
