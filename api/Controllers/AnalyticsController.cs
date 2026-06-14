using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ParkingApiPg.Data;
namespace ParkingApiPg.Controllers;

[ApiController]
[Route("api")]
public class AnalyticsController : ControllerBase
{
    private readonly ParkingDbContext _db;
    private readonly ParkingApiPg.Services.SettingsService _settings;
    public AnalyticsController(ParkingDbContext db, ParkingApiPg.Services.SettingsService settings)
    { _db = db; _settings = settings; }

    [HttpGet("occupancy")]
    public async Task<IActionResult> Occupancy()
    {
        var capacity = _settings.Capacity;
        var inside = await _db.LiveParking.CountAsync(x => x.Exit_Time == null);
        return Ok(new
        {
            timestamp = DateTime.Now,
            occupied = inside,
            capacity,
            occupancyRatePct = Math.Round(100.0 * inside / capacity, 2)
        });
    }

    [HttpGet("levels")]
    public async Task<IActionResult> Levels()
    {
        var rows = await _db.LiveParking.Where(x => x.Exit_Time == null)
            .GroupBy(x => x.Parking_Level)
            .Select(g => new { level = g.Key, occupied = g.Count() })
            .OrderBy(r => r.level).ToListAsync();
        return Ok(rows);
    }

    [HttpGet("daily")]
    public async Task<IActionResult> Daily()
    {
        var rows = await _db.LiveParking
            .GroupBy(x => x.Entry_Time.Date)
            .Select(g => new { date = g.Key, vehicles = g.Count(), revenue = g.Sum(x => x.Parking_Fee) })
            .OrderBy(r => r.date).ToListAsync();
        return Ok(rows);
    }
}
