using Microsoft.AspNetCore.Mvc;
using Npgsql;
namespace ParkingApiPg.Controllers;

// Serves the analytics tables as JSON so the web dashboard can chart them.
[ApiController]
[Route("api/data")]
public class DataController : ControllerBase
{
    private readonly string _cs;
    public DataController(IConfiguration cfg) => _cs = cfg.GetConnectionString("Default")!;

    static readonly HashSet<string> Allow = new(StringComparer.OrdinalIgnoreCase)
    {
        "Daily_Summary","Hourly_Summary","Hourly_Occupancy","Monthly_Summary",
        "Level_Summary","Event_Summary","Event_Log_Table","Forecast_30Days","Model_Comparison",
        "Forecast_Daily_V2","Forecast_Hourly_V2","Model_Comparison_V2","Model_Comparison_Hourly_V2",
        "Event_Calendar"
    };
    static readonly Dictionary<string,string> Special = new(StringComparer.OrdinalIgnoreCase)
    {
        ["vehicle_mix"]    = "SELECT \"Vehicle_Type\" AS label, COUNT(*) AS value FROM \"Transactions_Cleaned\" GROUP BY \"Vehicle_Type\" ORDER BY value DESC",
        ["payment_mix"]    = "SELECT \"Payment_Type\" AS label, COUNT(*) AS value FROM \"Transactions_Cleaned\" GROUP BY \"Payment_Type\" ORDER BY value DESC",
        ["duration_by_type"]= "SELECT \"Vehicle_Type\" AS label, ROUND(AVG(\"Parking_Duration_Hours\")::numeric,2) AS value FROM \"Transactions_Cleaned\" GROUP BY \"Vehicle_Type\"",
        // live vehicle mix of cars currently inside (Live_Parking, not the 2.5% sample)
        ["live_vehicle_mix"] = "SELECT \"Vehicle_Type\" AS label, COUNT(*) AS value FROM \"Live_Parking\" WHERE \"Exit_Time\" IS NULL GROUP BY 1 ORDER BY 2 DESC"
    };

    [HttpGet("{name}")]
    public async Task<IActionResult> Get(string name)
    {
        string sql;
        if (Special.TryGetValue(name, out var sp)) sql = sp;
        else if (Allow.Contains(name)) sql = $"SELECT * FROM \"{name}\"";
        else return BadRequest(new { error = "unknown dataset" });

        await using var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var rd = await cmd.ExecuteReaderAsync();
        var rows = new List<Dictionary<string, object?>>();
        while (await rd.ReadAsync())
        {
            var row = new Dictionary<string, object?>();
            for (int i = 0; i < rd.FieldCount; i++)
                row[rd.GetName(i)] = await rd.IsDBNullAsync(i) ? null : rd.GetValue(i);
            rows.Add(row);
        }
        return Ok(rows);
    }
}
