using Microsoft.AspNetCore.Mvc;
using ParkingApiPg.Services;
namespace ParkingApiPg.Controllers;

// Import planned events from an iCalendar (.ics) feed (e.g. a public Google
// Calendar) into Event_Calendar, so the ML forecast knows about upcoming events.
[ApiController]
[Route("api/events")]
public class EventsController : ControllerBase
{
    private readonly EventFeedService _feed;
    public EventsController(EventFeedService feed) => _feed = feed;

    public record FeedBody(string Url = "");

    [HttpGet("feed")]
    public async Task<IActionResult> Get()
    {
        var url = await _feed.GetSetting("ical_url") ?? "";
        var status = await _feed.GetSetting("ical_last_status") ?? "never imported";
        var upcoming = await _feed.Upcoming();
        return Ok(new { url, lastStatus = status, upcoming });
    }

    [HttpPost("feed/test")]
    public async Task<IActionResult> Test([FromBody] FeedBody body)
    {
        try
        {
            var events = await _feed.Fetch(body.Url);
            var sample = events.OrderBy(e => e.Date).Take(5)
                               .Select(e => new { date = e.Date, name = e.Name });
            return Ok(new { ok = true, count = events.Count, sample });
        }
        catch (Exception e) { return Ok(new { ok = false, message = e.Message }); }
    }

    [HttpPost("feed/import")]
    public async Task<IActionResult> Import([FromBody] FeedBody body)
    {
        try
        {
            var (imported, parsed) = await _feed.Import(body.Url);
            return Ok(new { ok = true, imported, parsed,
                message = $"Imported {imported} upcoming event day(s) from {parsed} parsed. Forecast is refreshing." });
        }
        catch (Exception e) { return Ok(new { ok = false, message = e.Message }); }
    }
}
