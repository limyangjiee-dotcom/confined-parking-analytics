"""
mock_parking_api.py — a stand-in for a THIRD-PARTY parking system's REST API.
============================================================================
Your supervisor's point: "usually communicate via API." This simulates the
parking system exposing its sessions over HTTP, so the analytics platform can
PULL them (instead of connecting straight to their database).

It returns the operator's EXISTING HISTORY: a number of complete past days at
full volume, plus a little live "today" activity. That history is what the ML
forecast trains on — so after you connect + sync, the forecast visibly reflects
this operator's data (not just the built-in synthetic baseline).

Serves:  GET http://localhost:8900/api/sessions[?days=N&per_day=M]
Header:  X-Api-Key: parking-vendor-key      (simulates the vendor's auth)
Body:    {"status":"ok","count":N,"data":[ {ts_in, ts_out, lpr, paid, deck, klass}, ... ]}

Run:  python mock_parking_api.py
Then in the web app -> Data Source (REST API):
  URL          http://localhost:8900/api/sessions
  Method       GET
  Records path data
  Auth header  X-Api-Key   /   parking-vendor-key
  Discover -> map lpr->plate, ts_in->entry, ts_out->exit, paid->fee,
              deck->level, klass->vehicle type -> Save -> Sync.

Tuning (optional): add ?days=120&per_day=3000 to the URL to change how much
history the operator "has". The history is realistic: day-of-week amplitude,
daily noise, a mild growth trend, and event spikes matching the iCal feed.
"""
import json
import random
import datetime as dt
from http.server import BaseHTTPRequestHandler, HTTPServer
from urllib.parse import urlparse, parse_qs

PORT = 8900
API_KEY = "parking-vendor-key"
PLATES = ["WXY", "BMK", "VBA", "PLT", "JHG", "SWJ", "WA", "VCC", "WB", "JKL"]
CLASSES = ["Car", "Car", "Car", "Car", "Motorcycle"]   # only car & motorcycle
DECKS = ["P1", "P2", "P3", "B1", "B2"]

# default "operator history": ~4 months of realistic days — day-of-week amplitude,
# daily noise, a mild growth trend, and recurring event spikes (aligned with the
# iCal feed below) — so after connect+sync the forecast has real patterns to learn.
# per_day=3000 keeps the payload (~450k records) syncing in well under a minute.
DEFAULT_DAYS = 120
DEFAULT_PER_DAY = 3000

# Mon..Sun demand profile (weekday build-up -> Friday jump -> Saturday peak)
DOW_MULT = {0: 0.84, 1: 0.82, 2: 0.88, 3: 0.96, 4: 1.18, 5: 1.38, 6: 1.24}

_CACHE = {}   # (days, per_day) -> generated dataset (so test/discover/sync agree)


def event_plan(days_back=150, days_fwd=21):
    """Deterministic event calendar shared by the history generator AND the iCal
    feed: every 3rd weekend is a MegaSale (big spike), first Wednesday of each
    month is a Tech Expo (midweek spike). Past events give the forecast event
    history to learn from; future ones give it event days to predict."""
    today = dt.date.today()
    plan = {}
    d = today - dt.timedelta(days=days_back)
    end = today + dt.timedelta(days=days_fwd)
    while d <= end:
        if d.weekday() == 5 and d.isocalendar()[1] % 3 == 0:   # every 3rd Saturday
            plan[d] = ("MegaSale Weekend", 1.40)
            if d + dt.timedelta(days=1) <= end:
                plan[d + dt.timedelta(days=1)] = ("MegaSale Weekend", 1.32)
        elif d.weekday() == 2 and d.day <= 7:                  # first Wednesday
            plan[d] = ("Tech Expo", 1.25)
        d += dt.timedelta(days=1)
    return plan


def _plate():
    return random.choice(PLATES) + str(random.randint(1, 9999))


def _fee(dur_min):
    if dur_min <= 15:
        return 0.0
    return round(2 + max(0, (dur_min / 60 - 3)) * 2.5, 2)


PAY_METHODS = ["TnG eWallet", "Card", "Cash", "Autopay"]
PAY_WEIGHTS = [45, 30, 15, 10]


def _session(t_in, t_out):
    amount = None if t_out is None else _fee((t_out - t_in).total_seconds() / 60)
    return {
        "lpr": _plate(),
        "ts_in": t_in.isoformat(timespec="seconds"),
        "ts_out": t_out.isoformat(timespec="seconds") if t_out else None,
        "paid": amount,
        "deck": random.choice(DECKS),
        "klass": random.choice(CLASSES),
        "pay_method": random.choices(PAY_METHODS, weights=PAY_WEIGHTS)[0],
    }


def gen_history(days, per_day):
    """Complete past days (yesterday backwards) — the operator's existing history,
    with realistic structure: day-of-week amplitude + ±8% daily noise + a mild
    growth trend toward today + event-day spikes from event_plan(). Each day mixes
    three driver populations so behaviour segmentation has something to find:
      visitors  (~87% weekday / ~97% weekend): midday arrivals, short stays
      workers   (~12% on weekdays): arrive 7–10am, stay 7–10 hours
      residents (~1%): overnight/all-day stays of 12–18 hours
    """
    today = dt.date.today()
    events = event_plan(days_back=days)
    out = []
    for i in range(1, days + 1):
        day = today - dt.timedelta(days=i)
        rng = random.Random(day.toordinal())              # deterministic per date
        mult = DOW_MULT[day.weekday()]
        mult *= rng.uniform(0.92, 1.08)                   # daily noise
        mult *= 1 + 0.10 * (days - i) / days              # mild growth toward today
        if day in events:
            mult *= events[day][1]                        # event spike
        n = int(per_day * mult)

        is_weekday = day.weekday() < 5
        n_res = max(1, int(n * 0.01))
        n_wrk = int(n * (0.12 if is_weekday else 0.02))
        n_vis = n - n_res - n_wrk

        def add(t_in, dur_min):
            out.append(_session(t_in, t_in + dt.timedelta(minutes=dur_min)))

        for _ in range(n_vis):     # visitors: midday peak, short stays
            hour = int(min(22, max(6, random.gauss(13.5, 3.2))))
            t_in = dt.datetime(day.year, day.month, day.day, hour,
                               random.randint(0, 59), random.randint(0, 59))
            add(t_in, max(8, int(random.gauss(110, 55))))
        for _ in range(n_wrk):     # workers: morning arrival, work-day stay
            hour = int(min(10, max(6, random.gauss(8.3, 1.0))))
            t_in = dt.datetime(day.year, day.month, day.day, hour,
                               random.randint(0, 59), random.randint(0, 59))
            add(t_in, max(320, min(690, int(random.gauss(540, 70)))))   # ~5.3–11.5 h
        for _ in range(n_res):     # residents: long overnight/all-day stays
            hour = random.randint(6, 21)
            t_in = dt.datetime(day.year, day.month, day.day, hour,
                               random.randint(0, 59), random.randint(0, 59))
            add(t_in, random.randint(730, 1080))                        # 12–18 h
    return out


def gen_today_live(n=120):
    """A few currently-inside + just-completed sessions so Real-Time has activity."""
    now = dt.datetime.now()
    out = []
    for _ in range(n):
        t_in = now - dt.timedelta(minutes=random.randint(2, 240))
        if random.random() < 0.35:
            out.append(_session(t_in, None))              # still inside
        else:
            dur = random.randint(8, 200)
            t_out = t_in + dt.timedelta(minutes=dur)
            out.append(_session(t_in, t_out if t_out <= now else None))
    return out


def gen_sessions(days, per_day):
    key = (days, per_day)
    if key not in _CACHE:
        _CACHE[key] = gen_history(days, per_day) + gen_today_live()
    return _CACHE[key]


def gen_ical():
    """An iCalendar (.ics) feed of the mall's events — stands in for a public
    Google Calendar. Includes PAST events (the same dates whose traffic spikes
    appear in the generated history, so the forecast can learn the event effect)
    plus the upcoming ones it should predict."""
    plan = event_plan(days_back=DEFAULT_DAYS, days_fwd=21)
    lines = ["BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//MockMall//Events//EN"]
    for i, (d, (name, _)) in enumerate(sorted(plan.items())):
        nxt = d + dt.timedelta(days=1)
        lines += ["BEGIN:VEVENT", f"UID:mock-event-{i}@mall",
                  f"DTSTART;VALUE=DATE:{d:%Y%m%d}", f"DTEND;VALUE=DATE:{nxt:%Y%m%d}",
                  f"SUMMARY:{name}", "END:VEVENT"]
    lines.append("END:VCALENDAR")
    return ("\r\n".join(lines) + "\r\n").encode()


def gen_alt(n_days=7, per_day=300):
    """The SAME kind of sessions but in a DIFFERENT vendor schema — different
    field names AND different JSON nesting (no auth header, records under
    result.records). Proves the connector isn't tied to one shape: you just
    re-map the fields on the Data Source page."""
    today = dt.date.today()
    out = []
    for d in range(1, n_days + 1):
        day = today - dt.timedelta(days=d)
        for _ in range(per_day):
            hour = int(min(22, max(6, random.gauss(13.5, 3.2))))
            t_in = dt.datetime(day.year, day.month, day.day, hour,
                               random.randint(0, 59), random.randint(0, 59))
            dur = max(8, int(random.gauss(110, 55)))
            t_out = t_in + dt.timedelta(minutes=dur)
            out.append({
                "vehicle_plate": _plate(),
                "arrival":       t_in.isoformat(timespec="seconds"),
                "departure":     t_out.isoformat(timespec="seconds"),
                "amount_paid":   _fee(dur),
                "deck_no":       random.choice(DECKS),
                "veh_class":     random.choice(CLASSES),
            })
    return out


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass  # quiet

    def do_GET(self):
        parsed = urlparse(self.path)
        if parsed.path == "/api/altsessions":
            # different vendor shape: no auth, different field names, records under result.records
            body = json.dumps({"result": {"records": gen_alt()}}).encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return
        if parsed.path in ("/calendar.ics", "/events.ics"):
            body = gen_ical()
            self.send_response(200)
            self.send_header("Content-Type", "text/calendar")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return
        if parsed.path != "/api/sessions":
            self.send_error(404, "not found"); return
        if self.headers.get("X-Api-Key") != API_KEY:
            self.send_response(401)
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            self.wfile.write(b'{"error":"invalid X-Api-Key"}')
            return
        q = parse_qs(parsed.query)
        try:
            days = max(1, min(120, int(q.get("days", [DEFAULT_DAYS])[0])))
            per_day = max(1, min(40000, int(q.get("per_day", [DEFAULT_PER_DAY])[0])))
        except ValueError:
            days, per_day = DEFAULT_DAYS, DEFAULT_PER_DAY
        data = gen_sessions(days, per_day)
        body = json.dumps({"status": "ok", "count": len(data), "data": data}).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


if __name__ == "__main__":
    print(f"Mock parking-system API on http://localhost:{PORT}/api/sessions "
          f"(header X-Api-Key: {API_KEY})")
    print(f"  default history: {DEFAULT_DAYS} complete days @ ~{DEFAULT_PER_DAY}/day "
          f"(override with ?days=N&per_day=M)")
    print(f"  event iCal feed: http://localhost:{PORT}/calendar.ics  (for the Data Source -> Event calendar feed)")
    HTTPServer(("0.0.0.0", PORT), Handler).serve_forever()
