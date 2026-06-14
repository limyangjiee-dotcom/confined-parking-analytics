using System.Diagnostics;
namespace ParkingApiPg.Services;

// Launches the Python ML forecast (run_forecasts_v2.py) in the background after a
// data sync, so the Forecast page refreshes automatically once new data arrives.
// Fire-and-forget; a guard prevents overlapping runs. Configurable in appsettings
// ("Forecast" section) — needs Python + the ML packages on the same machine.
public class ForecastService
{
    private readonly IConfiguration _cfg;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<ForecastService> _log;
    private static int _running = 0;   // 0 = idle, 1 = a forecast is in progress

    public ForecastService(IConfiguration cfg, IWebHostEnvironment env, ILogger<ForecastService> log)
    { _cfg = cfg; _env = env; _log = log; }

    /// <summary>Starts the forecast in the background. Returns true if a run was started.</summary>
    public bool TriggerInBackground()
    {
        if (!_cfg.GetValue("Forecast:AutoRunOnSync", true)) return false;
        // skip if one is already running (e.g. frequent auto-syncs)
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            _log.LogInformation("Forecast already running — skipping this trigger.");
            return false;
        }

        var python = _cfg["Forecast:PythonPath"];
        if (string.IsNullOrWhiteSpace(python)) python = "python";
        var dir = _cfg["Forecast:ScriptDir"];
        if (string.IsNullOrWhiteSpace(dir))
            dir = Path.GetFullPath(Path.Combine(_env.ContentRootPath, "..", "ml"));
        var script = _cfg["Forecast:Script"];
        if (string.IsNullOrWhiteSpace(script)) script = "run_forecasts_v2.py";

        _ = Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = python,
                    WorkingDirectory = dir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                psi.ArgumentList.Add(script);
                _log.LogInformation("Auto-forecast: {Python} {Script} (in {Dir})", python, script, dir);
                using var p = Process.Start(psi);
                if (p == null) { _log.LogWarning("Auto-forecast: failed to start process"); return; }
                var stderr = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode == 0) _log.LogInformation("Auto-forecast finished OK.");
                else _log.LogWarning("Auto-forecast exited {Code}: {Err}", p.ExitCode, stderr);
            }
            catch (Exception e) { _log.LogWarning(e, "Auto-forecast launch failed"); }
            finally { Interlocked.Exchange(ref _running, 0); }
        });
        return true;
    }
}
