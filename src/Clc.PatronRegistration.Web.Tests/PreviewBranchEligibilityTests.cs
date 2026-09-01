using Clc.PatronRegistration.Data;
using Clc.PatronRegistration.Helpers;
using Clc.PatronRegistration.Web.Settings;
using Clc.Polaris.Api.Models;
using Moq;

namespace Clc.PatronRegistration.Tests;

[TestClass]
public class PreviewBranchEligibilityTests
{
    [TestMethod]
    public void EligibleBranch_IsAcceptedForItsLibraryAndSystem()
    {
        var service = CreateService([Branch(3, 2)]);

        Assert.IsTrue(service.IsEligible(2, 3, 1));
        Assert.IsTrue(service.IsEligible(1, 3, 1));
        Assert.IsTrue(service.IsEligible(3, 3, 1));
    }

    [TestMethod]
    public void NonSelfRegistrationBranch_IsRejected()
    {
        var service = CreateService([]);

        Assert.IsFalse(service.IsEligible(2, 3, 1));
    }

    [TestMethod]
    public void BranchFromAnotherLibrary_IsRejected()
    {
        var service = CreateService([Branch(9, 8)]);

        Assert.IsFalse(service.IsEligible(2, 9, 1));
    }

    [TestMethod]
    public void BranchBecomingIneligible_IsRejectedOnSubsequentValidation()
    {
        var db = new Mock<IDbHelper>();
        db.SetupSequence(service => service.GetSelfRegistrationOrganizations(null))
            .Returns([Branch(3, 2)])
            .Returns([]);
        var service = new PreviewBranchEligibilityService(db.Object, CreateCache());

        Assert.IsTrue(service.IsEligible(2, 3, 1));
        Assert.IsFalse(service.IsEligible(2, 3, 1));
    }

    private static PreviewBranchEligibilityService CreateService(IEnumerable<OrganizationsGetRow> eligible)
    {
        var db = new Mock<IDbHelper>();
        db.Setup(service => service.GetSelfRegistrationOrganizations(null)).Returns(eligible);
        return new PreviewBranchEligibilityService(db.Object, CreateCache());
    }

    private static ICache CreateCache()
    {
        var organizations = new List<OrganizationsGetRow>
        {
            new() { OrganizationID = 1, OrganizationCodeID = 1, Name = "System" },
            new() { OrganizationID = 2, OrganizationCodeID = 2, Name = "Library" },
            Branch(3, 2),
            new() { OrganizationID = 8, OrganizationCodeID = 2, Name = "Other library" },
            Branch(9, 8)
        };
        var cache = new Mock<ICache>();
        cache.SetupGet(service => service.OrganizationCache).Returns(organizations);
        cache.Setup(service => service.GetOrg(It.IsAny<int>()))
            .Returns((int id) => organizations.Single(organization => organization.OrganizationID == id));
        return cache.Object;
    }

    private static OrganizationsGetRow Branch(int id, int libraryId) => new()
    {
        OrganizationID = id,
        OrganizationCodeID = 3,
        ParentOrganizationID = libraryId,
        Name = $"Branch {id}"
    };
}
