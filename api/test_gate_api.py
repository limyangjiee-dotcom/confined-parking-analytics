"""
test_gate_api.py — end-to-end test of the barrier-gate API.
Run with the API up:  python test_gate_api.py
Covers: device-key auth, entry, duplicate entry, misread/low-confidence,
unpaid exit denial, pay, paid exit, free-period exit, status, log.
"""
import requests

API = "http://localhost:5000"
ENTRY_KEY = {"X-Device-Key": "gate-entry-01-key-7f3a"}
EXIT_KEY = {"X-Device-Key": "gate-exit-01-key-9b2c"}
MASTER = {"X-Api-Key": "fyp-demo-key-2025"}

passed, failed = 0, 0


def check(name, cond, detail=""):
    global passed, failed
    if cond:
        passed += 1
        print(f"  PASS  {name}")
    else:
        failed += 1
        print(f"  FAIL  {name}  {detail}")


print("1) auth")
r = requests.post(f"{API}/api/gate/entry", json={"plate": "QAA1111"})
check("no key -> 401", r.status_code == 401, r.text)
r = requests.post(f"{API}/api/gate/entry", json={"plate": "QAA1111"},
                  headers={"X-Device-Key": "wrong-key"})
check("bad key -> 401", r.status_code == 401, r.text)

print("2) entry")
r = requests.post(f"{API}/api/gate/entry",
                  json={"plate": "qaa 1111", "confidence": 0.93}, headers=ENTRY_KEY)
j = r.json()
check("entry -> open", r.status_code == 200 and j.get("action") == "open", r.text)
check("plate normalized", j.get("plate") == "QAA1111", r.text)
ticket = j.get("ticketId", "")

r = requests.post(f"{API}/api/gate/entry",
                  json={"plate": "QAA1111", "confidence": 0.91}, headers=ENTRY_KEY)
j = r.json()
check("duplicate entry -> deny", j.get("action") == "deny" and "duplicate" in j.get("reason", ""), r.text)

r = requests.post(f"{API}/api/gate/entry",
                  json={"plate": "QBB2222", "confidence": 0.21}, headers=ENTRY_KEY)
check("low confidence -> deny", r.json().get("action") == "deny", r.text)

r = requests.post(f"{API}/api/gate/entry",
                  json={"plate": "###???", "confidence": 0.95}, headers=ENTRY_KEY)
check("garbage plate -> deny", r.json().get("action") == "deny", r.text)

print("3) status + free-period exit")
r = requests.get(f"{API}/api/gate/status/QAA1111", headers=MASTER)
j = r.json()
check("status shows ticket", r.status_code == 200 and j.get("ticketId") == ticket, r.text)
check("within free 15 min -> RM0 due", float(j.get("amountDue", -1)) == 0.0, r.text)

r = requests.post(f"{API}/api/gate/exit", json={"plate": "QAA1111"}, headers=EXIT_KEY)
j = r.json()
check("exit in free period -> open, fee 0",
      j.get("action") == "open" and float(j.get("fee", -1)) == 0.0, r.text)

print("4) unpaid exit -> pay -> exit  (fee forced via backdated entry)")
# create a ticket, then backdate its Entry_Time 5 hours in the DB to owe money
r = requests.post(f"{API}/api/gate/entry",
                  json={"plate": "QCC3333", "confidence": 0.88}, headers=ENTRY_KEY)
t2 = r.json().get("ticketId", "")
check("second entry -> open", bool(t2), r.text)

import psycopg2
conn = psycopg2.connect(host="localhost", port=5432, dbname="parking_db",
                        user="postgres", password="parking123")
cur = conn.cursor()
cur.execute('UPDATE "Live_Parking" SET "Entry_Time" = "Entry_Time" - INTERVAL \'5 hours\' '
            'WHERE "Ticket_ID" = %s', (t2,))
conn.commit()

r = requests.post(f"{API}/api/gate/exit", json={"plate": "QCC3333"}, headers=EXIT_KEY)
j = r.json()
check("unpaid exit -> deny with amount", j.get("action") == "deny" and "unpaid" in j.get("reason", ""), r.text)

r = requests.get(f"{API}/api/gate/status/QCC3333", headers=MASTER)
due = float(r.json().get("amountDue", -1))
# weekday 5h = 2 + 1 + 2.5  = 5.50 ; Fri/weekend flat = 2
weekday_expected = {5.5, 2.0}
check(f"due matches tariff (got RM{due})", due in weekday_expected, r.text)

r = requests.post(f"{API}/api/gate/pay", json={"plate": "QCC3333", "method": "TnG"}, headers=MASTER)
j = r.json()
check("pay records amount", float(j.get("amountPaid", -1)) == due, r.text)

r = requests.post(f"{API}/api/gate/exit", json={"plate": "QCC3333"}, headers=EXIT_KEY)
j = r.json()
check("paid exit -> open", j.get("action") == "open" and float(j.get("fee", -1)) == due, r.text)

print("5) exit with no ticket")
r = requests.post(f"{API}/api/gate/exit", json={"plate": "QZZ9999"}, headers=EXIT_KEY)
check("unknown plate exit -> deny", r.json().get("action") == "deny", r.text)

print("6) audit log")
r = requests.get(f"{API}/api/gate/log?limit=20", headers=MASTER)
logs = r.json()
check("log has entries", r.status_code == 200 and len(logs) >= 8, r.text)
check("log records device ids", any(l.get("device_ID") == "ENTRY-CAM-01" for l in logs)
      or any(l.get("Device_ID") == "ENTRY-CAM-01" for l in logs), str(logs[:2]))

# cleanup test rows so live data stays clean
cur.execute('DELETE FROM "Live_Parking" WHERE "Vehicle_ID" LIKE %s', ("Q%",))
cur.execute('DELETE FROM "Gate_Payments" WHERE "Ticket_ID" IN %s', ((ticket, t2),))
cur.execute('DELETE FROM "Gate_Log" WHERE "Plate" LIKE %s', ("Q%",))
conn.commit()
conn.close()

print(f"\n{passed} passed, {failed} failed")
raise SystemExit(1 if failed else 0)
