using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ParkingApiPg.Data;
using ParkingApiPg.Models;
using ParkingApiPg.Services;
namespace ParkingApiPg.Controllers;

[ApiController]
[Route("api/forecast")]
public class ForecastController : ControllerBase
{
    private readonly ParkingDbContext _db;
    private readonly ForecastService _forecast;
    public ForecastController(ParkingDbContext db, ForecastService forecast)
    { _db = db; _forecast = forecast; }

    // Serves the ML forecast (read from the Forecast_30Days table).
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var sql = @"SELECT ""Forecast_Date"" AS ""ForecastDate"",
                           ""Predicted_Occupancy_Rate_%"" AS ""PredictedOccupancy"",
                           ""Predicted_Revenue_RM"" AS ""PredictedRevenue""
                    FROM ""Forecast_30Days"" ORDER BY ""Forecast_Date""";
        var rows = await _db.Database.SqlQueryRaw<ForecastDto>(sql).ToListAsync();
        return Ok(rows);
    }

    // Run the ML forecast on demand (the "Run forecast now" button).
    [HttpPost("run")]
    public IActionResult Run()
    {
        var started = _forecast.RunNow();
        return Ok(new
        {
            started,
            running = _forecast.IsRunning,
            message = started ? "Forecast started — refreshing in the background."
                              : "A forecast is already running."
        });
    }

    // Poll this to know when the forecast has finished.
    [HttpGet("run-status")]
    public IActionResult RunStatus() => Ok(new
    {
        running = _forecast.IsRunning,
        lastFinishedAt = ForecastService.LastFinishedAt,
        lastOk = ForecastService.LastOk
    });
}
