using System.Data.Common;
using System.Text.Json;
using Npgsql;
using ParkingApiPg.Models;
namespace ParkingApiPg.Services;

// Pulls parking sessions from an external source — a database
// (PostgreSQL / MySQL / SQL Server) OR a REST API — normalizes them, loads
// them into Live_Parking, and rebuilds the analytics summaries. Shared by the
// manual /api/connector/sync endpoint and the scheduled background sync.
public class IngestionService
{
    private readonly string _ownCs;
    private readonly AggregationService _agg;
    private readonly IHttpClientFactory _http;
    private static readonly JsonSerializerOptions J = new() { PropertyNameCaseInsensitive = true };

    public IngestionService(IConfiguration cfg, AggregationService agg, IHttpClientFactory http)
    { _ownCs = cfg.GetConnectionString("Default")!; _agg = agg; _http = http; }

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
        var configured = cfg != null && (cfg.SourceType == "api"
            ? !string.IsNullOrWhiteSpace(cfg.Api?.Url)
            : !string.IsNullOrWhiteSpace(cfg.Connection?.Database));
        return (configured ? cfg : null, lastSync, lastStatus);
    }

    public async Task SaveConfig(DsConfig cfg)
    {
        if (cfg.Connection?.Password == "********")
        {
            var (existing, _, _) = await ReadConfig();
            if (existing?.Connection != null) cfg = cfg with { Connection = cfg.Connection with { Password = existing.Connection.Password } };
        }
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
            if (cfg.SourceType == "api")
            {
                var recs = await FetchApiRecords(cfg.Api!);
                return (true, $"Connected to API — {recs.Count} record(s) returned",
                        new { sampleKeys = recs.FirstOrDefault().ValueKind == JsonValueKind.Object
                            ? recs.First().EnumerateObject().Select(p => p.Name).ToArray() : Array.Empty<string>() });
            }
            await using var c = ConnectorDb.Create(cfg.Connection!);
            await c.OpenAsync();
            await using var cmd = c.CreateCommand();
            cmd.CommandText = ConnectorDb.VersionSql(cfg.Connection!.Engine);
            var v = (await cmd.ExecuteScalarAsync())?.ToString();
            return (true, "Connected to external parking database", new { server = v });
        }
        catch (Exception e) { return (false, e.Message, null); }
    }

    // ---------------- discover ----------------
    public async Task<(bool ok, string msg, object tables)> Discover(DsConfig cfg)
    {
        try
        {
            if (cfg.SourceType == "api")
            {
                var recs = await FetchApiRecords(cfg.Api!);
                var cols = recs.FirstOrDefault().ValueKind == JsonValueKind.Object
                    ? recs.First().EnumerateObject().Select(p => (object)new { name = p.Name, type = p.Value.ValueKind.ToString().ToLower() }).ToList()
                    : new List<object>();
                return (true, $"{recs.Count} record(s)", new[] { new { table = "(API response)", columns = cols } });
            }
            await using var c = ConnectorDb.Create(cfg.Connection!);
            await c.OpenAsync();
            await using var cmd = c.CreateCommand();
            cmd.CommandText = ConnectorDb.DiscoverSql(cfg.Connection!.Engine);
            await using var rd = await cmd.ExecuteReaderAsync();
            var tables = new Dictionary<string, List<object>>();
            while (await rd.ReadAsync())
            {
                var t = rd.GetString(0);
                if (!tables.TryGetValue(t, out var cols)) tables[t] = cols = new();
                cols.Add(new { name = rd.GetString(1), type = rd.GetString(2) });
            }
            return (true, $"{tables.Count} table(s)", tables.Select(kv => new { table = kv.Key, columns = kv.Value }));
        }
        catch (Exception e) { return (false, e.Message, Array.Empty<object>()); }
    }

    // ---------------- sync ----------------
    public async Task<SyncResult> Sync()
    {
        var (cfg, _, _) = await ReadConfig();
        if (cfg == null) return new(false, 0, 0, 0, "No data source configured yet");
        List<SessionRow> rows;
        int read;
        try
        {
            rows = cfg.SourceType == "api" ? await FetchApiRows(cfg) : await FetchDbRows(cfg);
            read = rows.Count;
        }
        catch (Exception e)
        {
            await SetStatus("ERROR — " + e.Message);
            return new(false, 0, 0, 0, e.Message);
        }

        int inserted = await IngestRows(rows);
        var (days, _) = await _agg.Rebuild();
        var via = cfg.SourceType == "api" ? "API" : cfg.Connection!.Engine;
        await SetStatus($"OK ({via}) — read {read}, imported {inserted} new; aggregated {days} day(s)");
        return new(true, read, rows.Count, inserted,
            $"Imported {inserted} new sessions via {via} and aggregated {days} day(s)");
    }

    // ---------------- DB fetch (multi-engine) ----------------
    private async Task<List<SessionRow>> FetchDbRows(DsConfig cfg)
    {
        var conn = cfg.Connection!; var m = cfg.Mapping; var eng = conn.Engine;
        if (!ConnectorSupport.Ident(m.SourceTable) || !ConnectorSupport.Ident(m.Plate) || !ConnectorSupport.Ident(m.EntryTime))
            throw new Exception("Mapping needs a valid source table, plate column and entry-time column");

        string Col(string s) => ConnectorSupport.Ident(s) ? ConnectorDb.Q(eng, s) : "NULL";
        var sql = $@"SELECT {ConnectorDb.Q(eng, m.Plate)} AS plate, {ConnectorDb.Q(eng, m.EntryTime)} AS entry,
                            {Col(m.ExitTime)} AS exitt, {Col(m.Fee)} AS fee,
                            {Col(m.Level)} AS lvl, {Col(m.VehicleType)} AS vtype
                     FROM {ConnectorDb.Q(eng, m.SourceTable)}";

        await using var c = ConnectorDb.Create(conn);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await using var rd = await cmd.ExecuteReaderAsync();
        var rows = new List<SessionRow>();
        while (await rd.ReadAsync())
        {
            if (await rd.IsDBNullAsync(0) || await rd.IsDBNullAsync(1)) continue;
            rows.Add(new SessionRow(
                rd.GetValue(0)?.ToString() ?? "",
                Convert.ToDateTime(rd.GetValue(1)),
                await rd.IsDBNullAsync(2) ? null : Convert.ToDateTime(rd.GetValue(2)),
                await rd.IsDBNullAsync(3) ? 0m : Convert.ToDecimal(rd.GetValue(3)),
                await rd.IsDBNullAsync(4) ? "" : rd.GetValue(4).ToString() ?? "",
                await rd.IsDBNullAsync(5) ? "Car" : rd.GetValue(5).ToString() ?? "Car"));
        }
        return rows;
    }

    // ---------------- API fetch ----------------
    private async Task<List<JsonElement>> FetchApiRecords(ApiSource api)
    {
        if (string.IsNullOrWhiteSpace(api.Url)) throw new Exception("API URL is required");
        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);
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
    private async Task<int> IngestRows(List<SessionRow> rows)
    {
        await using var own = new NpgsqlConnection(_ownCs);
        await own.OpenAsync();
        int inserted = 0;
        foreach (var r in rows)
        {
            await using var ins = new NpgsqlCommand(@"
                INSERT INTO ""Live_Parking""
                  (""Ticket_ID"",""Vehicle_ID"",""Entry_Time"",""Exit_Time"",""Parking_Fee"",
                   ""Parking_Level"",""Vehicle_Type"",""Payment_Type"",""Parking_Duration_Hours"",
                   ""Event_Status"",""Event_Name"")
                SELECT @tid,@plate,@entry,@exit,@fee,@level,@vtype,'Imported',@dur,'Non-Event Day',''
                WHERE NOT EXISTS (SELECT 1 FROM ""Live_Parking""
                                  WHERE ""Vehicle_ID""=@plate AND ""Entry_Time""=@entry)", own);
            ins.Parameters.AddWithValue("tid", "IMP" + Guid.NewGuid().ToString("N")[..8].ToUpper());
            ins.Parameters.AddWithValue("plate", r.Plate);
            ins.Parameters.AddWithValue("entry", r.Entry);
            ins.Parameters.AddWithValue("exit", (object?)r.Exit ?? DBNull.Value);
            ins.Parameters.AddWithValue("fee", r.Fee);
            ins.Parameters.AddWithValue("level", r.Level);
            ins.Parameters.AddWithValue("vtype", r.VehicleType);
            ins.Parameters.AddWithValue("dur", r.Exit.HasValue ? (object)(r.Exit.Value - r.Entry).TotalHours : DBNull.Value);
            inserted += await ins.ExecuteNonQueryAsync();
        }
        return inserted;
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
