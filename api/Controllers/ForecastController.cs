using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ParkingApiPg.Data;
using ParkingApiPg.Models;
namespace ParkingApiPg.Controllers;

[ApiController]
[Route("api/forecast")]
public class ForecastController : ControllerBase
{
    private readonly ParkingDbContext _db;
    public ForecastController(ParkingDbContext db) => _db = db;

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
}
