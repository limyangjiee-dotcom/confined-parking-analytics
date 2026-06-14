# Parking API (PostgreSQL) — Run Guide

This API records live parking events into PostgreSQL (`parking_db`) and serves analytics.
Power BI's Live Monitor page reads the `Live_Parking` table it creates.

## 1. One-time setup
- .NET 8 SDK installed (`dotnet --version` shows 8.x).
- PostgreSQL running with the `parking_db` database (already done).
- Open `appsettings.json` and set your postgres password:
  `"Password=CHANGE_ME"` → your actual password.

## 2. Run the API
```
cd ParkingApiPg
dotnet restore
dotnet run
```
On first run it creates the `Live_Parking` table automatically.
Look for: `Now listening on: http://localhost:5000`
Open **http://localhost:5000/swagger** to test endpoints.

Every /api call needs the header **X-Api-Key: fyp-demo-key-2025**.

## 3. Endpoints
| Method | Route | Purpose |
|---|---|---|
| POST | /api/parking/entry | record a car entering |
| POST | /api/parking/exit  | record a car leaving (+fee) |
| GET  | /api/occupancy     | live occupancy % now |
| GET  | /api/levels        | cars parked per level now |
| GET  | /api/daily         | vehicles + revenue per day |
| GET  | /api/forecast      | ML forecast (from Forecast_30Days) |

The GET endpoints are the "API for others" — external apps call them with the key.

## 4. Demo real-time
Second terminal (API still running):
```
pip install requests
python event_simulator.py
```
Watch GET /api/occupancy change. Then in Power BI add the `Live_Parking` table
(Get Data → PostgreSQL → parking_db → Live_Parking, DirectQuery) and turn on
Page refresh on the Live Monitor page.

## 5. Troubleshooting
- "password authentication failed" → wrong password in appsettings.json.
- "Npgsql ... connection refused" → PostgreSQL service not running.
- Build errors on first run → run `dotnet restore` again (it downloads packages).
