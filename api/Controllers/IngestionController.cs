using Microsoft.AspNetCore.Mvc;
using ParkingApiPg.Data;
using ParkingApiPg.Models;
namespace ParkingApiPg.Controllers;

[ApiController]
[Route("api/parking")]
public class IngestionController : ControllerBase
{
    private readonly ParkingDbContext _db;
    public IngestionController(ParkingDbContext db) => _db = db;

    [HttpPost("entry")]
    public async Task<IActionResult> Entry(EntryDto dto)
    {
        var ticket = "T" + Guid.NewGuid().ToString("N")[..8].ToUpper();
        var t = new LiveParking
        {
            Ticket_ID = ticket,
            Vehicle_ID = dto.VehicleId,
            Entry_Time = DateTime.Now,
            Parking_Level = dto.ParkingLevel,
            Vehicle_Type = dto.VehicleType,
            Payment_Type = dto.PaymentType,
            Event_Status = dto.EventStatus,
            Event_Name = dto.EventName
        };
        _db.LiveParking.Add(t);
        await _db.SaveChangesAsync();
        return Ok(new { ticketId = ticket, t.Id, message = "entry recorded" });
    }

    [HttpPost("exit")]
    public async Task<IActionResult> Exit(ExitDto dto)
    {
        var t = _db.LiveParking
            .Where(x => x.Ticket_ID == dto.TicketId && x.Exit_Time == null)
            .FirstOrDefault();
        if (t == null) return NotFound(new { error = "no open ticket found" });
        t.Exit_Time = DateTime.Now;
        t.Parking_Fee = dto.ParkingFee;
        t.Parking_Duration_Hours = (t.Exit_Time.Value - t.Entry_Time).TotalHours;
        await _db.SaveChangesAsync();
        return Ok(new { t.Ticket_ID, t.Parking_Duration_Hours, message = "exit recorded" });
    }
}
