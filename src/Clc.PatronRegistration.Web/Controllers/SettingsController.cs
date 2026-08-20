using System.Data;
using System.Security.Claims;
using Clc.PatronRegistration.Administration;
using Clc.PatronRegistration.Configuration;
using Clc.PatronRegistration.Helpers;
using Clc.PatronRegistration.Web.Models;
using Clc.PatronRegistration.Web.Settings;
using Clc.Polaris.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using Microsoft.Data.SqlClient;
using System.Globalization;
using Microsoft.AspNetCore.Http.Features;

namespace Clc.PatronRegistration.Web.Controllers;

[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("settings")]
public sealed class SettingsController(
    ISettingsAuthorizationService authorization,
    ISettingsAdministrationRepository repository,
    ISettingCatalog catalog,
    ICache cache,
    IPreviewTokenService previewTokens,
    IPreviewBranchEligibilityService previewBranchEligibility,
    IFormCodeAvailabilityService formCodeAvailability,
    ISettingsCacheInvalidator cacheInvalidator,
    ISettingsPageBrandingContextAccessor settingsPageBrandingContext,
    IRegistrationFormAssetRepository assetRepository,
    IRegistrationFormAssetAuthorization assetAuthorization,
    IOptions<SettingsAdministrationOptions> options) : Controller
{
    private readonly SettingsAdministrationOptions settingsOptions = options.Value;
    private IReadOnlyDictionary<string, SettingDefinition> CatalogByKey =>
        catalog.All.ToDictionary(setting => setting.Key, StringComparer.OrdinalIgnoreCase);

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        Response.Headers.CacheControl = "no-store, no-cache, max-age=0";
        Response.Headers.Pragma = "no-cache";
        Response.Headers["Referrer-Policy"] = "no-referrer";

        var principal = authorization.Describe(User);
        if (User.Identity?.IsAuthenticated == true && principal.HasRole &&
            (principal.IsGlobal || principal.OrganizationId.HasValue) && !TrySetBrandingContext(principal))
        {
            context.Result = Forbid();
        }
        base.OnActionExecuting(context);
    }

    private bool TrySetBrandingContext(SettingsPrincipal principal)
    {
        if (principal.IsGlobal)
        {
            settingsPageBrandingContext.Set(
                settingsOptions.SystemOrganizationId,
                settingsOptions.SystemOrganizationId);
            return true;
        }

        if (principal.OrganizationId is not { } organizationId)
        {
            return false;
        }

        try
        {
            var libraryId = cache.OrganizationCache.GetLibrary(organizationId).OrganizationID;
            settingsPageBrandingContext.Set(organizationId, libraryId);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    [HttpGet("")]
    public IActionResult Index(int? organizationId, string formCode = "")
    {
        formCode = FormCodeNormalizer.Normalize(formCode);
        var principal = RequireManager();
        if (principal is null)
        {
            return Forbid();
        }

        var target = organizationId ?? (principal.IsGlobal ? settingsOptions.SystemOrganizationId : GetLibraryId(principal.OrganizationId!.Value));
        if (!authorization.CanManage(User, target) || !formCodeAvailability.IsAvailable(target, formCode))
        {
            AuditRejected(target, formCode, "Invalid or unauthorized scope.");
            return Forbid();
        }

        var cacheSnapshot = CacheSnapshot.Capture(cache);
        var libraryId = target == settingsOptions.SystemOrganizationId
            ? settingsOptions.SystemOrganizationId
            : cacheSnapshot.Organizations.GetLibrary(target).OrganizationID;
        var draft = repository.GetActiveDraft(target, formCode);
        var resolver = new SettingsResolver();
        var visibleSettings = catalog.All.Where(setting => principal.IsGlobal || !setting.IsSensitive).ToList();
        var rows = new List<SettingRowViewModel>(visibleSettings.Count);

        for (var index = 0; index < visibleSettings.Count; index++)
        {
            var definition = visibleSettings[index];
            var draftChange = draft?.Changes.FirstOrDefault(change => change.Key.Equals(definition.Key, StringComparison.OrdinalIgnoreCase));
            var resolution = resolver.Resolve(cacheSnapshot.IndexedSettings, definition.Key, target, libraryId, formCode,
                settingsOptions.SystemOrganizationId);
            var inheritedResolution = resolution.OwnsOverride
                ? resolver.Resolve(cacheSnapshot.IndexedSettings, definition.Key, target, libraryId, formCode,
                    settingsOptions.SystemOrganizationId,
                    new HashSet<(int OrganizationId, string FormCode, string Key)>
                    {
                        (target, formCode, definition.Key)
                    })
                : null;
            var effectiveAssetMissing = false;
            var effectiveAsset = definition.ValueType == SettingValueType.Image
                ? ResolveAsset(resolution.EffectiveValue, target, formCode, out effectiveAssetMissing)
                : null;
            var stagedAssetValue = definition.ValueType == SettingValueType.Image
                ? draftChange?.Operation switch
                {
                    DraftOperation.Upsert => draftChange.Value,
                    DraftOperation.RemoveOverride => inheritedResolution?.EffectiveValue,
                    _ => resolution.EffectiveValue
                }
                : null;
            var stagedAssetMissing = false;
            var stagedAsset = definition.ValueType == SettingValueType.Image
                ? ResolveAsset(stagedAssetValue, target, formCode, out stagedAssetMissing)
                : null;
            var inheritedAssetMissing = false;
            var inheritedAsset = definition.ValueType == SettingValueType.Image && inheritedResolution is not null
                ? ResolveAsset(inheritedResolution.EffectiveValue, target, formCode, out inheritedAssetMissing)
                : null;
            rows.Add(new SettingRowViewModel(
                $"setting-{index}",
                definition,
                definition.IsSensitive ? SanitizeSensitiveResolution(resolution) : resolution,
                definition.IsSensitive ? null : draftChange?.Value,
                draftChange?.Operation,
                draft?.DraftId,
                DescribeSource(resolution),
                definition.IsSensitive ? null : inheritedResolution?.EffectiveValue,
                inheritedResolution?.SourceOrganizationId.HasValue == true,
                effectiveAsset,
                effectiveAssetMissing,
                stagedAsset,
                stagedAssetMissing,
                inheritedAsset,
                inheritedAssetMissing,
                inheritedResolution is null ? null : DescribeSource(inheritedResolution),
                DraftRevision: draft?.Revision));
        }

        var formCodes = formCodeAvailability.GetAvailable(libraryId).ToList();
        var formDisplayName = formCodes.FirstOrDefault(form => form.FormCode.Equals(formCode, StringComparison.OrdinalIgnoreCase))?.DisplayName
            ?? (formCode.Length == 0 ? "Default form" : formCode);

        var model = new SettingsIndexViewModel
        {
            OrganizationId = target,
            OrganizationName = GetOrganizationName(target),
            LibraryId = libraryId,
            FormCode = formCode,
            FormDisplayName = formDisplayName,
            IsGlobal = principal.IsGlobal,
            ScopeVersion = repository.GetVersion(target, formCode),
            ActiveDraft = draft is null ? null : SanitizeDraftForView(draft, principal.IsGlobal),
            HasRestrictedDraftChanges = draft is not null && DraftContainsSensitiveChanges(draft),
            CanManageRestrictedDraft = principal.IsGlobal,
            PreviewLinks = draft is null ? [] : repository.GetPreviewLinks(draft.DraftId),
            PreviewBranches = GetPreviewBranches(target),
            Scopes = GetAuthorizedScopes(principal),
            FormCodes = formCodes,
            Settings = rows
        };
        return View(model);
    }

    private SettingAssetPresentation? ResolveAsset(string? value, int organizationId, string formCode, out bool missing)
    {
        missing = false;
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var assetId) || assetId <= 0)
        {
            missing = !string.IsNullOrWhiteSpace(value);
            return null;
        }

        var metadata = assetAuthorization.GetAuthorizedMetadata(assetId, organizationId, formCode);
        if (metadata is null)
        {
            missing = true;
            return null;
        }
        var previewUrl = Url?.RouteUrl("SettingsRegistrationFormAsset", new
        {
            id = metadata.AssetId,
            organizationId,
            formCode
        }) ?? $"/settings/assets/{metadata.AssetId}?organizationId={organizationId}&formCode={Uri.EscapeDataString(formCode)}";
        return new SettingAssetPresentation(metadata.AssetId, metadata.FileName, previewUrl);
    }

    private string DescribeSource(ResolvedSetting resolution)
    {
        if (!resolution.SourceOrganizationId.HasValue)
        {
            return "No value is configured";
        }
        if (resolution.SourceOrganizationId == settingsOptions.SystemOrganizationId)
        {
            return "System defaults";
        }
        var formName = resolution.SourceFormCode.Length == 0 ? "Default form" : $"{resolution.SourceFormCode} form";
        return $"{GetOrganizationName(resolution.SourceOrganizationId.Value)} — {formName}";
    }

    [HttpGet("help")]
    public IActionResult Help(int? organizationId, string? formCode = null)
    {
        var principal = RequireManager();
        if (principal is null)
        {
            return Forbid();
        }

        var normalizedFormCode = FormCodeNormalizer.Normalize(formCode);
        if (!organizationId.HasValue)
        {
            return View(new SettingsHelpViewModel(null, string.Empty));
        }

        var isAuthorizedScope = GetAuthorizedScopes(principal)
            .Any(scope => scope.OrganizationId == organizationId.Value);
        var hasValidReturnContext = isAuthorizedScope &&
            formCodeAvailability.IsAvailable(organizationId.Value, normalizedFormCode);

        return View(new SettingsHelpViewModel(
            hasValidReturnContext ? organizationId : null,
            hasValidReturnContext ? normalizedFormCode : string.Empty));
    }

    private static ResolvedSetting SanitizeSensitiveResolution(ResolvedSetting resolution) => resolution with
    {
        EffectiveValue = null,
        CurrentOverrideValue = null
    };

    private SettingDraft SanitizeDraftForView(SettingDraft draft, bool includeSensitiveMetadata) => draft with
    {
        Changes = draft.Changes
            .Where(change => includeSensitiveMetadata ||
                !catalog.TryGet(change.Key, out var definition) || !definition.IsSensitive)
            .Select(change => catalog.TryGet(change.Key, out var definition) && definition.IsSensitive
                ? change with { Value = null }
                : change)
            .ToList()
    };

    [HttpPost("direct-save")]
    [ValidateAntiForgeryToken]
    public IActionResult DirectSave(SaveSettingsRequest request)
    {
        if (!ValidateScope(request.OrganizationId, request.FormCode))
        {
            AuditRejected(request.OrganizationId, request.FormCode, "Direct-save scope tampering rejected.");
            return Forbid();
        }

        var mutations = ValidateMutations(request.Changes, request.OrganizationId, request.FormCode);
        if (!ModelState.IsValid)
        {
            repository.WriteAudit("ValidationFailed", false, CreateAudit(request.OrganizationId, request.FormCode), "One or more setting values were invalid.");
            TempData["SettingsError"] = string.Join(" ", ModelState.Values.SelectMany(value => value.Errors).Select(error => error.ErrorMessage));
            TempData["SettingsErrorGroup"] = request.Changes.FirstOrDefault(change => ModelState.ContainsKey(change.Key))?.Key.Split('.')[0];
            return RedirectToAction(nameof(Index), new { organizationId = request.OrganizationId, formCode = request.FormCode });
        }

        try
        {
            repository.DirectSave(request.OrganizationId, request.FormCode, request.ExpectedVersion, mutations, CatalogByKey, CreateAudit(request.OrganizationId, request.FormCode));
            cacheInvalidator.LiveSettingsChanged($"DirectSave organization={request.OrganizationId} form={request.FormCode}");
        }
        catch (DBConcurrencyException exception)
        {
            return Conflict(exception.Message);
        }
        catch (SqlException exception) when (exception.Number == 1205)
        {
            return Conflict("Direct save conflicted with another settings change. Reload and review the settings.");
        }
        catch (InvalidOperationException exception)
        {
            repository.WriteAudit("ValidationFailed", false, CreateAudit(request.OrganizationId, request.FormCode), exception.Message);
            return BadRequest(exception.Message);
        }

        return RedirectToAction(nameof(Index), new { organizationId = request.OrganizationId, formCode = request.FormCode });
    }

    [HttpPost("drafts/changes")]
    [ValidateAntiForgeryToken]
    public IActionResult SaveToSharedDraft(SaveToSharedDraftRequest request)
    {
        if (!ValidateScope(request.OrganizationId, request.FormCode))
        {
            AuditRejected(request.OrganizationId, request.FormCode, "Save-to-draft scope tampering rejected.");
            return Forbid();
        }
        var mutations = ValidateMutations(request.Changes, request.OrganizationId, request.FormCode);
        if (!ModelState.IsValid)
        {
            repository.WriteAudit("ValidationFailed", false, CreateAudit(request.OrganizationId, request.FormCode), "Shared draft changes were invalid.", request.ExpectedDraftId);
            TempData["SettingsError"] = string.Join(" ", ModelState.Values.SelectMany(value => value.Errors).Select(error => error.ErrorMessage));
            TempData["SettingsErrorGroup"] = request.Changes.FirstOrDefault(change => ModelState.ContainsKey(change.Key))?.Key.Split('.')[0];
            return RedirectToAction(nameof(Index), new { organizationId = request.OrganizationId, formCode = request.FormCode });
        }
        try
        {
            var audit = CreateAudit(request.OrganizationId, request.FormCode);
            var result = request.ExpectedDraftRevision.HasValue
                ? repository.SaveToSharedDraft(request.OrganizationId, request.FormCode, request.ExpectedVersion, request.ExpectedDraftId,
                    mutations, CatalogByKey, audit, request.ExpectedDraftRevision.Value)
                : repository.SaveToSharedDraft(request.OrganizationId, request.FormCode, request.ExpectedVersion, request.ExpectedDraftId,
                    mutations, CatalogByKey, audit);
            TempData["SettingsStatus"] = result.DraftCreated
                ? $"Shared draft #{result.DraftId} was created with {mutations.Count} {(mutations.Count == 1 ? "change" : "changes")}."
                : $"{mutations.Count} {(mutations.Count == 1 ? "change was" : "changes were")} added to shared draft #{result.DraftId}.";
        }
        catch (DBConcurrencyException)
        {
            return DraftConflictResult(request.OrganizationId, request.FormCode);
        }
        catch (SqlException exception) when (exception.Number is 1205 or 2601 or 2627)
        {
            return DraftConflictResult(request.OrganizationId, request.FormCode);
        }
        catch (InvalidOperationException)
        {
            repository.WriteAudit("ValidationFailed", false,
                CreateAudit(request.OrganizationId, request.FormCode), "Shared draft changes were invalid.", request.ExpectedDraftId);
            TempData["SettingsError"] = "The shared draft changes could not be saved. Reloaded values are shown below.";
            return RedirectToAction(nameof(Index), new { organizationId = request.OrganizationId, formCode = request.FormCode });
        }
        return RedirectToAction(nameof(Index), new { organizationId = request.OrganizationId, formCode = request.FormCode });
    }

    [HttpPost("assets/upload")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(2_200_000)]
    [RequestFormLimits(MultipartBodyLengthLimit = 2_200_000)]
    public async Task<IActionResult> UploadHeaderImageAsset(
        IFormFile? file,
        int organizationId,
        string formCode)
    {
        formCode = FormCodeNormalizer.Normalize(formCode);
        const string settingKey = "header_image_asset_id";
        if (!ValidateScope(organizationId, formCode) || !catalog.TryGet(settingKey, out var definition) ||
            !authorization.CanManage(User, organizationId, definition.IsSensitive))
        {
            AuditRejected(organizationId, formCode, "Header-image upload scope or authorization was rejected.");
            return Forbid();
        }

        if (file is null)
        {
            return BadRequest(new { error = "Choose a PNG, JPEG, or WebP image to upload." });
        }

        if (file.Length == 0)
        {
            return BadRequest(new { error = "Choose a non-empty image file." });
        }

        if (file.Length > RegistrationFormAssetUploadValidation.MaximumUploadBytes)
        {
            return BadRequest(new { error = $"Image files must be {RegistrationFormAssetUploadValidation.MaximumUploadBytes / 1024 / 1024} MB or smaller." });
        }

        byte[] content;
        await using (var stream = file.OpenReadStream())
        await using (var buffer = new MemoryStream())
        {
            var chunk = new byte[64 * 1024];
            while (true)
            {
                var read = await stream.ReadAsync(chunk.AsMemory());
                if (read == 0)
                {
                    break;
                }
                if (buffer.Length + read > RegistrationFormAssetUploadValidation.MaximumUploadBytes)
                {
                    return BadRequest(new { error = $"Image files must be {RegistrationFormAssetUploadValidation.MaximumUploadBytes / 1024 / 1024} MB or smaller." });
                }
                await buffer.WriteAsync(chunk.AsMemory(0, read));
            }
            content = buffer.ToArray();
        }

        if (!RegistrationFormAssetUploadValidation.TryValidateUploadEnvelope(file.ContentType, content, file.FileName,
                out var sanitizedFileName, out var validationError))
        {
            return BadRequest(new { error = validationError });
        }

        // Asset creation is deliberately independent from setting mutation. The browser places this
        // returned ID into the normal row edit session, and Save/Save-to-draft persists it with peers.
        RegistrationFormAsset asset;
        try
        {
            // The repository repeats the complete image validation, including the
            // animation and decoded-pixel checks, before storing the bytes.
            asset = assetRepository.Create(sanitizedFileName, file.ContentType, content, organizationId, formCode);
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
        var previewUrl = Url?.RouteUrl("SettingsRegistrationFormAsset", new { id = asset.AssetId, organizationId, formCode })
            ?? $"/settings/assets/{asset.AssetId}?organizationId={organizationId}&formCode={Uri.EscapeDataString(formCode)}";
        return Ok(new
        {
            assetId = asset.AssetId,
            fileName = asset.FileName,
            previewUrl
        });
    }

    [HttpPost("drafts/{draftId:long}/changes/remove")]
    [ValidateAntiForgeryToken]
    public IActionResult RemoveDraftChange(long draftId, int organizationId, string formCode, string settingKey, long? expectedDraftRevision = null)
    {
        formCode = FormCodeNormalizer.Normalize(formCode);
        if (AuthorizedActiveDraft(draftId, organizationId, formCode) is null)
        {
            return DraftUnavailableResult(draftId, organizationId, formCode);
        }
        if (!catalog.TryGet(settingKey, out var definition) ||
            !authorization.CanManage(User, organizationId, definition.IsSensitive))
        {
            AuditRestrictedDraftRejection(organizationId, formCode, "Draft mutation removal was rejected.");
            return Forbid();
        }
        try
        {
            if (expectedDraftRevision.HasValue)
            {
                repository.RemoveDraftChange(draftId, settingKey, CatalogByKey, authorization.Describe(User).IsGlobal,
                    CreateAudit(organizationId, formCode), expectedDraftRevision.Value);
            }
            else
            {
                repository.RemoveDraftChange(draftId, settingKey, CatalogByKey, authorization.Describe(User).IsGlobal,
                    CreateAudit(organizationId, formCode));
            }
            TempData["SettingsStatus"] = $"Removed one change from shared draft #{draftId}.";
        }
        catch (UnauthorizedAccessException)
        {
            AuditRestrictedDraftRejection(organizationId, formCode, "Draft mutation removal was rejected.");
            return Forbid();
        }
        catch (DBConcurrencyException)
        {
            return DraftConflictResult(organizationId, formCode);
        }
        return RedirectToAction(nameof(Index), new { organizationId, formCode });
    }

    [HttpPost("drafts/{draftId:long}/commit")]
    [ValidateAntiForgeryToken]
    public IActionResult CommitDraft(long draftId, int organizationId, string formCode = "")
    {
        formCode = FormCodeNormalizer.Normalize(formCode);
        var draft = AuthorizedActiveDraft(draftId, organizationId, formCode);
        if (draft is null)
        {
            return DraftUnavailableResult(draftId, organizationId, formCode);
        }
        if (!CanManageDraftLifecycle(draft))
        {
            AuditRestrictedDraftRejection(organizationId, formCode, "Draft commit was rejected.");
            return Forbid();
        }
        try
        {
            repository.CommitDraft(draftId, CatalogByKey, authorization.Describe(User).IsGlobal, CreateAudit(organizationId, formCode));
            cacheInvalidator.LiveSettingsChanged($"CommitDraft draft={draftId} organization={organizationId} form={formCode}");
            TempData["SettingsStatus"] = $"Shared draft #{draftId} was published.";
        }
        catch (UnauthorizedAccessException)
        {
            AuditRestrictedDraftRejection(organizationId, formCode, "Draft commit was rejected.");
            return Forbid();
        }
        catch (DBConcurrencyException)
        {
            return DraftConflictResult(organizationId, formCode);
        }
        catch (SqlException exception) when (exception.Number == 1205)
        {
            return DraftConflictResult(organizationId, formCode);
        }
        catch (InvalidOperationException exception)
        {
            repository.WriteAudit("ValidationFailed", false, CreateAudit(organizationId, formCode), exception.Message, draftId);
            return BadRequest(exception.Message);
        }
        return RedirectToAction(nameof(Index), new { organizationId, formCode });
    }

    [HttpPost("drafts/{draftId:long}/discard")]
    [ValidateAntiForgeryToken]
    public IActionResult DiscardDraft(long draftId, int organizationId, string formCode = "")
    {
        formCode = FormCodeNormalizer.Normalize(formCode);
        var draft = AuthorizedActiveDraft(draftId, organizationId, formCode);
        if (draft is null)
        {
            return DraftUnavailableResult(draftId, organizationId, formCode);
        }
        if (!CanManageDraftLifecycle(draft))
        {
            AuditRestrictedDraftRejection(organizationId, formCode, "Draft discard was rejected.");
            return Forbid();
        }
        try
        {
            repository.DiscardDraft(draftId, CatalogByKey, authorization.Describe(User).IsGlobal, CreateAudit(organizationId, formCode));
            TempData["SettingsStatus"] = $"Shared draft #{draftId} was discarded.";
        }
        catch (UnauthorizedAccessException)
        {
            AuditRestrictedDraftRejection(organizationId, formCode, "Draft discard was rejected.");
            return Forbid();
        }
        catch (DBConcurrencyException)
        {
            return DraftConflictResult(organizationId, formCode);
        }
        return RedirectToAction(nameof(Index), new { organizationId, formCode });
    }

    [HttpPost("drafts/{draftId:long}/preview-links")]
    [ValidateAntiForgeryToken]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult CreatePreviewLink(long draftId, PreviewLinkRequest request)
    {
        var draft = AuthorizedActiveDraft(draftId, request.OrganizationId, request.FormCode);
        if (draft is null)
        {
            return DraftUnavailableResult(draftId, request.OrganizationId, request.FormCode);
        }
        if (!CanManageDraftLifecycle(draft))
        {
            AuditRestrictedDraftRejection(request.OrganizationId, request.FormCode, "Preview-link creation was rejected.");
            return Forbid();
        }
        var operationalBranchId = ResolveOperationalBranch(request.OrganizationId, request.OperationalBranchId);
        if (!operationalBranchId.HasValue)
        {
            ModelState.AddModelError(nameof(request.OperationalBranchId), "Select an operational branch authorized for this preview scope.");
            TempData["SettingsError"] = "Select an operational branch authorized for this preview scope.";
            return RedirectToAction(nameof(Index), new { organizationId = request.OrganizationId, formCode = request.FormCode });
        }
        var organizationName = OrganizationDisplayName(request.OrganizationId);
        var formName = GetFormDisplayName(request.OrganizationId, request.FormCode);
        var branchName = OrganizationDisplayName(operationalBranchId.Value);
        var token = previewTokens.Create();
        try
        {
            repository.CreatePreviewLink(draftId, token.Hash, request.AllowLiveSubmission, operationalBranchId.Value,
                settingsOptions.PreviewLinkLifetimeHours, CatalogByKey,
                authorization.Describe(User).IsGlobal, CreateAudit(request.OrganizationId, request.FormCode));
        }
        catch (UnauthorizedAccessException)
        {
            AuditRestrictedDraftRejection(request.OrganizationId, request.FormCode, "Preview-link creation was rejected.");
            return Forbid();
        }
        catch (DBConcurrencyException)
        {
            return DraftConflictResult(request.OrganizationId, request.FormCode);
        }
        var previewUrl = Url.Action("Index", "Preview", new { token = token.Plaintext }, Request.Scheme)!;
        SetPreviewTokenResponseHeaders();
        return View("PreviewLinkCreated", new PreviewLinkCreatedViewModel(
            previewUrl, draftId, request.OrganizationId, organizationName, request.FormCode, formName,
            operationalBranchId.Value, branchName, request.AllowLiveSubmission));
    }

    [HttpPost("preview-links/{previewLinkId:long}/revoke")]
    [ValidateAntiForgeryToken]
    public IActionResult RevokePreviewLink(long previewLinkId)
    {
        var link = repository.GetPreviewLink(previewLinkId);
        if (link is null)
        {
            return Conflict("The preview link no longer exists. Reload the settings page.");
        }
        if (!ValidateScope(link.OrganizationId, link.FormCode) || !CanManagePreviewLink(link))
        {
            AuditRestrictedDraftRejection(link.OrganizationId, link.FormCode, "Preview-link revocation was rejected.");
            return Forbid();
        }
        try
        {
            repository.RevokePreviewLink(previewLinkId, CatalogByKey, authorization.Describe(User).IsGlobal, CreateAudit(link.OrganizationId, link.FormCode));
            TempData["SettingsStatus"] = $"Preview link #{previewLinkId} was revoked.";
        }
        catch (UnauthorizedAccessException)
        {
            AuditRestrictedDraftRejection(link.OrganizationId, link.FormCode, "Preview-link revocation was rejected.");
            return Forbid();
        }
        catch (DBConcurrencyException)
        {
            return DraftConflictResult(link.OrganizationId, link.FormCode);
        }
        return RedirectToAction(nameof(Index), new { organizationId = link.OrganizationId, formCode = link.FormCode });
    }

    [HttpPost("preview-links/{previewLinkId:long}/restore")]
    [ValidateAntiForgeryToken]
    public IActionResult RestorePreviewLink(long previewLinkId) =>
        ChangeInactivePreviewLink(previewLinkId, restore: true);

    [HttpPost("preview-links/{previewLinkId:long}/delete")]
    [ValidateAntiForgeryToken]
    public IActionResult DeletePreviewLink(long previewLinkId) =>
        ChangeInactivePreviewLink(previewLinkId, restore: false);

    private IActionResult ChangeInactivePreviewLink(long previewLinkId, bool restore)
    {
        var link = repository.GetPreviewLink(previewLinkId);
        if (link is null)
            return Conflict("The preview link no longer exists. Reload the settings page.");
        var operation = restore ? "restoration" : "removal";
        if (!ValidateScope(link.OrganizationId, link.FormCode) || !CanManagePreviewLink(link))
        {
            AuditRestrictedDraftRejection(link.OrganizationId, link.FormCode, $"Preview-link {operation} was rejected.");
            return Forbid();
        }
        try
        {
            if (restore)
            {
                repository.RestorePreviewLink(previewLinkId, settingsOptions.PreviewLinkLifetimeHours, CatalogByKey,
                    authorization.Describe(User).IsGlobal, CreateAudit(link.OrganizationId, link.FormCode));
                TempData["SettingsStatus"] = $"Preview link #{previewLinkId} was restored for another {settingsOptions.PreviewLinkLifetimeHours} hours.";
            }
            else
            {
                repository.DeletePreviewLink(previewLinkId, CatalogByKey, authorization.Describe(User).IsGlobal,
                    CreateAudit(link.OrganizationId, link.FormCode));
                TempData["SettingsStatus"] = $"Preview link #{previewLinkId} was removed.";
            }
        }
        catch (UnauthorizedAccessException)
        {
            AuditRestrictedDraftRejection(link.OrganizationId, link.FormCode, $"Preview-link {operation} was rejected.");
            return Forbid();
        }
        catch (DBConcurrencyException)
        {
            return DraftConflictResult(link.OrganizationId, link.FormCode);
        }
        catch (SqlException exception) when (exception.Number == 1205)
        {
            return DraftConflictResult(link.OrganizationId, link.FormCode);
        }
        return RedirectToAction(nameof(Index), new { organizationId = link.OrganizationId, formCode = link.FormCode });
    }

    [HttpPost("preview-links/{previewLinkId:long}/live-submission")]
    [ValidateAntiForgeryToken]
    public IActionResult ReplacePreviewLinkMode(long previewLinkId, bool allowLiveSubmission)
    {
        var link = repository.GetPreviewLink(previewLinkId);
        if (link is null)
        {
            return Conflict("The preview link no longer exists. Reload the settings page.");
        }
        if (!ValidateScope(link.OrganizationId, link.FormCode) || !CanManagePreviewLink(link))
        {
            AuditRestrictedDraftRejection(link.OrganizationId, link.FormCode, "Preview live-submission change was rejected.");
            return Forbid();
        }
        if (link.AllowLiveSubmission == allowLiveSubmission)
        {
            return RedirectToAction(nameof(Index), new { organizationId = link.OrganizationId, formCode = link.FormCode });
        }
        var organizationName = OrganizationDisplayName(link.OrganizationId);
        var formName = GetFormDisplayName(link.OrganizationId, link.FormCode);
        var branchName = OrganizationDisplayName(link.OperationalBranchId);
        var replacementToken = previewTokens.Create();
        try
        {
            var replacementId = repository.ReplacePreviewLinkMode(previewLinkId, replacementToken.Hash,
                allowLiveSubmission, CatalogByKey, authorization.Describe(User).IsGlobal,
                CreateAudit(link.OrganizationId, link.FormCode));
            if (!replacementId.HasValue)
            {
                return RedirectToAction(nameof(Index), new { organizationId = link.OrganizationId, formCode = link.FormCode });
            }
        }
        catch (UnauthorizedAccessException)
        {
            AuditRestrictedDraftRejection(link.OrganizationId, link.FormCode, "Preview live-submission change was rejected.");
            return Forbid();
        }
        catch (DBConcurrencyException)
        {
            return DraftConflictResult(link.OrganizationId, link.FormCode);
        }
        var previewUrl = Url.Action("Index", "Preview", new { token = replacementToken.Plaintext }, Request.Scheme)!;
        SetPreviewTokenResponseHeaders();
        return View("PreviewLinkCreated", new PreviewLinkCreatedViewModel(
            previewUrl, link.DraftId, link.OrganizationId, organizationName, link.FormCode, formName,
            link.OperationalBranchId, branchName, allowLiveSubmission));
    }

    [HttpGet("audit")]
    public IActionResult Audit(string? search)
    {
        var principal = RequireManager();
        if (principal is null)
        {
            return Forbid();
        }
        int? libraryId = principal.IsGlobal ? null : GetLibraryId(principal.OrganizationId!.Value);
        var rows = SettingsAuditVisibility.ForAdministrator(
            repository.SearchAudit(libraryId, principal.IsGlobal, search), principal.IsGlobal).ToList();
        var targetLibraryIds = rows.Select(row => row.TargetLibraryId ?? row.TargetOrganizationId).Distinct().ToList();
        var formMetadata = targetLibraryIds.Count == 0
            ? []
            : repository.GetFormCodesForLibraries(targetLibraryIds, settingsOptions.SystemOrganizationId) ?? [];
        var systemForms = formMetadata.Where(form => form.OrganizationId == settingsOptions.SystemOrganizationId).ToList();
        var formNamesByLibrary = targetLibraryIds.ToDictionary(id => id, id => formMetadata
            .Where(form => form.OrganizationId == id)
            .Concat(id == settingsOptions.SystemOrganizationId ? [] : systemForms)
            .GroupBy(form => form.FormCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().DisplayName, StringComparer.OrdinalIgnoreCase));
        var catalogByKey = CatalogByKey;
        var events = rows.Select(row => SettingsAuditPresenter.Present(row, principal.IsGlobal,
            settingsOptions.SystemOrganizationId,
            id => cache.OrganizationCache.FirstOrDefault(organization => organization.OrganizationID == id)?.Name,
            (ownerId, formCode) => formNamesByLibrary.TryGetValue(ownerId, out var formNames) &&
                formNames.TryGetValue(formCode, out var displayName) ? displayName : null,
            catalogByKey)).ToList();
        return View(new SettingsAuditViewModel
        {
            SearchText = search ?? string.Empty,
            IsGlobalAdministrator = principal.IsGlobal,
            Events = events
        });
    }

    [HttpGet("forms")]
    public IActionResult Forms(int? libraryId)
    {
        var principal = RequireManager();
        if (principal is null)
        {
            return Forbid();
        }
        var targetLibrary = libraryId ?? (principal.IsGlobal ? settingsOptions.SystemOrganizationId : GetLibraryId(principal.OrganizationId!.Value));
        if (targetLibrary != settingsOptions.SystemOrganizationId && !authorization.CanManage(User, targetLibrary))
        {
            return Forbid();
        }
        return View(BuildFormsViewModel(targetLibrary, principal.IsGlobal));
    }

    [HttpPost("forms")]
    [ValidateAntiForgeryToken]
    public IActionResult CreateForm(FormCodeRequest request)
    {
        var principal = RequireManager();
        if (principal is null || !CanOwnFormCode(principal, request.OrganizationId))
        {
            return Forbid();
        }
        if (!ValidateFormCodeText(request))
        {
            return View("Forms", BuildFormsViewModel(request.OrganizationId, principal.IsGlobal));
        }
        var actor = User.Identity?.Name ?? "unknown";
        var metadata = new FormCodeMetadata(request.OrganizationId, request.FormCode, request.DisplayName, request.Description, DateTime.UtcNow, actor, DateTime.UtcNow, actor);
        try
        {
            repository.SaveFormCode(metadata, true, CreateAudit(request.OrganizationId, request.FormCode));
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(nameof(request.FormCode), exception.Message);
            return View("Forms", BuildFormsViewModel(request.OrganizationId, principal.IsGlobal));
        }
        cacheInvalidator.LiveSettingsChanged($"CreateForm organization={request.OrganizationId} form={request.FormCode}");
        return RedirectToAction(nameof(Forms), new { libraryId = request.OrganizationId });
    }

    [HttpPost("forms/{formCode}/customize")]
    [ValidateAntiForgeryToken]
    public IActionResult CustomizeForm(string formCode, FormCodeRequest request)
    {
        var principal = RequireManager();
        if (principal is null || formCode != request.FormCode || !CanOwnFormCode(principal, request.OrganizationId))
        {
            return Forbid();
        }
        if (!ValidateFormCodeText(request))
        {
            return View("Forms", BuildFormsViewModel(request.OrganizationId, principal.IsGlobal));
        }
        var actor = User.Identity?.Name ?? "unknown";
        var existing = repository.GetFormCodes(request.OrganizationId, settingsOptions.SystemOrganizationId)
            .FirstOrDefault(form => form.OrganizationId == request.OrganizationId && form.FormCode.Equals(formCode, StringComparison.OrdinalIgnoreCase));
        if (existing is not null && !request.ExpectedModifiedAtUtc.HasValue)
        {
            return DraftConflictResult(request.OrganizationId, formCode);
        }
        try
        {
            repository.SaveFormCode(new(request.OrganizationId, formCode, request.DisplayName, request.Description, DateTime.UtcNow, actor, DateTime.UtcNow, actor), existing is null,
                CreateAudit(request.OrganizationId, formCode), request.ExpectedModifiedAtUtc);
        }
        catch (DBConcurrencyException)
        {
            return DraftConflictResult(request.OrganizationId, formCode);
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(nameof(request.FormCode), exception.Message);
            return View("Forms", BuildFormsViewModel(request.OrganizationId, principal.IsGlobal));
        }
        cacheInvalidator.LiveSettingsChanged($"CustomizeForm organization={request.OrganizationId} form={formCode}");
        return RedirectToAction(nameof(Forms), new { libraryId = request.OrganizationId });
    }

    [HttpPost("forms/{formCode}/edit")]
    [ValidateAntiForgeryToken]
    public IActionResult EditForm(string formCode, FormCodeRequest request)
    {
        var principal = RequireManager();
        if (principal is null || formCode != request.FormCode || !CanOwnFormCode(principal, request.OrganizationId))
        {
            return Forbid();
        }
        if (!ValidateFormCodeText(request))
        {
            return View("Forms", BuildFormsViewModel(request.OrganizationId, principal.IsGlobal));
        }
        if (!request.ExpectedModifiedAtUtc.HasValue)
        {
            return DraftConflictResult(request.OrganizationId, formCode);
        }
        var actor = User.Identity?.Name ?? "unknown";
        try
        {
            repository.SaveFormCode(new(request.OrganizationId, formCode, request.DisplayName, request.Description, DateTime.UtcNow, actor, DateTime.UtcNow, actor), false,
                CreateAudit(request.OrganizationId, formCode), request.ExpectedModifiedAtUtc);
        }
        catch (DBConcurrencyException)
        {
            return DraftConflictResult(request.OrganizationId, formCode);
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(nameof(request.FormCode), exception.Message);
            return View("Forms", BuildFormsViewModel(request.OrganizationId, principal.IsGlobal));
        }
        cacheInvalidator.LiveSettingsChanged($"EditForm organization={request.OrganizationId} form={formCode}");
        return RedirectToAction(nameof(Forms), new { libraryId = request.OrganizationId });
    }

    [HttpGet("forms/{formCode}/delete")]
    public IActionResult ConfirmDeleteForm(string formCode, int organizationId)
    {
        var principal = RequireManager();
        if (principal is null || !CanDeleteFormCode(principal, organizationId, formCode))
        {
            return Forbid();
        }
        var organizations = AffectedOrganizations(organizationId);
        var snapshot = repository.GetFormCodeDeletionSnapshot(
            organizationId, formCode, settingsOptions.SystemOrganizationId, organizations);
        if (snapshot is null)
        {
            return NotFound("The selected form code is not owned by this scope.");
        }
        return View(new DeleteFormCodeViewModel
        {
            OrganizationId = organizationId,
            OwnerOrganizationName = cache.GetOrg(organizationId).Name,
            FormCode = formCode,
            Kind = snapshot.Target.Kind,
            IsLegacy = snapshot.Target.IsLegacy,
            SnapshotFingerprint = snapshot.Fingerprint,
            AffectedOrganizationNames = snapshot.AffectedOrganizationIds.Select(OrganizationDisplayName).ToList(),
            Impact = snapshot.Impact
        });
    }

    [HttpPost("forms/{formCode}/delete")]
    [ValidateAntiForgeryToken]
    public IActionResult DeleteForm(string formCode, int organizationId, FormCodeDeletionKind kind, bool isLegacy, string snapshotFingerprint)
    {
        var principal = RequireManager();
        if (principal is null || !CanDeleteFormCode(principal, organizationId, formCode))
        {
            return Forbid();
        }
        try
        {
            repository.DeleteFormCode(new FormCodeDeletionTarget(organizationId, formCode, kind, isLegacy), snapshotFingerprint,
                settingsOptions.SystemOrganizationId, AffectedOrganizations(organizationId), CreateAudit(organizationId, formCode));
        }
        catch (DBConcurrencyException exception)
        {
            return Conflict(exception.Message);
        }
        catch (SqlException exception) when (exception.Number == 1205)
        {
            return Conflict("The form-code deletion conflicted with another settings change. Review the deletion impact again.");
        }
        cacheInvalidator.LiveSettingsChanged($"DeleteForm organization={organizationId} form={formCode}");
        return RedirectToAction(nameof(Forms), new { libraryId = organizationId });
    }

    private SettingsPrincipal? RequireManager()
    {
        var principal = authorization.Describe(User);
        return principal.HasRole && principal.OrganizationId.HasValue ? principal : null;
    }

    private void SetPreviewTokenResponseHeaders()
    {
        Response.Headers.CacheControl = "no-store, no-cache, max-age=0";
        Response.Headers.Pragma = "no-cache";
        Response.Headers["Referrer-Policy"] = "no-referrer";
    }

    private FormsViewModel BuildFormsViewModel(int libraryId, bool isGlobal)
    {
        var forms = repository.GetFormCodes(libraryId, settingsOptions.SystemOrganizationId);
        var legacyForms = formCodeAvailability.GetLegacy(libraryId);
        var organizationIds = forms.Select(form => form.OrganizationId)
            .Concat(legacyForms.Select(form => form.OwnerOrganizationId))
            .Append(libraryId)
            .Append(settingsOptions.SystemOrganizationId)
            .Distinct()
            .ToList();
        var organizationNames = organizationIds.ToDictionary(id => id, OrganizationDisplayName);
        return new FormsViewModel
        {
            LibraryId = libraryId,
            LibraryName = OrganizationDisplayName(libraryId),
            SystemOrganizationId = settingsOptions.SystemOrganizationId,
            IsGlobal = isGlobal,
            Forms = forms,
            LegacyForms = legacyForms,
            OrganizationNames = organizationNames
        };
    }

    private bool ValidateScope(int organizationId, string formCode) =>
        RequireManager() is not null && authorization.CanManage(User, organizationId) && formCodeAvailability.IsAvailable(organizationId, formCode);

    private IActionResult DraftConflictResult(int organizationId, string formCode)
    {
        TempData["SettingsError"] = "The shared draft changed while you were working. Reloaded values are shown below. Review them before trying again.";
        return RedirectToAction(nameof(Index), new { organizationId, formCode });
    }

    private string GetFormDisplayName(int organizationId, string formCode)
    {
        if (formCode.Length == 0)
        {
            return "Default form";
        }
        var libraryId = organizationId == settingsOptions.SystemOrganizationId
            ? settingsOptions.SystemOrganizationId
            : GetLibraryId(organizationId);
        try
        {
            var metadata = repository.GetFormCodes(libraryId, settingsOptions.SystemOrganizationId) ?? [];
            var preferred = metadata.FirstOrDefault(form =>
                form.OrganizationId == libraryId &&
                form.FormCode.Equals(formCode, StringComparison.OrdinalIgnoreCase))
                ?? metadata.FirstOrDefault(form =>
                    form.FormCode.Equals(formCode, StringComparison.OrdinalIgnoreCase));
            return preferred?.DisplayName ?? formCode;
        }
        catch (SqlException)
        {
            return formCode;
        }
        catch (InvalidOperationException)
        {
            return formCode;
        }
    }

    private SettingDraft? AuthorizedActiveDraft(long draftId, int organizationId, string formCode)
    {
        if (!ValidateScope(organizationId, formCode))
        {
            return null;
        }
        var draft = repository.GetDraft(draftId);
        return draft is { Status: DraftStatus.Active } && draft.OrganizationId == organizationId && draft.FormCode.Equals(formCode, StringComparison.OrdinalIgnoreCase) ? draft : null;
    }

    private IActionResult DraftUnavailableResult(long draftId, int organizationId, string formCode)
    {
        if (!ValidateScope(organizationId, formCode))
        {
            return Forbid();
        }

        var draft = repository.GetDraft(draftId);
        if (draft is null ||
            (draft.OrganizationId == organizationId && draft.FormCode.Equals(formCode, StringComparison.OrdinalIgnoreCase)))
        {
            return Conflict("The shared draft is no longer active. Reload the settings page.");
        }

        return Forbid();
    }

    private bool DraftContainsSensitiveChanges(SettingDraft draft) => draft.Changes.Any(change =>
        catalog.TryGet(change.Key, out var definition) && definition.IsSensitive);

    private bool CanManageDraftLifecycle(SettingDraft draft) =>
        !DraftContainsSensitiveChanges(draft) || authorization.Describe(User).IsGlobal;

    private bool CanManagePreviewLink(PreviewLinkRecord link)
    {
        var draft = repository.GetDraft(link.DraftId);
        return draft is not null && CanManageDraftLifecycle(draft);
    }

    private void AuditRestrictedDraftRejection(int organizationId, string formCode, string reason)
    {
        try
        {
            repository.WriteAudit("RestrictedDraftOperationRejected", false, CreateAudit(organizationId, formCode), reason);
        }
        catch
        {
            // Authorization rejection must not fail open if its target cannot be audited.
        }
    }

    private List<SettingMutation> ValidateMutations(IEnumerable<SettingMutationInput> inputs, int organizationId, string formCode)
    {
        var result = new List<SettingMutation>();
        foreach (var input in inputs)
        {
            if (!catalog.TryGet(input.Key, out var definition) || !authorization.CanManage(User, organizationId, definition.IsSensitive))
            {
                ModelState.AddModelError("setting", "One or more submitted settings are unrecognized or inaccessible.");
                continue;
            }
            if (!DraftOperationValidation.TryParseSupported(input.Operation, out var operation))
            {
                ModelState.AddModelError(input.Key, "Invalid operation.");
                continue;
            }
            var normalizedValue = operation == DraftOperation.Upsert
                ? SafeHtmlPolicy.SanitizeForSetting(definition, input.Value)
                : null;
            var error = operation == DraftOperation.Upsert ? definition.Validate(normalizedValue) : null;
            if (error is not null)
            {
                ModelState.AddModelError(input.Key, error);
                continue;
            }
            if (operation == DraftOperation.Upsert && definition.ValueType == SettingValueType.Image)
            {
                if (!int.TryParse(normalizedValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var assetId) ||
                    assetAuthorization.GetAuthorizedMetadata(assetId, organizationId, formCode) is null)
                {
                    ModelState.AddModelError(input.Key, "The uploaded image is missing or is not available in this settings scope.");
                    continue;
                }
            }
            result.Add(new SettingMutation(input.Key, operation, normalizedValue));
        }
        if (result.Count == 0)
        {
            ModelState.AddModelError("changes", "Submit at least one setting change.");
        }
        return result;
    }

    private List<ScopeOption> GetAuthorizedScopes(SettingsPrincipal principal)
    {
        if (principal.IsGlobal)
        {
            var libraries = cache.OrganizationCache.Where(organization => organization.OrganizationCodeID == 2)
                .ToDictionary(organization => organization.OrganizationID);
            var scopes = new List<ScopeOption>
            {
                new(settingsOptions.SystemOrganizationId, "System defaults", ScopeOptionGroup.System)
            };
            scopes.AddRange(libraries.Values.OrderBy(library => library.Name, StringComparer.OrdinalIgnoreCase)
                .Select(library => new ScopeOption(library.OrganizationID, library.Name, ScopeOptionGroup.Libraries)));
            scopes.AddRange(cache.OrganizationCache.Where(organization => organization.OrganizationCodeID == 3)
                .Select(branch =>
                {
                    var parentName = branch.ParentOrganizationID.HasValue && libraries.TryGetValue(branch.ParentOrganizationID.Value, out var parent)
                        ? parent.Name : string.Empty;
                    var label = string.IsNullOrEmpty(parentName) ? branch.Name : $"{parentName} — {branch.Name}";
                    return new ScopeOption(branch.OrganizationID, label, ScopeOptionGroup.Branches, parentName);
                })
                .OrderBy(branch => branch.SortParent, StringComparer.OrdinalIgnoreCase)
                .ThenBy(branch => branch.DisplayName, StringComparer.OrdinalIgnoreCase));
            return scopes;
        }
        var libraryId = GetLibraryId(principal.OrganizationId!.Value);
        var libraryName = cache.GetOrg(libraryId).Name;
        var result = new List<ScopeOption> { new(libraryId, libraryName, ScopeOptionGroup.Libraries) };
        result.AddRange(cache.GetBranches(libraryId).OrderBy(branch => branch.Name, StringComparer.OrdinalIgnoreCase)
            .Select(branch => new ScopeOption(branch.OrganizationID, $"{libraryName} — {branch.Name}", ScopeOptionGroup.Branches, libraryName)));
        return result;
    }

    private IReadOnlyList<ScopeOption> GetPreviewBranches(int scopeOrganizationId)
    {
        return previewBranchEligibility.GetEligibleBranches(scopeOrganizationId, settingsOptions.SystemOrganizationId);
    }

    private int? ResolveOperationalBranch(int scopeOrganizationId, int? requestedBranchId)
    {
        if (scopeOrganizationId != settingsOptions.SystemOrganizationId && cache.GetOrg(scopeOrganizationId).OrganizationCodeID == 3)
        {
            return (requestedBranchId is null || requestedBranchId == scopeOrganizationId) &&
                   previewBranchEligibility.IsEligible(scopeOrganizationId, scopeOrganizationId, settingsOptions.SystemOrganizationId)
                ? scopeOrganizationId
                : null;
        }
        if (!requestedBranchId.HasValue)
        {
            return null;
        }
        return previewBranchEligibility.IsEligible(scopeOrganizationId, requestedBranchId.Value, settingsOptions.SystemOrganizationId)
            ? requestedBranchId
            : null;
    }

    private bool CanOwnFormCode(SettingsPrincipal principal, int ownerOrganizationId)
    {
        if (ownerOrganizationId == settingsOptions.SystemOrganizationId)
        {
            return principal.IsGlobal;
        }
        return authorization.CanManage(User, ownerOrganizationId) && ownerOrganizationId == GetLibraryId(ownerOrganizationId);
    }

    private bool CanDeleteFormCode(SettingsPrincipal principal, int ownerOrganizationId, string formCode) =>
        !string.IsNullOrWhiteSpace(formCode) && CanOwnFormCode(principal, ownerOrganizationId);

    private bool ValidateFormCodeText(FormCodeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.FormCode) || request.FormCode.Length > 64 ||
            !request.FormCode.All(character => char.IsLetterOrDigit(character) || character is '-' or '_') ||
            string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Length > 200 || request.Description?.Length > 2000)
        {
            ModelState.AddModelError(nameof(request.FormCode), "Use a nonblank code of letters, numbers, hyphens, or underscores and a display name.");
            return false;
        }
        return true;
    }

    private IReadOnlyCollection<int> AffectedOrganizations(int ownerOrganizationId)
    {
        if (ownerOrganizationId == settingsOptions.SystemOrganizationId)
        {
            return cache.OrganizationCache.Select(organization => organization.OrganizationID).Append(settingsOptions.SystemOrganizationId).Distinct().ToList();
        }
        return cache.GetBranches(ownerOrganizationId).Select(branch => branch.OrganizationID).Append(ownerOrganizationId).ToList();
    }

    private int GetLibraryId(int organizationId) => cache.OrganizationCache.GetLibrary(organizationId).OrganizationID;

    private string OrganizationDisplayName(int organizationId) => organizationId == settingsOptions.SystemOrganizationId
        ? "System defaults"
        : cache.OrganizationCache.FirstOrDefault(organization => organization.OrganizationID == organizationId)?.Name
            ?? $"Organization {organizationId}";

    private string GetOrganizationName(int organizationId) => organizationId == settingsOptions.SystemOrganizationId
        ? "System defaults"
        : cache.GetOrg(organizationId).DisplayName;

    private AuditContext CreateAudit(int organizationId, string formCode)
    {
        var actor = authorization.Describe(User);
        var targetLibraryId = organizationId == settingsOptions.SystemOrganizationId ? settingsOptions.SystemOrganizationId : GetLibraryId(organizationId);
        return new AuditContext(
            User.FindFirstValue("oid") ?? User.FindFirstValue(ClaimTypes.NameIdentifier),
            User.Identity?.Name,
            actor.OrganizationId,
            organizationId,
            targetLibraryId,
            FormCodeNormalizer.Normalize(formCode),
            HttpContext.TraceIdentifier,
            Request.GetTrueClientIP());
    }

    private void AuditRejected(int organizationId, string formCode, string reason)
    {
        try
        {
            repository.WriteAudit("AuthorizationRejected", false, CreateAudit(organizationId, formCode), reason);
        }
        catch
        {
            // Authorization must still fail when the requested target cannot safely be resolved for auditing.
        }
    }
}
