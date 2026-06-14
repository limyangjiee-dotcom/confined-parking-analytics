using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ParkingApiPg.Data;
using ParkingApiPg.Models;
namespace ParkingApiPg.Controllers;

[ApiController]
[Route("api/gate")]
public class GateController : ControllerBase
{
    private readonly ParkingDbContext _db;
    public GateController(ParkingDbContext db) => _db = db;

    // Malaysian plates: 1-3 letters, 1-4 digits, optional trailing letter (e.g. SWJ2558, WXY123A)
    private static readonly Regex PlateRx = new(@"^[A-Z]{1,3}\d{1,4}[A-Z]?$", RegexOptions.Compiled);
    private const double MinConfidence = 0.40;

    private string DeviceId => HttpContext.Items["DeviceId"] as string ?? "unknown";

    private static string NormalizePlate(string? plate) =>
        Regex.Replace((plate ?? "").ToUpperInvariant(), @"[\s\-]", "");

    private async Task<GateLog> Log(string plate, string action, string reason,
                                    string ticket = "", double? confidence = null)
    {
        var entry = new GateLog
        {
            Log_Time = DateTime.Now, Device_ID = DeviceId, Plate = plate,
            Action = action, Reason = reason, Ticket_ID = ticket, Confidence = confidence
        };
        _db.GateLog.Add(entry);
        await _db.SaveChangesAsync();
        return entry;
    }

    private async Task<IActionResult> Deny(string plate, string reason,
                                           double? confidence = null, object? extra = null)
    {
        await Log(plate, "deny", reason, confidence: confidence);
        return Ok(new { action = "deny", reason, plate, extra });
    }

    // ------------------------------------------------------------------
    // POST /api/gate/entry — camera reads a plate at the entry barrier
    // ------------------------------------------------------------------
    [HttpPost("entry")]
    public async Task<IActionResult> Entry(GateEntryDto dto)
    {
        var plate = NormalizePlate(dto.Plate);

        if (dto.Confidence is < MinConfidence)
            return await Deny(plate, $"OCR confidence {dto.Confidence:0.00} below {MinConfidence:0.00} — press button for manual ticket", dto.Confidence);
        if (!PlateRx.IsMatch(plate))
            return await Deny(plate, "plate unreadable / invalid format — press button for manual ticket", dto.Confidence);

        var openTicket = await _db.LiveParking
            .Where(x => x.Vehicle_ID == plate && x.Exit_Time == null)
            .FirstOrDefaultAsync();
        if (openTicket != null)
            return await Deny(plate, $"duplicate entry — vehicle already inside since {openTicket.Entry_Time:HH:mm} (ticket {openTicket.Ticket_ID}); possible misread", dto.Confidence,
                              new { openTicket.Ticket_ID });

        // stamp today's event from the Event_Calendar so live analytics see it
        var today = DateTime.Today;
        var ev = await _db.EventCalendar.FirstOrDefaultAsync(e => e.Event_Date == today);

        var ticket = "T" + Guid.NewGuid().ToString("N")[..8].ToUpper();
        _db.LiveParking.Add(new LiveParking
        {
            Ticket_ID = ticket, Vehicle_ID = plate, Entry_Time = DateTime.Now,
            Parking_Level = dto.ParkingLevel, Vehicle_Type = dto.VehicleType,
            Payment_Type = "Pending",
            Event_Status = ev != null ? "Event Day" : "Non-Event Day",
            Event_Name = ev?.Event_Name ?? ""
        });
        await _db.SaveChangesAsync();
        await Log(plate, "open", "entry ok", ticket, dto.Confidence);
        return Ok(new { action = "open", ticketId = ticket, plate, message = "welcome — barrier opening" });
    }

    // ------------------------------------------------------------------
    // POST /api/gate/exit — camera reads a plate at the exit barrier
    // ------------------------------------------------------------------
    [HttpPost("exit")]
    public async Task<IActionResult> Exit(GateExitDto dto)
    {
        var plate = NormalizePlate(dto.Plate);

        var ticket = dto.TicketId != null
            ? await _db.LiveParking.FirstOrDefaultAsync(x => x.Ticket_ID == dto.TicketId && x.Exit_Time == null)
            : await _db.LiveParking.Where(x => x.Vehicle_ID == plate && x.Exit_Time == null)
                                   .OrderBy(x => x.Entry_Time).FirstOrDefaultAsync();
        if (ticket == null)
            return await Deny(plate, "no open ticket for this plate — possible misread; insert ticket for manual exit", dto.Confidence);

        var now = DateTime.Now;
        var due = Tariff.FeeFor(ticket.Entry_Time, now);
        decimal paid = 0;
        if (due > 0)
        {
            var payments = await _db.GatePayments.Where(p => p.Ticket_ID == ticket.Ticket_ID).ToListAsync();
            paid = payments.Sum(p => p.Amount);
            if (payments.Count > 0)
            {
                // honor the amount that was due when they paid, within the grace period
                var lastPaid = payments.Max(p => p.Paid_At);
                if ((now - lastPaid).TotalMinutes <= Tariff.PaymentGraceMinutes)
                    due = Math.Min(due, Tariff.FeeFor(ticket.Entry_Time, lastPaid));
            }
            if (paid < due)
                return await Deny(ticket.Vehicle_ID,
                    $"unpaid — RM {due - paid:0.00} outstanding, please pay at the autopay station",
                    dto.Confidence, new { ticketId = ticket.Ticket_ID, amountDue = due - paid });
        }

        ticket.Exit_Time = now;
        ticket.Parking_Fee = due;
        ticket.Parking_Duration_Hours = (now - ticket.Entry_Time).TotalHours;
        if (ticket.Payment_Type == "Pending") ticket.Payment_Type = due == 0 ? "Free" : "Autopay";
        await _db.SaveChangesAsync();
        await Log(ticket.Vehicle_ID, "open", due == 0 ? "exit ok (free period)" : "exit ok (paid)",
                  ticket.Ticket_ID, dto.Confidence);
        return Ok(new { action = "open", ticketId = ticket.Ticket_ID, fee = due,
                        durationHours = Math.Round(ticket.Parking_Duration_Hours ?? 0, 2),
                        message = "thank you — barrier opening" });
    }

    // ------------------------------------------------------------------
    // POST /api/gate/pay — autopay station settles a ticket
    // ------------------------------------------------------------------
    [HttpPost("pay")]
    public async Task<IActionResult> Pay(GatePayDto dto)
    {
        var plate = NormalizePlate(dto.Plate);
        var ticket = dto.TicketId != null
            ? await _db.LiveParking.FirstOrDefaultAsync(x => x.Ticket_ID == dto.TicketId && x.Exit_Time == null)
            : await _db.LiveParking.Where(x => x.Vehicle_ID == plate && x.Exit_Time == null)
                                   .OrderBy(x => x.Entry_Time).FirstOrDefaultAsync();
        if (ticket == null) return NotFound(new { error = "no open ticket found" });

        var now = DateTime.Now;
        var paid = await _db.GatePayments.Where(p => p.Ticket_ID == ticket.Ticket_ID)
                                         .SumAsync(p => (decimal?)p.Amount) ?? 0;
        var due = Tariff.FeeFor(ticket.Entry_Time, now) - paid;
        if (due <= 0)
            return Ok(new { ticketId = ticket.Ticket_ID, amountPaid = 0m, message = "nothing to pay" });

        _db.GatePayments.Add(new GatePayment
        {
            Ticket_ID = ticket.Ticket_ID, Amount = due, Paid_At = now, Method = dto.Method
        });
        ticket.Payment_Type = dto.Method;
        await _db.SaveChangesAsync();
        return Ok(new { ticketId = ticket.Ticket_ID, amountPaid = due,
                        payBefore = now.AddMinutes(Tariff.PaymentGraceMinutes),
                        message = $"RM {due:0.00} paid — please exit within {Tariff.PaymentGraceMinutes} minutes" });
    }

    // ------------------------------------------------------------------
    // GET /api/gate/status/{plate} — autopay station / kiosk lookup
    // ------------------------------------------------------------------
    [HttpGet("status/{plate}")]
    public async Task<IActionResult> Status(string plate)
    {
        plate = NormalizePlate(plate);
        var ticket = await _db.LiveParking.Where(x => x.Vehicle_ID == plate && x.Exit_Time == null)
                                          .OrderBy(x => x.Entry_Time).FirstOrDefaultAsync();
        if (ticket == null) return NotFound(new { error = "no open ticket for this plate" });

        var now = DateTime.Now;
        var paid = await _db.GatePayments.Where(p => p.Ticket_ID == ticket.Ticket_ID)
                                         .SumAsync(p => (decimal?)p.Amount) ?? 0;
        return Ok(new
        {
            ticketId = ticket.Ticket_ID, plate = ticket.Vehicle_ID,
            entryTime = ticket.Entry_Time,
            durationHours = Math.Round((now - ticket.Entry_Time).TotalHours, 2),
            amountDue = Math.Max(Tariff.FeeFor(ticket.Entry_Time, now) - paid, 0),
            amountPaid = paid
        });
    }

    // ------------------------------------------------------------------
    // GET /api/gate/log?limit=50 — recent gate decisions (demo / audit)
    // ------------------------------------------------------------------
    [HttpGet("log")]
    public async Task<IActionResult> RecentLog(int limit = 50) =>
        Ok(await _db.GateLog.OrderByDescending(x => x.Id).Take(Math.Clamp(limit, 1, 500)).ToListAsync());
}
