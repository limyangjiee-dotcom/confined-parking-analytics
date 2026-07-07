using System.Text;
using System.Text.Json;
using Npgsql;
namespace ParkingApiPg.Services;

// AI summary / Q&A layer (supervisor request): turns the platform's aggregated
// analytics into plain-English insights via the Google Gemini API (free tier).
//
// PRIVACY BY DESIGN: only aggregated statistics (daily totals, mixes, forecasts)
// are ever sent to the AI — never raw sessions or plate numbers.
//
// The AI answers FROM the provided context only (no SQL generation), so it cannot
// invent numbers that are not in the analytics. Key comes from config "Ai:ApiKey"
// (put it in api/appsettings.Local.json — gitignored) or env GEMINI_API_KEY.
public class AiService
{
    private readonly string _cs;
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _cfg;

    public AiService(IConfiguration cfg, IHttpClientFactory http)
    { _cs = cfg.GetConnectionString("Default")!; _http = http; _cfg = cfg; }

    public string? ApiKey =>
        _cfg["Ai:ApiKey"].NullIfEmpty() ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY").NullIfEmpty();
    public string Model => _cfg["Ai:Model"].NullIfEmpty() ?? "gemini-2.0-flash";
    public bool Configured => ApiKey != null;

    // ---------------- context: a compact, aggregated snapshot ----------------
    public async Task<string> BuildContext(DateTime from, DateTime to)
    {
        await using var c = new NpgsqlConnection(_cs);
        await c.OpenAsync();
        var sb = new StringBuilder();
        sb.AppendLine($"DATA CONTEXT (aggregated parking analytics, {from:yyyy-MM-dd} to {to:yyyy-MM-dd}):");

        async Task Section(string title, string sql)
        {
            try
            {
                await using var cmd = new NpgsqlCommand(sql, c);
                cmd.Parameters.AddWithValue("f", from); cmd.Parameters.AddWithValue("t", to);
                await using var rd = await cmd.ExecuteReaderAsync();
                var rows = new List<string>();
                while (await rd.ReadAsync())
                {
                    var parts = new List<string>();
                    for (int i = 0; i < rd.FieldCount; i++)
                        parts.Add($"{rd.GetName(i)}={(rd.IsDBNull(i) ? "-" : rd.GetValue(i))}");
                    rows.Add(string.Join(", ", parts));
                }
                if (rows.Count > 0)
                {
                    sb.AppendLine($"\n## {title}");
                    foreach (var r in rows) sb.AppendLine("- " + r);
                }
            }
            catch { /* table may be empty on a fresh install — skip section */ }
        }

        await Section("Overall KPIs", @"
            SELECT SUM(""Total_Vehicles"")::bigint AS total_vehicles,
                   ROUND(SUM(""Total_Revenue"")::numeric,0) AS total_revenue_rm,
                   ROUND(AVG(""Occupancy_Rate_%"")::numeric,1) AS avg_peak_occupancy_pct,
                   COUNT(*) FILTER (WHERE ""Occupancy_Rate_%"">=100) AS days_at_capacity,
                   SUM(""Event_Flag"")::int AS event_days, COUNT(*) AS days_covered
            FROM ""Daily_Summary"" WHERE ""Entry_Date"" BETWEEN @f AND @t");

        await Section("Average vehicles by day of week", @"
            SELECT trim(""Day_Name"") AS day, ROUND(AVG(""Total_Vehicles"")::numeric,0) AS avg_vehicles,
                   ROUND(AVG(""Total_Revenue"")::numeric,0) AS avg_revenue_rm
            FROM ""Daily_Summary"" WHERE ""Entry_Date"" BETWEEN @f AND @t
            GROUP BY 1 ORDER BY AVG(""Total_Vehicles"") DESC");

        await Section("Busiest hours (avg arrivals)", @"
            SELECT ""Entry_Hour"" AS hour, ROUND(AVG(""Vehicle_Count"")::numeric,0) AS avg_arrivals
            FROM ""Hourly_Summary"" WHERE ""Entry_Date"" BETWEEN @f AND @t
            GROUP BY 1 ORDER BY 2 DESC LIMIT 5");

        await Section("Event days vs normal days", @"
            SELECT CASE WHEN ""Event_Flag""=1 THEN 'event day' ELSE 'normal day' END AS day_kind,
                   COUNT(*) AS days, ROUND(AVG(""Total_Vehicles"")::numeric,0) AS avg_vehicles,
                   ROUND(AVG(""Total_Revenue"")::numeric,0) AS avg_revenue_rm
            FROM ""Daily_Summary"" WHERE ""Entry_Date"" BETWEEN @f AND @t GROUP BY 1");

        await Section("Vehicle mix", @"
            SELECT ""Vehicle_Type"" AS type, COUNT(*) AS sessions
            FROM ""Transactions_Cleaned"" WHERE ""Entry_Date"" BETWEEN @f AND @t
            GROUP BY 1 ORDER BY 2 DESC LIMIT 4");

        await Section("Payment mix", @"
            SELECT ""Payment_Type"" AS method, COUNT(*) AS sessions
            FROM ""Transactions_Cleaned"" WHERE ""Entry_Date"" BETWEEN @f AND @t
            GROUP BY 1 ORDER BY 2 DESC LIMIT 6");

        await Section("Driver segments (Resident >=12h stay; Worker weekday 5-11am arrival 5-12h stay; else Visitor)", @"
            SELECT CASE WHEN ""Parking_Duration_Hours"">=12 THEN 'Resident'
                        WHEN ""Is_Weekend""='Weekday' AND ""Entry_Hour"" BETWEEN 5 AND 11
                             AND ""Parking_Duration_Hours"" BETWEEN 5 AND 12 THEN 'Worker'
                        ELSE 'Visitor' END AS segment,
                   COUNT(*) AS sessions, ROUND(AVG(""Parking_Duration_Hours"")::numeric,1) AS avg_stay_hours
            FROM ""Transactions_Cleaned""
            WHERE ""Entry_Date"" BETWEEN @f AND @t AND ""Parking_Duration_Hours"" IS NOT NULL
            GROUP BY 1 ORDER BY 2 DESC");

        await Section("7-day demand forecast (ML)", @"
            SELECT ""Date"" AS date, ""Day_Name"" AS day, ""Day_Type"" AS day_type, ""Weather"" AS weather,
                   ""Predicted_Vehicles"" AS predicted_vehicles, ""Predicted_Revenue"" AS predicted_revenue_rm,
                   ""Prediction_Basis"" AS basis
            FROM ""Forecast_Daily_V2"" ORDER BY ""Date"" LIMIT 7");

        await Section("Upcoming calendar events", @"
            SELECT ""Event_Date"" AS date, ""Event_Name"" AS event
            FROM ""Event_Calendar"" WHERE ""Event_Date"" >= CURRENT_DATE ORDER BY 1 LIMIT 8");

        return sb.ToString();
    }

    // ---------------- prompts ----------------
    private static string SystemPreamble => """
        You are the built-in AI analyst of a parking analytics platform used by a
        building owner / parking operator in Malaysia (currency RM). Answer ONLY from
        the data context provided — never invent numbers. Be concise, plain-English,
        and business-focused. Format money as RM with thousand separators.
        """;

    public async Task<string> Summarize(string page, DateTime from, DateTime to)
    {
        var ctx = await BuildContext(from, to);
        var focus = page switch
        {
            "occupancy" => "Focus on occupancy patterns: busiest days and hours, days at capacity, the daily rhythm.",
            "revenue"   => "Focus on revenue: totals, which days/hours earn most, payment mix.",
            "vehicle"   => "Focus on the vehicle mix and stay durations.",
            "behaviour" => "Focus on the driver segments (workers / visitors / residents) and what they mean for the operator.",
            "events"    => "Focus on how event days compare to normal days (traffic and revenue lift).",
            "forecast"  => "Focus on the 7-day forecast: which days will be busy and why, upcoming events, weather. " +
                           "End with 2-3 concrete operational recommendations (staffing, preparation).",
            "realtime"  => "Give a short operational status summary.",
            _           => "Give an executive overview: overall performance, key patterns, anything notable.",
        };
        var prompt = $"{SystemPreamble}\n\n{ctx}\n\nTASK: Write a short summary (4-7 sentences, may use up to 5 bullet points). {focus}";
        return await CallGemini(prompt);
    }

    public async Task<string> Ask(string question, DateTime from, DateTime to)
    {
        var ctx = await BuildContext(from, to);
        var prompt = $"{SystemPreamble}\n\n{ctx}\n\nUSER QUESTION: {question}\n\n" +
                     "Answer in 1-4 sentences using only the context above. " +
                     "If the context does not contain the answer, say so plainly and suggest which dashboard page may help.";
        return await CallGemini(prompt);
    }

    // ---------------- Gemini call ----------------
    private async Task<string> CallGemini(string prompt)
    {
        if (!Configured)
            return "AI is not configured yet — add your free Gemini API key to api/appsettings.Local.json (see README).";
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{Model}:generateContent?key={ApiKey}";
        var body = JsonSerializer.Serialize(new
        {
            contents = new[] { new { role = "user", parts = new[] { new { text = prompt } } } },
            generationConfig = new { temperature = 0.4, maxOutputTokens = 800 }
        });
        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(45);
        using var resp = await client.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"));
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Gemini API error {(int)resp.StatusCode}: {Snippet(text)}");
        using var doc = JsonDocument.Parse(text);
        var answer = doc.RootElement.GetProperty("candidates")[0]
                        .GetProperty("content").GetProperty("parts")[0]
                        .GetProperty("text").GetString();
        return answer?.Trim() ?? "(empty response)";
    }

    private static string Snippet(string s) => s.Length > 300 ? s[..300] : s;
}

internal static class AiStringExt
{
    public static string? NullIfEmpty(this string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
