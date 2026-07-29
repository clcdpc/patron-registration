using System.Data;
using Clc.PatronRegistration.Administration;
using Clc.PatronRegistration.Configuration;
using Clc.PatronRegistration.Helpers;
using Clc.PatronRegistration.Web.Models;
using Clc.PatronRegistration.Web.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Clc.Polaris.Api;

namespace Clc.PatronRegistration.Web.Controllers;

[Authorize]
[Route("settings")]
public sealed class SettingsController(ISettingsAuthorizationService authorization, ISettingsAdministrationRepository repository,
    ISettingCatalog catalog, ICache cache) : Controller
{
    [HttpGet("")]
    public IActionResult Index(int? organizationId, string formCode = "")
    {
        var principal = authorization.Describe(User);
        if (!principal.HasRole || principal.OrganizationId is null) return Forbid();
        var target = organizationId ?? (principal.IsGlobal ? 1 : principal.OrganizationId.Value);
        if (!authorization.CanManage(User, target)) return Forbid();
        var libraryId = target == 1 ? 1 : cache.OrganizationCache.GetLibrary(target).OrganizationID;
        var draft = repository.GetActiveDraft(target, formCode);
        var resolver = new SettingsResolver();
        var visible = catalog.All.Where(x => principal.IsGlobal || !x.IsSensitive);
        var model = new SettingsIndexViewModel { OrganizationId = target, LibraryId = libraryId, FormCode = formCode, IsGlobal = principal.IsGlobal,
            ScopeVersion = repository.GetVersion(target, formCode), ActiveDraftId = draft?.DraftId,
            Settings = visible.Select(def => new SettingRowViewModel(def, resolver.Resolve(cache.SettingsCache, def.Key, target, libraryId, formCode),
                draft?.Changes.FirstOrDefault(x => x.Key.Equals(def.Key, StringComparison.OrdinalIgnoreCase))?.Value,
                draft?.Changes.FirstOrDefault(x => x.Key.Equals(def.Key, StringComparison.OrdinalIgnoreCase))?.Operation)).ToList() };
        return View(model);
    }

    [HttpPost("direct-save")]
    [ValidateAntiForgeryToken]
    public IActionResult DirectSave(SaveSettingsRequest request)
    {
        if (!authorization.CanManage(User, request.OrganizationId)) return Forbid();
        var mutations = new List<SettingMutation>();
        foreach (var input in request.Changes)
        {
            if (!catalog.TryGet(input.Key, out var definition) || !authorization.CanManage(User, request.OrganizationId, definition.IsSensitive)) return BadRequest("Unrecognized or inaccessible setting key.");
            if (!Enum.TryParse<DraftOperation>(input.Operation, out var operation)) return BadRequest("Invalid operation.");
            var error = operation == DraftOperation.Upsert ? definition.Validate(input.Value) : null;
            if (error is not null) ModelState.AddModelError(input.Key, error);
            mutations.Add(new(input.Key, operation, input.Value));
        }
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        try { repository.DirectSave(request.OrganizationId, request.FormCode ?? string.Empty, request.ExpectedVersion, mutations, User.Identity?.Name ?? "unknown"); cache.RebuildCache(); }
        catch (DBConcurrencyException ex) { return Conflict(ex.Message); }
        return RedirectToAction(nameof(Index), new { organizationId = request.OrganizationId, formCode = request.FormCode });
    }

    [HttpGet("audit")]
    public IActionResult Audit(string? search)
    {
        var principal = authorization.Describe(User); if (!principal.HasRole || principal.OrganizationId is null) return Forbid();
        int? library = principal.IsGlobal ? null : cache.OrganizationCache.GetLibrary(principal.OrganizationId.Value).OrganizationID;
        return View(repository.SearchAudit(library, search));
    }

    [HttpGet("forms")]
    public IActionResult Forms() => View();
}
