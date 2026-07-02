"""
weather_seed.py — synthetic daily weather for the parking forecast (FYP demo)
=============================================================================
Creates/fills a "Weather_Daily" table with a deterministic synthetic weather
series (rainy flag + rainfall + temp) for every day the analytics cover, plus
the next 45 days (so the forecast has a weather outlook to predict against).

WHY synthetic: the parking data is itself synthetic and was generated WITHOUT a
weather signal, so no real weather feed would correlate with it. This seeds a
weather series; forecast_v2.py then applies a realistic "rain -> more mall
traffic" sensitivity so the model can learn and USE weather as a predictor.
On a real operator's data the correlation is already present and no seeding is needed.

Deterministic (seeded per calendar date) so re-running is stable. Malaysia-ish:
frequent tropical rain, wetter in the Apr-May and Oct-Dec monsoon months.

Run:  python weather_seed.py
"""
import datetime as dt
import random
import psycopg2

DB = dict(host="localhost", port=5432, dbname="parking_db", user="postgres", password="parking123")

WETTER_MONTHS = {4, 5, 10, 11, 12}   # higher rain probability


def weather_for(d: dt.date):
    """Deterministic (rainy, rain_mm, temp_c) for a given date."""
    rng = random.Random(d.toordinal())          # same date -> same weather
    p_rain = 0.55 if d.month in WETTER_MONTHS else 0.32
    rainy = rng.random() < p_rain
    rain_mm = round(rng.uniform(4, 45), 1) if rainy else 0.0
    temp_c = round(rng.uniform(24, 28) if rainy else rng.uniform(28, 34), 1)
    return int(rainy), rain_mm, temp_c


def main():
    conn = psycopg2.connect(**DB)
    conn.autocommit = True
    cur = conn.cursor()

    cur.execute('''CREATE TABLE IF NOT EXISTS "Weather_Daily" (
        "Weather_Date" date PRIMARY KEY,
        "Is_Rainy"     int  NOT NULL DEFAULT 0,
        "Rain_Mm"      numeric NOT NULL DEFAULT 0,
        "Temp_C"       numeric NOT NULL DEFAULT 0
    );''')

    # cover every analytics date + the next 45 days
    cur.execute('SELECT min("Entry_Date"), max("Entry_Date") FROM "Daily_Summary";')
    lo, hi = cur.fetchone()
    if lo is None:
        lo = dt.date.today() - dt.timedelta(days=540)
        hi = dt.date.today()
    end = max(hi, dt.date.today()) + dt.timedelta(days=45)

    d, n = lo, 0
    while d <= end:
        rainy, mm, temp = weather_for(d)
        cur.execute('''INSERT INTO "Weather_Daily" ("Weather_Date","Is_Rainy","Rain_Mm","Temp_C")
                       VALUES (%s,%s,%s,%s)
                       ON CONFLICT ("Weather_Date") DO UPDATE
                         SET "Is_Rainy"=EXCLUDED."Is_Rainy","Rain_Mm"=EXCLUDED."Rain_Mm","Temp_C"=EXCLUDED."Temp_C";''',
                    (d, rainy, mm, temp))
        d += dt.timedelta(days=1); n += 1

    cur.execute('SELECT count(*), sum("Is_Rainy") FROM "Weather_Daily";')
    total, rainy_days = cur.fetchone()
    print(f'Weather_Daily seeded: {n} written; {total} rows total, {rainy_days} rainy '
          f'({100*rainy_days/total:.0f}% rainy).')
    print(f'Range: {lo} .. {end}')
    cur.close(); conn.close()


if __name__ == "__main__":
    main()
