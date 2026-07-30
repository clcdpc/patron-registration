using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Clc.PatronRegistration.Web.Controllers;

public sealed class AccountController : Controller
{
    [AllowAnonymous]
    [HttpGet("/Account/AccessDenied")]
    public IActionResult AccessDenied(string? returnUrl = null)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return View(viewName: null, model: Url.IsLocalUrl(returnUrl) ? returnUrl : null);
    }
}
