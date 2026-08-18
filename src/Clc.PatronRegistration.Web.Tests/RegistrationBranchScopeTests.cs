using Clc.Melissa;
using Clc.Melissa.Models;
using Clc.PatronRegistration.Configuration;
using Clc.PatronRegistration.Data;
using Clc.PatronRegistration.Web.Controllers;
using Clc.PatronRegistration.Web.Settings;
using Clc.Polaris.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.Extensions.DependencyInjection;
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
    public void RouteRequired_SelectedBranchOptional_DynamicFieldDoesNotKeepRouteError()
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
        controller.ModelState.AddModelError(nameof(Registration.User5), "Route provider required this field.");

        var result = controller.Submit(ValidRegistration(routeSettings.Object, user5: null));

        Assert.IsFalse(result.Errors.Any(error => error.Key == nameof(Registration.User5)));
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

        controller.Submit(ValidRegistration(routeSettings.Object, user5: null));

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

        controller.Submit(ValidRegistration(routeSettings.Object, user5: null));

        emailFactory.Verify(factory => factory.Create("branch-postmark"), Times.Once);
        emailFactory.Verify(factory => factory.Create("route-postmark"), Times.Never);
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
        out Mock<IRegistrationScopeResolver> scopeResolver)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllersWithViews();
        var provider = services.BuildServiceProvider();
        var httpContext = new DefaultHttpContext { RequestServices = provider };
        new HttpContextAccessor().HttpContext = httpContext;

        scopeResolver = new Mock<IRegistrationScopeResolver>();
        scopeResolver.Setup(resolver => resolver.ResolveForSubmission(httpContext, routeSettings, 3))
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

    private static Mock<IDbHelper> DuplicateFreeDb()
    {
        var db = new Mock<IDbHelper>();
        db.Setup(value => value.CheckPatronIsDuplicate(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>()))
            .Returns(false);
        return db;
    }

    private static Mock<ISettingProvider> Settings(bool requiredUser5, string? melissaKey = null,
        string? postmarkKey = null, string? dlFormat = null)
    {
        var settings = new Mock<ISettingProvider>();
        settings.Setup(value => value.GetFieldRequired(nameof(Registration.User5))).Returns(requiredUser5);
        settings.Setup(value => value.GetFieldLabel(nameof(Registration.User5))).Returns("Responsible person");
        settings.SetupGet(value => value.DisplayResponsiblePersonField).Returns(true);
        settings.SetupGet(value => value.DisableBranch).Returns(false);
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
        User5 = user5,
        Password = "1234",
        Password2 = "1234",
        StreetOne = "1 Main St",
        City = "Columbus",
        State = "OH",
        PostalCode = "43215"
    };
}
