"""
mock_parking_system.py — a stand-in for a THIRD-PARTY parking system.
=====================================================================
Creates a separate PostgreSQL database `ext_parking_demo` with a schema
that looks nothing like ours (foreign table/column names), as if it
belonged to another vendor's gate/camera system. Our analytics platform
then connects to it, discovers it, maps the fields, and syncs the data.

Run:  python mock_parking_system.py
Then in the web app -> Data Source page:
  host=localhost port=5432 db=ext_parking_demo user=postgres pass=parking123
  Discover -> table `anpr_sessions`
  Map:  plate=lpr_plate  entry=gate_in_at  exit=gate_out_at
        fee=paid_amount   level=deck_code   type=vehicle_class
  Save -> Sync.
"""
import random
import datetime as dt
import psycopg2

ADMIN = dict(host="localhost", port=5432, dbname="postgres",
             user="postgres", password="parking123")
EXT_DB = "ext_parking_demo"
PLATE_PREFIX = ["WXY", "BMK", "VBA", "PLT", "JHG", "SWJ", "WA", "VCC"]
CLASSES = ["Car", "Car", "Car", "Car", "Motorcycle"]   # only car & motorcycle
DECKS = ["P1", "P2", "P3", "B1", "B2"]


def ensure_db():
    con = psycopg2.connect(**ADMIN); con.autocommit = True
    cur = con.cursor()
    cur.execute("SELECT 1 FROM pg_database WHERE datname = %s", (EXT_DB,))
    if not cur.fetchone():
        cur.execute(f'CREATE DATABASE "{EXT_DB}"')
        print(f"created database {EXT_DB}")
    else:
        print(f"database {EXT_DB} already exists")
    con.close()


def seed():
    con = psycopg2.connect(host="localhost", port=5432, dbname=EXT_DB,
                           user="postgres", password="parking123")
    cur = con.cursor()
    # a vendor-shaped schema, deliberately NOT matching our column names
    cur.execute('''
        DROP TABLE IF EXISTS anpr_sessions;
        CREATE TABLE anpr_sessions (
            session_id     serial PRIMARY KEY,
            lpr_plate      text NOT NULL,
            gate_in_at     timestamptz NOT NULL,
            gate_out_at    timestamptz NULL,
            paid_amount    numeric(8,2) NULL,
            deck_code      text NULL,
            vehicle_class  text NULL,
            lane_id        int NULL
        );
    ''')

    now = dt.datetime.now()
    rows = []
    # ~120 sessions over the last 3 hours; ~35 still inside (no gate_out_at)
    for i in range(120):
        plate = random.choice(PLATE_PREFIX) + str(random.randint(1, 9999))
        in_at = now - dt.timedelta(minutes=random.randint(2, 180))
        inside = random.random() < 0.30
        if inside:
            out_at, amount, dur = None, None, None
        else:
            dur = random.randint(8, 240)
            out_at = in_at + dt.timedelta(minutes=dur)
            if out_at > now:
                out_at, amount = None, None
            else:
                amount = 0 if dur <= 15 else round(2 + max(0, (dur/60 - 3)) * 2.5, 2)
        rows.append((plate, in_at, out_at, amount,
                     random.choice(DECKS), random.choice(CLASSES),
                     random.randint(1, 6)))

    cur.executemany('''INSERT INTO anpr_sessions
        (lpr_plate, gate_in_at, gate_out_at, paid_amount, deck_code, vehicle_class, lane_id)
        VALUES (%s,%s,%s,%s,%s,%s,%s)''', rows)
    con.commit()
    cur.execute("SELECT COUNT(*), COUNT(*) FILTER (WHERE gate_out_at IS NULL) FROM anpr_sessions")
    total, inside = cur.fetchone()
    con.close()
    print(f"seeded {total} sessions ({inside} currently inside) into {EXT_DB}.anpr_sessions")


if __name__ == "__main__":
    ensure_db()
    seed()
    print("done — point the Data Source page at db 'ext_parking_demo'.")
