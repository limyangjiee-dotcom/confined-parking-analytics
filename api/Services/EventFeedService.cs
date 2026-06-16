using System.Globalization;
using System.Text;
using Npgsql;
namespace ParkingApiPg.Services;

// Imports planned events from an iCalendar (.ics) feed — e.g. a public Google
// Calendar — into the Event_Calendar table, so the ML forecast becomes aware of
// upcoming event days. Self-contained: a tiny VEVENT parser (no external deps).
// One parsed event = one (Event_Date, Event_Name) row; multi-day events expand.
public class EventFeedService
{
    private readonly string _cs;
    private readonly IHttpClientFactory _http;
    private readonly ForecastService _forecast;

    public EventFeedService(IConfiguration cfg, IHttpClientFactory http, ForecastService forecast)
    { _cs = cfg.GetConnectionString("Default")!; _http = http; _forecast = forecast; }

    public record CalEvent(DateTime Date, string Name);

    // ---------- settings (stored in App_Settings) ----------
    public async Task<string?> GetSetting(string key)
    {
        await using var c = new NpgsqlConnection(_cs);
        await c.OpenAsync();
        return (await new NpgsqlCommand(
            @"SELECT ""Value"" FROM ""App_Settings"" WHERE ""Key""=@k", c)
            { Parameters = { new("k", key) } }.ExecuteScalarAsync()) as string;
    }

    private async Task SetSetting(string key, string value)
    {
        await using var c = new NpgsqlConnection(_cs);
        await c.OpenAsync();
        await new NpgsqlCommand(
            @"INSERT INTO ""App_Settings"" (""Key"",""Value"") VALUES (@k,@v)
              ON CONFLICT (""Key"") DO UPDATE SET ""Value""=@v", c)
            { Parameters = { new("k", key), new("v", value) } }.ExecuteNonQueryAsync();
    }

    // ---------- fetch + parse ----------
    public async Task<List<CalEvent>> Fetch(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) throw new Exception("iCal feed URL is required");
        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        var ics = await client.GetStringAsync(url);
        return Parse(ics);
    }

    // Minimal RFC-5545 VEVENT parser: line-unfolding + SUMMARY/DTSTART/DTEND.
    public static List<CalEvent> Parse(string ics)
    {
        // unfold: a CRLF (or LF) followed by a space/tab continues the previous line
        var unfolded = ics.Replace("\r\n", "\n").Replace("\n ", "").Replace("\n\t", "");
        var lines = unfolded.Split('\n');

        var events = new List<CalEvent>();
        bool inEvent = false;
        string? summary = null; DateTime? start = null, end = null; bool startIsDate = false;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line == "BEGIN:VEVENT") { inEvent = true; summary = null; start = end = null; startIsDate = false; continue; }
            if (line == "END:VEVENT")
            {
                if (inEvent && start.HasValue)
                {
                    var name = string.IsNullOrWhiteSpace(summary) ? "Event" : Unescape(summary!);
                    // expand multi-day events (DTEND is exclusive for all-day events)
                    var from = start.Value.Date;
                    var to = end?.Date ?? from;
                    if (startIsDate && end.HasValue && to > from) to = to.AddDays(-1);
                    if (to < from) to = from;
                    if ((to - from).TotalDays > 30) to = from.AddDays(30);   // safety cap
                    for (var d = from; d <= to; d = d.AddDays(1))
                        events.Add(new CalEvent(d, name));
                }
                inEvent = false; continue;
            }
            if (!inEvent) continue;

            var (key, prms, val) = SplitProp(line);
            switch (key)
            {
                case "SUMMARY": summary = val; break;
                case "DTSTART": start = ParseIcsDate(val, prms, out startIsDate); break;
                case "DTEND": end = ParseIcsDate(val, prms, out _); break;
            }
        }
        return events;
    }

    private static (string key, string prms, string val) SplitProp(string line)
    {
        int colon = line.IndexOf(':');
        if (colon < 0) return (line, "", "");
        var left = line[..colon];
        var val = line[(colon + 1)..];
        int semi = left.IndexOf(';');
        return semi < 0 ? (left.ToUpperInvariant(), "", val)
                        : (left[..semi].ToUpperInvariant(), left[(semi + 1)..], val);
    }

    private static DateTime? ParseIcsDate(string val, string prms, out bool isDateOnly)
    {
        isDateOnly = prms.Contains("VALUE=DATE", StringComparison.OrdinalIgnoreCase) || (val.Length == 8 && !val.Contains('T'));
        val = val.Trim();
        // DATE: 20260620   |   DATE-TIME: 20260620T100000 / ...Z
        if (val.Length >= 8 && DateTime.TryParseExact(val[..8], "yyyyMMdd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d;
        return null;
    }

    private static string Unescape(string s) => s
        .Replace("\\n", " ").Replace("\\N", " ")
        .Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\").Trim();

    // ---------- import ----------
    public async Task<(int imported, int parsed)> Import(string url)
    {
        var all = await Fetch(url);
        // keep today onward (the forecast cares about upcoming events) + dedup by date
        var today = DateTime.Today;
        var upcoming = all.Where(e => e.Date >= today.AddDays(-1))
                          .GroupBy(e => e.Date.Date)
                          .Select(g => g.First())
                          .ToList();

        await using var c = new NpgsqlConnection(_cs);
        await c.OpenAsync();
        int n = 0;
        foreach (var e in upcoming)
        {
            await using var cmd = new NpgsqlCommand(
                @"INSERT INTO ""Event_Calendar"" (""Event_Date"",""Event_Name"",""Expected_Scale"")
                  VALUES (@d,@n,'Medium')
                  ON CONFLICT (""Event_Date"") DO UPDATE SET ""Event_Name""=EXCLUDED.""Event_Name""", c);
            cmd.Parameters.AddWithValue("d", e.Date.Date);
            cmd.Parameters.AddWithValue("n", e.Name.Length > 200 ? e.Name[..200] : e.Name);
            n += await cmd.ExecuteNonQueryAsync();
        }
        await SetSetting("ical_url", url);
        await SetSetting("ical_last_status",
            $"OK — {DateTime.Now:yyyy-MM-dd HH:mm}: parsed {all.Count}, imported {upcoming.Count} upcoming");
        _forecast.TriggerInBackground();   // refresh the forecast so new event days show
        return (upcoming.Count, all.Count);
    }

    // ---------- preview (upcoming events in the table) ----------
    public async Task<List<object>> Upcoming(int limit = 20)
    {
        await using var c = new NpgsqlConnection(_cs);
        await c.OpenAsync();
        var list = new List<object>();
        await using var rd = await new NpgsqlCommand(
            @"SELECT ""Event_Date"",""Event_Name"",""Expected_Scale"" FROM ""Event_Calendar""
              WHERE ""Event_Date"" >= CURRENT_DATE - 1 ORDER BY ""Event_Date"" LIMIT @lim", c)
            { Parameters = { new("lim", Math.Clamp(limit, 1, 200)) } }.ExecuteReaderAsync();
        while (await rd.ReadAsync())
            list.Add(new { date = rd.GetDateTime(0), name = rd.GetString(1), scale = rd.GetString(2) });
        return list;
    }
}
