# Data-Source Connector — connect to an external parking system

**Architecture:** this platform is an **analytics layer**. It does **not** own the
gate, barrier or ANPR camera — those belong to the parking system. The platform
**connects to that parking system's REST API**, discovers the fields it returns,
maps them to our analytics model, and **pulls the data in for analysis**.

```
  External parking system            This analytics platform
  ┌───────────────────────┐          ┌──────────────────────────────┐
  │ gate / barrier / ANPR │  JSON    │  Data Source connector       │
  │ camera  →  ITS REST API├─────────►│  test → discover → map → sync│
  │ (any field names)      │  pull    │  → Live_Parking → dashboards │
  └───────────────────────┘          │  → aggregate → summaries → ML │
                                      └──────────────────────────────┘
```

## Endpoints (`/api/connector`, master `X-Api-Key`)
- `POST /test` — call the external API, report how many records came back + their field names.
- `POST /discover` — fetch a sample and return the JSON fields so you can map them.
- `GET /config` / `POST /config` — load / save the API config + field mapping (auth value stored server-side, masked on read).
- `POST /sync` — pull the sessions, insert new rows into `Live_Parking` (`Payment_Type='Imported'`), deduped on (plate, entry-time), then rebuild the analytics summaries and refresh the ML forecast.
- `GET /status` — last sync time/status, imported total, preview rows.

## Web page
**Data Source** in the sidebar (`connect.html`): enter the API endpoint →
**Test connection** → **Discover fields** (auto-guesses the mapping from the field
names) → adjust mapping → **Save** → **Sync data now**. Imported sessions then flow
into `Live_Parking`, the aggregation rebuilds the summary tables, and the ML
forecast retrains — so every analytics page (and the 7-day forecast) reflects the
connected operator's data automatically.

## Demo — mock third-party parking system (REST API)
`mock_parking_api.py` stands in for a vendor's parking system exposing its sessions
over HTTP. It serves the operator's **existing history** (complete past days at full
volume) so that after you connect + sync, the forecast visibly reflects this
operator's data.

```
python mock_parking_api.py            # serves http://localhost:8900/api/sessions

# then in the web app -> Data Source (REST API):
#   URL          http://localhost:8900/api/sessions
#   Method       GET
#   Records path data
#   Auth header  X-Api-Key   /   parking-vendor-key
#   Discover  -> maps lpr->plate, ts_in->entry, ts_out->exit, paid->fee,
#                deck->level, klass->vehicle type  -> Save -> Sync

python _cleanup_imported.py           # optional: remove imported demo rows
```

Tuning: add `?days=120&per_day=3000` (the defaults) to the URL to change how much
history the operator "has". The generated history is realistic — day-of-week
amplitude (Mon light → Sat peak), ±8% daily noise, a mild growth trend, and
recurring event spikes (every 3rd weekend = MegaSale, first Wednesday = Tech Expo)
that line up with the iCal feed at `/calendar.ics` — so after connect + sync the
forecast has genuine patterns to learn and show.

## Production notes (scope-honest)
- The auth value is stored in `Data_Source_Config` as text for the demo; use a secrets store in production.
- Sync is manual (button) / scheduled (auto-sync interval on the Data Source page); for very large histories add an incremental high-water mark on entry-time.
- Single external source (one config row); multi-source / multi-tenant is future work.
