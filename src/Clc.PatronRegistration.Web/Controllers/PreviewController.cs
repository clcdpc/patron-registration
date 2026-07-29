using Clc.Melissa;
using Clc.PatronRegistration.Administration;
using Clc.PatronRegistration.Configuration;
using Clc.PatronRegistration.Data;
using Clc.PatronRegistration.Helpers;
using Clc.PatronRegistration.Web.Settings;
using Clc.Polaris.Api;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Clc.PatronRegistration.Web.Controllers;

[Route("preview")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class PreviewController(
    ISettingsAdministrationRepository repository,
    IPreviewTokenService tokenService,
    ICache cache,
    IDbHelper db,
    IPapiClient papi,
    IMelissaRestClient melissa,
    IEmailSender emailSender,
    IOptions<SettingsAdministrationOptions> options) : Controller
{
    [HttpGet("{token}")]
    public IActionResult Index(string token, bool forceDl = false, bool agreementAccepted = false)
    {
        SetSecurityHeaders();
        var context = Resolve(token);
        if (context is null)
        {
            return NotFound("This preview link is invalid or no longer active.");
        }

        var renderOrganizationId = GetPreviewRenderOrganization(context);
        var model = Registration.BuildBaseRegistration(
            renderOrganizationId,
            forceDl,
            Request.GetTrueClientIP(),
            context.Settings,
            db);
        model.BypassAgreement = agreementAccepted;
        ViewData["IsSettingsPreview"] = true;
        ViewData["AllowLiveSubmission"] = context.Link.AllowLiveSubmission;
        ViewData["PreviewToken"] = token;
        repository.WriteAudit("PreviewAccess", true, AnonymousAudit(context));
        return View("~/Views/Registration/Create.cshtml", model);
    }

    [HttpPost("{token}")]
    [ValidateAntiForgeryToken]
    public IActionResult Submit(string token, Registration registration)
    {
        SetSecurityHeaders();
        var context = Resolve(token);
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

        registration.UseSettings(context.Settings);
        var result = registration.CreateRegistration(
            Request.GetTrueClientIP(),
            ModelState,
            context.Settings,
            db,
            papi,
            melissa,
            emailSender);
        repository.WriteAudit("LivePreviewSubmission", true, AnonymousAudit(context), previewLinkId: context.Link.PreviewLinkId);
        return Json(result);
    }

    [HttpPost("{token}/dupe-check")]
    [ValidateAntiForgeryToken]
    public IActionResult DupeCheck(string token, Registration registration)
    {
        SetSecurityHeaders();
        var context = Resolve(token);
        if (context is null)
        {
            return NotFound();
        }
        registration.UseSettings(context.Settings);
        return Json(registration.DupeCheck(db, papi));
    }

    [HttpPost("{token}/driver-license")]
    [ValidateAntiForgeryToken]
    public IActionResult DriverLicense(string token, string dlinfo)
    {
        SetSecurityHeaders();
        var context = Resolve(token);
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

    private PreviewContext? Resolve(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }
        var link = repository.FindPreviewLink(tokenService.Hash(token));
        if (link is null || link.RevokedAtUtc.HasValue || link.ExpiresAtUtc < DateTime.UtcNow || link.DraftStatus != DraftStatus.Active.ToString())
        {
            return null;
        }
        var draft = repository.GetDraft(link.DraftId);
        if (draft is not { Status: DraftStatus.Active } || draft.OrganizationId != link.OrganizationId || !draft.FormCode.Equals(link.FormCode, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var operationalLibraryId = draft.OrganizationId == options.Value.SystemOrganizationId
            ? cache.OrganizationCache.First(organization => organization.OrganizationCodeID == 3).ParentOrganizationID
                ?? options.Value.SystemOrganizationId
            : cache.OrganizationCache.GetLibrary(draft.OrganizationId).OrganizationID;
        var settings = new PreviewSettingProvider(draft, cache, options.Value.SystemOrganizationId, operationalLibraryId);
        return new PreviewContext(link, draft, settings);
    }

    private AuditContext AnonymousAudit(PreviewContext context) => new(
        null,
        "Shared preview link",
        null,
        context.Draft.OrganizationId,
        context.Settings.LibraryId,
        context.Draft.FormCode,
        HttpContext.TraceIdentifier,
        Request.GetTrueClientIP());

    private int GetPreviewRenderOrganization(PreviewContext context)
    {
        if (context.Draft.OrganizationId != options.Value.SystemOrganizationId &&
            cache.GetOrg(context.Draft.OrganizationId).OrganizationCodeID == 3)
        {
            return context.Draft.OrganizationId;
        }
        if (context.Draft.OrganizationId != options.Value.SystemOrganizationId)
        {
            return cache.GetBranches(context.Draft.OrganizationId).First().OrganizationID;
        }
        return cache.OrganizationCache.First(organization => organization.OrganizationCodeID == 3).OrganizationID;
    }

    private void SetSecurityHeaders()
    {
        Response.Headers.ReferrerPolicy = "no-referrer";
        Response.Headers.CacheControl = "no-store, no-cache, max-age=0";
        Response.Headers.Pragma = "no-cache";
    }

    private sealed record PreviewContext(PreviewLinkRecord Link, SettingDraft Draft, PreviewSettingProvider Settings);
}
