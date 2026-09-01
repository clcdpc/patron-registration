using Clc.PatronRegistration.Helpers;
using Clc.PatronRegistration.Web.Settings;
using Clc.Polaris.Api.Models;
using Microsoft.Extensions.Options;
using Moq;

namespace Clc.PatronRegistration.Tests;

[TestClass]
public class FormCodeAvailabilityTests
{
    [TestMethod]
    public void SelectorAndAuthorizationAgreeForSystemLibraryAndBranchLegacyCodes()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        repository.Setup(service => service.GetFormCodes(It.IsAny<int>(), 1)).Returns([]);
        repository.Setup(service => service.GetLegacyFormCodes()).Returns(
        [
            new(1, "kiosk"),
            new(2, "library-form"),
            new(3, "branch-form"),
            new(3, "shared"),
            new(9, "shared")
        ]);
        var service = new FormCodeAvailabilityService(repository.Object, CreateCache(), Options.Create(new SettingsAdministrationOptions()));

        var libraryTwo = service.GetAvailable(2);

        CollectionAssert.IsSubsetOf(new[] { "kiosk", "library-form", "branch-form", "shared" }, libraryTwo.Select(item => item.FormCode).ToList());
        foreach (var form in libraryTwo)
        {
            Assert.IsTrue(service.IsAvailable(2, form.FormCode));
            Assert.IsTrue(service.IsAvailable(3, form.FormCode));
        }
        Assert.IsFalse(service.IsAvailable(2, "other-only"));
        Assert.AreEqual(1, libraryTwo.Count(item => item.FormCode == "shared"));
        Assert.IsTrue(libraryTwo.Single(item => item.FormCode == "kiosk").DisplayName.Contains("unregistered"));
    }

    [TestMethod]
    public void LegacyCodeFromAnotherLibraryIsDenied()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        repository.Setup(service => service.GetFormCodes(It.IsAny<int>(), 1)).Returns([]);
        repository.Setup(service => service.GetLegacyFormCodes()).Returns([new LegacyFormCodeRow(9, "other-only")]);
        var service = new FormCodeAvailabilityService(repository.Object, CreateCache(), Options.Create(new SettingsAdministrationOptions()));

        Assert.IsFalse(service.IsAvailable(2, "other-only"));
        Assert.IsTrue(service.IsAvailable(8, "other-only"));
    }

    private static ICache CreateCache()
    {
        var organizations = new List<OrganizationsGetRow>
        {
            new() { OrganizationID = 1, OrganizationCodeID = 1, Name = "System" },
            new() { OrganizationID = 2, OrganizationCodeID = 2, Name = "Library 2" },
            new() { OrganizationID = 3, OrganizationCodeID = 3, ParentOrganizationID = 2, Name = "Branch 3" },
            new() { OrganizationID = 8, OrganizationCodeID = 2, Name = "Library 8" },
            new() { OrganizationID = 9, OrganizationCodeID = 3, ParentOrganizationID = 8, Name = "Branch 9" }
        };
        var cache = new Mock<ICache>();
        cache.SetupGet(service => service.OrganizationCache).Returns(organizations);
        return cache.Object;
    }
}
