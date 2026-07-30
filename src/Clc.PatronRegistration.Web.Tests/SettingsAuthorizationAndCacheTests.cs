using System.Security.Claims;
using Clc.PatronRegistration.Helpers;
using Clc.PatronRegistration.Web.Settings;
using Microsoft.Extensions.Options;
using Moq;

namespace Clc.PatronRegistration.Tests;

[TestClass]
public class SettingsAuthorizationAndCacheTests
{
    [TestMethod]
    public void ConfiguredGlobalOrganization_CanManageSensitiveSystemSetting()
    {
        var options = Options.Create(new SettingsAdministrationOptions
        {
            GlobalOrganizationId = -99,
            SystemOrganizationId = 42
        });
        var service = new SettingsAuthorizationService(new TestCache(), options);
        var user = Principal(-99, includeRole: true);

        Assert.IsTrue(service.CanManage(user, 42, sensitive: true));
    }

    [TestMethod]
    public void LibraryAdministrator_CanManageOwnBranchButNotSystemOrSensitiveSettings()
    {
        var service = new SettingsAuthorizationService(new TestCache(), Options.Create(new SettingsAdministrationOptions()));
        var user = Principal("Clc.OrganizationId", "2", includeRole: true);

        Assert.AreEqual(new SettingsPrincipal(true, 2, false), service.Describe(user));
        Assert.IsTrue(service.CanManage(user, 3));
        Assert.IsFalse(service.CanManage(user, 1));
        Assert.IsFalse(service.CanManage(user, 3, sensitive: true));
    }

    [DataTestMethod]
    [DataRow("organization")]
    [DataRow("organization_id")]
    [DataRow("extension_Organization")]
    public void LegacyOrganizationClaims_RemainSupported(string claimType)
    {
        var service = AuthorizationService();

        Assert.AreEqual(
            new SettingsPrincipal(true, 2, false),
            service.Describe(Principal(claimType, "2", includeRole: true)));
    }

    [TestMethod]
    public void OrganizationClaimTypeMatching_IsCaseInsensitive()
    {
        var service = AuthorizationService();

        Assert.AreEqual(
            new SettingsPrincipal(true, 2, false),
            service.Describe(Principal("cLc.oRgAnIzAtIoNiD", "2", includeRole: true)));
    }

    [DataTestMethod]
    [DataRow("not-an-integer")]
    [DataRow("")]
    public void MalformedOrganizationClaim_IsDenied(string claimValue)
    {
        var service = AuthorizationService();
        var user = Principal("Clc.OrganizationId", claimValue, includeRole: true);

        Assert.IsNull(service.Describe(user).OrganizationId);
        Assert.IsFalse(service.CanManage(user, 2));
    }

    [TestMethod]
    public void MissingOrganizationClaim_IsDenied()
    {
        var service = AuthorizationService();
        var user = Principal(null, null, includeRole: true);

        Assert.IsNull(service.Describe(user).OrganizationId);
        Assert.IsFalse(service.CanManage(user, 2));
    }

    [TestMethod]
    public void MissingRole_IsDenied()
    {
        var service = new SettingsAuthorizationService(new TestCache(), Options.Create(new SettingsAdministrationOptions()));

        var user = Principal("Clc.OrganizationId", "2", includeRole: false);

        Assert.IsFalse(service.Describe(user).HasRole);
        Assert.IsFalse(service.CanManage(user, 2));
    }

    [TestMethod]
    public async Task GenerationChecker_RebuildsOnlyAfterRemoteGenerationChanges()
    {
        var cache = new Mock<ICache>();
        cache.SetupGet(service => service.IsInitialized).Returns(true);
        var repository = new Mock<ISettingsAdministrationRepository>();
        repository.SetupSequence(service => service.GetCacheGeneration())
            .Returns(4).Returns(4)
            .Returns(5).Returns(5);
        var invalidator = new SettingsCacheInvalidator(cache.Object, repository.Object);

        await invalidator.CheckForRemoteChangesAsync();
        await invalidator.CheckForRemoteChangesAsync();

        cache.Verify(service => service.RebuildCache(), Times.Exactly(2));
    }

    [TestMethod]
    public void LocalLiveChange_RebuildsImmediatelyAndObservesGeneration()
    {
        var cache = new Mock<ICache>();
        var repository = new Mock<ISettingsAdministrationRepository>();
        repository.Setup(service => service.GetCacheGeneration()).Returns(9);
        var invalidator = new SettingsCacheInvalidator(cache.Object, repository.Object);

        invalidator.LiveSettingsChanged();

        cache.Verify(service => service.RebuildCache(), Times.Once);
        repository.Verify(service => service.GetCacheGeneration(), Times.Exactly(2));
    }

    [TestMethod]
    public async Task FirstPoll_RebuildsAnAlreadyLoadedCacheBeforeRecordingGeneration()
    {
        var cache = new Mock<ICache>();
        cache.SetupGet(service => service.IsInitialized).Returns(true);
        var repository = new Mock<ISettingsAdministrationRepository>();
        repository.SetupSequence(service => service.GetCacheGeneration()).Returns(12).Returns(12);
        var invalidator = new SettingsCacheInvalidator(cache.Object, repository.Object);

        await invalidator.CheckForRemoteChangesAsync();

        cache.Verify(service => service.RebuildCache(), Times.Once);
    }

    [TestMethod]
    public async Task ChangeDuringRebuild_TriggersAnotherRebuild()
    {
        var cache = new Mock<ICache>();
        cache.SetupGet(service => service.IsInitialized).Returns(true);
        var repository = new Mock<ISettingsAdministrationRepository>();
        repository.SetupSequence(service => service.GetCacheGeneration())
            .Returns(20)
            .Returns(21)
            .Returns(21);
        var invalidator = new SettingsCacheInvalidator(cache.Object, repository.Object);

        await invalidator.CheckForRemoteChangesAsync();

        cache.Verify(service => service.RebuildCache(), Times.Exactly(2));
    }

    [TestMethod]
    public void CacheHelper_ReadsCurrentCollectionsFromConfiguredCacheProvider()
    {
        var cache = new MutableCache
        {
            OrganizationCache =
            [
                new() { OrganizationID = 2, OrganizationCodeID = 2, Name = "Library" },
                new() { OrganizationID = 3, OrganizationCodeID = 3, ParentOrganizationID = 2, Name = "Old branch" }
            ],
            SettingsCache = [new() { OrganizationID = 2, Setting = "registration_text", Value = "old" }]
        };
        CacheHelper.Configure(cache);
        var originalOrganizations = CacheHelper.OrganizationCache;
        var originalSettings = CacheHelper.SettingsCache;

        cache.OrganizationCache =
        [
            new() { OrganizationID = 2, OrganizationCodeID = 2, Name = "Library" },
            new() { OrganizationID = 4, OrganizationCodeID = 3, ParentOrganizationID = 2, Name = "New branch" }
        ];
        cache.SettingsCache = [new() { OrganizationID = 2, Setting = "registration_text", Value = "new" }];

        Assert.AreNotSame(originalOrganizations, CacheHelper.OrganizationCache);
        Assert.AreNotSame(originalSettings, CacheHelper.SettingsCache);
        Assert.AreSame(cache.OrganizationCache, CacheHelper.OrganizationCache);
        Assert.AreSame(cache.SettingsCache, CacheHelper.SettingsCache);
        Assert.AreEqual("New branch", CacheHelper.GetOrg(4).Name);
        Assert.AreEqual(4, CacheHelper.GetBranches(2).Single().OrganizationID);
        Assert.AreEqual("new", CacheHelper.SettingsCache.Single().Value);
    }

    private sealed class MutableCache : ICache
    {
        public List<Clc.PatronRegistration.Configuration.RegistrationFormSetting> SettingsCache { get; set; } = [];
        public List<Clc.Polaris.Api.Models.OrganizationsGetRow> OrganizationCache { get; set; } = [];
        public bool IsInitialized => true;
        public void RebuildCache() { }
        public Clc.Polaris.Api.Models.OrganizationsGetRow GetOrg(int orgId) =>
            OrganizationCache.Single(organization => organization.OrganizationID == orgId);
        public List<Clc.Polaris.Api.Models.OrganizationsGetRow> GetBranches(int orgId) =>
            OrganizationCache.Where(organization => organization.ParentOrganizationID == orgId).ToList();
    }

    private static SettingsAuthorizationService AuthorizationService() =>
        new(new TestCache(), Options.Create(new SettingsAdministrationOptions()));

    private static ClaimsPrincipal Principal(int organizationId, bool includeRole) =>
        Principal("organization", organizationId.ToString(), includeRole);

    private static ClaimsPrincipal Principal(string? claimType, string? claimValue, bool includeRole)
    {
        var claims = new List<Claim>();
        if (claimType is not null)
        {
            claims.Add(new Claim(claimType, claimValue ?? string.Empty));
        }
        if (includeRole)
        {
            claims.Add(new Claim(ClaimTypes.Role, "Clc.CardReg.ManageSettings"));
        }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test", ClaimTypes.Name, ClaimTypes.Role));
    }
}
