using Microsoft.AspNetCore.Mvc;
using ParkingApiPg.Services;
namespace ParkingApiPg.Controllers;

// AI insights endpoints: per-page summaries (incl. forecast recommendations) and
// ask-your-data Q&A. Only aggregated statistics reach the AI — never raw sessions.
[ApiController]
[Route("api/ai")]
public class AiController : ControllerBase
{
    private readonly AiService _ai;
    public AiController(AiService ai) => _ai = ai;

    public record SummaryBody(string Page = "overview", string? From = null, string? To = null);
    public record AskBody(string Question = "", string? From = null, string? To = null);

    private static (DateTime from, DateTime to) Range(string? from, string? to)
    {
        var f = DateTime.TryParse(from, out var pf) ? pf : new DateTime(2000, 1, 1);
        var t = DateTime.TryParse(to, out var pt) ? pt : new DateTime(2100, 1, 1);
        return (f, t);
    }

    [HttpGet("status")]
    public IActionResult Status() => Ok(new { configured = _ai.Configured, model = _ai.Model });

    [HttpPost("summary")]
    public async Task<IActionResult> Summary([FromBody] SummaryBody body)
    {
        try
        {
            var (f, t) = Range(body.From, body.To);
            var text = await _ai.Summarize(body.Page ?? "overview", f, t);
            return Ok(new { ok = true, text });
        }
        catch (Exception e) { return Ok(new { ok = false, text = "AI request failed: " + e.Message }); }
    }

    [HttpPost("ask")]
    public async Task<IActionResult> Ask([FromBody] AskBody body)
    {
        if (string.IsNullOrWhiteSpace(body.Question))
            return Ok(new { ok = false, text = "Please type a question first." });
        try
        {
            var (f, t) = Range(body.From, body.To);
            var text = await _ai.Ask(body.Question.Trim(), f, t);
            return Ok(new { ok = true, text });
        }
        catch (Exception e) { return Ok(new { ok = false, text = "AI request failed: " + e.Message }); }
    }
}
