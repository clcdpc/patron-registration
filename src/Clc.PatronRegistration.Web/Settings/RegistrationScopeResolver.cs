using Clc.PatronRegistration.Configuration;
using Clc.PatronRegistration.Data;
using Clc.PatronRegistration.Helpers;
using Clc.Polaris.Api.Models;

namespace Clc.PatronRegistration.Web.Settings;

public sealed record RegistrationScopeResolution(bool IsValid, ISettingProvider Settings);

public interface IRegistrationScopeResolver
{
    RegistrationScopeResolution ResolveForSubmission(HttpContext httpContext, ISettingProvider requestSettings, int submittedBranchId);

    IReadOnlyList<OrganizationsGetRow> GetAvailableBranches(HttpContext httpContext, ISettingProvider requestSettings);
}

/// <summary>
/// Resolves the provider for the branch that will receive a registration. The route provider
/// is only the initial page scope; a posted branch is accepted only when it is an eligible
/// registration branch in that scope.
/// </summary>
public sealed class RegistrationScopeResolver(
    IDbHelper db,
    ICache cache,
    IRequestSettingProviderResolver settingProviderResolver) : IRegistrationScopeResolver
{
    public RegistrationScopeResolution ResolveForSubmission(
        HttpContext httpContext,
        ISettingProvider requestSettings,
        int submittedBranchId)
    {
        var branch = GetCandidateBranches(requestSettings)
            .FirstOrDefault(candidate => candidate.OrganizationID == submittedBranchId);
        if (branch is null)
        {
            return new(false, requestSettings);
        }

        return new(true, settingProviderResolver.ResolveForOrganization(httpContext, branch.OrganizationID));
    }

    public IReadOnlyList<OrganizationsGetRow> GetAvailableBranches(
        HttpContext httpContext,
        ISettingProvider requestSettings)
    {
        return GetCandidateBranches(requestSettings)
            .Where(branch => !settingProviderResolver
                .ResolveForOrganization(httpContext, branch.OrganizationID)
                .DisableBranch)
            .ToList();
    }

    private IReadOnlyList<OrganizationsGetRow> GetCandidateBranches(ISettingProvider requestSettings)
    {
        var scope = cache.GetOrg(requestSettings.OrganizationId);
        return scope.OrganizationCodeID switch
        {
            // A branch route remains scoped to its parent library for the
            // optional home-branch selector. The submitted branch is still
            // resolved and validated independently below, so settings and
            // credentials come from the selected sibling branch.
            3 => db.GetSelfRegistrationBranches(scope.ParentOrganizationID).ToList(),
            2 => db.GetSelfRegistrationBranches(scope.OrganizationID).ToList(),
            _ => db.GetSelfRegistrationBranches().ToList()
        };
    }
}
