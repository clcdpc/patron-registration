using Clc.Melissa;
using Clc.PatronRegistration.Administration;
using Clc.PatronRegistration.Configuration;
using Clc.PatronRegistration.Data;
using Clc.PatronRegistration.Web.Controllers;
using Clc.PatronRegistration.Web.Settings;
using Clc.Polaris.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using System.ComponentModel.DataAnnotations;
using Clc.PatronRegistration.Validators;
using Moq;

namespace Clc.PatronRegistration.Tests;

[TestClass]
public class PreviewRequestContextTests
{
    [TestMethod]
    public void Resolver_ProvidesDraftDynamicMetadataAndStagedClientCredentials()
    {
        var draft = ActiveDraft(
            new("label.NameFirst", DraftOperation.Upsert, "Preview first name"),
            new("require.NameFirst", DraftOperation.Upsert, "true"),
            new("alert.NameFirst", DraftOperation.Upsert, "Preview name required"),
            new("melissa_data_api_key", DraftOperation.Upsert, "preview-melissa-key"),
            new("postmark_api_key", DraftOperation.Upsert, "preview-postmark-key"));
        var context = CreateResolver(draft: draft).Resolve("valid-token");

        Assert.IsNotNull(context);
        Assert.AreEqual("Preview first name", context.Settings.GetFieldLabel("NameFirst"));
        Assert.IsTrue(context.Settings.GetFieldRequired("NameFirst"));
        Assert.AreEqual("Preview name required", context.Settings.GetFieldErrorMessage("NameFirst"));

        var emailFactory = new Mock<IEmailSenderFactory>();
        emailFactory.Setup(factory => factory.Create("preview-postmark-key")).Returns(Mock.Of<IEmailSender>());
        var melissaFactory = new Mock<IMelissaClientFactory>();
        melissaFactory.Setup(factory => factory.Create("preview-melissa-key")).Returns(Mock.Of<IMelissaRestClient>());
        RegistrationClientProvider.CreateEmail(context.Settings, emailFactory.Object);
        RegistrationClientProvider.CreateMelissa(context.Settings, melissaFactory.Object);
        emailFactory.Verify(factory => factory.Create("preview-postmark-key"), Times.Once);
        melissaFactory.Verify(factory => factory.Create("preview-melissa-key"), Times.Once);
    }

    [DataTestMethod]
    [DataRow(true, "Active", false)]
    [DataRow(false, "Committed", false)]
    [DataRow(false, "Discarded", false)]
    [DataRow(false, "Active", true)]
    public void InvalidRevokedExpiredOrInactiveLink_CannotResolvePreviewSettings(bool revoked, string status, bool expired)
    {
        var link = Link(status, revoked ? DateTime.UtcNow : null, expired ? DateTime.UtcNow.AddMinutes(-1) : null);

        Assert.IsNull(CreateResolver(link: link).Resolve("token"));
    }

    [TestMethod]
    public void BranchBecomingIneligible_InvalidatesContextResolution()
    {
        var eligibility = new Mock<IPreviewBranchEligibilityService>();
        eligibility.SetupSequence(service => service.IsEligible(3, 3, 1)).Returns(true).Returns(false);
        var resolver = CreateResolver(eligibility: eligibility);

        Assert.IsNotNull(resolver.Resolve("token"));
        Assert.IsNull(resolver.Resolve("token"));
    }

    [TestMethod]
    public void InvalidToken_CannotResolvePreviewSettings()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        repository.Setup(service => service.FindPreviewLink(It.IsAny<byte[]>())).Returns((PreviewLinkRecord?)null);
        var resolver = new PreviewContextResolver(
            repository.Object,
            new PreviewTokenService(),
            Mock.Of<IPreviewBranchEligibilityService>(),
            new TestCache(),
            Options.Create(new SettingsAdministrationOptions()));

        Assert.IsNull(resolver.Resolve("invalid"));
    }

    [TestMethod]
    public void SafePreviewSubmit_DoesNotInvokeExternalClients()
    {
        var context = CreateResolver(link: Link("Active", allowLiveSubmission: false)).Resolve("token")!;
        var repository = new Mock<ISettingsAdministrationRepository>();
        var papi = new Mock<IPapiClient>();
        var melissa = new Mock<IMelissaRestClient>();
        var email = new Mock<IEmailSender>();
        var controller = new PreviewController(repository.Object, new PreviewRequestContextAccessor { IsPreviewRequest = true, Current = context }, new TestCache(), Mock.Of<IDbHelper>(), papi.Object, melissa.Object, email.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var result = controller.Submit("ignored", new Registration(context.Settings));

        Assert.IsInstanceOfType<JsonResult>(result);
        var attempt = ((JsonResult)result).Value as RegistrationAttempt;
        Assert.IsNotNull(attempt);
        Assert.AreEqual(0, attempt.Errors.Count);
        StringAssert.Contains(attempt.Message, "MVC validation passed");
        Assert.AreEqual(0, papi.Invocations.Count);
        Assert.AreEqual(0, melissa.Invocations.Count);
        Assert.AreEqual(0, email.Invocations.Count);
    }

    [DataTestMethod]
    [DataRow("NameFirst", "Preview first name is required.")]
    [DataRow("EmailAddress", "The email address is invalid.")]
    public void SafePreview_ReturnsActualMvcErrorsWithoutExternalCalls(string field, string message)
    {
        var context = CreateResolver(link: Link("Active", allowLiveSubmission: false)).Resolve("token")!;
        var repository = new Mock<ISettingsAdministrationRepository>();
        var papi = new Mock<IPapiClient>();
        var melissa = new Mock<IMelissaRestClient>();
        var email = new Mock<IEmailSender>();
        var controller = new PreviewController(repository.Object, new PreviewRequestContextAccessor { IsPreviewRequest = true, Current = context }, new TestCache(), Mock.Of<IDbHelper>(), papi.Object, melissa.Object, email.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.ModelState.AddModelError(field, message);

        var result = (JsonResult)controller.Submit("ignored", new Registration(context.Settings));
        var attempt = (RegistrationAttempt)result.Value!;

        Assert.AreEqual(new KeyValuePair<string, string>(field, message), attempt.Errors.Single());
        Assert.AreEqual(0, papi.Invocations.Count);
        Assert.AreEqual(0, melissa.Invocations.Count);
        Assert.AreEqual(0, email.Invocations.Count);
    }

    [TestMethod]
    public void LivePreview_StagedRequiredFieldModelStateBlocksAllExternalCalls()
    {
        var draft = ActiveDraft(new("require.PhoneVoice1", DraftOperation.Upsert, "true"));
        var context = CreateResolver(draft: draft, link: Link("Active", allowLiveSubmission: true)).Resolve("token")!;
        var repository = new Mock<ISettingsAdministrationRepository>();
        var papi = new Mock<IPapiClient>();
        var melissa = new Mock<IMelissaRestClient>();
        var email = new Mock<IEmailSender>();
        var controller = new PreviewController(repository.Object, new PreviewRequestContextAccessor { IsPreviewRequest = true, Current = context }, new TestCache(), Mock.Of<IDbHelper>(), papi.Object, melissa.Object, email.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.ModelState.AddModelError("PhoneVoice1", "Phone is required by this preview draft.");

        var result = (JsonResult)controller.Submit("ignored", new Registration(context.Settings));
        var attempt = (RegistrationAttempt)result.Value!;

        Assert.IsFalse(attempt.IsSuccess);
        Assert.AreEqual("PhoneVoice1", attempt.Errors.Single().Key);
        Assert.AreEqual(0, papi.Invocations.Count);
        Assert.AreEqual(0, melissa.Invocations.Count);
        Assert.AreEqual(0, email.Invocations.Count);
    }

    [TestMethod]
    public void RequestSettingResolver_UsesPreviewProviderBeforeModelBindingAndRejectsInvalidPreview()
    {
        var context = CreateResolver().Resolve("token")!;
        var accessor = new PreviewRequestContextAccessor { IsPreviewRequest = true, Current = context };
        var resolver = new RequestSettingProviderResolver(accessor, new TestCache(), Options.Create(new SettingsAdministrationOptions()), new RegistrationConfiguration());

        Assert.AreSame(context.Settings, resolver.Resolve(new DefaultHttpContext()));

        accessor.Current = null;
        Assert.ThrowsException<InvalidOperationException>(() => resolver.Resolve(new DefaultHttpContext()));
    }

    [TestMethod]
    public void RequestSettingResolver_NormalRegistrationUsesRouteOrganizationAndFormCode()
    {
        var resolver = new RequestSettingProviderResolver(new PreviewRequestContextAccessor(), new TestCache(), Options.Create(new SettingsAdministrationOptions()), new RegistrationConfiguration());
        var httpContext = new DefaultHttpContext();
        httpContext.Request.RouteValues["orgId"] = "3";
        httpContext.Request.RouteValues["formCode"] = "kids";

        var settings = resolver.Resolve(httpContext);

        Assert.AreEqual(3, settings.OrganizationId);
        Assert.AreEqual("kids", settings.FormCode);
    }

    [TestMethod]
    public void PreviewProvider_IsUsedByRegistrationConstructionDisplayNameAndRequiredValidation()
    {
        var context = CreateResolver(draft: ActiveDraft(
            new("label.NameFirst", DraftOperation.Upsert, "Preview first name"),
            new("require.NameFirst", DraftOperation.Upsert, "true"),
            new("alert.NameFirst", DraftOperation.Upsert, "Preview alert"))).Resolve("token")!;
        var services = new ServiceCollection().AddSingleton<ISettingProvider>(context.Settings).BuildServiceProvider();
        var httpContext = new DefaultHttpContext { RequestServices = services };
        new HttpContextAccessor { HttpContext = httpContext };

        var registration = new Registration();
        var displayName = new DbConfiguredDisplayNameAttribute(nameof(Registration.NameFirst)).DisplayName;
        var validation = new DbConfiguredRequired().GetValidationResult(
            string.Empty,
            new ValidationContext(registration, services, null) { MemberName = nameof(Registration.NameFirst) });

        Assert.AreSame(context.Settings, registration.Settings);
        Assert.AreEqual("Preview first name", displayName);
        Assert.IsNotNull(validation);
        StringAssert.Contains(validation.ErrorMessage, "Preview first name");
        Assert.AreEqual("Preview alert", context.Settings.GetFieldErrorMessage(nameof(Registration.NameFirst)));
    }

    private static PreviewContextResolver CreateResolver(
        SettingDraft? draft = null,
        PreviewLinkRecord? link = null,
        Mock<IPreviewBranchEligibilityService>? eligibility = null)
    {
        draft ??= ActiveDraft();
        link ??= Link("Active");
        var repository = new Mock<ISettingsAdministrationRepository>();
        repository.Setup(service => service.FindPreviewLink(It.IsAny<byte[]>())).Returns(link);
        repository.Setup(service => service.GetDraft(link.DraftId)).Returns(draft);
        eligibility ??= new Mock<IPreviewBranchEligibilityService>();
        eligibility.Setup(service => service.IsEligible(draft.OrganizationId, link.OperationalBranchId, 1)).Returns(true);
        return new PreviewContextResolver(repository.Object, new PreviewTokenService(), eligibility.Object, new TestCache(), Options.Create(new SettingsAdministrationOptions()));
    }

    private static SettingDraft ActiveDraft(params SettingMutation[] changes) =>
        new(7, 3, string.Empty, 0, DraftStatus.Active, changes);

    private static PreviewLinkRecord Link(
        string status,
        DateTime? revokedAt = null,
        DateTime? expiresAt = null,
        bool allowLiveSubmission = false) =>
        new(9, 7, new byte[32], allowLiveSubmission, revokedAt, expiresAt, 3, string.Empty, status, 3);
}
