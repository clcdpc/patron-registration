using Clc.PatronRegistration.Data;
using Clc.PatronRegistration.Helpers;
using Clc.PatronRegistration.Web.Models;
using Clc.Polaris.Api;

namespace Clc.PatronRegistration.Web.Settings;

public interface IPreviewBranchEligibilityService
{
    IReadOnlyList<ScopeOption> GetEligibleBranches(int scopeOrganizationId, int systemOrganizationId);
    bool IsEligible(int scopeOrganizationId, int operationalBranchId, int systemOrganizationId);
}

public sealed class PreviewBranchEligibilityService(IDbHelper db, ICache cache) : IPreviewBranchEligibilityService
{
    public IReadOnlyList<ScopeOption> GetEligibleBranches(int scopeOrganizationId, int systemOrganizationId)
    {
        var eligible = db.GetSelfRegistrationOrganizations()
            .Where(organization => organization.OrganizationCodeID == 3)
            .ToList();
        if (scopeOrganizationId == systemOrganizationId)
        {
            return eligible.Select(ToOption).OrderBy(option => option.DisplayName).ToList();
        }

        var scope = cache.GetOrg(scopeOrganizationId);
        if (scope.OrganizationCodeID == 3)
        {
            return eligible.Any(branch => branch.OrganizationID == scopeOrganizationId)
                ? [ToOption(scope)]
                : [];
        }
        return eligible
            .Where(branch => branch.ParentOrganizationID == scopeOrganizationId)
            .Select(ToOption)
            .OrderBy(option => option.DisplayName)
            .ToList();
    }

    public bool IsEligible(int scopeOrganizationId, int operationalBranchId, int systemOrganizationId) =>
        GetEligibleBranches(scopeOrganizationId, systemOrganizationId)
            .Any(branch => branch.OrganizationId == operationalBranchId);

    private static ScopeOption ToOption(Clc.Polaris.Api.Models.OrganizationsGetRow organization) =>
        new(organization.OrganizationID, organization.DisplayName);
}
