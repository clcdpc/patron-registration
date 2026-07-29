using System.Security.Claims;
using Clc.PatronRegistration.Helpers;
using Microsoft.Extensions.Options;
using Clc.Polaris.Api;

namespace Clc.PatronRegistration.Web.Settings;

public interface ISettingsAuthorizationService
{
    SettingsPrincipal Describe(ClaimsPrincipal user);
    bool CanManage(ClaimsPrincipal user, int targetOrganizationId, bool sensitive = false);
}
public sealed record SettingsPrincipal(bool HasRole, int? OrganizationId, bool IsGlobal);

public sealed class SettingsAuthorizationService(ICache cache, IOptions<SettingsAdministrationOptions> options) : ISettingsAuthorizationService
{
    private readonly SettingsAdministrationOptions config = options.Value;
    public SettingsPrincipal Describe(ClaimsPrincipal user)
    {
        var hasRole = user.IsInRole(config.RequiredRole);
        var raw = user.Claims.FirstOrDefault(c => c.Type is "organization" or "organization_id" or "extension_Organization")?.Value;
        int? organization = int.TryParse(raw, out var value) ? value : null;
        return new(hasRole, organization, organization == config.GlobalOrganizationId);
    }
    public bool CanManage(ClaimsPrincipal user, int targetOrganizationId, bool sensitive = false)
    {
        var principal = Describe(user);
        if (!principal.HasRole || principal.OrganizationId is null) return false;
        if (principal.IsGlobal) return true;
        if (sensitive || targetOrganizationId == config.SystemOrganizationId) return false;
        if (targetOrganizationId == principal.OrganizationId) return true;
        try
        {
            var targetLibrary = cache.OrganizationCache.GetLibrary(targetOrganizationId).OrganizationID;
            var actorLibrary = cache.OrganizationCache.GetLibrary(principal.OrganizationId.Value).OrganizationID;
            return targetLibrary == actorLibrary;
        }
        catch { return false; }
    }
}
