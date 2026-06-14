"""
mock_parking_api.py — a stand-in for a THIRD-PARTY parking system's REST API.
============================================================================
Your supervisor's point: "usually communicate via API." This simulates the
parking system exposing its sessions over HTTP, so the analytics platform can
PULL them (instead of connecting straight to their database).

Serves:  GET http://localhost:8900/api/sessions
Header:  X-Api-Key: parking-vendor-key      (simulates the vendor's auth)
Body:    {"status":"ok","data":[ {ts_in, ts_out, lpr, paid, deck, klass}, ... ]}

Run:  python mock_parking_api.py
Then in the web app -> Data Source -> Source type = REST API:
  URL          http://localhost:8900/api/sessions
  Auth header  X-Api-Key   /   parking-vendor-key
  Records path data
  Discover -> map lpr->plate, ts_in->entry, ts_out->exit, paid->fee,
              deck->level, klass->vehicle type -> Save -> Sync.
"""
import json
import random
import datetime as dt
from http.server import BaseHTTPRequestHandler, HTTPServer

PORT = 8900
API_KEY = "parking-vendor-key"
PLATES = ["WXY", "BMK", "VBA", "PLT", "JHG", "SWJ", "WA", "VCC"]
CLASSES = ["car", "car", "car", "car", "motorcycle", "van", "lorry"]
DECKS = ["P1", "P2", "P3", "B1", "B2"]


def gen_sessions(n=110):
    now = dt.datetime.now()
    out = []
    for _ in range(n):
        plate = random.choice(PLATES) + str(random.randint(1, 9999))
        t_in = now - dt.timedelta(minutes=random.randint(2, 180))
        inside = random.random() < 0.30
        if inside:
            t_out, amount = None, None
        else:
            dur = random.randint(8, 240)
            t_out = t_in + dt.timedelta(minutes=dur)
            if t_out > now:
                t_out, amount = None, None
            else:
                amount = 0 if dur <= 15 else round(2 + max(0, (dur / 60 - 3)) * 2.5, 2)
        out.append({
            "lpr": plate,
            "ts_in": t_in.isoformat(timespec="seconds"),
            "ts_out": t_out.isoformat(timespec="seconds") if t_out else None,
            "paid": amount,
            "deck": random.choice(DECKS),
            "klass": random.choice(CLASSES),
        })
    return out


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass  # quiet

    def do_GET(self):
        if self.path.split("?")[0] != "/api/sessions":
            self.send_error(404, "not found"); return
        if self.headers.get("X-Api-Key") != API_KEY:
            self.send_response(401)
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            self.wfile.write(b'{"error":"invalid X-Api-Key"}')
            return
        body = json.dumps({"status": "ok", "data": gen_sessions()}).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


if __name__ == "__main__":
    print(f"Mock parking-system API on http://localhost:{PORT}/api/sessions "
          f"(header X-Api-Key: {API_KEY})")
    HTTPServer(("0.0.0.0", PORT), Handler).serve_forever()
