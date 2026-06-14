"""
mock_parking_mysql.py — a third-party parking system on MySQL.
==============================================================
Creates database `ext_parking_mysql` with a vendor-shaped table on the local
MySQL server, so the connector's MySQL engine can be proven end-to-end.

Run:  python mock_parking_mysql.py
Then Data Source -> Database -> MySQL:
  host=localhost port=3306 db=ext_parking_mysql user=root pass=parking123
  Discover -> car_movements -> map plate_no/time_in/time_out/fee_charged/bay_zone/veh_kind
"""
import random
import datetime as dt
import pymysql

ROOT = dict(host="localhost", port=3306, user="root", password="parking123")
DB = "ext_parking_mysql"
PLATES = ["WXY", "BMK", "VBA", "PLT", "JHG", "SWJ", "WA", "VCC"]
CLASSES = ["car", "car", "car", "car", "motorcycle", "van", "lorry"]
ZONES = ["Zone-A", "Zone-B", "Zone-C", "Basement-1", "Basement-2"]


def main():
    con = pymysql.connect(**ROOT, autocommit=True)
    cur = con.cursor()
    cur.execute(f"CREATE DATABASE IF NOT EXISTS {DB}")
    cur.execute(f"USE {DB}")
    cur.execute("DROP TABLE IF EXISTS car_movements")
    cur.execute("""
        CREATE TABLE car_movements (
            movement_id  INT AUTO_INCREMENT PRIMARY KEY,
            plate_no     VARCHAR(16) NOT NULL,
            time_in      DATETIME NOT NULL,
            time_out     DATETIME NULL,
            fee_charged  DECIMAL(8,2) NULL,
            bay_zone     VARCHAR(20) NULL,
            veh_kind     VARCHAR(20) NULL
        )
    """)
    now = dt.datetime.now()
    rows = []
    for _ in range(100):
        plate = random.choice(PLATES) + str(random.randint(1, 9999))
        t_in = now - dt.timedelta(minutes=random.randint(2, 180))
        inside = random.random() < 0.30
        if inside:
            t_out, fee = None, None
        else:
            dur = random.randint(8, 240)
            t_out = t_in + dt.timedelta(minutes=dur)
            if t_out > now:
                t_out, fee = None, None
            else:
                fee = 0 if dur <= 15 else round(2 + max(0, (dur / 60 - 3)) * 2.5, 2)
        rows.append((plate, t_in.strftime("%Y-%m-%d %H:%M:%S"),
                     t_out.strftime("%Y-%m-%d %H:%M:%S") if t_out else None,
                     fee, random.choice(ZONES), random.choice(CLASSES)))
    cur.executemany(
        "INSERT INTO car_movements (plate_no,time_in,time_out,fee_charged,bay_zone,veh_kind) "
        "VALUES (%s,%s,%s,%s,%s,%s)", rows)
    cur.execute("SELECT COUNT(*), SUM(time_out IS NULL) FROM car_movements")
    total, inside = cur.fetchone()
    con.close()
    print(f"seeded {total} rows ({inside} inside) into MySQL {DB}.car_movements")


if __name__ == "__main__":
    main()
