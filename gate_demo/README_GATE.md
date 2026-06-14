# Barrier-Gate Integration (supervisor request)

Single-carpark barrier-gate layer on top of the existing ASP.NET API
(`C:\FYP\ParkingApiPg_PostgreSQL\ParkingApiPg`). Multi-tenant = future work.

## What was added (all in the existing API project)

| Piece | Where |
|---|---|
| `POST /api/gate/entry` → `{action: open/deny, ticketId, reason}` | `Controllers/GateController.cs` |
| `POST /api/gate/exit` → open, or deny with `amountDue` if unpaid | same |
| `POST /api/gate/pay` (autopay station) + `GET /api/gate/status/{plate}` | same |
| `GET /api/gate/log` — audit trail of every gate decision | same |
| Per-device API keys (`Gate_Devices` table, `X-Device-Key` header) | `Program.cs` middleware |
| Tables `Gate_Devices`, `Gate_Log`, `Gate_Payments` auto-created + seeded on startup | `Program.cs` |
| Tariff (free 15 min; weekday RM2/3h, +RM1 hr4, +RM2.50/h; Fri/weekend flat RM2) | `Models/Tariff.cs` |
| Webcam/EasyOCR plate reader posting to the gate | `C:\FYP\gate_demo\plate_reader.py` |
| End-to-end tests (18 checks, all passing) | `ParkingApiPg/test_gate_api.py` |

## Edge cases handled
- **Duplicate entry** (plate already inside) → deny + reason, possible misread flagged.
- **Misread / low OCR confidence** (< 0.40) or invalid plate format → deny,
  "press button for manual ticket"; logged with confidence in `Gate_Log`.
- **Unpaid exit** → deny with outstanding RM amount; pay at `/api/gate/pay`,
  then 15-min grace period to exit (amount due is frozen at payment time
  within the grace window).
- **Exit with no ticket** (misread at exit) → deny, manual-exit instruction.
- Entries are stamped with today's event from `Event_Calendar`
  (verified: a gate entry on 2026-06-13 got `Event Day / MegaSale`).

## Auth model
- Gates authenticate with their own key: `X-Device-Key` checked against
  `Gate_Devices` (revocable per device, `Is_Active` flag).
  Seeded demo devices: `ENTRY-CAM-01` / `gate-entry-01-key-7f3a`,
  `EXIT-CAM-01` / `gate-exit-01-key-9b2c`.
- Master `X-Api-Key: fyp-demo-key-2025` still works everywhere
  (kiosk endpoints `/pay`, `/status`, `/log` use it).

## Demo (API must be running: `dotnet run` in the API folder)
```
# no camera needed:
python plate_reader.py --gate entry --plate SWJ2558      # barrier opens, ticket issued
python plate_reader.py --gate entry --plate SWJ2558      # deny: duplicate (already inside)
python plate_reader.py --gate exit  --plate SWJ2558      # opens free if <15 min, else deny unpaid
python plate_reader.py --status     --plate SWJ2558      # fee due
python plate_reader.py --pay        --plate SWJ2558      # autopay
python plate_reader.py --gate exit  --plate SWJ2558      # barrier opens

# with a webcam (pip install easyocr opencv-python; needs PyTorch —
# if pip fails on Python 3.14, use a Python 3.12 venv):
python plate_reader.py --gate entry --webcam             # SPACE reads the plate
python plate_reader.py --gate entry --image car_photo.jpg
```

## Tests
```
cd C:\FYP\ParkingApiPg_PostgreSQL\ParkingApiPg
python test_gate_api.py     # 18 checks: auth, entry, duplicate, misread,
                            # free exit, unpaid deny, pay, paid exit, log
```
(The test backdates one ticket 5 h via SQL to force a fee, and cleans up
its own rows afterwards.)
