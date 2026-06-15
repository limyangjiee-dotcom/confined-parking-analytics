using Microsoft.AspNetCore.Mvc;
using ParkingApiPg.Services;
namespace ParkingApiPg.Controllers;

// Platform-side configuration (no writes to the external parking system):
// analytics capacity and connector auto-sync interval.
[ApiController]
[Route("api/settings")]
public class SettingsController : ControllerBase
{
    private readonly SettingsService _settings;
    public SettingsController(SettingsService settings) => _settings = settings;

    [HttpGet]
    public IActionResult Get() => Ok(new { settings = _settings.All(), capacity = _settings.Capacity, syncIntervalSeconds = _settings.SyncIntervalSeconds });

    [HttpPost]
    public IActionResult Save([FromBody] Dictionary<string, string> body)
    {
        _settings.Save(body ?? new());
        return Ok(new { ok = true, message = "Settings saved", settings = _settings.All() });
    }
}
