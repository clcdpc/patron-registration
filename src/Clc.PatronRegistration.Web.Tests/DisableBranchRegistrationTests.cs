using Clc.Melissa;
using Clc.Melissa.Models;
using Clc.PatronRegistration.Administration;
using Clc.PatronRegistration.Configuration;
using Clc.PatronRegistration.Data;
using Clc.PatronRegistration.Helpers;
using Clc.PatronRegistration.Web.Controllers;
using Clc.PatronRegistration.Web.Settings;
using Clc.Polaris.Api;
using Clc.Polaris.Api.Models;
using Clc.Rest;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.Extensions.Options;
using Moq;

namespace Clc.PatronRegistration.Tests;

[TestClass]
public sealed class DisableBranchRegistrationTests
{
    [TestMethod]
    public void EnabledNormalRegistration_ContinuesIntoExistingWorkflow()
    {
        var settings = Settings(disabled: false);
        settings.SetupGet(value => value.PhoneNumberFormat).Returns("($1) $2-$3");
        settings.SetupGet(value => value.FormCode).Returns(string.Empty);
        var db = new Mock<IDbHelper>();
        db.Setup(value => value.CheckPatronIsDuplicate(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>()))
            .Returns(false);
        var melissa = new Mock<IMelissaRestClient>();
        melissa.Setup(value => value.PersonatorRequest(It.IsAny<PersonatorRequestRecord>()))
            .Throws(new InvalidOperationException("normal workflow reached"));
        var registration = ValidRegistration(settings.Object);

        var exception = Assert.ThrowsException<InvalidOperationException>(() => registration.CreateRegistration(
            "127.0.0.1", new ModelStateDictionary(), settings.Object, db.Object,
            Mock.Of<IPapiClient>(), melissa.Object, Mock.Of<IEmailSender>()));

        Assert.AreEqual("normal workflow reached", exception.Message);
        melissa.Verify(value => value.PersonatorRequest(It.IsAny<PersonatorRequestRecord>()), Times.Once);
    }

    [TestMethod]
    public void DisabledNormalRegistration_BlocksBeforeEveryRegistrationSideEffect()
    {
        var settings = Settings(disabled: true);
        settings.SetupGet(value => value.PerformPapiDupeBypass).Returns(true);
        settings.SetupGet(value => value.AddToRecordSetId).Returns(73);
        settings.SetupGet(value => value.PostRegistrationNoteText).Returns("note");
        settings.SetupGet(value => value.WelcomeEmailTemplateText).Returns("welcome");
        settings.SetupGet(value => value.WelcomeEmailFromAddress).Returns("from@example.com");
        var db = new Mock<IDbHelper>();
        var papi = new Mock<IPapiClient>();
        var melissa = new Mock<IMelissaRestClient>();
        var email = new Mock<IEmailSender>();
        var registration = ValidRegistration(settings.Object);

        var previousGlobal = DbHelper.Global;
        try
        {
            DbHelper.Global = db.Object;
            var result = registration.CreateRegistration(
                "127.0.0.1", new ModelStateDictionary(), settings.Object, db.Object,
                papi.Object, melissa.Object, email.Object);

            Assert.AreEqual(RegistrationStatus.Disabled, result.Status);
            Assert.AreEqual(Registration.RegistrationUnavailableMessage, result.Message);
            melissa.Verify(value => value.PersonatorRequest(It.IsAny<PersonatorRequestRecord>()), Times.Never);
            papi.Verify(value => value.PatronRegistrationCreate(It.IsAny<PatronRegistrationParams>()), Times.Never);
            papi.Verify(value => value.RecordSetContentAdd(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
            papi.Verify(value => value.UpdatePatronNotesData(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<UpdateNoteMode>(), It.IsAny<int?>()), Times.Never);
            email.Verify(value => value.Send(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            db.Verify(value => value.AddRegistrationHistoryEntry(It.IsAny<RegistrationHistoryEntry>()), Times.Never);
        }
        finally
        {
            DbHelper.Global = previousGlobal;
        }
    }

    [TestMethod]
    public void DisableAfterLoad_IsRecheckedAtSubmission()
    {
        var disabled = false;
        var settings = new Mock<ISettingProvider>();
        settings.SetupGet(value => value.DisableBranch).Returns(() => disabled);
        var db = new Mock<IDbHelper>();
        var melissa = new Mock<IMelissaRestClient>();
        var registration = ValidRegistration(settings.Object);

        Assert.IsFalse(registration.IsRegistrationDisabled);
        disabled = true;

        var result = registration.CreateRegistration(
            "127.0.0.1", new ModelStateDictionary(), settings.Object, db.Object,
            Mock.Of<IPapiClient>(), melissa.Object, Mock.Of<IEmailSender>());

        Assert.AreEqual(RegistrationStatus.Disabled, result.Status);
        melissa.Verify(value => value.PersonatorRequest(It.IsAny<PersonatorRequestRecord>()), Times.Never);
        db.VerifyNoOtherCalls();
    }

    [TestMethod]
    public void HoneypotBehavior_RemainsSeparateAndSideEffectFree()
    {
        var settings = Settings(disabled: false);
        var registration = new Registration(settings.Object) { a_password = "bot-filled" };
        var db = new Mock<IDbHelper>();
        var papi = new Mock<IPapiClient>();
        var melissa = new Mock<IMelissaRestClient>();
        var email = new Mock<IEmailSender>();

        Assert.IsTrue(registration.HasHoneypotValue);
        Assert.IsTrue(registration.ShouldSkipRegistration());
        Assert.IsFalse(new Registration(settings.Object).HasHoneypotValue);

        var result = registration.CreateRegistration(
            "127.0.0.1", new ModelStateDictionary(), settings.Object, db.Object,
            papi.Object, melissa.Object, email.Object);

        Assert.AreEqual(RegistrationStatus.Error, result.Status);
        Assert.AreEqual(string.Empty, result.Message);
        Assert.IsFalse(registration.IsRegistrationDisabled);
        Assert.AreEqual(0, papi.Invocations.Count);
        Assert.AreEqual(0, melissa.Invocations.Count);
        Assert.AreEqual(0, email.Invocations.Count);
        Assert.AreEqual(0, db.Invocations.Count);
    }

    [TestMethod]
    public void DisabledNormalRegistrationPost_UsesEffectiveSubmittedBranchProvider()
    {
        var routeSettings = Settings(disabled: false);
        routeSettings.SetupGet(value => value.OrganizationId).Returns(2);
        routeSettings.SetupGet(value => value.LibraryId).Returns(2);
        var effectiveBranchSettings = Settings(disabled: true);
        effectiveBranchSettings.SetupGet(value => value.OrganizationId).Returns(3);
        effectiveBranchSettings.SetupGet(value => value.LibraryId).Returns(2);
        var scopeResolver = new Mock<IRegistrationScopeResolver>();
        scopeResolver.Setup(value => value.ResolveForSubmission(It.IsAny<HttpContext>(), routeSettings.Object, 3))
            .Returns(new RegistrationScopeResolution(true, effectiveBranchSettings.Object));
        var controller = Controller(routeSettings.Object, scopeResolver.Object,
            Mock.Of<IPapiClient>(), Mock.Of<IMelissaRestClient>(), Mock.Of<IDbHelper>(), Mock.Of<IEmailSender>());
        var registration = ValidRegistration(routeSettings.Object);
        registration.PatronBranchID = 3;

        var result = controller.Submit(registration);

        Assert.AreEqual(RegistrationStatus.Disabled, result.Status);
        Assert.AreSame(effectiveBranchSettings.Object, registration.Settings);
        Assert.AreEqual(2, registration.LibraryId);
        scopeResolver.Verify(value => value.ResolveForSubmission(It.IsAny<HttpContext>(), routeSettings.Object, 3), Times.Once);
    }

    [TestMethod]
    public void DisabledNormalRegistrationGet_ReturnsUnavailableViewWithoutRegistrationForm()
    {
        CacheHelper.Configure(new TestCache());
        var settings = Settings(disabled: true);
        settings.SetupGet(value => value.OrganizationId).Returns(3);
        settings.SetupGet(value => value.LibraryId).Returns(2);
        settings.SetupGet(value => value.DriversLicenseButtonEnabledIpAddresses).Returns([]);
        settings.SetupGet(value => value.EnablePatronBranchSelectOption).Returns(false);
        var db = new Mock<IDbHelper>();
        db.Setup(value => value.GetSelfRegistrationOrganizations(null)).Returns([Organization(3, 2)]);
        db.Setup(value => value.GetSelfRegistrationBranches(2)).Returns([Organization(3, 2)]);
        db.Setup(value => value.GetPickupBranches(2)).Returns([]);
        db.Setup(value => value.GetGendersToOrganizations(3)).Returns([]);
        var scopeResolver = new Mock<IRegistrationScopeResolver>();
        scopeResolver.Setup(value => value.ResolveForSubmission(It.IsAny<HttpContext>(), settings.Object, 3))
            .Returns(new RegistrationScopeResolution(true, settings.Object));
        var controller = Controller(settings.Object, scopeResolver.Object,
            Mock.Of<IPapiClient>(), Mock.Of<IMelissaRestClient>(), db.Object, Mock.Of<IEmailSender>());

        var result = controller.Create(3);

        var view = result as ViewResult;
        Assert.IsNotNull(view);
        Assert.AreEqual("Unavailable", view.ViewName);
        var root = FindRepositoryRoot();
        var unavailable = File.ReadAllText(Path.Combine(root, "src/Clc.PatronRegistration.Web/Views/Registration/Unavailable.cshtml"));
        StringAssert.Contains(unavailable, "Registration currently unavailable");
        Assert.IsFalse(unavailable.Contains("registerButton", StringComparison.Ordinal));
    }

    [TestMethod]
    public void BranchSelection_UsesEffectiveBranchSettingsAndHidesDisabledBranch()
    {
        var organizations = new List<OrganizationsGetRow>
        {
            Organization(1, null, 1),
            Organization(2, 1, 2),
            Organization(3, 2),
            Organization(4, 2)
        };
        var cache = new Mock<ICache>();
        cache.SetupGet(value => value.OrganizationCache).Returns(organizations);
        cache.SetupGet(value => value.SettingsCache).Returns([
            Setting(2, "enable_patron_branch_select_option", "true", "form"),
            Setting(3, "disable_branch", "true", "form")
        ]);
        cache.Setup(value => value.GetOrg(It.IsAny<int>())).Returns((int id) => organizations.Single(value => value.OrganizationID == id));
        var db = new Mock<IDbHelper>();
        db.Setup(value => value.GetSelfRegistrationBranches(2)).Returns([organizations[2], organizations[3]]);
        var requestSettings = new DbSettingProvider(2, cache.Object, "form", 1);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.RouteValues["formCode"] = "form";
        var settingResolver = new RequestSettingProviderResolver(
            new PreviewRequestContextAccessor(), new SettingsPageBrandingContextAccessor(),
            Mock.Of<ISettingsAuthorizationService>(), Mock.Of<IFormCodeAvailabilityService>(), cache.Object,
            Options.Create(new SettingsAdministrationOptions()), new RegistrationConfiguration());
        var resolver = new RegistrationScopeResolver(db.Object, cache.Object, settingResolver);

        var available = resolver.GetAvailableBranches(httpContext, requestSettings);
        var disabledSubmission = resolver.ResolveForSubmission(httpContext, requestSettings, 3);
        var enabledSubmission = resolver.ResolveForSubmission(httpContext, requestSettings, 4);

        CollectionAssert.AreEquivalent(new[] { 4 }, available.Select(value => value.OrganizationID).ToArray());
        Assert.IsTrue(disabledSubmission.IsValid);
        Assert.IsTrue(disabledSubmission.Settings.DisableBranch);
        Assert.IsTrue(enabledSubmission.IsValid);
        Assert.IsFalse(enabledSubmission.Settings.DisableBranch);
    }

    [TestMethod]
    public void DisableBranch_InheritanceUsesTheExistingResolverPrecedence()
    {
        Assert.IsTrue(Provider(Setting(1, "disable_branch", "true")).DisableBranch);
        Assert.IsFalse(Provider(
            Setting(1, "disable_branch", "true"), Setting(2, "disable_branch", "false")).DisableBranch);
        Assert.IsFalse(Provider(
            Setting(1, "disable_branch", "true"), Setting(3, "disable_branch", "false")).DisableBranch);
        Assert.IsFalse(Provider(
            Setting(1, "disable_branch", "true"), Setting(3, "disable_branch", "false", "form")).DisableBranch);
        Assert.IsFalse(Provider(
            Setting(1, "disable_branch", "true"), Setting(2, "disable_branch", "true"),
            Setting(3, "disable_branch", "false"), Setting(3, "disable_branch", "false", "form")).DisableBranch);
    }

    [TestMethod]
    public void LivePreview_WithEffectiveDisableBranchTrue_BlocksAllSideEffects()
    {
        var cache = new TestCache { SettingsCache = [Setting(3, "disable_branch", "true")] };
        var draft = Draft(3);
        var settings = new PreviewSettingProvider(draft, 3, cache, 1);
        var (controller, papi, melissa, email, db) = PreviewControllerFor(settings, allowLive: true);

        var result = (JsonResult)controller.Submit("ignored", ValidRegistration(settings));
        var attempt = (RegistrationAttempt)result.Value!;

        Assert.AreEqual(RegistrationStatus.Disabled, attempt.Status);
        melissa.Verify(value => value.PersonatorRequest(It.IsAny<PersonatorRequestRecord>()), Times.Never);
        papi.Verify(value => value.PatronRegistrationCreate(It.IsAny<PatronRegistrationParams>()), Times.Never);
        papi.Verify(value => value.RecordSetContentAdd(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
        papi.Verify(value => value.UpdatePatronNotesData(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<UpdateNoteMode>(), It.IsAny<int?>()), Times.Never);
        email.Verify(value => value.Send(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        db.Verify(value => value.AddRegistrationHistoryEntry(It.IsAny<RegistrationHistoryEntry>()), Times.Never);
    }

    [TestMethod]
    public void LivePreview_WithInheritedDisableBranchTrue_BlocksSubmission()
    {
        var cache = new TestCache { SettingsCache = [Setting(1, "disable_branch", "true")] };
        var settings = new PreviewSettingProvider(Draft(2), 3, cache, 1);

        Assert.IsTrue(settings.DisableBranch);
        var (controller, papi, melissa, email, _) = PreviewControllerFor(settings, allowLive: true);
        var result = (JsonResult)controller.Submit("ignored", ValidRegistration(settings));

        Assert.AreEqual(RegistrationStatus.Disabled, ((RegistrationAttempt)result.Value!).Status);
        Assert.AreEqual(0, papi.Invocations.Count);
        Assert.AreEqual(0, melissa.Invocations.Count);
        Assert.AreEqual(0, email.Invocations.Count);
    }

    [TestMethod]
    public void LivePreview_DraftFalseOverrideFollowsEffectivePreviewProvider()
    {
        var cache = new TestCache { SettingsCache = [Setting(1, "disable_branch", "true")] };
        var settings = new PreviewSettingProvider(
            Draft(3, new SettingMutation("disable_branch", DraftOperation.Upsert, "false")), 3, cache, 1);

        Assert.IsFalse(settings.DisableBranch);
        var db = new Mock<IDbHelper>();
        db.Setup(value => value.CheckPatronIsDuplicate(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>()))
            .Returns(false);
        var melissa = new Mock<IMelissaRestClient>();
        melissa.Setup(value => value.PersonatorRequest(It.IsAny<PersonatorRequestRecord>()))
            .Throws(new InvalidOperationException("preview workflow reached"));
        var repository = new Mock<ISettingsAdministrationRepository>();
        repository.Setup(service => service.TryAdmitLivePreviewSubmission(It.IsAny<long>(), It.IsAny<long>()))
            .Returns(Mock.Of<ILivePreviewSubmissionAdmission>());
        var controller = new PreviewController(
            repository.Object,
            new PreviewRequestContextAccessor { IsPreviewRequest = true, Current = PreviewContext(settings, true) },
            new TestCache(), db.Object, Mock.Of<IPapiClient>(), melissa.Object, Mock.Of<IEmailSender>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var exception = Assert.ThrowsException<InvalidOperationException>(() => controller.Submit("ignored", ValidRegistration(settings)));

        Assert.AreEqual("preview workflow reached", exception.Message);
        melissa.Verify(value => value.PersonatorRequest(It.IsAny<PersonatorRequestRecord>()), Times.Once);
    }

    [TestMethod]
    public void SafePreview_DisabledConfigurationRemainsSideEffectFree()
    {
        var cache = new TestCache { SettingsCache = [Setting(3, "disable_branch", "true")] };
        var settings = new PreviewSettingProvider(Draft(3), 3, cache, 1);
        var (controller, papi, melissa, email, _) = PreviewControllerFor(settings, allowLive: false);

        var result = (JsonResult)controller.Submit("ignored", ValidRegistration(settings));
        var attempt = (RegistrationAttempt)result.Value!;

        Assert.AreEqual(RegistrationStatus.Error, attempt.Status);
        StringAssert.Contains(attempt.Message, "safe preview");
        Assert.AreEqual(0, papi.Invocations.Count);
        Assert.AreEqual(0, melissa.Invocations.Count);
        Assert.AreEqual(0, email.Invocations.Count);
    }

    private static Mock<ISettingProvider> Settings(bool disabled)
    {
        var settings = new Mock<ISettingProvider>();
        settings.SetupGet(value => value.DisableBranch).Returns(disabled);
        settings.SetupGet(value => value.DriversLicenseButtonEnabledIpAddresses).Returns([]);
        return settings;
    }

    private static Registration ValidRegistration(ISettingProvider settings) => new(settings)
    {
        NameFirst = "Jane",
        NameLast = "Doe",
        Birthdate = new DateTime(2000, 1, 1),
        PatronBranchID = 3,
        State = "OH",
        StreetOne = "1 Main St",
        City = "Columbus",
        PostalCode = "43215"
    };

    private static RegistrationFormSetting Setting(int organizationId, string key, string value, string formCode = "") => new()
    {
        OrganizationID = organizationId,
        Setting = key,
        Value = value,
        FormCode = formCode
    };

    private static ISettingProvider Provider(params RegistrationFormSetting[] settings) =>
        new DbSettingProvider(3, new TestCache { SettingsCache = settings.ToList() }, "form", 1);

    private static SettingDraft Draft(int organizationId, params SettingMutation[] changes) =>
        new(7, organizationId, "", 0, DraftStatus.Active, changes);

    private static PreviewRequestContext PreviewContext(ISettingProvider settings, bool allowLive) =>
        new(new PreviewLinkRecord(9, 7, new byte[32], allowLive, null, null, 3, "", "Active", 3)
            { LiveSettingsGeneration = allowLive ? 1 : null },
            Draft(3), (PreviewSettingProvider)settings);

    private static (PreviewController Controller, Mock<IPapiClient> Papi, Mock<IMelissaRestClient> Melissa,
        Mock<IEmailSender> Email, Mock<IDbHelper> Db) PreviewControllerFor(PreviewSettingProvider settings, bool allowLive)
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        repository.Setup(service => service.TryAdmitLivePreviewSubmission(It.IsAny<long>(), It.IsAny<long>()))
            .Returns(Mock.Of<ILivePreviewSubmissionAdmission>());
        var papi = new Mock<IPapiClient>();
        var melissa = new Mock<IMelissaRestClient>();
        var email = new Mock<IEmailSender>();
        var db = new Mock<IDbHelper>();
        var controller = new PreviewController(
            repository.Object,
            new PreviewRequestContextAccessor { IsPreviewRequest = true, Current = PreviewContext(settings, allowLive) },
            new TestCache(), db.Object, papi.Object, melissa.Object, email.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        return (controller, papi, melissa, email, db);
    }

    private static RegistrationController Controller(
        ISettingProvider settings,
        IRegistrationScopeResolver scopeResolver,
        IPapiClient papi,
        IMelissaRestClient melissa,
        IDbHelper db,
        IEmailSender email)
    {
        var melissaFactory = new Mock<IMelissaClientFactory>();
        melissaFactory.Setup(factory => factory.Create(It.IsAny<string>())).Returns(melissa);
        var emailFactory = new Mock<IEmailSenderFactory>();
        emailFactory.Setup(factory => factory.Create(It.IsAny<string>())).Returns(email);
        return new(papi, db, settings, emailFactory.Object, melissaFactory.Object,
            Mock.Of<IObjectModelValidator>(), scopeResolver)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private static OrganizationsGetRow Organization(int id, int? parentId, int code = 3) => new()
    {
        OrganizationID = id,
        ParentOrganizationID = parentId,
        OrganizationCodeID = code,
        DisplayName = $"Organization {id}",
        Name = $"Organization {id}",
        Abbreviation = $"O{id}"
    };

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "Clc.PatronRegistration.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
