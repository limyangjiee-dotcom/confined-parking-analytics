namespace ParkingApiPg.Services;

// Keeps the event calendar connected automatically: shortly after startup, and
// then on the ical_refresh_seconds interval, it imports the configured iCal feed
// into Event_Calendar. The feed URL is seeded by default (see Program.cs), so the
// event calendar is connected out of the box — no manual step required.
// Failures (e.g. the feed source is offline) are non-fatal and logged quietly.
public class EventFeedBackgroundService : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<EventFeedBackgroundService> _log;
    public EventFeedBackgroundService(IServiceProvider sp, ILogger<EventFeedBackgroundService> log)
    { _sp = sp; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        // small delay so the web host + DB are ready before the first import
        try { await Task.Delay(TimeSpan.FromSeconds(8), stop); } catch { return; }

        DateTime lastRun = DateTime.MinValue;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var feed = _sp.GetRequiredService<EventFeedService>();
                var intervalStr = await feed.GetSetting("ical_refresh_seconds");
                var interval = int.TryParse(intervalStr, out var v) ? v : 3600;   // default hourly
                if (interval > 0 && (DateTime.UtcNow - lastRun).TotalSeconds >= interval)
                {
                    var r = await feed.ImportSaved(triggerForecast: false);
                    lastRun = DateTime.UtcNow;
                    if (r.HasValue)
                        _log.LogInformation("iCal auto-import: {N} upcoming event day(s) from {P} parsed",
                                            r.Value.imported, r.Value.parsed);
                }
            }
            catch (Exception e) { _log.LogDebug(e, "iCal auto-import skipped (feed unreachable?)"); }
            try { await Task.Delay(TimeSpan.FromSeconds(30), stop); } catch { break; }   // check cadence
        }
    }
}
