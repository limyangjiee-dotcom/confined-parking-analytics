# Demo Guide — Confined Parking Data Analytics System

A step-by-step script for presenting the system live. Two stories to tell:
**(1)** the platform runs analytics on a parking database, and **(2)** it can connect
to an *external* parking system's database/API and analyse that too.

> Verified working end-to-end on a dry run: all 10 pages render, 30 API endpoints
> respond, the live simulator feeds data, and a connector sync imports successfully.

---

## A. Before the presentation (do this once, in advance)

1. **PostgreSQL running** and `parking_db` restored (see `README.md` → Setup).
2. **Pre-build the API** so it starts instantly:
   ```
   cd C:\FYP\parking-analytics-fyp\api
   dotnet build
   ```
3. Open **3 terminals** in advance so you don't type much live:
   - Terminal 1 → the API
   - Terminal 2 → the live simulator
   - Terminal 3 → the connector mock
4. Have a few **screenshots saved** as a backup (in case of projector/network issues).

---

## B. The live demo (recommended order)

### ① Start the system — Terminal 1
```
cd C:\FYP\parking-analytics-fyp\api
dotnet run --urls http://localhost:5000
```
Open **http://localhost:5000** in a browser (use the URL, not a file path).
> Say: *"The dashboard and API are one ASP.NET Core application reading a PostgreSQL database."*

### ② Tour the analytics (this is "analytics on the database")
Use the **date filter** (top-right) on each page to prove it queries the DB live.
- **Overview** — KPIs, monthly vehicles & revenue, live occupancy, trends, busiest days.
- **Occupancy** — day×hour heatmap; **Entries vs Exits by Hour** (arrivals AM, departures PM).
- **Vehicle Analysis** — vehicle type / payment / parking-duration breakdown.
- **Driver Behaviour** — resident / worker / visitor segmentation from stay patterns.
- **Events Impact** — how owner-organised events lift traffic & revenue.
- **Revenue** — profit figures: monthly, daily, cumulative, by daypart & level.

### ③ Prediction — **Predictive Analytics** page
> Say: *"It forecasts the next 7 days — vehicles, revenue and occupancy — with
> day-type detection, office vs after-hours split, event boosts, and confidence
> scores, all trained on the historical data."*
Point at the **Tomorrow** card and the **7-day strip** with confidence bars.

### ④ Real-time — Terminal 2 (makes it feel alive)
```
cd C:\FYP\parking-analytics-fyp\api
python event_simulator.py
```
Open **Real-Time Status** → entries/exits per minute, the occupancy gauge and "inside"
count update every 5 seconds.
> (Optional Terminal 3: `python ..\scripts\rollup_live.py` to fold live data into the analytics pages.)

### ⑤ Connect to an external system — Terminal 3 (the showcase)
Start a stand-in parking system, then connect to it from the **Data Source** page.

**Option A — REST API (the standard integration your supervisor mentioned):**
```
cd C:\FYP\parking-analytics-fyp\connector_demo
python mock_parking_api.py
```
Data Source page → Source type **REST API**:
- URL `http://localhost:8900/api/sessions`
- Auth header `X-Api-Key` / value `parking-vendor-key`
- Records path `data`
- **Test → Discover → map the fields → Sync**

**Option B — a different DATABASE (PostgreSQL or MySQL):**
```
python mock_parking_system.py     # PostgreSQL -> ext_parking_demo
# or
python mock_parking_mysql.py      # MySQL -> ext_parking_mysql  (root / parking123)
```
Data Source → Source type **Database** → pick the engine → host `localhost`, the db
name above, user/pass → **Test → Discover → map → Sync**.

> Say: *"My system connected to a different parking operator's system, discovered its
> schema, mapped its fields to my model, pulled the data in, and rebuilt the analytics."*
Then flip back to **Overview / Real-Time** to show the imported data is now analysed.
> **Note:** after a sync the **ML forecast auto-refreshes in the background** (~1 minute) —
> so the **Predictive Analytics** page also updates on its own, no manual step. (Configurable
> via `Forecast.AutoRunOnSync` in `api/appsettings.json`; needs Python on the same machine.)

### ⑥ (Optional) Barrier gate — **Gate Monitor** page
Type a plate → **Entry gate** (opens), **Entry** again (duplicate → deny), **Autopay**,
**Exit gate** (opens). Shows the gate logic + audit log.

---

## C. Key talking points
- *"Every chart is a live SQL query on PostgreSQL — change the date range and it re-queries."*
- *"The analytics and ML are data-source-agnostic: connect a real operator's database
  (PostgreSQL / MySQL / SQL Server) or their REST API, and the same dashboards and 7-day
  forecast run on their data — automatically aggregated after each sync."*
- *"Real-time, historical analytics, machine-learning forecasting and external integration
  in one platform."*

## D. Safety net (read this!)
- If the **simulator or connector misbehaves live, the dashboards still work** on the
  historical data — just use the date filter. The core analytics never depend on the live bits.
- To stop the background scripts: close their terminal windows (Ctrl+C).
- Open the dashboard at **http://localhost:5000**, never as a `file://` path.
- Practise the **first 60 seconds** (start API → open browser) so the opening is smooth.

## E. One-line summary for the panel
> *"A BI platform that connects to a parking system's database or API, runs real-time and
> historical analytics, segments driver behaviour, measures event impact, and forecasts
> the next 7 days of demand, revenue and occupancy."*
