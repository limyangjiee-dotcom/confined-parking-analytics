using ParkingApiPg.Services;
namespace ParkingApiPg.Services;

// Runs the connector sync on the interval configured in settings
// (sync_interval_seconds; 0 disables scheduling — manual sync only).
public class SyncBackgroundService : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<SyncBackgroundService> _log;
    public SyncBackgroundService(IServiceProvider sp, ILogger<SyncBackgroundService> log)
    { _sp = sp; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        DateTime lastRun = DateTime.MinValue;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var settings = _sp.GetRequiredService<SettingsService>();
                var interval = settings.SyncIntervalSeconds;
                if (interval > 0 && (DateTime.UtcNow - lastRun).TotalSeconds >= interval)
                {
                    var ingestion = _sp.GetRequiredService<IngestionService>();
                    var r = await ingestion.Sync();
                    lastRun = DateTime.UtcNow;
                    _log.LogInformation("Scheduled sync: {Msg}", r.Message);
                }
            }
            catch (Exception e) { _log.LogWarning(e, "Scheduled sync failed"); }
            await Task.Delay(TimeSpan.FromSeconds(15), stop);   // check cadence
        }
    }
}
