using System.Security.Claims;
using Clc.PatronRegistration.Helpers;
using Clc.Polaris.Api;
using Microsoft.Extensions.Options;

namespace Clc.PatronRegistration.Web.Settings;

public interface ISettingsAuthorizationService
{
    SettingsPrincipal Describe(ClaimsPrincipal user);
    bool CanManage(ClaimsPrincipal user, int targetOrganizationId, bool sensitive = false);
}

public sealed record SettingsPrincipal(bool HasRole, int? OrganizationId, bool IsGlobal);

public sealed class SettingsAuthorizationService(
    ICache cache,
    IOptions<SettingsAdministrationOptions> options) : ISettingsAuthorizationService
{
    private static readonly string[] OrganizationClaimTypes =
    [
        "Clc.OrganizationId",
        "organization",
        "organization_id",
        "extension_Organization"
    ];

    private readonly SettingsAdministrationOptions config = options.Value;

    public SettingsPrincipal Describe(ClaimsPrincipal user)
    {
        var hasRole = user.IsInRole(config.RequiredRole);
        var organizationClaim = user.Claims
            .FirstOrDefault(claim => OrganizationClaimTypes.Contains(claim.Type, StringComparer.OrdinalIgnoreCase))
            ?.Value;
        int? organizationId = int.TryParse(organizationClaim, out var value) ? value : null;
        return new SettingsPrincipal(hasRole, organizationId, organizationId == config.GlobalOrganizationId);
    }

    public bool CanManage(ClaimsPrincipal user, int targetOrganizationId, bool sensitive = false)
    {
        var principal = Describe(user);
        if (!principal.HasRole || principal.OrganizationId is null)
        {
            return false;
        }
        if (principal.IsGlobal)
        {
            return true;
        }
        if (sensitive || targetOrganizationId == config.SystemOrganizationId)
        {
            return false;
        }
        if (targetOrganizationId == principal.OrganizationId)
        {
            return true;
        }

        try
        {
            var targetLibrary = cache.OrganizationCache.GetLibrary(targetOrganizationId).OrganizationID;
            var actorLibrary = cache.OrganizationCache.GetLibrary(principal.OrganizationId.Value).OrganizationID;
            return targetLibrary == actorLibrary;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
