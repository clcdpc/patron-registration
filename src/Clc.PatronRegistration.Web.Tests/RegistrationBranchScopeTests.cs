using Clc.Melissa;
using Clc.Melissa.Models;
using Clc.PatronRegistration.Configuration;
using Clc.PatronRegistration.Data;
using Clc.PatronRegistration.Helpers;
using Clc.PatronRegistration.Web.Controllers;
using Clc.PatronRegistration.Web.Settings;
using Clc.Polaris.Api;
using Clc.Polaris.Api.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;

namespace Clc.PatronRegistration.Tests;

[TestClass]
public sealed class RegistrationBranchScopeTests
{
    [TestMethod]
    public void RouteOptional_SelectedBranchRequired_DynamicFieldIsAuthoritative()
    {
        var routeSettings = Settings(requiredUser5: false);
        var selectedSettings = Settings(requiredUser5: true);
        var melissaFactory = new Mock<IMelissaClientFactory>();
        melissaFactory.Setup(factory => factory.Create(It.IsAny<string>())).Returns(Mock.Of<IMelissaRestClient>());
        var emailFactory = new Mock<IEmailSenderFactory>();
        emailFactory.Setup(factory => factory.Create(It.IsAny<string>())).Returns(Mock.Of<IEmailSender>());
        var controller = CreateController(routeSettings.Object, selectedSettings.Object,
            melissaFactory, emailFactory, out _);

        var result = controller.Submit(ValidRegistration(routeSettings.Object, user5: null));

        Assert.AreEqual(RegistrationStatus.Error, result.Status);
        Assert.AreEqual("Responsible person is required.", result.Errors.Single(error => error.Key == nameof(Registration.User5)).Value);
    }

    [TestMethod]
    public void SelectedBranchOptional_DynamicFieldDoesNotCreateValidationError()
    {
        var routeSettings = Settings(requiredUser5: true);
        var selectedSettings = Settings(requiredUser5: false);
        var melissa = new Mock<IMelissaRestClient>();
        melissa.Setup(client => client.PersonatorRequest(It.IsAny<PersonatorRequestRecord>()))
            .Throws(new InvalidOperationException("selected branch workflow reached"));
        var melissaFactory = new Mock<IMelissaClientFactory>();
        melissaFactory.Setup(factory => factory.Create(It.IsAny<string>())).Returns(melissa.Object);
        var emailFactory = new Mock<IEmailSenderFactory>();
        emailFactory.Setup(factory => factory.Create(It.IsAny<string>())).Returns(Mock.Of<IEmailSender>());
        var controller = CreateController(routeSettings.Object, selectedSettings.Object,
            melissaFactory, emailFactory, out _);
        var exception = Assert.ThrowsException<InvalidOperationException>(() =>
            controller.Submit(ValidRegistration(routeSettings.Object, user5: null)));

        Assert.AreEqual("selected branch workflow reached", exception.Message);
        melissaFactory.Verify(factory => factory.Create(It.IsAny<string>()), Times.Once);
    }

    [TestMethod]
    public void NormalSubmission_UsesSelectedBranchMelissaKey()
    {
        var routeSettings = Settings(requiredUser5: false, melissaKey: "route-melissa");
        var selectedSettings = Settings(requiredUser5: false, melissaKey: "branch-melissa");
        var melissa = new Mock<IMelissaRestClient>();
        melissa.Setup(client => client.PersonatorRequest(It.IsAny<PersonatorRequestRecord>()))
            .Throws(new InvalidOperationException("selected branch Melissa client used"));
        var melissaFactory = new Mock<IMelissaClientFactory>();
        melissaFactory.Setup(factory => factory.Create("branch-melissa")).Returns(melissa.Object);
        var emailFactory = new Mock<IEmailSenderFactory>();
        emailFactory.Setup(factory => factory.Create(It.IsAny<string>())).Returns(Mock.Of<IEmailSender>());
        var controller = CreateController(routeSettings.Object, selectedSettings.Object,
            melissaFactory, emailFactory, out _);

        Assert.ThrowsException<InvalidOperationException>(() =>
            controller.Submit(ValidRegistration(routeSettings.Object, user5: null)));

        melissaFactory.Verify(factory => factory.Create("branch-melissa"), Times.Once);
        melissaFactory.Verify(factory => factory.Create("route-melissa"), Times.Never);
    }

    [TestMethod]
    public void NormalSubmission_UsesSelectedBranchPostmarkKey()
    {
        var routeSettings = Settings(requiredUser5: false, postmarkKey: "route-postmark");
        var selectedSettings = Settings(requiredUser5: false, postmarkKey: "branch-postmark");
        var melissa = new Mock<IMelissaRestClient>();
        melissa.Setup(client => client.PersonatorRequest(It.IsAny<PersonatorRequestRecord>()))
            .Throws(new InvalidOperationException("selected branch workflow reached"));
        var melissaFactory = new Mock<IMelissaClientFactory>();
        melissaFactory.Setup(factory => factory.Create(It.IsAny<string>())).Returns(melissa.Object);
        var emailFactory = new Mock<IEmailSenderFactory>();
        emailFactory.Setup(factory => factory.Create("branch-postmark")).Returns(Mock.Of<IEmailSender>());
        var controller = CreateController(routeSettings.Object, selectedSettings.Object,
            melissaFactory, emailFactory, out _);

        Assert.ThrowsException<InvalidOperationException>(() =>
            controller.Submit(ValidRegistration(routeSettings.Object, user5: null)));

        emailFactory.Verify(factory => factory.Create("branch-postmark"), Times.Once);
        emailFactory.Verify(factory => factory.Create("route-postmark"), Times.Never);
    }

    [TestMethod]
    public void NormalSubmission_UsesSelectedBranchWithinThePreservedLibraryScope()
    {
        var routeSettings = Settings(requiredUser5: false, melissaKey: "route-melissa",
            postmarkKey: "route-postmark", organizationId: 2, libraryId: 2);
        var selectedSettings = Settings(requiredUser5: false, melissaKey: "branch-melissa",
            postmarkKey: "branch-postmark", organizationId: 3, libraryId: 2);
        var melissa = new Mock<IMelissaRestClient>();
        melissa.Setup(client => client.PersonatorRequest(It.IsAny<PersonatorRequestRecord>()))
            .Throws(new InvalidOperationException("selected branch Melissa client used"));
        var melissaFactory = new Mock<IMelissaClientFactory>();
        melissaFactory.Setup(factory => factory.Create("branch-melissa")).Returns(melissa.Object);
        var emailFactory = new Mock<IEmailSenderFactory>();
        emailFactory.Setup(factory => factory.Create("branch-postmark")).Returns(Mock.Of<IEmailSender>());
        var controller = CreateController(routeSettings.Object, selectedSettings.Object,
            melissaFactory, emailFactory, out var scopeResolver, selectedBranchId: 3);

        Assert.ThrowsException<InvalidOperationException>(() =>
            controller.Submit(ValidRegistration(routeSettings.Object, user5: null)));

        scopeResolver.Verify(value => value.ResolveForSubmission(
            It.IsAny<HttpContext>(), routeSettings.Object, 3), Times.Once);
        melissaFactory.Verify(factory => factory.Create("branch-melissa"), Times.Once);
        melissaFactory.Verify(factory => factory.Create("route-melissa"), Times.Never);
        emailFactory.Verify(factory => factory.Create("branch-postmark"), Times.Once);
        emailFactory.Verify(factory => factory.Create("route-postmark"), Times.Never);
    }

    [TestMethod]
    public void LibraryScopedBranchReload_RemainsSwitchableAndResolvesTheSelectedBranch()
    {
        var organizations = new List<OrganizationsGetRow>
        {
            Organization(1, null, 1),
            Organization(2, 1, 2),
            Organization(3, 2),
            Organization(4, 2),
            Organization(5, 1)
        };
        var cache = new Mock<ICache>();
        cache.SetupGet(value => value.OrganizationCache).Returns(organizations);
        cache.SetupGet(value => value.SettingsCache).Returns([]);
        cache.Setup(value => value.GetOrg(It.IsAny<int>()))
            .Returns((int id) => organizations.Single(value => value.OrganizationID == id));
        var db = new Mock<IDbHelper>();
        db.Setup(value => value.GetSelfRegistrationBranches(2)).Returns([organizations[2], organizations[3]]);
        db.Setup(value => value.GetSelfRegistrationBranches(null)).Returns([organizations[2], organizations[3], organizations[4]]);
        var requestSettings = new DbSettingProvider(2, cache.Object, "form", 1);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.RouteValues["formCode"] = "form";
        var settingResolver = new RequestSettingProviderResolver(
            new PreviewRequestContextAccessor(), new SettingsPageBrandingContextAccessor(),
            Mock.Of<ISettingsAuthorizationService>(), Mock.Of<IFormCodeAvailabilityService>(), cache.Object,
            Options.Create(new SettingsAdministrationOptions()), new RegistrationConfiguration());
        var resolver = new RegistrationScopeResolver(db.Object, cache.Object, settingResolver);

        var availableAfterReload = resolver.GetAvailableBranches(httpContext, requestSettings);
        var branchThree = resolver.ResolveForSubmission(httpContext, requestSettings, 3);
        var branchFour = resolver.ResolveForSubmission(httpContext, requestSettings, 4);
        var outOfScope = resolver.ResolveForSubmission(httpContext, requestSettings, 5);

        CollectionAssert.AreEquivalent(new[] { 3, 4 }, availableAfterReload.Select(value => value.OrganizationID).ToArray());
        Assert.IsTrue(branchThree.IsValid);
        Assert.AreEqual(3, branchThree.Settings.OrganizationId);
        Assert.IsTrue(branchFour.IsValid);
        Assert.AreEqual(4, branchFour.Settings.OrganizationId);
        Assert.IsFalse(outOfScope.IsValid);
    }

    [TestMethod]
    public void BranchScopedRoute_WithBranchSelectionEnabled_OffersSiblingsAndRejectsAnotherLibrary()
    {
        var organizations = new List<OrganizationsGetRow>
        {
            Organization(1, null, 1),
            Organization(2, 1, 2),
            Organization(3, 2),
            Organization(4, 2),
            Organization(8, 1, 2),
            Organization(9, 8)
        };
        var cache = new Mock<ICache>();
        cache.SetupGet(value => value.OrganizationCache).Returns(organizations);
        cache.SetupGet(value => value.SettingsCache).Returns([]);
        cache.Setup(value => value.GetOrg(It.IsAny<int>()))
            .Returns((int id) => organizations.Single(value => value.OrganizationID == id));
        var db = new Mock<IDbHelper>();
        db.Setup(value => value.GetSelfRegistrationBranches(2)).Returns([organizations[2], organizations[3]]);
        var requestSettings = new DbSettingProvider(3, cache.Object, "form", 1);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.RouteValues["formCode"] = "form";
        var settingResolver = new RequestSettingProviderResolver(
            new PreviewRequestContextAccessor(), new SettingsPageBrandingContextAccessor(),
            Mock.Of<ISettingsAuthorizationService>(), Mock.Of<IFormCodeAvailabilityService>(), cache.Object,
            Options.Create(new SettingsAdministrationOptions()), new RegistrationConfiguration());
        var resolver = new RegistrationScopeResolver(db.Object, cache.Object, settingResolver);

        var available = resolver.GetAvailableBranches(httpContext, requestSettings);
        var sibling = resolver.ResolveForSubmission(httpContext, requestSettings, 4);
        var outOfLibrary = resolver.ResolveForSubmission(httpContext, requestSettings, 9);

        CollectionAssert.AreEquivalent(new[] { 3, 4 }, available.Select(value => value.OrganizationID).ToArray());
        Assert.IsTrue(sibling.IsValid);
        Assert.AreEqual(4, sibling.Settings.OrganizationId);
        Assert.AreEqual(2, sibling.Settings.LibraryId);
        Assert.IsFalse(outOfLibrary.IsValid);
    }

    [TestMethod]
    public void BranchScopedRoute_RendersSelectedSiblingSettingsAndRejectsOutOfLibrarySelection()
    {
        var organizations = new List<OrganizationsGetRow>
        {
            Organization(1, null, 1),
            Organization(2, 1, 2),
            Organization(3, 2),
            Organization(4, 2),
            Organization(8, 1, 2),
            Organization(9, 8)
        };
        var routeSettings = Settings(requiredUser5: false, organizationId: 3, libraryId: 2);
        var siblingSettings = Settings(requiredUser5: true, organizationId: 4, libraryId: 2,
            melissaKey: "sibling-melissa", postmarkKey: "sibling-postmark");
        CacheHelper.Configure(new TestCache { OrganizationCache = organizations });
        var db = new Mock<IDbHelper>();
        db.Setup(value => value.GetSelfRegistrationOrganizations(null)).Returns(organizations);
        db.Setup(value => value.GetGendersToOrganizations(4)).Returns([]);
        db.Setup(value => value.GetPickupBranches(2)).Returns([]);
        var scopeResolver = new Mock<IRegistrationScopeResolver>();
        scopeResolver.Setup(value => value.ResolveForSubmission(
                It.IsAny<HttpContext>(), routeSettings.Object, 4))
            .Returns(new RegistrationScopeResolution(true, siblingSettings.Object));
        scopeResolver.Setup(value => value.ResolveForSubmission(
                It.IsAny<HttpContext>(), routeSettings.Object, 9))
            .Returns(new RegistrationScopeResolution(false, routeSettings.Object));
        scopeResolver.Setup(value => value.GetAvailableBranches(
                It.IsAny<HttpContext>(), routeSettings.Object))
            .Returns([organizations[2], organizations[3]]);
        var controller = CreateGetController(routeSettings.Object, db.Object, scopeResolver.Object);

        var selectedResult = controller.Create(3, selectedBranchId: 4);
        var selectedView = selectedResult as ViewResult;
        var selectedModel = selectedView?.Model as Registration;

        Assert.IsNotNull(selectedModel);
        Assert.AreSame(siblingSettings.Object, selectedModel.Settings);
        Assert.AreEqual(4, selectedModel.PatronBranchID);
        CollectionAssert.AreEquivalent(new[] { "3", "4" },
            selectedModel.Branches.Select(value => value.Value).ToArray());

        var rejectedResult = controller.Create(3, selectedBranchId: 9);

        Assert.AreEqual("Unavailable", ((ViewResult)rejectedResult).ViewName);
        scopeResolver.Verify(value => value.ResolveForSubmission(
            It.IsAny<HttpContext>(), routeSettings.Object, 4), Times.Once);
        scopeResolver.Verify(value => value.ResolveForSubmission(
            It.IsAny<HttpContext>(), routeSettings.Object, 9), Times.Once);
    }

    [DataTestMethod]
    [DataRow(3)]
    [DataRow(4)]
    public void LibraryScopedBranchReload_RendersSelectedSettingsAndPreservesWorkflowFlags(int selectedBranchId)
    {
        var organizations = new List<OrganizationsGetRow>
        {
            Organization(1, null, 1),
            Organization(2, 1, 2),
            Organization(3, 2),
            Organization(4, 2)
        };
        CacheHelper.Configure(new TestCache { OrganizationCache = organizations });

        var routeSettings = Settings(requiredUser5: false, organizationId: 2, libraryId: 2);
        var selectedSettings = Settings(requiredUser5: true, organizationId: selectedBranchId, libraryId: 2,
            melissaKey: "selected-melissa", postmarkKey: "selected-postmark");
        selectedSettings.SetupGet(value => value.WarningText).Returns("Selected branch agreement");
        var db = new Mock<IDbHelper>();
        db.Setup(value => value.GetSelfRegistrationOrganizations(null)).Returns(organizations);
        db.Setup(value => value.GetSelfRegistrationBranches(2)).Returns([organizations[2], organizations[3]]);
        db.Setup(value => value.GetPickupBranches(2)).Returns([]);
        db.Setup(value => value.GetGendersToOrganizations(selectedBranchId)).Returns([]);
        var scopeResolver = new Mock<IRegistrationScopeResolver>();
        scopeResolver.Setup(value => value.ResolveForSubmission(
                It.IsAny<HttpContext>(), routeSettings.Object, selectedBranchId))
            .Returns(new RegistrationScopeResolution(true, selectedSettings.Object));
        scopeResolver.Setup(value => value.GetAvailableBranches(
                It.IsAny<HttpContext>(), routeSettings.Object))
            .Returns([organizations[2], organizations[3]]);
        var controller = CreateGetController(routeSettings.Object, db.Object, scopeResolver.Object);

        var result = controller.Create(2, forceDl: true, agreementAccepted: true, selectedBranchId: selectedBranchId);
        var view = result as ViewResult;
        var model = view?.Model as Registration;

        Assert.IsNotNull(view);
        Assert.IsNotNull(model);
        Assert.AreSame(selectedSettings.Object, model.Settings);
        Assert.AreEqual(selectedBranchId, model.PatronBranchID);
        Assert.IsTrue(model.ShowDlButton);
        Assert.IsTrue(model.BypassAgreement);
        CollectionAssert.AreEquivalent(new[] { "3", "4" },
            model.Branches.Select(value => value.Value).ToArray());
        scopeResolver.Verify(value => value.ResolveForSubmission(
            It.IsAny<HttpContext>(), routeSettings.Object, selectedBranchId), Times.Once);
    }

    [TestMethod]
    public void DisabledSelectedBranch_IsUnavailableAfterLibraryScopedReload()
    {
        var organizations = new List<OrganizationsGetRow>
        {
            Organization(1, null, 1),
            Organization(2, 1, 2),
            Organization(3, 2),
            Organization(4, 2)
        };
        var routeSettings = Settings(requiredUser5: false, organizationId: 2, libraryId: 2);
        var disabledSettings = Settings(requiredUser5: false, organizationId: 3, libraryId: 2, disabled: true);
        var db = new Mock<IDbHelper>();
        db.Setup(value => value.GetSelfRegistrationOrganizations(null)).Returns(organizations);
        var scopeResolver = new Mock<IRegistrationScopeResolver>();
        scopeResolver.Setup(value => value.ResolveForSubmission(
                It.IsAny<HttpContext>(), routeSettings.Object, 3))
            .Returns(new RegistrationScopeResolution(true, disabledSettings.Object));
        var controller = CreateGetController(routeSettings.Object, db.Object, scopeResolver.Object);

        var result = controller.Create(2, selectedBranchId: 3);

        var view = result as ViewResult;
        Assert.IsNotNull(view);
        Assert.AreEqual("Unavailable", view.ViewName);
    }

    [TestMethod]
    public void BranchSwitchPost_RendersSubmittedModelWithSelectedBranchSettingsWithoutCachingIt()
    {
        var organizations = new List<OrganizationsGetRow>
        {
            Organization(1, null, 1),
            Organization(2, 1, 2),
            Organization(3, 2),
            Organization(4, 2)
        };
        var routeSettings = Settings(requiredUser5: false, organizationId: 2, libraryId: 2);
        var selectedSettings = Settings(requiredUser5: true, organizationId: 4, libraryId: 2,
            melissaKey: "selected-melissa", postmarkKey: "selected-postmark");
        selectedSettings.SetupGet(value => value.WarningText).Returns("Selected branch agreement");
        var db = new Mock<IDbHelper>();
        db.Setup(value => value.GetSelfRegistrationOrganizations(null)).Returns(organizations);
        db.Setup(value => value.GetGendersToOrganizations(4)).Returns([]);
        var scopeResolver = new Mock<IRegistrationScopeResolver>();
        scopeResolver.Setup(value => value.ResolveForSubmission(
                It.IsAny<HttpContext>(), routeSettings.Object, 4))
            .Returns(new RegistrationScopeResolution(true, selectedSettings.Object));
        scopeResolver.Setup(value => value.GetAvailableBranches(
                It.IsAny<HttpContext>(), routeSettings.Object))
            .Returns([organizations[2], organizations[3]]);
        var controller = CreateGetController(routeSettings.Object, db.Object, scopeResolver.Object);
        controller.HttpContext.Request.Method = "POST";

        var submitted = ValidRegistration(routeSettings.Object, user5: "Earlier patron");
        submitted.PatronBranchID = 4;
        submitted.NameFirst = "Earlier";
        submitted.EmailAddress = "earlier@example.test";
        submitted.Password = "1234";
        submitted.Password2 = "1234";

        var result = controller.ChangeBranch(submitted, 2, forceDl: true, agreementAccepted: true);

        var view = result as PartialViewResult;
        var model = view?.Model as Registration;
        Assert.IsNotNull(view);
        Assert.IsNotNull(model);
        Assert.AreSame(selectedSettings.Object, model.Settings);
        Assert.AreEqual("Earlier", model.NameFirst);
        Assert.AreEqual("earlier@example.test", model.EmailAddress);
        Assert.AreEqual("1234", model.Password);
        Assert.AreEqual("1234", model.Password2);
        Assert.AreEqual(4, model.PatronBranchID);
        Assert.IsTrue(model.BypassAgreement);
        Assert.AreEqual("no-store", controller.Response.Headers.CacheControl.ToString());
        scopeResolver.Verify(value => value.ResolveForSubmission(
            It.IsAny<HttpContext>(), routeSettings.Object, 4), Times.Once);
    }

    [TestMethod]
    public void RegistrationDriverLicense_RejectsInvalidConfiguredFormatInsteadOfUsingMagstripe()
    {
        var settings = Settings(requiredUser5: false, dlFormat: "unsupported");
        var controller = CreateController(settings.Object, settings.Object,
            new Mock<IMelissaClientFactory>(), new Mock<IEmailSenderFactory>(), out _);

        var result = controller.dl("$unsupported");

        Assert.IsInstanceOfType<BadRequestObjectResult>(result);
    }

    private static RegistrationController CreateController(
        ISettingProvider routeSettings,
        ISettingProvider selectedSettings,
        Mock<IMelissaClientFactory> melissaFactory,
        Mock<IEmailSenderFactory> emailFactory,
        out Mock<IRegistrationScopeResolver> scopeResolver,
        int selectedBranchId = 3)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllersWithViews();
        services.AddSingleton<ISettingProvider>(routeSettings);
        var provider = services.BuildServiceProvider();
        var httpContext = new DefaultHttpContext { RequestServices = provider };
        new HttpContextAccessor().HttpContext = httpContext;

        scopeResolver = new Mock<IRegistrationScopeResolver>();
        scopeResolver.Setup(resolver => resolver.ResolveForSubmission(
                httpContext, routeSettings, selectedBranchId))
            .Returns(new RegistrationScopeResolution(true, selectedSettings));
        var controller = new RegistrationController(
            Mock.Of<IPapiClient>(),
            DuplicateFreeDb().Object,
            routeSettings,
            emailFactory.Object,
            melissaFactory.Object,
            provider.GetRequiredService<IObjectModelValidator>(),
            scopeResolver.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };
        return controller;
    }

    private static RegistrationController CreateGetController(
        ISettingProvider settings,
        IDbHelper db,
        IRegistrationScopeResolver scopeResolver)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllersWithViews();
        var provider = services.BuildServiceProvider();
        var httpContext = new DefaultHttpContext { RequestServices = provider };
        return new RegistrationController(
            Mock.Of<IPapiClient>(), db, settings,
            Mock.Of<IEmailSenderFactory>(), Mock.Of<IMelissaClientFactory>(),
            provider.GetRequiredService<IObjectModelValidator>(), scopeResolver)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };
    }

    private static Mock<IDbHelper> DuplicateFreeDb()
    {
        var db = new Mock<IDbHelper>();
        db.Setup(value => value.CheckPatronIsDuplicate(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>()))
            .Returns(false);
        return db;
    }

    private static Mock<ISettingProvider> Settings(bool requiredUser5, string? melissaKey = null,
        string? postmarkKey = null, string? dlFormat = null, int organizationId = 3,
        int libraryId = 2, bool disabled = false)
    {
        var settings = new Mock<ISettingProvider>();
        settings.Setup(value => value.GetFieldRequired(nameof(Registration.User5))).Returns(requiredUser5);
        settings.Setup(value => value.GetFieldLabel(nameof(Registration.User5))).Returns("Responsible person");
        settings.SetupGet(value => value.DisplayResponsiblePersonField).Returns(true);
        settings.SetupGet(value => value.DisableBranch).Returns(disabled);
        settings.SetupGet(value => value.OrganizationId).Returns(organizationId);
        settings.SetupGet(value => value.LibraryId).Returns(libraryId);
        settings.SetupGet(value => value.EnablePatronBranchSelectOption).Returns(true);
        settings.SetupGet(value => value.EnableDriversLicenseSwipe).Returns(false);
        settings.SetupGet(value => value.DriversLicenseButtonEnabledIpAddresses).Returns([]);
        settings.SetupGet(value => value.DisplayMailingListCheckbox).Returns(false);
        settings.SetupGet(value => value.ForceEcardRemotely).Returns(false);
        settings.SetupGet(value => value.PhoneNumberFormat).Returns("($1) $2-$3");
        settings.SetupGet(value => value.FormCode).Returns(string.Empty);
        settings.SetupGet(value => value.MelissaDataApiKey).Returns(melissaKey ?? string.Empty);
        settings.SetupGet(value => value.PostmarkApiKey).Returns(postmarkKey ?? string.Empty);
        settings.SetupGet(value => value.DriversLicenseFormat).Returns(dlFormat ?? string.Empty);
        return settings;
    }

    private static Registration ValidRegistration(ISettingProvider settings, string? user5) => new(settings)
    {
        PatronBranchID = 3,
        NameFirst = "Jane",
        NameLast = "Doe",
        Birthdate = new DateTime(2000, 1, 1),
        EmailAddress = "jane@example.com",
        User5 = user5,
        Password = "1234",
        Password2 = "1234",
        StreetOne = "1 Main St",
        City = "Columbus",
        State = "OH",
        PostalCode = "43215"
    };

    private static OrganizationsGetRow Organization(int id, int? parentId, int code = 3) => new()
    {
        OrganizationID = id,
        ParentOrganizationID = parentId,
        OrganizationCodeID = code,
        DisplayName = $"Organization {id}",
        Name = $"Organization {id}",
        Abbreviation = $"O{id}"
    };
}
