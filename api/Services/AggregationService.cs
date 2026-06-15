using Npgsql;
namespace ParkingApiPg.Services;

// Rebuilds the date-grained analytics summary tables from Live_Parking
// (which is where the connector imports an external system's sessions).
// Self-contained set-based SQL — no Python / Task Scheduler needed for the
// connect → analyse loop. Surgical: only rebuilds the dates present in
// Live_Parking, so unrelated historical days are left untouched.
public class AggregationService
{
    private readonly string _cs;
    private readonly SettingsService _settings;
    public AggregationService(IConfiguration cfg, SettingsService settings)
    { _cs = cfg.GetConnectionString("Default")!; _settings = settings; }

    // peak-period label for an hour column expression named {h}
    private static string Peak(string h) => $@"
        CASE WHEN {h} BETWEEN 7 AND 9 THEN 'Morning Peak'
             WHEN {h} BETWEEN 12 AND 14 THEN 'Lunch Peak'
             WHEN {h} BETWEEN 18 AND 20 THEN 'Evening Peak'
             WHEN {h} BETWEEN 10 AND 17 THEN 'Daytime'
             WHEN {h} BETWEEN 21 AND 23 THEN 'Night'
             ELSE 'Late Night/Early' END";

    public async Task<(int days, int txns)> Rebuild()
    {
        await using var c = new NpgsqlConnection(_cs);
        await c.OpenAsync();
        await using var tx = await c.BeginTransactionAsync();

        var sql = $@"
        CREATE TEMP TABLE _sess ON COMMIT DROP AS
        SELECT ""Vehicle_ID"" AS plate, ""Entry_Time"" AS t_in,
               ""Exit_Time"" AS raw_exit,
               COALESCE(""Exit_Time"", now()::timestamp) AS t_out,
               COALESCE(""Parking_Fee"",0)::numeric AS fee,
               ""Parking_Duration_Hours""::numeric AS dur,
               COALESCE(""Parking_Level"",'') AS lvl, COALESCE(""Vehicle_Type"",'Car') AS vt,
               COALESCE(""Payment_Type"",'') AS pay, COALESCE(""Event_Status"",'Non-Event Day') AS es,
               COALESCE(""Event_Name"",'') AS en, ""Ticket_ID"" AS tid,
               ""Entry_Time""::date AS d, extract(hour FROM ""Entry_Time"")::int AS h
        FROM ""Live_Parking"";

        -- per-hour arrivals / revenue / avg-duration (set-based GROUP BY)
        CREATE TEMP TABLE _arr ON COMMIT DROP AS
        SELECT d, h, count(*)::int AS arrivals,
               COALESCE(sum(fee),0)::numeric AS hrev,
               avg(dur) FILTER (WHERE dur IS NOT NULL)::numeric AS hdur
        FROM _sess GROUP BY d, h;

        -- per-hour concurrency: expand each session into the hour buckets it
        -- occupies (entry hour .. exit hour) and count. O(rows × hours/session),
        -- far cheaper than a per-(day,hour) correlated subquery over all sessions.
        CREATE TEMP TABLE _occ ON COMMIT DROP AS
        SELECT gs::date AS d, extract(hour FROM gs)::int AS h, count(*)::int AS concurrent
        FROM _sess s
        CROSS JOIN LATERAL generate_series(date_trunc('hour', s.t_in),
                                           date_trunc('hour', s.t_out),
                                           interval '1 hour') AS gs
        GROUP BY 1, 2;

        CREATE TEMP TABLE _conc ON COMMIT DROP AS
        SELECT COALESCE(a.d, o.d) AS d, COALESCE(a.h, o.h) AS h,
               COALESCE(o.concurrent, 0) AS concurrent,
               COALESCE(a.arrivals, 0) AS arrivals,
               COALESCE(a.hrev, 0) AS hrev, a.hdur AS hdur
        FROM _arr a FULL OUTER JOIN _occ o ON a.d = o.d AND a.h = o.h;

        -- remove existing rows for exactly the dates we are about to rebuild
        DELETE FROM ""Daily_Summary""       WHERE ""Entry_Date"" IN (SELECT DISTINCT d FROM _sess);
        DELETE FROM ""Hourly_Summary""      WHERE ""Entry_Date"" IN (SELECT DISTINCT d FROM _sess);
        DELETE FROM ""Hourly_Occupancy""    WHERE ""Entry_Date"" IN (SELECT DISTINCT d FROM _sess);
        DELETE FROM ""Event_Log_Table""     WHERE ""Entry_Date"" IN (SELECT DISTINCT d FROM _sess);
        DELETE FROM ""Transactions_Cleaned"" WHERE ""Entry_Date"" IN (SELECT DISTINCT d FROM _sess);

        INSERT INTO ""Daily_Summary""
          (""Entry_Date"",""Total_Vehicles"",""Total_Revenue"",""Average_Fee"",""Average_Duration_Hours"",
           ""Event_Flag"",""Day_Name"",""Month"",""Is_Weekend"",""Event_Status"",""Quarter"",""Occupancy_Rate_%"",""Turnover_Rate"")
        SELECT s.d, count(*), ROUND(sum(s.fee),2), ROUND(avg(s.fee),2), ROUND(COALESCE(avg(s.dur),0),2),
               CASE WHEN bool_or(s.es='Event Day') THEN 1 ELSE 0 END,
               trim(to_char(s.d,'Day')), trim(to_char(s.d,'Month')),
               CASE WHEN extract(isodow FROM s.d)>=6 THEN 'Weekend' ELSE 'Weekday' END,
               CASE WHEN bool_or(s.es='Event Day') THEN 'Event Day' ELSE 'Non-Event Day' END,
               extract(quarter FROM s.d)::int,
               ROUND((SELECT max(concurrent) FROM _conc c WHERE c.d=s.d)::numeric/@cap*100,2),
               ROUND(count(*)::numeric/@cap,4)
        FROM _sess s GROUP BY s.d;

        INSERT INTO ""Hourly_Summary""
          (""Entry_Date"",""Entry_Hour"",""Vehicle_Count"",""Revenue"",""Average_Duration_Hours"",""Day_Name"",""Is_Weekend"",""Peak_Period"")
        SELECT d, h, arrivals, ROUND(hrev,2), ROUND(COALESCE(hdur,0),2),
               trim(to_char(d,'Day')),
               CASE WHEN extract(isodow FROM d)>=6 THEN 'Weekend' ELSE 'Weekday' END,
               {Peak("h")}
        FROM _conc WHERE arrivals>0;

        INSERT INTO ""Hourly_Occupancy""
          (""Entry_Date"",""Entry_Hour"",""Concurrent_Vehicles"",""Occupancy_Rate_%"",""Day_Name"",""Day_Type"",""Peak_Period"")
        SELECT d, h, concurrent, ROUND(concurrent::numeric/@cap*100,2),
               trim(to_char(d,'Day')),
               CASE WHEN extract(isodow FROM d)>=6 THEN 'Weekend' ELSE 'Weekday' END,
               {Peak("h")}
        FROM _conc WHERE concurrent>0 OR arrivals>0;

        INSERT INTO ""Event_Log_Table"" (""Entry_Date"",""Event_Category"",""Event_Status"",""Revenue_RM"",""Vehicles"")
        SELECT s.d, mode() WITHIN GROUP (ORDER BY s.en), 'Event Day', ROUND(sum(s.fee),2), count(*)
        FROM _sess s WHERE s.es='Event Day' GROUP BY s.d;

        INSERT INTO ""Transactions_Cleaned""
          (""Vehicle_ID"",""Entry_Time"",""Exit_Time"",""Parking_Fee"",""Parking_Level"",""Event_Flag"",""Event_Name"",
           ""Vehicle_Type"",""Payment_Type"",""Parking_Duration_Hours"",""Entry_Date"",""Entry_Year"",""Entry_Month_No"",
           ""Entry_Month"",""Entry_Day"",""Entry_Hour"",""Day_Name"",""Day_Of_Week_No"",""Is_Weekend"",""Peak_Period"",
           ""Event_Category"",""Event_Status"",""Quarter"",""Entry_Week"",""Revenue_Per_Hour"",""Ticket_ID"")
        SELECT plate, t_in, raw_exit, fee, lvl,
               CASE WHEN es='Event Day' THEN 1 ELSE 0 END, en, vt, pay,
               CASE WHEN dur IS NOT NULL THEN ROUND(dur,2) END,
               d, extract(year FROM t_in)::int, extract(month FROM t_in)::int, trim(to_char(t_in,'Month')),
               extract(day FROM t_in)::int, h, trim(to_char(t_in,'Day')), extract(isodow FROM t_in)::int-1,
               CASE WHEN extract(isodow FROM t_in)>=6 THEN 'Weekend' ELSE 'Weekday' END,
               {Peak("h")},
               CASE WHEN es='Event Day' THEN en ELSE 'None' END, es, extract(quarter FROM t_in)::int,
               extract(week FROM t_in)::int,
               CASE WHEN dur>0 THEN ROUND(fee/dur,2) ELSE 0 END, tid
        FROM _sess;";

        await using (var cmd = new NpgsqlCommand(sql, c, tx))
        {
            cmd.CommandTimeout = 300;   // large operator histories can take a while
            cmd.Parameters.AddWithValue("cap", _settings.Capacity);
            await cmd.ExecuteNonQueryAsync();
        }

        var days = (long)(await new NpgsqlCommand("SELECT count(DISTINCT \"Entry_Time\"::date) FROM \"Live_Parking\"", c, tx).ExecuteScalarAsync() ?? 0L);
        var txns = (long)(await new NpgsqlCommand("SELECT count(*) FROM \"Live_Parking\"", c, tx).ExecuteScalarAsync() ?? 0L);
        await tx.CommitAsync();
        return ((int)days, (int)txns);
    }
}
