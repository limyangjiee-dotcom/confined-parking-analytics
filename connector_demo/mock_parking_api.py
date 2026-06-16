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

Tuning (optional): add ?days=28&per_day=12000 to the URL to change how much
history the operator "has". More complete days => a bigger forecast shift.
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

# default "operator history": 28 complete days (4 of every weekday) so the
# forecast's "avg of last 4 <weekday>" basis is fully driven by this data.
DEFAULT_DAYS = 28
DEFAULT_PER_DAY = 12000

_CACHE = {}   # (days, per_day) -> generated dataset (so test/discover/sync agree)


def _plate():
    return random.choice(PLATES) + str(random.randint(1, 9999))


def _fee(dur_min):
    if dur_min <= 15:
        return 0.0
    return round(2 + max(0, (dur_min / 60 - 3)) * 2.5, 2)


def _session(t_in, t_out):
    amount = None if t_out is None else _fee((t_out - t_in).total_seconds() / 60)
    return {
        "lpr": _plate(),
        "ts_in": t_in.isoformat(timespec="seconds"),
        "ts_out": t_out.isoformat(timespec="seconds") if t_out else None,
        "paid": amount,
        "deck": random.choice(DECKS),
        "klass": random.choice(CLASSES),
    }


def gen_history(days, per_day):
    """Complete past days (yesterday backwards) — the operator's existing history."""
    today = dt.date.today()
    out = []
    for d in range(1, days + 1):
        day = today - dt.timedelta(days=d)
        is_weekend = day.weekday() >= 5
        n = int(per_day * (1.3 if is_weekend else 1.0))   # busier weekends
        for _ in range(n):
            # arrivals peak around early afternoon, spread across the day
            hour = int(min(22, max(6, random.gauss(13.5, 3.2))))
            t_in = dt.datetime(day.year, day.month, day.day, hour,
                               random.randint(0, 59), random.randint(0, 59))
            dur = max(8, int(random.gauss(110, 55)))      # minutes
            t_out = t_in + dt.timedelta(minutes=dur)
            out.append(_session(t_in, t_out))
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
    """A small iCalendar (.ics) feed of upcoming mall events — stands in for a
    public Google Calendar so the Data Source page can import event days."""
    today = dt.date.today()
    plan = [
        (today + dt.timedelta(days=3),  "MegaSale Weekend"),
        (today + dt.timedelta(days=4),  "MegaSale Weekend"),
        (today + dt.timedelta(days=10), "Tech Expo 2026"),
        (today + dt.timedelta(days=14), "Career Fair"),
        (today + dt.timedelta(days=18), "Weekend Concert"),
    ]
    lines = ["BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//MockMall//Events//EN"]
    for i, (d, name) in enumerate(plan):
        nxt = d + dt.timedelta(days=1)
        lines += ["BEGIN:VEVENT", f"UID:mock-event-{i}@mall",
                  f"DTSTART;VALUE=DATE:{d:%Y%m%d}", f"DTEND;VALUE=DATE:{nxt:%Y%m%d}",
                  f"SUMMARY:{name}", "END:VEVENT"]
    lines.append("END:VCALENDAR")
    return ("\r\n".join(lines) + "\r\n").encode()


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass  # quiet

    def do_GET(self):
        parsed = urlparse(self.path)
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
