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
        var user = Principal(2, includeRole: true);

        Assert.IsTrue(service.CanManage(user, 3));
        Assert.IsFalse(service.CanManage(user, 1));
        Assert.IsFalse(service.CanManage(user, 3, sensitive: true));
    }

    [TestMethod]
    public void MissingRole_IsDenied()
    {
        var service = new SettingsAuthorizationService(new TestCache(), Options.Create(new SettingsAdministrationOptions()));

        Assert.IsFalse(service.CanManage(Principal(2, includeRole: false), 2));
    }

    [TestMethod]
    public async Task GenerationChecker_RebuildsOnlyAfterRemoteGenerationChanges()
    {
        var cache = new Mock<ICache>();
        var repository = new Mock<ISettingsAdministrationRepository>();
        repository.SetupSequence(service => service.GetCacheGeneration()).Returns(4).Returns(5);
        var invalidator = new SettingsCacheInvalidator(cache.Object, repository.Object);

        await invalidator.CheckForRemoteChangesAsync();
        await invalidator.CheckForRemoteChangesAsync();

        cache.Verify(service => service.RebuildCache(), Times.Once);
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
        repository.Verify(service => service.GetCacheGeneration(), Times.Once);
    }

    private static ClaimsPrincipal Principal(int organizationId, bool includeRole)
    {
        var claims = new List<Claim> { new("organization", organizationId.ToString()) };
        if (includeRole)
        {
            claims.Add(new Claim(ClaimTypes.Role, "Clc.CardReg.ManageSettings"));
        }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test", ClaimTypes.Name, ClaimTypes.Role));
    }
}
