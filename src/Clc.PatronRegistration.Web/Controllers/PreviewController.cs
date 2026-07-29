using Clc.Melissa;
using Clc.PatronRegistration.Administration;
using Clc.PatronRegistration.Configuration;
using Clc.PatronRegistration.Data;
using Clc.PatronRegistration.Helpers;
using Clc.PatronRegistration.Web.Settings;
using Clc.Polaris.Api;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace Clc.PatronRegistration.Web.Controllers;

[Route("preview")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class PreviewController(
    ISettingsAdministrationRepository repository,
    IPreviewRequestContextAccessor previewRequestContext,
    ICache cache,
    IDbHelper db,
    IPapiClient papi,
    IMelissaRestClient melissa,
    IEmailSender emailSender) : Controller
{
    [HttpGet("{token}")]
    public IActionResult Index(string token, bool forceDl = false, bool agreementAccepted = false)
    {
        SetSecurityHeaders();
        var context = previewRequestContext.Current;
        if (context is null)
        {
            return NotFound("This preview link is invalid or no longer active.");
        }

        var model = Registration.BuildBaseRegistration(
            context.Link.OperationalBranchId,
            forceDl,
            Request.GetTrueClientIP(),
            context.Settings,
            db);
        var branch = cache.GetOrg(context.Link.OperationalBranchId);
        model.PatronBranchID = context.Link.OperationalBranchId;
        model.LibraryId = context.Settings.LibraryId;
        model.Branches = new SelectList(new[] { branch }, "OrganizationID", "DisplayName", context.Link.OperationalBranchId);
        model.BypassAgreement = agreementAccepted;
        ViewData["IsSettingsPreview"] = true;
        ViewData["AllowLiveSubmission"] = context.Link.AllowLiveSubmission;
        ViewData["PreviewToken"] = token;
        ViewData["PreviewOperationalBranchName"] = branch.DisplayName;
        repository.WriteAudit("PreviewAccess", true, AnonymousAudit(context));
        return View("~/Views/Registration/Create.cshtml", model);
    }

    [HttpPost("{token}")]
    [ValidateAntiForgeryToken]
    public IActionResult Submit(string token, Registration registration)
    {
        SetSecurityHeaders();
        var context = previewRequestContext.Current;
        if (context is null)
        {
            return NotFound("This preview link is invalid or no longer active.");
        }

        if (!context.Link.AllowLiveSubmission)
        {
            repository.WriteAudit("SafePreviewSubmissionBlocked", false, AnonymousAudit(context), "Safe preview never performs registration side effects.");
            return Json(new
            {
                isSuccess = false,
                message = "Safe preview validation completed. Patron creation and all side effects were blocked.",
                errors = Array.Empty<object>()
            });
        }

        ApplyOperationalContext(registration, context.Settings, context.Link.OperationalBranchId);
        try
        {
            var result = registration.CreateRegistration(
                Request.GetTrueClientIP(),
                ModelState,
                context.Settings,
                db,
                papi,
                melissa,
                emailSender);
            var failureReason = result.IsSuccess
                ? null
                : $"Registration status: {result.Status}; validation errors: {result.Errors.Count}.";
            repository.WriteAudit(
                "LivePreviewSubmission",
                result.IsSuccess,
                AnonymousAudit(context),
                failureReason,
                previewLinkId: context.Link.PreviewLinkId,
                metadataJson: $"{{\"status\":\"{result.Status}\",\"errorCount\":{result.Errors.Count}}}");
            return Json(result);
        }
        catch (Exception exception)
        {
            repository.WriteAudit(
                "LivePreviewSubmission",
                false,
                AnonymousAudit(context),
                $"Registration workflow threw {exception.GetType().Name}.",
                previewLinkId: context.Link.PreviewLinkId);
            throw;
        }
    }

    [HttpPost("{token}/dupe-check")]
    [ValidateAntiForgeryToken]
    public IActionResult DupeCheck(string token, Registration registration)
    {
        SetSecurityHeaders();
        var context = previewRequestContext.Current;
        if (context is null)
        {
            return NotFound();
        }
        ApplyOperationalContext(registration, context.Settings, context.Link.OperationalBranchId);
        return Json(registration.DupeCheck(db, papi));
    }

    [HttpPost("{token}/driver-license")]
    [ValidateAntiForgeryToken]
    public IActionResult DriverLicense(string token, string dlinfo)
    {
        SetSecurityHeaders();
        var context = previewRequestContext.Current;
        if (context is null)
        {
            return NotFound();
        }
        if (string.IsNullOrWhiteSpace(dlinfo) || dlinfo == "null")
        {
            return Json(string.Empty);
        }
        return Json(context.Settings.DriversLicenseFormat.Equals("barcode", StringComparison.OrdinalIgnoreCase)
            ? DriverLicenseHelper.ProcessDlBarcode(dlinfo)
            : DriverLicenseHelper.ProcessDlMagstripe(dlinfo));
    }

    private AuditContext AnonymousAudit(PreviewRequestContext context) => new(
        null,
        "Shared preview link",
        null,
        context.Draft.OrganizationId,
        context.Settings.LibraryId,
        context.Draft.FormCode,
        HttpContext.TraceIdentifier,
        Request.GetTrueClientIP());

    public static void ApplyOperationalContext(Registration registration, ISettingProvider settings, int operationalBranchId)
    {
        registration.UseSettings(settings);
        registration.PatronBranchID = operationalBranchId;
        registration.LibraryId = settings.LibraryId;
    }

    private void SetSecurityHeaders()
    {
        Response.Headers.ReferrerPolicy = "no-referrer";
        Response.Headers.CacheControl = "no-store, no-cache, max-age=0";
        Response.Headers.Pragma = "no-cache";
    }

}
