"""
run_forecasts_v2.py — daily Task Scheduler entry point
Refreshes BOTH V2 forecast tables in one run:
  Forecast_Daily_V2  (forecast_v2.py)
  Forecast_Hourly_V2 (forecast_hourly_v2.py)
Safe to run alongside the old "Parking Forecast Retrain" task — writing
the same table twice in a day is harmless (if_exists="replace").
"""
import forecast_v2
import forecast_hourly_v2

forecast_v2.main()
forecast_hourly_v2.main()
