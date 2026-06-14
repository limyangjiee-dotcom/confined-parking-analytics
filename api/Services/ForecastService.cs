using System.Diagnostics;
namespace ParkingApiPg.Services;

// Launches the Python ML forecast (run_forecasts_v2.py) in the background — after a
// data sync (auto) or on demand from the "Run forecast now" button. Fire-and-forget;
// a guard prevents overlapping runs. Configurable in appsettings ("Forecast" section).
// Needs Python + the ML packages on the same machine.
public class ForecastService
{
    private readonly IConfiguration _cfg;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<ForecastService> _log;
    private static int _running = 0;   // 0 = idle, 1 = a forecast is in progress

    public ForecastService(IConfiguration cfg, IWebHostEnvironment env, ILogger<ForecastService> log)
    { _cfg = cfg; _env = env; _log = log; }

    public bool IsRunning => Volatile.Read(ref _running) == 1;
    public static DateTime? LastFinishedAt { get; private set; }
    public static bool LastOk { get; private set; }

    /// <summary>Triggered after a sync — only runs if Forecast:AutoRunOnSync is true.</summary>
    public bool TriggerInBackground()
    {
        if (!_cfg.GetValue("Forecast:AutoRunOnSync", true)) return false;
        return Start();
    }

    /// <summary>Triggered explicitly (e.g. the "Run forecast now" button) — always runs.</summary>
    public bool RunNow() => Start();

    private bool Start()
    {
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
            bool ok = false;
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
                _log.LogInformation("Forecast: {Python} {Script} (in {Dir})", python, script, dir);
                using var p = Process.Start(psi);
                if (p == null) { _log.LogWarning("Forecast: failed to start process"); return; }
                var stderr = p.StandardError.ReadToEnd();
                p.WaitForExit();
                ok = p.ExitCode == 0;
                if (ok) _log.LogInformation("Forecast finished OK.");
                else _log.LogWarning("Forecast exited {Code}: {Err}", p.ExitCode, stderr);
            }
            catch (Exception e) { _log.LogWarning(e, "Forecast launch failed"); }
            finally
            {
                LastOk = ok;
                LastFinishedAt = DateTime.Now;
                Interlocked.Exchange(ref _running, 0);
            }
        });
        return true;
    }
}
