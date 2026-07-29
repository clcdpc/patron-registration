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
using Microsoft.Extensions.Options;

namespace Clc.PatronRegistration.Web.Controllers;

[Authorize]
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
    IOptions<SettingsAdministrationOptions> options) : Controller
{
    private readonly SettingsAdministrationOptions settingsOptions = options.Value;
    private IReadOnlyDictionary<string, SettingDefinition> CatalogByKey =>
        catalog.All.ToDictionary(setting => setting.Key, StringComparer.OrdinalIgnoreCase);

    [HttpGet("")]
    public IActionResult Index(int? organizationId, string formCode = "")
    {
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

        var libraryId = target == settingsOptions.SystemOrganizationId ? settingsOptions.SystemOrganizationId : GetLibraryId(target);
        var draft = repository.GetActiveDraft(target, formCode);
        var resolver = new SettingsResolver();
        var visibleSettings = catalog.All.Where(setting => principal.IsGlobal || !setting.IsSensitive).ToList();
        var rows = new List<SettingRowViewModel>(visibleSettings.Count);

        for (var index = 0; index < visibleSettings.Count; index++)
        {
            var definition = visibleSettings[index];
            var draftChange = draft?.Changes.FirstOrDefault(change => change.Key.Equals(definition.Key, StringComparison.OrdinalIgnoreCase));
            rows.Add(new SettingRowViewModel(
                $"setting-{index}",
                definition,
                resolver.Resolve(cache.SettingsCache, definition.Key, target, libraryId, formCode, settingsOptions.SystemOrganizationId),
                draftChange?.Value,
                draftChange?.Operation,
                draft?.DraftId));
        }

        var model = new SettingsIndexViewModel
        {
            OrganizationId = target,
            OrganizationName = GetOrganizationName(target),
            LibraryId = libraryId,
            FormCode = formCode,
            IsGlobal = principal.IsGlobal,
            ScopeVersion = repository.GetVersion(target, formCode),
            ActiveDraft = draft,
            HasRestrictedDraftChanges = draft is not null && DraftContainsSensitiveChanges(draft),
            CanManageRestrictedDraft = principal.IsGlobal,
            PreviewLinks = draft is null ? [] : repository.GetPreviewLinks(draft.DraftId),
            PreviewBranches = GetPreviewBranches(target),
            Scopes = GetAuthorizedScopes(principal),
            FormCodes = formCodeAvailability.GetAvailable(libraryId).ToList(),
            Settings = rows
        };
        return View(model);
    }

    [HttpPost("direct-save")]
    [ValidateAntiForgeryToken]
    public IActionResult DirectSave(SaveSettingsRequest request)
    {
        if (!ValidateScope(request.OrganizationId, request.FormCode))
        {
            AuditRejected(request.OrganizationId, request.FormCode, "Direct-save scope tampering rejected.");
            return Forbid();
        }

        var mutations = ValidateMutations(request.Changes, request.OrganizationId);
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
            cacheInvalidator.LiveSettingsChanged();
        }
        catch (DBConcurrencyException exception)
        {
            return Conflict(exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            repository.WriteAudit("ValidationFailed", false, CreateAudit(request.OrganizationId, request.FormCode), exception.Message);
            return BadRequest(exception.Message);
        }

        return RedirectToAction(nameof(Index), new { organizationId = request.OrganizationId, formCode = request.FormCode });
    }

    [HttpPost("drafts")]
    [ValidateAntiForgeryToken]
    public IActionResult CreateDraft(int organizationId, string formCode = "")
    {
        if (!ValidateScope(organizationId, formCode))
        {
            return Forbid();
        }
        var draftId = repository.CreateDraft(organizationId, formCode, CreateAudit(organizationId, formCode));
        return RedirectToAction(nameof(Index), new { organizationId, formCode, draftId });
    }

    [HttpPost("drafts/{draftId:long}/changes")]
    [ValidateAntiForgeryToken]
    public IActionResult SaveDraft(long draftId, DraftChangesRequest request)
    {
        var draft = AuthorizedActiveDraft(draftId, request.OrganizationId, request.FormCode);
        if (draft is null)
        {
            return DraftUnavailableResult(draftId, request.OrganizationId, request.FormCode);
        }
        var mutations = ValidateMutations(request.Changes, request.OrganizationId);
        if (!ModelState.IsValid)
        {
            repository.WriteAudit("ValidationFailed", false, CreateAudit(request.OrganizationId, request.FormCode), "Draft changes were invalid.", draftId);
            TempData["SettingsError"] = string.Join(" ", ModelState.Values.SelectMany(value => value.Errors).Select(error => error.ErrorMessage));
            TempData["SettingsErrorGroup"] = request.Changes.FirstOrDefault(change => ModelState.ContainsKey(change.Key))?.Key.Split('.')[0];
            return RedirectToAction(nameof(Index), new { organizationId = request.OrganizationId, formCode = request.FormCode });
        }
        try
        {
            repository.SaveDraftChanges(draftId, mutations, CatalogByKey, CreateAudit(request.OrganizationId, request.FormCode));
        }
        catch (DBConcurrencyException exception)
        {
            return Conflict(exception.Message);
        }
        return RedirectToAction(nameof(Index), new { organizationId = request.OrganizationId, formCode = request.FormCode });
    }

    [HttpPost("drafts/{draftId:long}/changes/remove")]
    [ValidateAntiForgeryToken]
    public IActionResult RemoveDraftChange(long draftId, int organizationId, string formCode, string settingKey)
    {
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
            repository.RemoveDraftChange(draftId, settingKey, CatalogByKey, authorization.Describe(User).IsGlobal, CreateAudit(organizationId, formCode));
        }
        catch (UnauthorizedAccessException)
        {
            AuditRestrictedDraftRejection(organizationId, formCode, "Draft mutation removal was rejected.");
            return Forbid();
        }
        catch (DBConcurrencyException exception)
        {
            return Conflict(exception.Message);
        }
        return RedirectToAction(nameof(Index), new { organizationId, formCode });
    }

    [HttpPost("drafts/{draftId:long}/commit")]
    [ValidateAntiForgeryToken]
    public IActionResult CommitDraft(long draftId, int organizationId, string formCode = "")
    {
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
            cacheInvalidator.LiveSettingsChanged();
        }
        catch (UnauthorizedAccessException)
        {
            AuditRestrictedDraftRejection(organizationId, formCode, "Draft commit was rejected.");
            return Forbid();
        }
        catch (DBConcurrencyException exception)
        {
            return Conflict(exception.Message);
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
        }
        catch (UnauthorizedAccessException)
        {
            AuditRestrictedDraftRejection(organizationId, formCode, "Draft discard was rejected.");
            return Forbid();
        }
        catch (DBConcurrencyException exception)
        {
            return Conflict(exception.Message);
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
        var token = previewTokens.Create();
        var operationalBranchId = ResolveOperationalBranch(request.OrganizationId, request.OperationalBranchId);
        if (!operationalBranchId.HasValue)
        {
            ModelState.AddModelError(nameof(request.OperationalBranchId), "Select an operational branch authorized for this preview scope.");
            TempData["SettingsError"] = "Select an operational branch authorized for this preview scope.";
            return RedirectToAction(nameof(Index), new { organizationId = request.OrganizationId, formCode = request.FormCode });
        }
        try
        {
            repository.CreatePreviewLink(draftId, token.Hash, request.AllowLiveSubmission, operationalBranchId.Value, CatalogByKey,
                authorization.Describe(User).IsGlobal, CreateAudit(request.OrganizationId, request.FormCode));
        }
        catch (UnauthorizedAccessException)
        {
            AuditRestrictedDraftRejection(request.OrganizationId, request.FormCode, "Preview-link creation was rejected.");
            return Forbid();
        }
        catch (DBConcurrencyException exception)
        {
            return Conflict(exception.Message);
        }
        var previewUrl = Url.Action("Index", "Preview", new { token = token.Plaintext }, Request.Scheme)!;
        SetPreviewTokenResponseHeaders();
        return View("PreviewLinkCreated", model: previewUrl);
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
        }
        catch (UnauthorizedAccessException)
        {
            AuditRestrictedDraftRejection(link.OrganizationId, link.FormCode, "Preview-link revocation was rejected.");
            return Forbid();
        }
        catch (DBConcurrencyException exception)
        {
            return Conflict(exception.Message);
        }
        return RedirectToAction(nameof(Index), new { organizationId = link.OrganizationId, formCode = link.FormCode });
    }

    [HttpPost("preview-links/{previewLinkId:long}/live-submission")]
    [ValidateAntiForgeryToken]
    public IActionResult ToggleLiveSubmission(long previewLinkId, bool allowLiveSubmission)
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
        try
        {
            repository.TogglePreviewLiveSubmission(previewLinkId, allowLiveSubmission, CatalogByKey,
                authorization.Describe(User).IsGlobal, CreateAudit(link.OrganizationId, link.FormCode));
        }
        catch (UnauthorizedAccessException)
        {
            AuditRestrictedDraftRejection(link.OrganizationId, link.FormCode, "Preview live-submission change was rejected.");
            return Forbid();
        }
        catch (DBConcurrencyException exception)
        {
            return Conflict(exception.Message);
        }
        return RedirectToAction(nameof(Index), new { organizationId = link.OrganizationId, formCode = link.FormCode });
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
        var rows = repository.SearchAudit(libraryId, principal.IsGlobal, search);
        return View(SettingsAuditVisibility.ForAdministrator(rows, principal.IsGlobal));
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
        cacheInvalidator.LiveSettingsChanged();
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
            .Any(form => form.OrganizationId == request.OrganizationId && form.FormCode.Equals(formCode, StringComparison.OrdinalIgnoreCase));
        try
        {
            repository.SaveFormCode(new(request.OrganizationId, formCode, request.DisplayName, request.Description, DateTime.UtcNow, actor, DateTime.UtcNow, actor), !existing, CreateAudit(request.OrganizationId, formCode));
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(nameof(request.FormCode), exception.Message);
            return View("Forms", BuildFormsViewModel(request.OrganizationId, principal.IsGlobal));
        }
        cacheInvalidator.LiveSettingsChanged();
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
        var actor = User.Identity?.Name ?? "unknown";
        try
        {
            repository.SaveFormCode(new(request.OrganizationId, formCode, request.DisplayName, request.Description, DateTime.UtcNow, actor, DateTime.UtcNow, actor), false, CreateAudit(request.OrganizationId, formCode));
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(nameof(request.FormCode), exception.Message);
            return View("Forms", BuildFormsViewModel(request.OrganizationId, principal.IsGlobal));
        }
        cacheInvalidator.LiveSettingsChanged();
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
        var target = repository.GetFormCodeDeletionTarget(
            organizationId, formCode, settingsOptions.SystemOrganizationId, organizations);
        if (target is null)
        {
            return NotFound("The selected form code is not owned by this scope.");
        }
        return View(new DeleteFormCodeViewModel
        {
            OrganizationId = organizationId,
            OwnerOrganizationName = cache.GetOrg(organizationId).Name,
            FormCode = formCode,
            Kind = target.Kind,
            IsLegacy = target.IsLegacy,
            AffectedOrganizationNames = organizations.Select(id => cache.GetOrg(id).Name).ToList(),
            Impact = repository.GetFormCodeImpact(organizationId, formCode, organizations)
        });
    }

    [HttpPost("forms/{formCode}/delete")]
    [ValidateAntiForgeryToken]
    public IActionResult DeleteForm(string formCode, int organizationId, FormCodeDeletionKind kind, bool isLegacy)
    {
        var principal = RequireManager();
        if (principal is null || !CanDeleteFormCode(principal, organizationId, formCode))
        {
            return Forbid();
        }
        try
        {
            repository.DeleteFormCode(new FormCodeDeletionTarget(organizationId, formCode, kind, isLegacy),
                settingsOptions.SystemOrganizationId, AffectedOrganizations(organizationId), CreateAudit(organizationId, formCode));
        }
        catch (DBConcurrencyException exception)
        {
            return Conflict(exception.Message);
        }
        cacheInvalidator.LiveSettingsChanged();
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
        Response.Headers.ReferrerPolicy = "no-referrer";
    }

    private FormsViewModel BuildFormsViewModel(int libraryId, bool isGlobal) => new()
    {
        LibraryId = libraryId,
        SystemOrganizationId = settingsOptions.SystemOrganizationId,
        IsGlobal = isGlobal,
        Forms = repository.GetFormCodes(libraryId, settingsOptions.SystemOrganizationId),
        LegacyForms = formCodeAvailability.GetLegacy(libraryId)
    };

    private bool ValidateScope(int organizationId, string formCode) =>
        RequireManager() is not null && authorization.CanManage(User, organizationId) && formCodeAvailability.IsAvailable(organizationId, formCode);

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

    private List<SettingMutation> ValidateMutations(IEnumerable<SettingMutationInput> inputs, int organizationId)
    {
        var result = new List<SettingMutation>();
        foreach (var input in inputs)
        {
            if (!catalog.TryGet(input.Key, out var definition) || !authorization.CanManage(User, organizationId, definition.IsSensitive))
            {
                ModelState.AddModelError("setting", "One or more submitted settings are unrecognized or inaccessible.");
                continue;
            }
            if (!Enum.TryParse<DraftOperation>(input.Operation, out var operation))
            {
                ModelState.AddModelError(input.Key, "Invalid operation.");
                continue;
            }
            var error = operation == DraftOperation.Upsert ? definition.Validate(input.Value) : null;
            if (error is not null)
            {
                ModelState.AddModelError(input.Key, error);
            }
            result.Add(new SettingMutation(input.Key, operation, operation == DraftOperation.RemoveOverride ? null : input.Value));
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
            var scopes = cache.OrganizationCache
                .Where(organization => organization.OrganizationCodeID is 2 or 3)
                .Select(organization => new ScopeOption(organization.OrganizationID, organization.DisplayName))
                .ToList();
            scopes.Insert(0, new ScopeOption(settingsOptions.SystemOrganizationId, "System defaults"));
            return scopes;
        }
        var libraryId = GetLibraryId(principal.OrganizationId!.Value);
        var result = new List<ScopeOption> { new(libraryId, GetOrganizationName(libraryId)) };
        result.AddRange(cache.GetBranches(libraryId).Select(branch => new ScopeOption(branch.OrganizationID, branch.DisplayName)));
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
            formCode,
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
