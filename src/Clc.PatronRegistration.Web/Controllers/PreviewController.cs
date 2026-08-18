using System.Globalization;
using Clc.Melissa;
using Clc.PatronRegistration.Administration;
using Clc.PatronRegistration.Configuration;
using Clc.PatronRegistration.Data;
using Clc.PatronRegistration.Helpers;
using Clc.PatronRegistration.Web.Models;
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
        ViewData["RegistrationDisabled"] = context.Settings.DisableBranch;
        ViewData["PreviewToken"] = previewRequestContext.PlaintextToken;
        ViewData["PreviewOperationalBranchName"] = branch.DisplayName;
        repository.WriteAudit("PreviewAccess", true, AnonymousAudit(context));
        return View("~/Views/Registration/Create.cshtml", model);
    }

    [HttpGet("{token}/assets/{id:int}", Name = "PreviewRegistrationFormAsset")]
    public IActionResult Asset(
        string token,
        int id,
        [FromServices] IRegistrationFormAssetRepository assets,
        [FromServices] IRegistrationFormAssetAuthorization assetAuthorization)
    {
        // PreviewRequestContextMiddleware has already authenticated the bearer token
        // and populated the draft overlay before this action can run.
        var current = previewRequestContext.Current;
        if (current is null || id <= 0 || current.Settings.HeaderImageAssetId != id)
        {
            return NotFound();
        }

        SetSecurityHeaders();
        var metadata = assetAuthorization.GetAuthorizedMetadata(
            id, current.Link.OperationalBranchId, current.Draft.FormCode);
        // The effective preview settings run at the operational branch, while a
        // newly uploaded asset staged by the draft may only be authorized at the
        // draft's own scope. Try that scope only after the effective operational
        // scope has rejected the selected asset.
        var stagedAtDraftScope = current.Draft.Changes.Any(change =>
            change.Operation == DraftOperation.Upsert &&
            string.Equals(change.Key, "header_image_asset_id", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(change.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var stagedAssetId) &&
            stagedAssetId == id);
        if (metadata is null && current.Draft.OrganizationId != current.Link.OperationalBranchId && stagedAtDraftScope)
        {
            metadata = assetAuthorization.GetAuthorizedMetadata(
                id, current.Draft.OrganizationId, current.Draft.FormCode);
        }
        if (metadata is null)
        {
            return NotFound();
        }

        Response.Headers.ETag = $"\"{metadata.ContentHash}\"";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (MatchesIfNoneMatch(Response.Headers.ETag.ToString()))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        var asset = assets.Get(id);
        return asset is null ? NotFound() : File(asset.Content, asset.ContentType);
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
            var errors = RegistrationAttempt.ErrorsFromModelState(ModelState);
            repository.WriteAudit(
                "SafePreviewSubmissionBlocked",
                errors.Count == 0,
                AnonymousAudit(context),
                errors.Count == 0 ? null : $"MVC validation failed with {errors.Count} error(s).");
            return Json(new RegistrationAttempt
            {
                Status = RegistrationStatus.Error,
                Message = errors.Count == 0
                    ? "MVC validation passed. Final submission and all registration side effects were blocked by safe preview."
                    : "Please correct the validation errors. Final submission and all registration side effects were blocked.",
                Errors = errors
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
        if (context.Settings.DisableBranch)
        {
            return Json(DupeCheckResult.False());
        }
        ApplyOperationalContext(registration, context.Settings, context.Link.OperationalBranchId);
        return Json(registration.DupeCheck(db, papi));
    }

    [HttpPost("{token}/age-block-check")]
    [ValidateAntiForgeryToken]
    public IActionResult AgeBlockCheck(string token, DateTime? birthdate)
    {
        SetSecurityHeaders();
        var context = previewRequestContext.Current;
        if (context is null)
        {
            return NotFound();
        }
        return Json(AgeBlockPolicy.Evaluate(context.Settings, birthdate));
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
        var format = DriversLicenseFormatSettingParser.Parse(context.Settings.DriversLicenseFormat);
        if (format.State == DriversLicenseFormatSettingState.Invalid)
        {
            return BadRequest("Driver’s-license scanner format is not configured with a supported value.");
        }

        return Json(format.State == DriversLicenseFormatSettingState.Barcode
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
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers.CacheControl = "no-store, no-cache, max-age=0";
        Response.Headers.Pragma = "no-cache";
    }

    private bool MatchesIfNoneMatch(string etag)
    {
        var header = Request.Headers.IfNoneMatch.ToString();
        return header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(candidate => candidate == "*" || NormalizeEntityTag(candidate).Equals(etag, StringComparison.Ordinal));
    }

    private static string NormalizeEntityTag(string value) =>
        value.StartsWith("W/", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;

}
