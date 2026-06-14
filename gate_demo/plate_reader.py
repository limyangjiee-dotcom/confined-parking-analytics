"""
plate_reader.py — webcam / image plate reader for the barrier-gate demo
=======================================================================
Reads a Malaysian number plate with EasyOCR and posts it to the parking
API's gate endpoint, then prints the gate decision (open / deny + reason).

Modes (pick one):
  --webcam            live camera; press SPACE to read the plate, Q to quit
  --image FILE [...]  read plate from photo file(s)
  --plate ABC1234     skip OCR entirely (no easyocr/torch needed) — handy
                      to demo the API flow without a camera

Examples:
  python plate_reader.py --gate entry --plate SWJ2558
  python plate_reader.py --gate entry --image car.jpg
  python plate_reader.py --gate exit  --webcam
  python plate_reader.py --pay        --plate SWJ2558   (autopay station)
  python plate_reader.py --status     --plate SWJ2558

Deps for OCR modes:  pip install easyocr opencv-python
(EasyOCR pulls in PyTorch ~2 GB. If pip fails on Python 3.14, create a
Python 3.12 venv just for this script.)  --plate mode needs only requests.
"""
import argparse
import re
import sys

import requests

API = "http://localhost:5000"
DEVICE_KEYS = {  # seeded in Gate_Devices by the API on first run
    "entry": "gate-entry-01-key-7f3a",
    "exit": "gate-exit-01-key-9b2c",
}
MASTER_KEY = "fyp-demo-key-2025"   # used for /pay and /status (kiosk, not a gate)
PLATE_RX = re.compile(r"^[A-Z]{1,3}\d{1,4}[A-Z]?$")


def norm(text):
    return re.sub(r"[^A-Z0-9]", "", text.upper())


def best_plate(ocr_results):
    """Pick the most plate-looking, highest-confidence OCR result.
    Also tries joining pairs (Malaysian plates are often two-line)."""
    cands = []
    for _, text, conf in ocr_results:
        t = norm(text)
        if PLATE_RX.match(t):
            cands.append((t, conf))
    for i in range(len(ocr_results) - 1):
        joined = norm(ocr_results[i][1]) + norm(ocr_results[i + 1][1])
        if PLATE_RX.match(joined):
            conf = min(ocr_results[i][2], ocr_results[i + 1][2])
            cands.append((joined, conf))
    return max(cands, key=lambda c: c[1]) if cands else (None, 0.0)


def read_image(path, reader):
    results = reader.readtext(path)
    plate, conf = best_plate(results)
    raw = [(t, round(c, 2)) for _, t, c in results]
    print(f"  OCR saw: {raw}")
    return plate, conf


def post_gate(gate, plate, confidence):
    r = requests.post(f"{API}/api/gate/{gate}",
                      json={"plate": plate, "confidence": confidence},
                      headers={"X-Device-Key": DEVICE_KEYS[gate]})
    show(r)


def show(r):
    try:
        j = r.json()
    except ValueError:
        print(f"  HTTP {r.status_code}: {r.text}")
        return
    if "action" in j:
        bar = "OPENING ====/" if j["action"] == "open" else "CLOSED  ====X"
        print(f"  GATE {bar}  {j.get('reason') or j.get('message', '')}")
    for k, v in j.items():
        if k not in ("action", "reason", "message"):
            print(f"    {k}: {v}")


def main():
    ap = argparse.ArgumentParser(description="Barrier-gate plate reader demo")
    ap.add_argument("--gate", choices=["entry", "exit"], default="entry")
    ap.add_argument("--webcam", action="store_true")
    ap.add_argument("--image", nargs="+")
    ap.add_argument("--plate", help="manual plate, skips OCR")
    ap.add_argument("--pay", action="store_true", help="pay open ticket for --plate")
    ap.add_argument("--status", action="store_true", help="show fee due for --plate")
    ap.add_argument("--camera-index", type=int, default=0)
    args = ap.parse_args()

    if args.pay or args.status:
        if not args.plate:
            sys.exit("--pay/--status need --plate")
        hdr = {"X-Api-Key": MASTER_KEY}
        if args.pay:
            show(requests.post(f"{API}/api/gate/pay",
                               json={"plate": args.plate}, headers=hdr))
        else:
            show(requests.get(f"{API}/api/gate/status/{args.plate}", headers=hdr))
        return

    if args.plate:
        print(f"Posting manual plate {args.plate} to /api/gate/{args.gate}")
        post_gate(args.gate, args.plate, None)
        return

    try:
        import easyocr
    except ImportError:
        sys.exit("easyocr not installed — pip install easyocr opencv-python "
                 "(or use --plate ABC1234 to demo without OCR)")
    print("Loading EasyOCR model (first run downloads ~100 MB)...")
    reader = easyocr.Reader(["en"], gpu=False)

    if args.image:
        for path in args.image:
            print(f"\n{path}:")
            plate, conf = read_image(path, reader)
            if plate is None:
                # still post — the API logs the misread and answers deny
                print("  no plate pattern found, posting low-confidence read")
                post_gate(args.gate, "UNREADABLE", 0.0)
            else:
                print(f"  plate: {plate}  (confidence {conf:.2f})")
                post_gate(args.gate, plate, conf)
        return

    if args.webcam:
        import cv2
        cap = cv2.VideoCapture(args.camera_index)
        if not cap.isOpened():
            sys.exit(f"cannot open camera {args.camera_index}")
        print("SPACE = read plate and post to gate, Q = quit")
        while True:
            ok, frame = cap.read()
            if not ok:
                break
            cv2.imshow(f"Gate camera ({args.gate}) - SPACE to read, Q to quit", frame)
            key = cv2.waitKey(1) & 0xFF
            if key == ord("q"):
                break
            if key == ord(" "):
                results = reader.readtext(frame)
                plate, conf = best_plate(results)
                if plate is None:
                    print("no plate found in frame, posting low-confidence read")
                    post_gate(args.gate, "UNREADABLE", 0.0)
                else:
                    print(f"plate: {plate}  (confidence {conf:.2f})")
                    post_gate(args.gate, plate, conf)
        cap.release()
        cv2.destroyAllWindows()
        return

    ap.print_help()


if __name__ == "__main__":
    main()
