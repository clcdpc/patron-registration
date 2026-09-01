using System.Security.Claims;
using Clc.PatronRegistration.Data;
using Clc.PatronRegistration.Security;
using Clc.PatronRegistration.Web.Settings;
using Microsoft.Extensions.Options;

namespace Clc.PatronRegistration.Tests;

[TestClass]
public sealed class ClcAzureAdClaimsTransformerTests
{
    private const string OrganizationClaimType = "Clc.OrganizationId";
    private const string SettingsRole = "Clc.CardReg.ManageSettings";

    [TestMethod]
    public async Task MappedEmailUpn_AddsExpectedOrganizationClaim()
    {
        var db = new FakeAuthDbHelper { MappedDomain = "library.example", OrganizationId = 7 };
        var principal = Principal(preferredUsername: "patron@library.example");

        await TransformAsync(db, principal);

        Assert.AreEqual("patron@library.example", db.LastOrganizationLookup);
        Assert.AreEqual("7", principal.Claims.Single(claim => claim.Type == OrganizationClaimType).Value);
    }

    [TestMethod]
    public async Task NonEmailPreferredUsername_DoesNotThrowOrAddOrganizationClaim()
    {
        var db = new FakeAuthDbHelper { MappedDomain = "library.example", OrganizationId = 7 };
        var principal = Principal(preferredUsername: "+15551234567");

        await TransformAsync(db, principal);

        Assert.AreEqual("+15551234567", db.LastOrganizationLookup);
        Assert.IsFalse(principal.Claims.Any(claim => claim.Type == OrganizationClaimType));
    }

    [TestMethod]
    public async Task NullOrMissingName_DoesNotThrowOrAddOrganizationClaim()
    {
        var db = new FakeAuthDbHelper { MappedDomain = "library.example", OrganizationId = 7 };
        var principal = Principal();

        await TransformAsync(db, principal);

        Assert.IsNull(db.LastOrganizationLookup);
        Assert.IsFalse(principal.Claims.Any(claim => claim.Type == OrganizationClaimType));
    }

    [TestMethod]
    public async Task WellFormedEmailWithUnmappedDomain_DoesNotAddOrganizationClaim()
    {
        var db = new FakeAuthDbHelper { MappedDomain = "library.example", OrganizationId = 7 };
        var principal = Principal(preferredUsername: "patron@unknown.example");

        await TransformAsync(db, principal);

        Assert.IsFalse(principal.Claims.Any(claim => claim.Type == OrganizationClaimType));
    }

    [TestMethod]
    public async Task RepeatedTransformCalls_DoNotDuplicateOrganizationOrRoleClaims()
    {
        var db = new FakeAuthDbHelper
        {
            MappedDomain = "library.example",
            OrganizationId = 7,
            Roles = [SettingsRole]
        };
        var principal = Principal(
            preferredUsername: "patron@library.example",
            claims: [
                new Claim("groups", "Clc.CardReg.ViewReports"),
                new Claim(OrganizationClaimType, "999")]);

        await TransformAsync(db, principal);
        await TransformAsync(db, principal);

        Assert.AreEqual(1, principal.Claims.Count(claim => claim.Type == OrganizationClaimType));
        Assert.AreEqual("7", principal.Claims.Single(claim => claim.Type == OrganizationClaimType).Value);
        Assert.AreEqual(1, principal.Claims.Count(claim => claim.Type == ClaimTypes.Role && claim.Value == SettingsRole));
        Assert.AreEqual(1, principal.Claims.Count(claim => claim.Type == ClaimTypes.Role && claim.Value == "Clc.CardReg.ViewReports"));
    }

    [TestMethod]
    public async Task SettingsRoleWithoutResolvedOrganization_IsDeniedCleanly()
    {
        var db = new FakeAuthDbHelper
        {
            Roles = [SettingsRole]
        };
        var principal = Principal(preferredUsername: "patron@unknown.example");

        await TransformAsync(db, principal);

        var authorization = new SettingsAuthorizationService(
            new TestCache(),
            Options.Create(new SettingsAdministrationOptions()));

        Assert.IsTrue(principal.IsInRole(SettingsRole));
        Assert.IsNull(authorization.Describe(principal).OrganizationId);
        Assert.IsFalse(authorization.CanManage(principal, 2));
    }

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("15551234567")]
    [DataRow("patron")]
    [DataRow("patron@")]
    [DataRow("@library.example")]
    [DataRow("patron@@library.example")]
    [DataRow("patron@ library.example")]
    public void InvalidLoginIdentifier_GetOrgForUserReturnsNoOrganization(string? identifier)
    {
        var db = new AuthDbHelper("server-that-is-not-used", "test-app");

        Assert.IsNull(db.GetOrgForUser(identifier));
    }

    private static Task<ClaimsPrincipal> TransformAsync(IAuthDbHelper db, ClaimsPrincipal principal) =>
        new ClcAzureAdClaimsTransformer(db).TransformAsync(principal);

    private static ClaimsPrincipal Principal(
        string? name = null,
        string? preferredUsername = null,
        Claim[]? claims = null)
    {
        var allClaims = claims?.ToList() ?? [];
        if (name is not null)
        {
            allClaims.Add(new Claim(ClaimTypes.Name, name));
        }
        if (preferredUsername is not null)
        {
            allClaims.Add(new Claim("preferred_username", preferredUsername));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(allClaims, "test", ClaimTypes.Name, ClaimTypes.Role));
    }

    private sealed class FakeAuthDbHelper : IAuthDbHelper
    {
        public string? MappedDomain { get; init; }
        public int? OrganizationId { get; init; }
        public List<string> Roles { get; init; } = [];
        public string? LastRoleLookup { get; private set; }
        public string? LastOrganizationLookup { get; private set; }

        public List<string> GetRolesForUser(string? username)
        {
            LastRoleLookup = username;
            return Roles;
        }

        public int? GetOrgForUser(string? username)
        {
            LastOrganizationLookup = username;
            if (OrganizationId is null || !AuthDbHelper.TryGetEmailDomain(username, out var domain))
            {
                return null;
            }

            return string.Equals(domain, MappedDomain, StringComparison.OrdinalIgnoreCase)
                ? OrganizationId
                : null;
        }

        public List<string> GetDomains() => [];
    }
}
