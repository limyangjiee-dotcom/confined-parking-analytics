using Microsoft.AspNetCore.Mvc;
using Npgsql;
using ParkingApiPg.Models;
using ParkingApiPg.Services;
namespace ParkingApiPg.Controllers;

// ============================================================
//  External parking-system connector (INBOUND / read-only).
//  Connects to a third-party parking system — either its DATABASE
//  (PostgreSQL / MySQL / SQL Server) or its REST API — discovers the
//  schema, maps fields to our analytics model, and pulls data in.
//  All the source logic lives in IngestionService (shared with the
//  scheduled background sync).
// ============================================================
[ApiController]
[Route("api/connector")]
public class ConnectorController : ControllerBase
{
    private readonly string _ownCs;
    private readonly IngestionService _ingestion;
    public ConnectorController(IConfiguration cfg, IngestionService ingestion)
    { _ownCs = cfg.GetConnectionString("Default")!; _ingestion = ingestion; }

    [HttpPost("test")]
    public async Task<IActionResult> Test([FromBody] DsConfig cfg)
    {
        var (ok, msg, extra) = await _ingestion.Test(cfg);
        return Ok(new { ok, message = ok ? msg : msg, extra });
    }

    [HttpPost("discover")]
    public async Task<IActionResult> Discover([FromBody] DsConfig cfg)
    {
        var (ok, msg, tables) = await _ingestion.Discover(cfg);
        return Ok(new { ok, message = msg, tables });
    }

    [HttpGet("config")]
    public async Task<IActionResult> GetConfig()
    {
        var (cfg, lastSync, lastStatus) = await _ingestion.ReadConfig();
        if (cfg == null)
            return Ok(new { configured = false, config = new DsConfig(new SourceConn(), new Mapping(), "database", new ApiSource()), lastSync, lastStatus });
        var conn = cfg.Connection ?? new SourceConn();
        var api = cfg.Api ?? new ApiSource();
        var masked = cfg with
        {
            Connection = conn with { Password = string.IsNullOrEmpty(conn.Password) ? "" : "********" },
            Api = api with { AuthValue = string.IsNullOrEmpty(api.AuthValue) ? "" : "********" }
        };
        return Ok(new { configured = true, config = masked, lastSync, lastStatus });
    }

    [HttpPost("config")]
    public async Task<IActionResult> SaveConfig([FromBody] DsConfig cfg)
    {
        await _ingestion.SaveConfig(cfg);
        return Ok(new { ok = true, message = "Data source configuration saved" });
    }

    [HttpPost("sync")]
    public async Task<IActionResult> Sync()
    {
        var r = await _ingestion.Sync();
        return Ok(new { ok = r.Ok, read = r.Read, matched = r.Matched, inserted = r.Inserted, message = r.Message });
    }

    [HttpGet("status")]
    public async Task<IActionResult> Status()
    {
        var (cfg, lastSync, lastStatus) = await _ingestion.ReadConfig();
        await using var c = new NpgsqlConnection(_ownCs);
        await c.OpenAsync();
        var imported = (long)(await new NpgsqlCommand(
            @"SELECT COUNT(*) FROM ""Live_Parking"" WHERE ""Payment_Type""='Imported'", c).ExecuteScalarAsync() ?? 0L);
        var preview = new List<object>();
        await using (var rd = await new NpgsqlCommand(@"
            SELECT ""Ticket_ID"",""Vehicle_ID"",""Entry_Time"",""Exit_Time"",""Parking_Fee"",""Parking_Level"",""Vehicle_Type""
            FROM ""Live_Parking"" WHERE ""Payment_Type""='Imported'
            ORDER BY ""Entry_Time"" DESC LIMIT 12", c).ExecuteReaderAsync())
            while (await rd.ReadAsync())
                preview.Add(new {
                    ticket = rd.GetString(0), plate = rd.GetString(1),
                    entry = rd.GetValue(2), exit = await rd.IsDBNullAsync(3) ? null : rd.GetValue(3),
                    fee = rd.GetValue(4), level = rd.GetValue(5), vehicleType = rd.GetValue(6) });
        return Ok(new { configured = cfg != null, sourceType = cfg?.SourceType ?? "database", lastSync, lastStatus, importedTotal = imported, preview });
    }
}
