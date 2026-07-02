using Microsoft.AspNetCore.Mvc;
using Npgsql;
using ParkingApiPg.Models;
using ParkingApiPg.Services;
namespace ParkingApiPg.Controllers;

// ============================================================
//  External parking-system connector (INBOUND / read-only).
//  Connects to a third-party parking system via its REST API,
//  discovers the response fields, maps them to our analytics model,
//  and pulls data in. All the source logic lives in IngestionService
//  (shared with the scheduled background sync).
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
            return Ok(new { configured = false, config = new DsConfig(new Mapping(), new ApiSource()), lastSync, lastStatus });
        var api = cfg.Api ?? new ApiSource();
        var masked = cfg with
        {
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

    // Import already-mapped session rows (used by the CSV file import on the Data
    // Source page — the browser parses the CSV, maps its columns, and posts canonical rows).
    public record ImportRow(string Plate = "", string Entry = "", string Exit = "",
                            string Fee = "", string Level = "", string VehicleType = "");
    public record ImportBody(List<ImportRow>? Rows);

    [HttpPost("import-rows")]
    public async Task<IActionResult> ImportRows([FromBody] ImportBody body)
    {
        if (body?.Rows == null || body.Rows.Count == 0)
            return Ok(new { ok = false, message = "No rows provided" });
        var rows = new List<SessionRow>();
        foreach (var r in body.Rows)
        {
            if (string.IsNullOrWhiteSpace(r.Plate) || !DateTime.TryParse(r.Entry, out var entry)) continue;
            DateTime? exit = DateTime.TryParse(r.Exit, out var ex) ? ex : null;
            decimal fee = decimal.TryParse(r.Fee, out var f) ? f : 0m;
            rows.Add(new SessionRow(r.Plate.Trim(), entry, exit, fee,
                r.Level?.Trim() ?? "", string.IsNullOrWhiteSpace(r.VehicleType) ? "Car" : r.VehicleType.Trim()));
        }
        if (rows.Count == 0)
            return Ok(new { ok = false, message = "No valid rows — each needs at least a plate and a parseable entry time." });
        var res = await _ingestion.ImportSessions(rows, "CSV file");
        return Ok(new { ok = res.Ok, read = body.Rows.Count, inserted = res.Inserted, message = res.Message });
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
        return Ok(new { configured = cfg != null, lastSync, lastStatus, importedTotal = imported, preview });
    }
}
