using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using ParkingApiPg.Models;
namespace ParkingApiPg.Services;

// Pulls parking sessions from an external parking system's REST API,
// normalizes them, loads them into Live_Parking, and rebuilds the analytics
// summaries. Shared by the manual /api/connector/sync endpoint and the
// scheduled background sync.
public class IngestionService
{
    private readonly string _ownCs;
    private readonly AggregationService _agg;
    private readonly IHttpClientFactory _http;
    private readonly ForecastService _forecast;
    private static readonly JsonSerializerOptions J = new() { PropertyNameCaseInsensitive = true };

    public IngestionService(IConfiguration cfg, AggregationService agg, IHttpClientFactory http, ForecastService forecast)
    { _ownCs = cfg.GetConnectionString("Default")!; _agg = agg; _http = http; _forecast = forecast; }

    // ---------------- config ----------------
    public async Task<(DsConfig? cfg, DateTime? lastSync, string lastStatus)> ReadConfig()
    {
        await using var c = new NpgsqlConnection(_ownCs);
        await c.OpenAsync();
        await using var rd = await new NpgsqlCommand(
            @"SELECT ""Config_Json"",""Last_Sync"",""Last_Status"" FROM ""Data_Source_Config"" WHERE ""Id""=1", c).ExecuteReaderAsync();
        if (!await rd.ReadAsync()) return (null, null, "never synced");
        var json = rd.GetString(0);
        var lastSync = await rd.IsDBNullAsync(1) ? (DateTime?)null : rd.GetDateTime(1);
        var lastStatus = rd.GetString(2);
        DsConfig? cfg = null;
        try { cfg = JsonSerializer.Deserialize<DsConfig>(json, J); } catch { }
        var configured = cfg != null && !string.IsNullOrWhiteSpace(cfg.Api?.Url);
        return (configured ? cfg : null, lastSync, lastStatus);
    }

    public async Task SaveConfig(DsConfig cfg)
    {
        if (cfg.Api?.AuthValue == "********")
        {
            var (existing, _, _) = await ReadConfig();
            if (existing?.Api != null) cfg = cfg with { Api = cfg.Api with { AuthValue = existing.Api.AuthValue } };
        }
        var json = JsonSerializer.Serialize(cfg);
        await using var c = new NpgsqlConnection(_ownCs);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO ""Data_Source_Config"" (""Id"", ""Config_Json"") VALUES (1, @j)
            ON CONFLICT (""Id"") DO UPDATE SET ""Config_Json"" = @j", c);
        cmd.Parameters.AddWithValue("j", json);
        await cmd.ExecuteNonQueryAsync();
    }

    // ---------------- test ----------------
    public async Task<(bool ok, string msg, object? extra)> Test(DsConfig cfg)
    {
        try
        {
            var recs = await FetchApiRecords(cfg.Api!);
            return (true, $"Connected to API — {recs.Count} record(s) returned",
                    new { sampleKeys = recs.FirstOrDefault().ValueKind == JsonValueKind.Object
                        ? recs.First().EnumerateObject().Select(p => p.Name).ToArray() : Array.Empty<string>() });
        }
        catch (Exception e) { return (false, e.Message, null); }
    }

    // ---------------- discover ----------------
    public async Task<(bool ok, string msg, object tables)> Discover(DsConfig cfg)
    {
        try
        {
            var recs = await FetchApiRecords(cfg.Api!);
            var cols = recs.FirstOrDefault().ValueKind == JsonValueKind.Object
                ? recs.First().EnumerateObject().Select(p => (object)new { name = p.Name, type = p.Value.ValueKind.ToString().ToLower() }).ToList()
                : new List<object>();
            return (true, $"{recs.Count} record(s)", new[] { new { table = "(API response)", columns = cols } });
        }
        catch (Exception e) { return (false, e.Message, Array.Empty<object>()); }
    }

    // ---------------- sync ----------------
    public async Task<SyncResult> Sync()
    {
        var (cfg, _, _) = await ReadConfig();
        if (cfg == null) return new(false, 0, 0, 0, "No data source configured yet");
        List<SessionRow> rows;
        try { rows = await FetchApiRows(cfg); }
        catch (Exception e) { await SetStatus("ERROR — " + e.Message); return new(false, 0, 0, 0, e.Message); }
        return await ImportSessions(rows, "API");
    }

    // Shared load path: ingest a batch of normalized sessions, rebuild the summary
    // tables, and refresh the ML forecast. Used by the API sync AND the CSV file import,
    // so any source (API of any shape, or a spreadsheet) flows through the same pipeline.
    public async Task<SyncResult> ImportSessions(List<SessionRow> rows, string via)
    {
        int read = rows.Count;
        int inserted = await IngestRows(rows);
        var (days, _) = await _agg.Rebuild();
        var forecastStarted = _forecast.TriggerInBackground();
        await SetStatus($"OK ({via}) — read {read}, imported {inserted} new; aggregated {days} day(s)"
                        + (forecastStarted ? "; forecast refreshing" : ""));
        return new(true, read, rows.Count, inserted,
            $"Imported {inserted} new sessions via {via} and aggregated {days} day(s)"
            + (forecastStarted ? ". Forecast is refreshing in the background." : "."));
    }

    // ---------------- API fetch ----------------
    private async Task<List<JsonElement>> FetchApiRecords(ApiSource api)
    {
        if (string.IsNullOrWhiteSpace(api.Url)) throw new Exception("API URL is required");
        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(120);   // allow large operator-history pulls
        using var req = new HttpRequestMessage(
            api.Method?.ToUpper() == "POST" ? HttpMethod.Post : HttpMethod.Get, api.Url);
        if (!string.IsNullOrWhiteSpace(api.AuthHeader))
            req.Headers.TryAddWithoutValidation(api.AuthHeader, api.AuthValue);
        using var resp = await client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var text = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(text);

        // navigate to the records array (RecordsPath = dotted, or empty for root array)
        JsonElement node = doc.RootElement;
        if (!string.IsNullOrWhiteSpace(api.RecordsPath))
            foreach (var part in api.RecordsPath.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(part, out var next))
                    throw new Exception($"records path '{api.RecordsPath}' not found in API response");
                node = next;
            }
        if (node.ValueKind != JsonValueKind.Array)
            throw new Exception("API response is not a JSON array (set the records path if it is nested, e.g. 'data')");
        // clone elements so they survive after the JsonDocument is disposed
        return node.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    private async Task<List<SessionRow>> FetchApiRows(DsConfig cfg)
    {
        var m = cfg.Mapping;
        if (string.IsNullOrWhiteSpace(m.Plate) || string.IsNullOrWhiteSpace(m.EntryTime))
            throw new Exception("Mapping needs at least the plate and entry-time JSON fields");
        var recs = await FetchApiRecords(cfg.Api!);
        var rows = new List<SessionRow>();
        foreach (var el in recs)
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var plate = Str(el, m.Plate);
            var entry = Dt(el, m.EntryTime);
            if (plate == null || entry == null) continue;
            rows.Add(new SessionRow(plate, entry.Value, Dt(el, m.ExitTime),
                Dec(el, m.Fee), Str(el, m.Level) ?? "", Str(el, m.VehicleType) ?? "Car"));
        }
        return rows;
    }

    private static JsonElement? Prop(JsonElement el, string key)
        => !string.IsNullOrWhiteSpace(key) && el.TryGetProperty(key, out var v) ? v : (JsonElement?)null;
    private static string? Str(JsonElement el, string key)
    {
        var p = Prop(el, key); if (p == null) return null;
        return p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.ToString();
    }
    private static DateTime? Dt(JsonElement el, string key)
    {
        var s = Str(el, key);
        return DateTime.TryParse(s, out var d) ? d : (DateTime?)null;
    }
    private static decimal Dec(JsonElement el, string key)
    {
        var p = Prop(el, key); if (p == null) return 0m;
        if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetDecimal(out var d)) return d;
        return decimal.TryParse(p.Value.ToString(), out var d2) ? d2 : 0m;
    }

    // ---------------- shared ingest ----------------
    // Bulk-load the batch via binary COPY into a temp table, then do ONE
    // set-based, deduped insert into Live_Parking. This scales to large
    // operator histories (hundreds of thousands of rows) far better than
    // row-by-row inserts.
    private async Task<int> IngestRows(List<SessionRow> rows)
    {
        if (rows.Count == 0) return 0;
        await using var own = new NpgsqlConnection(_ownCs);
        await own.OpenAsync();

        await using (var create = new NpgsqlCommand(
            @"CREATE TEMP TABLE _imp (plate text, entry timestamp, exitt timestamp NULL,
                                      fee numeric, lvl text, vtype text)", own))
            await create.ExecuteNonQueryAsync();

        await using (var importer = await own.BeginBinaryImportAsync(
            "COPY _imp (plate, entry, exitt, fee, lvl, vtype) FROM STDIN (FORMAT BINARY)"))
        {
            foreach (var r in rows)
            {
                await importer.StartRowAsync();
                await importer.WriteAsync(r.Plate, NpgsqlDbType.Text);
                await importer.WriteAsync(r.Entry, NpgsqlDbType.Timestamp);
                if (r.Exit.HasValue) await importer.WriteAsync(r.Exit.Value, NpgsqlDbType.Timestamp);
                else await importer.WriteNullAsync();
                await importer.WriteAsync(r.Fee, NpgsqlDbType.Numeric);
                await importer.WriteAsync(r.Level, NpgsqlDbType.Text);
                await importer.WriteAsync(r.VehicleType, NpgsqlDbType.Text);
            }
            await importer.CompleteAsync();
        }

        // dedup within the batch (plate+entry) and against rows already imported
        await using var ins = new NpgsqlCommand(@"
            INSERT INTO ""Live_Parking""
              (""Ticket_ID"",""Vehicle_ID"",""Entry_Time"",""Exit_Time"",""Parking_Fee"",
               ""Parking_Level"",""Vehicle_Type"",""Payment_Type"",""Parking_Duration_Hours"",
               ""Event_Status"",""Event_Name"")
            SELECT 'IMP' || upper(substr(md5(random()::text || d.plate || d.entry::text), 1, 8)),
                   d.plate, d.entry, d.exitt, d.fee, d.lvl, d.vtype, 'Imported',
                   CASE WHEN d.exitt IS NULL THEN NULL
                        ELSE EXTRACT(EPOCH FROM (d.exitt - d.entry)) / 3600.0 END,
                   'Non-Event Day', ''
            FROM (SELECT DISTINCT ON (plate, entry) plate, entry, exitt, fee, lvl, vtype
                  FROM _imp ORDER BY plate, entry) d
            WHERE NOT EXISTS (SELECT 1 FROM ""Live_Parking"" lp
                              WHERE lp.""Vehicle_ID"" = d.plate AND lp.""Entry_Time"" = d.entry)", own);
        return await ins.ExecuteNonQueryAsync();
    }

    public async Task SetStatus(string status)
    {
        await using var c = new NpgsqlConnection(_ownCs);
        await c.OpenAsync();
        await new NpgsqlCommand(
            @"UPDATE ""Data_Source_Config"" SET ""Last_Sync""=now(), ""Last_Status""=@s WHERE ""Id""=1",
            c) { Parameters = { new("s", status) } }.ExecuteNonQueryAsync();
    }
}
