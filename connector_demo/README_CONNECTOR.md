# Data-Source Connector — connect to an external parking system

**Architecture:** this platform is an **analytics layer**. It does **not** own the
gate, barrier or ANPR camera — those belong to the parking system. The platform
**connects to that parking system's database**, discovers its schema, maps its
fields to our analytics model, and **pulls the data in for analysis**.

```
  External parking system            This analytics platform
  ┌───────────────────────┐          ┌──────────────────────────────┐
  │ gate / barrier / ANPR │  data    │  Data Source connector       │
  │ camera  →  ITS database├─────────►│  test → discover → map → sync│
  │ (any schema, any names)│  fetch   │  → Live_Parking → dashboards │
  └───────────────────────┘          │  → rollup → summaries → ML   │
                                      └──────────────────────────────┘
```

## Endpoints (`/api/connector`, master `X-Api-Key`)
- `POST /test` — open a connection to the external DB, return server version.
- `POST /discover` — read `information_schema`, return its tables + columns.
- `GET /config` / `POST /config` — load / save connection + field mapping (password stored server-side, masked on read).
- `POST /sync` — read the mapped columns from the external table, insert new rows into `Live_Parking` (`Payment_Type='Imported'`), deduped on (plate, entry-time).
- `GET /status` — last sync time/status, imported total, preview rows.

Implemented for **PostgreSQL** sources. The `engine` field carries the type so
MySQL / SQL Server can be added by registering their ADO.NET provider
(MySqlConnector / Microsoft.Data.SqlClient) — the rest of the flow is unchanged.

## Web page
**Data Source** in the sidebar (`connect.html`): enter the external DB details →
**Test connection** → **Discover tables** (auto-guesses the field mapping from
column names) → adjust mapping → **Save** → **Sync data now**. Imported sessions
then appear on Real-Time and, via the existing `rollup_live.py`, in the analytics
and forecast.

## Demo — mock third-party parking system
`mock_parking_system.py` creates a **separate** database `ext_parking_demo` with a
vendor-shaped table `anpr_sessions` (foreign column names: `lpr_plate`,
`gate_in_at`, `gate_out_at`, `paid_amount`, `deck_code`, `vehicle_class`) and ~120
sample sessions. This stands in for another vendor's system.

```
python mock_parking_system.py        # create + seed the external DB
# then in the web app -> Data Source:
#   host=localhost port=5432 db=ext_parking_demo user=postgres pass=parking123
#   Discover -> anpr_sessions -> mapping auto-fills -> Save -> Sync
python _cleanup_imported.py           # optional: remove imported demo rows
```

Verified end-to-end (2026-06-13): test → discover (found `anpr_sessions`) →
auto-map → sync imported 120 sessions → re-sync deduped to 0 → `/api/occupancy`
and the Real-Time page reflect the imported data.

## Production notes (scope-honest)
- Password is stored in `Data_Source_Config` as text for the demo; encrypt or use a
  secrets store in production.
- Sync is manual (button) / on-demand; schedule it (Task Scheduler, like the rollup)
  for continuous ingestion, with an incremental high-water mark on entry-time.
- Single external source (one config row); multi-source/multi-tenant is future work.
