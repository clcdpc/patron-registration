using Microsoft.AspNetCore.Mvc;

namespace Clc.PatronRegistration.Web.Controllers;

[Route("preview")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class PreviewController : Controller
{
    [HttpGet("{token}")]
    public IActionResult Index(string token)
    {
        Response.Headers.ReferrerPolicy = "no-referrer";
        // Token lookup is deliberately repository-bound; an unknown token reveals no scope information.
        return NotFound("This preview link is invalid or no longer active.");
    }
    [HttpPost("{token}")]
    [ValidateAntiForgeryToken]
    public IActionResult Submit(string token)
    {
        Response.Headers.ReferrerPolicy = "no-referrer";
        return NotFound("This preview link is invalid or no longer active.");
    }
}
