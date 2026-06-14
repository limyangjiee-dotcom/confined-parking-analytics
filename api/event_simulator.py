"""Real-time demo with plate + ticket + events. Run: python event_simulator.py"""
import requests, random, time

API = "http://localhost:5000"
HEADERS = {"X-Api-Key": "fyp-demo-key-2025"}
LEVELS  = ["B1","B2","B3","B4","P1","P2","P3"]
VTYPES  = ["Car","Car","Car","Motorcycle"]
PAYS    = ["Touch 'n Go","TNG eWallet","Credit/Debit Card"]
LETTERS = "ABCDEFGHJKLMNPRSTUVWXY"
EVENTS  = ["Concert","Expo","CareerFair","RoadShow","MegaSale"]

def plate():
    pre = "".join(random.choice(LETTERS) for _ in range(random.choice([2, 3])))
    return f"{pre}{random.randint(1, 9999)}"

open_v = []            # (plate, ticket)
event_left = 0         # >0 means an event is currently running (entries remaining)
event_name = ""
print("Simulating live parking events (with event-days)... Ctrl+C to stop.")
try:
    while True:
        # randomly start an event burst
        if event_left <= 0 and random.random() < 0.015:
            event_name = random.choice(EVENTS)
            event_left = random.randint(40, 90)
            print(f"\n*** EVENT STARTED: {event_name} (~{event_left} cars, heavier traffic) ***\n")
        in_event = event_left > 0

        if open_v and random.random() < 0.35:
            pl, ticket = open_v.pop(random.randrange(len(open_v)))
            r = requests.post(f"{API}/api/parking/exit",
                              json={"ticketId": ticket, "parkingFee": random.choice([0,2,2,3,5.5,8])},
                              headers=HEADERS)
            print("EXIT ", pl, ticket, r.status_code)
        else:
            pl = plate()
            r = requests.post(f"{API}/api/parking/entry",
                              json={"vehicleId": pl, "parkingLevel": random.choice(LEVELS),
                                    "vehicleType": random.choice(VTYPES), "paymentType": random.choice(PAYS),
                                    "eventStatus": "Event Day" if in_event else "Non-Event Day",
                                    "eventName": event_name if in_event else ""},
                              headers=HEADERS)
            if r.status_code == 200:
                open_v.append((pl, r.json()["ticketId"]))
            tag = f"  [EVENT: {event_name}]" if in_event else ""
            print("ENTRY", pl, r.status_code, tag)
            if in_event:
                event_left -= 1
                if event_left == 0:
                    print(f"\n*** EVENT ENDED: {event_name} ***\n")
        # heavier traffic during events (shorter gaps)
        time.sleep(random.uniform(0.2, 0.6) if in_event else random.uniform(0.4, 1.5))
except KeyboardInterrupt:
    print("\nStopped.")
