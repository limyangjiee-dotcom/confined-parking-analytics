using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace ParkingApiPg.Controllers;

// Aggregated analytics endpoints powering the web dashboard charts.
// All filterable endpoints accept ?from=YYYY-MM-DD&to=YYYY-MM-DD (inclusive).
[ApiController]
[Route("api/dash")]
public class DashboardController : ControllerBase
{
    private readonly string _cs;
    public DashboardController(IConfiguration cfg) => _cs = cfg.GetConnectionString("Default")!;

    private static (DateTime f, DateTime t) Range(string? from, string? to)
    {
        var f = DateTime.TryParse(from, out var ff) ? ff : new DateTime(2000, 1, 1);
        var t = DateTime.TryParse(to, out var tt) ? tt : new DateTime(2100, 1, 1);
        return f <= t ? (f, t) : (t, f);
    }

    private async Task<List<Dictionary<string, object?>>> Query(string sql, string? from = null, string? to = null)
    {
        var (f, t) = Range(from, to);
        await using var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        // typed parameters: a bare DBNull/untyped value gives Npgsql no type info and
        // PostgreSQL rejects the statement ("could not determine data type of parameter")
        if (sql.Contains("@f"))
        {
            cmd.Parameters.Add(new NpgsqlParameter("f", NpgsqlTypes.NpgsqlDbType.Date) { Value = f });
            cmd.Parameters.Add(new NpgsqlParameter("t", NpgsqlTypes.NpgsqlDbType.Date) { Value = t });
        }
        await using var rd = await cmd.ExecuteReaderAsync();
        var rows = new List<Dictionary<string, object?>>();
        while (await rd.ReadAsync())
        {
            var row = new Dictionary<string, object?>();
            for (int i = 0; i < rd.FieldCount; i++)
                row[rd.GetName(i)] = await rd.IsDBNullAsync(i) ? null : rd.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    private const string DailyWhere = @"""Entry_Date""::date BETWEEN @f AND @t";

    // ---------- Overview / shared ----------

    [HttpGet("kpis")]
    public async Task<IActionResult> Kpis(string? from = null, string? to = null) => Ok((await Query($@"
        SELECT COALESCE(SUM(""Total_Vehicles""),0)::bigint AS vehicles,
               ROUND(COALESCE(SUM(""Total_Revenue""),0)::numeric,0) AS revenue,
               ROUND(AVG(""Occupancy_Rate_%"")::numeric,1) AS avg_occupancy,
               ROUND(MAX(""Occupancy_Rate_%"")::numeric,1) AS peak_occupancy,
               ROUND(AVG(""Average_Duration_Hours"")::numeric,2) AS avg_duration,
               ROUND(AVG(""Average_Fee"")::numeric,2) AS avg_fee,
               COALESCE(SUM(""Event_Flag""),0)::int AS event_days,
               COUNT(*)::int AS days,
               ROUND(AVG(""Total_Vehicles"")::numeric,0) AS avg_daily_vehicles,
               COUNT(*) FILTER (WHERE ""Occupancy_Rate_%"" >= 100)::int AS full_days
        FROM ""Daily_Summary"" WHERE {DailyWhere}", from, to)).FirstOrDefault());

    [HttpGet("monthly")]
    public async Task<IActionResult> Monthly(string? from = null, string? to = null) => Ok(await Query($@"
        SELECT to_char(date_trunc('month', ""Entry_Date""::date), 'Mon YY') AS label,
               MIN(""Entry_Date""::date) AS first_day,
               SUM(""Total_Vehicles"")::bigint AS vehicles,
               ROUND(SUM(""Total_Revenue"")::numeric,0) AS revenue,
               ROUND(AVG(""Occupancy_Rate_%"")::numeric,1) AS avg_occupancy,
               SUM(""Event_Flag"")::int AS event_days,
               ROUND(AVG(""Average_Fee"")::numeric,2) AS avg_fee
        FROM ""Daily_Summary"" WHERE {DailyWhere}
        GROUP BY date_trunc('month', ""Entry_Date""::date) ORDER BY 2", from, to));

    [HttpGet("daily")]
    public async Task<IActionResult> DailyTrend(string? from = null, string? to = null) => Ok(await Query($@"
        SELECT ""Entry_Date"" AS date, ""Total_Vehicles"" AS vehicles, ""Total_Revenue"" AS revenue,
               ""Occupancy_Rate_%"" AS occupancy, ""Average_Fee"" AS avg_fee,
               ""Average_Duration_Hours"" AS avg_duration, ""Is_Weekend"" AS day_type,
               ""Event_Status"" AS event_status, ""Day_Name"" AS day_name
        FROM ""Daily_Summary"" WHERE {DailyWhere}
        ORDER BY ""Entry_Date""", from, to));

    [HttpGet("daytype")]
    public async Task<IActionResult> DayType(string? from = null, string? to = null) => Ok(await Query($@"
        SELECT ""Is_Weekend"" AS day_type, COUNT(*)::int AS days,
               SUM(""Total_Vehicles"")::bigint AS vehicles,
               ROUND(AVG(""Total_Vehicles"")::numeric,0) AS avg_vehicles,
               ROUND(SUM(""Total_Revenue"")::numeric,0) AS revenue,
               ROUND(AVG(""Total_Revenue"")::numeric,0) AS avg_revenue,
               ROUND(AVG(""Occupancy_Rate_%"")::numeric,1) AS avg_occupancy
        FROM ""Daily_Summary"" WHERE {DailyWhere}
        GROUP BY 1 ORDER BY 1", from, to));

    // ---------- Occupancy ----------

    [HttpGet("hourly-occupancy")]
    public async Task<IActionResult> HourlyOccupancy(string? from = null, string? to = null) => Ok(await Query($@"
        SELECT ""Entry_Hour"" AS hour, ""Day_Type"" AS day_type,
               ROUND(AVG(""Occupancy_Rate_%"")::numeric,1) AS avg_occupancy,
               ROUND(AVG(""Concurrent_Vehicles"")::numeric,0) AS avg_concurrent
        FROM ""Hourly_Occupancy"" WHERE {DailyWhere}
        GROUP BY 1,2 ORDER BY 1", from, to));

    [HttpGet("heatmap")]
    public async Task<IActionResult> Heatmap(string? from = null, string? to = null) => Ok(await Query($@"
        SELECT ""Day_Name"" AS day, ""Entry_Hour"" AS hour,
               ROUND(AVG(""Occupancy_Rate_%"")::numeric,1) AS occupancy
        FROM ""Hourly_Occupancy"" WHERE {DailyWhere}
        GROUP BY 1,2", from, to));

    [HttpGet("peak-periods")]
    public async Task<IActionResult> PeakPeriods(string? from = null, string? to = null) => Ok(await Query($@"
        SELECT ""Peak_Period"" AS label,
               ROUND(AVG(""Occupancy_Rate_%"")::numeric,1) AS avg_occupancy,
               ROUND(AVG(""Concurrent_Vehicles"")::numeric,0) AS avg_concurrent
        FROM ""Hourly_Occupancy"" WHERE {DailyWhere}
        GROUP BY 1 ORDER BY 2 DESC", from, to));

    [HttpGet("hourly-arrivals")]
    public async Task<IActionResult> HourlyArrivals(string? from = null, string? to = null) => Ok(await Query($@"
        SELECT ""Entry_Hour"" AS hour, ""Is_Weekend"" AS day_type,
               ROUND(AVG(""Vehicle_Count"")::numeric,0) AS avg_arrivals,
               ROUND(AVG(""Revenue"")::numeric,0) AS avg_revenue
        FROM ""Hourly_Summary"" WHERE {DailyWhere}
        GROUP BY 1,2 ORDER BY 1", from, to));

    // entries vs exits by hour-of-day (the daily flow rhythm: arrive AM, leave PM)
    [HttpGet("entries-exits-hourly")]
    public async Task<IActionResult> EntriesExitsHourly(string? from = null, string? to = null) => Ok(await Query($@"
        WITH e AS (SELECT ""Entry_Hour"" AS h, COUNT(*)::bigint AS n
                   FROM ""Transactions_Cleaned"" WHERE {DailyWhere} GROUP BY 1),
             x AS (SELECT EXTRACT(hour FROM ""Exit_Time"")::int AS h, COUNT(*)::bigint AS n
                   FROM ""Transactions_Cleaned"" WHERE {DailyWhere} AND ""Exit_Time"" IS NOT NULL GROUP BY 1)
        SELECT g.h AS hour, COALESCE(e.n,0) AS entries, COALESCE(x.n,0) AS exits
        FROM generate_series(0,23) AS g(h)
        LEFT JOIN e ON e.h = g.h LEFT JOIN x ON x.h = g.h ORDER BY 1", from, to));

    // ---------- Vehicle (Transactions_Cleaned = 2.5% sample; show shares, not absolute counts) ----------

    [HttpGet("vehicle-mix")]
    public async Task<IActionResult> VehicleMix(string? from = null, string? to = null) => Ok(await Query($@"
        SELECT ""Vehicle_Type"" AS label, COUNT(*)::bigint AS value
        FROM ""Transactions_Cleaned"" WHERE {DailyWhere}
        GROUP BY 1 ORDER BY 2 DESC", from, to));

    [HttpGet("payment-mix")]
    public async Task<IActionResult> PaymentMix(string? from = null, string? to = null) => Ok(await Query($@"
        SELECT ""Payment_Type"" AS label, COUNT(*)::bigint AS value
        FROM ""Transactions_Cleaned"" WHERE {DailyWhere}
        GROUP BY 1 ORDER BY 2 DESC", from, to));

    [HttpGet("duration-by-type")]
    public async Task<IActionResult> DurationByType(string? from = null, string? to = null) => Ok(await Query($@"
        SELECT ""Vehicle_Type"" AS label,
               ROUND(AVG(""Parking_Duration_Hours"")::numeric,2) AS avg_duration,
               ROUND(AVG(""Parking_Fee"")::numeric,2) AS avg_fee
        FROM ""Transactions_Cleaned"" WHERE {DailyWhere} AND ""Parking_Duration_Hours"" IS NOT NULL
        GROUP BY 1 ORDER BY 2 DESC", from, to));

    [HttpGet("duration-dist")]
    public async Task<IActionResult> DurationDist(string? from = null, string? to = null) => Ok(await Query($@"
        SELECT CASE WHEN ""Parking_Duration_Hours"" < 1 THEN '< 1h'
                    WHEN ""Parking_Duration_Hours"" < 2 THEN '1-2h'
                    WHEN ""Parking_Duration_Hours"" < 3 THEN '2-3h'
                    WHEN ""Parking_Duration_Hours"" < 4 THEN '3-4h'
                    WHEN ""Parking_Duration_Hours"" < 6 THEN '4-6h'
                    ELSE '6h+' END AS bucket,
               COUNT(*)::bigint AS value, MIN(""Parking_Duration_Hours"") AS ord
        FROM ""Transactions_Cleaned"" WHERE {DailyWhere} AND ""Parking_Duration_Hours"" IS NOT NULL
        GROUP BY 1 ORDER BY 3", from, to));

    // ---------- Driver behaviour (resident / worker / visitor) ----------
    // Classified from each session's stay pattern (duration + arrival time + day type):
    //   Resident  = very long stay / overnight (>= 12h)
    //   Worker    = weekday, morning arrival (5-11h), office-length stay (5-12h)
    //   Visitor   = everything else (short/medium daytime/weekend stays)
    private const string Seg = @"CASE
        WHEN ""Parking_Duration_Hours"" >= 12 THEN 'Resident'
        WHEN ""Is_Weekend"" = 'Weekday' AND ""Entry_Hour"" BETWEEN 5 AND 11
             AND ""Parking_Duration_Hours"" BETWEEN 5 AND 12 THEN 'Worker'
        ELSE 'Visitor' END";

    [HttpGet("driver-segments")]
    public async Task<IActionResult> DriverSegments(string? from = null, string? to = null) => Ok(await Query($@"
        SELECT {Seg} AS segment, COUNT(*)::bigint AS visits,
               ROUND(AVG(""Parking_Duration_Hours"")::numeric,2) AS avg_duration,
               ROUND(AVG(""Parking_Fee"")::numeric,2) AS avg_fee,
               ROUND(AVG(""Entry_Hour"")::numeric,1) AS avg_arrival_hour
        FROM ""Transactions_Cleaned"" WHERE {DailyWhere} AND ""Parking_Duration_Hours"" IS NOT NULL
        GROUP BY 1 ORDER BY 2 DESC", from, to));

    [HttpGet("driver-segments-hourly")]
    public async Task<IActionResult> DriverSegmentsHourly(string? from = null, string? to = null) => Ok(await Query($@"
        SELECT ""Entry_Hour"" AS hour, {Seg} AS segment, COUNT(*)::bigint AS visits
        FROM ""Transactions_Cleaned"" WHERE {DailyWhere} AND ""Parking_Duration_Hours"" IS NOT NULL
        GROUP BY 1,2 ORDER BY 1", from, to));

    // ---------- Events ----------

    [HttpGet("event-impact")]
    public async Task<IActionResult> EventImpact(string? from = null, string? to = null) => Ok(await Query($@"
        SELECT ""Event_Status"" AS status, COUNT(*)::int AS days,
               ROUND(AVG(""Total_Vehicles"")::numeric,0) AS avg_vehicles,
               ROUND(AVG(""Total_Revenue"")::numeric,0) AS avg_revenue,
               ROUND(AVG(""Occupancy_Rate_%"")::numeric,1) AS avg_occupancy,
               ROUND(AVG(""Average_Duration_Hours"")::numeric,2) AS avg_duration
        FROM ""Daily_Summary"" WHERE {DailyWhere}
        GROUP BY 1 ORDER BY 1", from, to));

    [HttpGet("event-log")]
    public async Task<IActionResult> EventLog(string? from = null, string? to = null) => Ok(await Query($@"
        SELECT ""Entry_Date"" AS date, ""Event_Category"" AS category, ""Event_Status"" AS status,
               ""Revenue_RM"" AS revenue, ""Vehicles"" AS vehicles
        FROM ""Event_Log_Table"" WHERE {DailyWhere}
        ORDER BY ""Entry_Date"" DESC", from, to));

    [HttpGet("event-categories")]
    public async Task<IActionResult> EventCategories(string? from = null, string? to = null) => Ok(await Query($@"
        SELECT ""Event_Category"" AS label, COUNT(*)::int AS events,
               ROUND(SUM(""Revenue_RM"")::numeric,0) AS revenue,
               SUM(""Vehicles"")::bigint AS vehicles
        FROM ""Event_Log_Table"" WHERE {DailyWhere}
        GROUP BY 1 ORDER BY 3 DESC", from, to));

    // ---------- Revenue ----------

    [HttpGet("revenue-by-period")]
    public async Task<IActionResult> RevenueByPeriod(string? from = null, string? to = null) => Ok(await Query($@"
        SELECT ""Peak_Period"" AS label, ROUND(SUM(""Revenue"")::numeric,0) AS revenue
        FROM ""Hourly_Summary"" WHERE {DailyWhere}
        GROUP BY 1 ORDER BY 2 DESC", from, to));

    [HttpGet("levels-alltime")]
    public async Task<IActionResult> LevelsAllTime()
    {
        // prefer the curated full-population rollup; when it's absent (e.g. a fresh
        // install populated via the connector) compute per-level from the sessions
        var rollup = await Query(@"
            SELECT ""Parking_Level"" AS label, ""Vehicle_Count"" AS vehicles,
                   ROUND(""Revenue""::numeric,0) AS revenue, ""Average_Duration_Hours"" AS avg_duration
            FROM ""Level_Summary"" ORDER BY 1");
        if (rollup.Count > 0) return Ok(rollup);
        return Ok(await Query(@"
            SELECT ""Parking_Level"" AS label, COUNT(*)::int AS vehicles,
                   ROUND(SUM(""Parking_Fee"")::numeric,0) AS revenue,
                   ROUND(AVG(""Parking_Duration_Hours"")::numeric,2) AS avg_duration
            FROM ""Transactions_Cleaned""
            WHERE COALESCE(""Parking_Level"",'') <> ''
            GROUP BY 1 ORDER BY 1"));
    }

    // ---------- Real-time (Live_Parking) ----------

    [HttpGet("live-stats")]
    public async Task<IActionResult> LiveStats() => Ok((await Query(@"
        SELECT COUNT(*) FILTER (WHERE ""Entry_Time""::date = CURRENT_DATE)::int AS entries_today,
               COUNT(*) FILTER (WHERE ""Exit_Time""::date = CURRENT_DATE)::int AS exits_today,
               COUNT(*) FILTER (WHERE ""Exit_Time"" IS NULL)::int AS inside,
               ROUND(COALESCE(SUM(""Parking_Fee"") FILTER (WHERE ""Exit_Time""::date = CURRENT_DATE),0)::numeric,2) AS revenue_today,
               MAX(""Entry_Time"") AS last_entry
        FROM ""Live_Parking""")).FirstOrDefault());

    [HttpGet("live-timeline")]
    public async Task<IActionResult> LiveTimeline()
    {
        var entries = await Query(@"
            SELECT date_trunc('minute', ""Entry_Time"") AS minute, COUNT(*)::int AS n
            FROM ""Live_Parking"" WHERE ""Entry_Time"" > now()::timestamp - interval '60 minutes'
            GROUP BY 1 ORDER BY 1");
        var exits = await Query(@"
            SELECT date_trunc('minute', ""Exit_Time"") AS minute, COUNT(*)::int AS n
            FROM ""Live_Parking"" WHERE ""Exit_Time"" > now()::timestamp - interval '60 minutes'
            GROUP BY 1 ORDER BY 1");
        return Ok(new { entries, exits });
    }

    [HttpGet("live-recent")]
    public async Task<IActionResult> LiveRecent(int limit = 15) => Ok(await Query($@"
        SELECT ""Ticket_ID"" AS ticket, ""Vehicle_ID"" AS plate, ""Entry_Time"" AS entry_time,
               ""Exit_Time"" AS exit_time, ""Parking_Level"" AS level, ""Vehicle_Type"" AS vehicle_type,
               ""Payment_Type"" AS payment_type, ""Parking_Fee"" AS fee
        FROM ""Live_Parking""
        ORDER BY GREATEST(""Entry_Time"", COALESCE(""Exit_Time"", ""Entry_Time"")) DESC
        LIMIT {Math.Clamp(limit, 1, 100)}"));
}
