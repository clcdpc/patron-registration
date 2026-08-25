using Clc.Melissa;
using Clc.PatronRegistration.Administration;
using Clc.PatronRegistration.Configuration;
using Clc.PatronRegistration.Data;
using Clc.PatronRegistration.Helpers;
using Clc.PatronRegistration.Web.Controllers;
using Clc.PatronRegistration.Web.Settings;
using Clc.Polaris.Api;
using Clc.Polaris.Api.Models;
using Clc.Rest;
using Clc.Rest.Models;
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
    public void PreviewController_InvalidResponseSetsPortableSecurityHeaders()
    {
        var controller = new PreviewController(
            Mock.Of<ISettingsAdministrationRepository>(),
            new PreviewRequestContextAccessor { IsPreviewRequest = true },
            new TestCache(),
            Mock.Of<IDbHelper>(),
            Mock.Of<IPapiClient>(),
            Mock.Of<IMelissaRestClient>(),
            Mock.Of<IEmailSender>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var result = controller.Submit("invalid", new Registration(Mock.Of<ISettingProvider>()));

        Assert.IsInstanceOfType<NotFoundObjectResult>(result);
        Assert.AreEqual("no-referrer", controller.Response.Headers["Referrer-Policy"].ToString());
        Assert.AreEqual("no-store, no-cache, max-age=0", controller.Response.Headers.CacheControl.ToString());
        Assert.AreEqual("no-cache", controller.Response.Headers.Pragma.ToString());
    }

    [TestMethod]
    public void PreviewAssetRouteAllowsAnAssetStagedAtTheDraftScope()
    {
        var draft = new SettingDraft(7, 2, string.Empty, 0, DraftStatus.Active,
            [new SettingMutation("header_image_asset_id", DraftOperation.Upsert, "42")]);
        var context = new PreviewRequestContext(
            Link("Active"),
            draft,
            new PreviewSettingProvider(draft, 3, new TestCache(), 1));
        var accessor = new PreviewRequestContextAccessor { IsPreviewRequest = true, PlaintextToken = "preview-token", Current = context };
        var assets = new Mock<IRegistrationFormAssetRepository>();
        assets.Setup(service => service.GetMetadata(42)).Returns(new RegistrationFormAssetMetadata(
            42, "draft.png", "image/png", "hash", DateTime.UtcNow, DateTime.UtcNow));
        assets.Setup(service => service.Get(42)).Returns(new RegistrationFormAsset(
            42, "draft.png", "image/png", [1, 2], "hash", DateTime.UtcNow, DateTime.UtcNow));
        var assetAuthorization = new Mock<IRegistrationFormAssetAuthorization>();
        assetAuthorization.Setup(service => service.GetAuthorizedMetadata(42, 2, string.Empty))
            .Returns(assets.Object.GetMetadata(42));
        var controller = new PreviewController(
            Mock.Of<ISettingsAdministrationRepository>(), accessor, new TestCache(), Mock.Of<IDbHelper>(),
            Mock.Of<IPapiClient>(), Mock.Of<IMelissaRestClient>(), Mock.Of<IEmailSender>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var result = (FileContentResult)controller.Asset("preview-token", 42, assets.Object, assetAuthorization.Object);

        CollectionAssert.AreEqual(new byte[] { 1, 2 }, result.FileContents);
        Assert.IsInstanceOfType(controller.Asset("preview-token", 99, assets.Object, assetAuthorization.Object), typeof(NotFoundResult));
        assetAuthorization.Verify(service => service.GetAuthorizedMetadata(42, 3, string.Empty), Times.Once);
        assetAuthorization.Verify(service => service.GetAuthorizedMetadata(42, 2, string.Empty), Times.Once);
        assets.Verify(service => service.Get(99), Times.Never);
    }

    [TestMethod]
    public void PreviewAssetRouteAuthorizesEffectiveAssetAtOperationalBranch()
    {
        var cache = new TestCache
        {
            SettingsCache =
            [
                new RegistrationFormSetting
                {
                    OrganizationID = 3,
                    FormCode = string.Empty,
                    Setting = "header_image_asset_id",
                    Value = "42"
                }
            ]
        };
        var draft = new SettingDraft(7, 2, string.Empty, 0, DraftStatus.Active, []);
        var settings = new PreviewSettingProvider(draft, 3, cache, 1);
        Assert.AreEqual(42, settings.HeaderImageAssetId);
        var context = new PreviewRequestContext(Link("Active"), draft, settings);
        var accessor = new PreviewRequestContextAccessor { IsPreviewRequest = true, PlaintextToken = "preview-token", Current = context };
        var assets = new Mock<IRegistrationFormAssetRepository>();
        var metadata = new RegistrationFormAssetMetadata(
            42, "branch.png", "image/png", "hash", DateTime.UtcNow, DateTime.UtcNow, 3, string.Empty);
        assets.Setup(service => service.GetMetadata(42)).Returns(metadata);
        assets.Setup(service => service.Get(42)).Returns(new RegistrationFormAsset(
            42, "branch.png", "image/png", [1, 2], "hash", DateTime.UtcNow, DateTime.UtcNow, 3, string.Empty));
        var assetAuthorization = new Mock<IRegistrationFormAssetAuthorization>();
        assetAuthorization.Setup(service => service.GetAuthorizedMetadata(42, 3, string.Empty)).Returns(metadata);
        var controller = new PreviewController(
            Mock.Of<ISettingsAdministrationRepository>(), accessor, new TestCache(), Mock.Of<IDbHelper>(),
            Mock.Of<IPapiClient>(), Mock.Of<IMelissaRestClient>(), Mock.Of<IEmailSender>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var result = (FileContentResult)controller.Asset("preview-token", 42, assets.Object, assetAuthorization.Object);

        CollectionAssert.AreEqual(new byte[] { 1, 2 }, result.FileContents);
        assetAuthorization.Verify(service => service.GetAuthorizedMetadata(42, 3, string.Empty), Times.Once);
        assetAuthorization.Verify(service => service.GetAuthorizedMetadata(42, 2, string.Empty), Times.Never);
    }

    [TestMethod]
    public async Task InvalidPreviewMiddlewareResponseSetsPortableSecurityHeaders()
    {
        var nextCalled = false;
        var middleware = new PreviewRequestContextMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var httpContext = new DefaultHttpContext();
        httpContext.Request.RouteValues["controller"] = "Preview";
        httpContext.Request.RouteValues["token"] = "invalid-token";
        var resolver = new Mock<IPreviewContextResolver>();
        resolver.Setup(service => service.Resolve("invalid-token")).Returns((PreviewRequestContext?)null);

        await middleware.InvokeAsync(httpContext, new PreviewRequestContextAccessor(), resolver.Object);

        Assert.IsFalse(nextCalled);
        Assert.AreEqual(StatusCodes.Status404NotFound, httpContext.Response.StatusCode);
        Assert.AreEqual("no-store", httpContext.Response.Headers.CacheControl.ToString());
        Assert.AreEqual("no-referrer", httpContext.Response.Headers["Referrer-Policy"].ToString());
    }

    [TestMethod]
    public void PreviewMiddleware_RedactsTokenFromApplicationRequestDiagnostics()
    {
        const string token = "recognizable-preview-token-for-redaction";
        var context = new DefaultHttpContext();
        context.Request.Path = $"/preview/{token}";
        context.Request.RouteValues["token"] = token;
        context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestFeature>()!.RawTarget = $"/preview/{token}";

        PreviewRequestContextMiddleware.RedactPreviewTarget(context);

        Assert.AreEqual("/preview/[redacted]", context.Request.Path.Value);
        Assert.AreEqual("/preview/[redacted]", context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestFeature>()!.RawTarget);
        Assert.AreEqual("[redacted]", context.Request.RouteValues["token"]);
        Assert.IsFalse(context.Request.Path.Value!.Contains(token, StringComparison.Ordinal));
    }

    [TestMethod]
    public void Resolver_ProvidesDraftDynamicMetadataAndStagedClientCredentials()
    {
        var draft = ActiveDraft(
            new("label.NameFirst", DraftOperation.Upsert, "Preview first name"),
            new("require.PhoneVoice1", DraftOperation.Upsert, "true"),
            new("alert.NameFirst", DraftOperation.Upsert, "Preview name required"),
            new("melissa_data_api_key", DraftOperation.Upsert, "preview-melissa-key"),
            new("postmark_api_key", DraftOperation.Upsert, "preview-postmark-key"));
        var context = CreateResolver(draft: draft).Resolve("valid-token");

        Assert.IsNotNull(context);
        Assert.AreEqual("Preview first name", context.Settings.GetFieldLabel("NameFirst"));
        Assert.IsTrue(context.Settings.GetFieldRequired("PhoneVoice1"));
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
        repository.Setup(service => service.ResolvePreviewContext(It.IsAny<byte[]>()))
            .Returns((PreviewContextSnapshot?)null);
        var resolver = new PreviewContextResolver(
            repository.Object,
            new PreviewTokenService(),
            Mock.Of<IPreviewBranchEligibilityService>(),
            new TestCache(),
            Options.Create(new SettingsAdministrationOptions()));

        Assert.IsNull(resolver.Resolve("invalid"));
    }

    [TestMethod]
    public void RestrictedTransitionCommittedDuringLookup_DoesNotExposeRevokedLinkDraft()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        repository.Setup(service => service.ResolvePreviewContext(It.IsAny<byte[]>()))
            .Returns((PreviewContextSnapshot?)null);
        var resolver = new PreviewContextResolver(
            repository.Object,
            new PreviewTokenService(),
            Mock.Of<IPreviewBranchEligibilityService>(),
            new TestCache(),
            Options.Create(new SettingsAdministrationOptions()));

        var context = resolver.Resolve("old-link-revoked-during-sensitive-transition");

        Assert.IsNull(context);
        repository.Verify(service => service.GetDraft(It.IsAny<long>()), Times.Never);
    }

    [TestMethod]
    public void SafePreviewSubmit_DoesNotInvokeExternalClients()
    {
        var context = CreateResolver(link: Link("Active", allowLiveSubmission: false)).Resolve("token")!;
        var repository = new Mock<ISettingsAdministrationRepository>();
        repository.Setup(service => service.TryAdmitLivePreviewSubmission(It.IsAny<long>(), It.IsAny<long>()))
            .Returns(Mock.Of<ILivePreviewSubmissionAdmission>());
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
    public void LivePreviewSubmit_RejectsGenerationChangeBeforeExternalClients()
    {
        var context = CreateResolver(link: Link("Active", allowLiveSubmission: true)).Resolve("token")!;
        var repository = new Mock<ISettingsAdministrationRepository>();
        repository.Setup(service => service.TryAdmitLivePreviewSubmission(context.Link.PreviewLinkId, 1)).Returns((ILivePreviewSubmissionAdmission?)null);
        var papi = new Mock<IPapiClient>();
        var melissa = new Mock<IMelissaRestClient>();
        var email = new Mock<IEmailSender>();
        var controller = new PreviewController(repository.Object,
            new PreviewRequestContextAccessor { IsPreviewRequest = true, Current = context },
            new TestCache(), Mock.Of<IDbHelper>(), papi.Object, melissa.Object, email.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var result = controller.Submit("ignored", new Registration(context.Settings));

        Assert.IsInstanceOfType<NotFoundObjectResult>(result);
        Assert.AreEqual(0, papi.Invocations.Count);
        Assert.AreEqual(0, melissa.Invocations.Count);
        Assert.AreEqual(0, email.Invocations.Count);
    }

    [TestMethod]
    public void LivePreviewSubmit_PreservesSuccessfulRegistrationWhenAuditPersistenceFails()
    {
        var draft = ActiveDraft();
        var settings = new PreviewSettingProvider(draft, 3, new TestCache(), 1);
        var context = new PreviewRequestContext(Link("Active", allowLiveSubmission: true), draft, settings);
        var repository = new Mock<ISettingsAdministrationRepository>();
        var admission = new Mock<ILivePreviewSubmissionAdmission>();
        repository.Setup(service => service.TryAdmitLivePreviewSubmission(context.Link.PreviewLinkId, 1))
            .Returns(admission.Object);
        repository.Setup(service => service.WriteAudit(
                It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<AuditContext>(),
                It.IsAny<string>(), It.IsAny<long?>(), It.IsAny<long?>(), It.IsAny<string>()))
            .Throws(new InvalidOperationException("audit persistence is unavailable"));

        var db = new Mock<IDbHelper>();
        db.Setup(service => service.CheckPatronIsDuplicate(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>()))
            .Returns(false);
        db.Setup(service => service.AddRegistrationHistoryEntry(It.IsAny<RegistrationHistoryEntry>())).Returns(true);

        var melissa = new Mock<IMelissaRestClient>();
        melissa.Setup(service => service.PersonatorRequest(It.IsAny<Clc.Melissa.Models.PersonatorRequestRecord>()))
            .Returns(new RestResponse<Clc.Melissa.Models.PersonatorResponse>
            {
                Data = new Clc.Melissa.Models.PersonatorResponse
                {
                    Records =
                    [
                        new Clc.Melissa.Models.Record
                        {
                            Results = "AS01",
                            AddressLine1 = "1 Main St",
                            AddressLine2 = string.Empty,
                            City = "Columbus",
                            State = "OH",
                            PostalCode = "43215"
                        }
                    ]
                }
            });

        var papi = new Mock<IPapiClient>();
        papi.Setup(service => service.PatronRegistrationCreate(It.IsAny<PatronRegistrationParams>()))
            .Returns(new RestResponse<PatronRegistrationCreateResult>
            {
                Data = new PatronRegistrationCreateResult
                {
                    PatronID = 123,
                    Barcode = "2000000000123",
                    PAPIErrorCode = 0
                }
            });

        var controller = new PreviewController(
            repository.Object,
            new PreviewRequestContextAccessor { IsPreviewRequest = true, Current = context },
            new TestCache(),
            db.Object,
            papi.Object,
            melissa.Object,
            Mock.Of<IEmailSender>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        var registration = new Registration(settings)
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

        var previousGlobal = DbHelper.Global;
        try
        {
            DbHelper.Global = db.Object;

            var result = (JsonResult)controller.Submit("ignored", registration);
            var attempt = (RegistrationAttempt)result.Value!;

            Assert.AreEqual(RegistrationStatus.Success, attempt.Status);
            papi.Verify(service => service.PatronRegistrationCreate(It.IsAny<PatronRegistrationParams>()), Times.Once);
            melissa.Verify(service => service.PersonatorRequest(It.IsAny<Clc.Melissa.Models.PersonatorRequestRecord>()), Times.Once);
            repository.Verify(service => service.WriteAudit(
                "LivePreviewSubmission", true, It.IsAny<AuditContext>(), null, null, context.Link.PreviewLinkId,
                It.Is<string>(json => json.Contains("Success", StringComparison.Ordinal))), Times.Once);
            admission.Verify(value => value.Dispose(), Times.Once);
            repository.Verify(service => service.IsLivePreviewCurrent(It.IsAny<long>(), It.IsAny<long>()), Times.Never);
        }
        finally
        {
            DbHelper.Global = previousGlobal;
        }
    }

    [TestMethod]
    public void LivePreview_StagedRequiredFieldModelStateBlocksAllExternalCalls()
    {
        var draft = ActiveDraft(new SettingMutation("require.PhoneVoice1", DraftOperation.Upsert, "true"));
        var context = CreateResolver(draft: draft, link: Link("Active", allowLiveSubmission: true)).Resolve("token")!;
        var repository = new Mock<ISettingsAdministrationRepository>();
        repository.Setup(service => service.TryAdmitLivePreviewSubmission(It.IsAny<long>(), It.IsAny<long>()))
            .Returns(Mock.Of<ILivePreviewSubmissionAdmission>());
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
        var resolver = new RequestSettingProviderResolver(accessor, new SettingsPageBrandingContextAccessor(),
            Mock.Of<ISettingsAuthorizationService>(), Mock.Of<IFormCodeAvailabilityService>(),
            new TestCache(), Options.Create(new SettingsAdministrationOptions()), new RegistrationConfiguration());

        Assert.AreSame(context.Settings, resolver.Resolve(new DefaultHttpContext()));

        accessor.Current = null;
        Assert.ThrowsException<InvalidOperationException>(() => resolver.Resolve(new DefaultHttpContext()));
    }

    [TestMethod]
    public void RequestSettingResolver_NormalRegistrationUsesRouteOrganizationAndFormCode()
    {
        var resolver = new RequestSettingProviderResolver(new PreviewRequestContextAccessor(), new SettingsPageBrandingContextAccessor(),
            Mock.Of<ISettingsAuthorizationService>(), Mock.Of<IFormCodeAvailabilityService>(),
            new TestCache(), Options.Create(new SettingsAdministrationOptions()), new RegistrationConfiguration());
        var httpContext = new DefaultHttpContext();
        httpContext.Request.RouteValues["orgId"] = "3";
        httpContext.Request.RouteValues["formCode"] = "kids";

        var settings = resolver.Resolve(httpContext);

        Assert.AreEqual(3, settings.OrganizationId);
        Assert.AreEqual("kids", settings.FormCode);
    }

    [DataTestMethod]
    [DataRow(2, 2)]
    [DataRow(3, 2)]
    public void RequestSettingResolver_SettingsBrandingUsesAuthenticatedScopeAndDefaultForm(int organizationId, int libraryId)
    {
        var branding = new SettingsPageBrandingContextAccessor();
        branding.Set(organizationId, libraryId);
        var cache = new TestCache
        {
            SettingsCache =
            [
                new() { OrganizationID = organizationId, FormCode = string.Empty, Setting = "header_image_asset_id", Value = "42" },
                new() { OrganizationID = organizationId, FormCode = string.Empty, Setting = "css_file", Value = "branding-css" },
                new() { OrganizationID = 3, FormCode = "kids", Setting = "header_image_asset_id", Value = "43" }
            ]
        };
        var resolver = new RequestSettingProviderResolver(new PreviewRequestContextAccessor(), branding,
            Mock.Of<ISettingsAuthorizationService>(), Mock.Of<IFormCodeAvailabilityService>(), cache,
            Options.Create(new SettingsAdministrationOptions()), new RegistrationConfiguration());
        var request = new DefaultHttpContext();
        request.Request.RouteValues["orgId"] = "3";
        request.Request.RouteValues["formCode"] = "kids";

        var settings = resolver.Resolve(request);

        Assert.AreEqual(organizationId, settings.OrganizationId);
        Assert.AreEqual(libraryId, ((DbSettingProvider)settings).LibraryId);
        Assert.AreEqual(string.Empty, settings.FormCode);
        Assert.AreEqual(42, settings.HeaderImageAssetId);
        Assert.AreEqual("branding-css", settings.CssFile);
    }

    [TestMethod]
    public void RequestSettingResolver_PreviewTakesPriorityOverSettingsBranding()
    {
        var preview = CreateResolver().Resolve("token")!;
        var branding = new SettingsPageBrandingContextAccessor();
        branding.Set(2, 2);
        var resolver = new RequestSettingProviderResolver(
            new PreviewRequestContextAccessor { IsPreviewRequest = true, Current = preview }, branding,
            Mock.Of<ISettingsAuthorizationService>(), Mock.Of<IFormCodeAvailabilityService>(),
            new TestCache(), Options.Create(new SettingsAdministrationOptions()), new RegistrationConfiguration());

        Assert.AreSame(preview.Settings, resolver.Resolve(new DefaultHttpContext()));
    }

    [TestMethod]
    public void SettingsBrandingAccessor_StateIsNotSharedByDifferentScopes()
    {
        var firstRequest = new SettingsPageBrandingContextAccessor();
        var secondRequest = new SettingsPageBrandingContextAccessor();
        firstRequest.Set(3, 2);

        Assert.AreEqual(new SettingsPageBrandingContext(3, 2), firstRequest.Current);
        Assert.IsNull(secondRequest.Current);
    }

    [TestMethod]
    public void PreviewProvider_IsUsedByRegistrationConstructionDisplayNameAndRequiredValidation()
    {
        var context = CreateResolver(draft: ActiveDraft(
            new("label.NameFirst", DraftOperation.Upsert, "Preview first name"),
            new("label.PhoneVoice1", DraftOperation.Upsert, "Preview first name"),
            new("require.PhoneVoice1", DraftOperation.Upsert, "true"),
            new("alert.NameFirst", DraftOperation.Upsert, "Preview alert"))).Resolve("token")!;
        var services = new ServiceCollection().AddSingleton<ISettingProvider>(context.Settings).BuildServiceProvider();
        var httpContext = new DefaultHttpContext { RequestServices = services };
        new HttpContextAccessor { HttpContext = httpContext };

        var registration = new Registration();
        var displayName = new DbConfiguredDisplayNameAttribute(nameof(Registration.NameFirst)).DisplayName;
        var validation = new DbConfiguredRequired().GetValidationResult(
            string.Empty,
            new ValidationContext(registration, services, null) { MemberName = nameof(Registration.PhoneVoice1) });

        Assert.AreSame(context.Settings, registration.Settings);
        Assert.AreEqual("Preview first name", displayName);
        Assert.IsNotNull(validation);
        StringAssert.Contains(validation.ErrorMessage, "Preview first name");
        Assert.AreEqual("Preview alert", context.Settings.GetFieldErrorMessage(nameof(Registration.NameFirst)));
    }

    [TestMethod]
    public void PreviewProvider_UsesStagedPreferredPickupRequirednessOnlyWhenDisplayed()
    {
        var hiddenContext = CreateResolver(draft: ActiveDraft(
            new SettingMutation("require.RequestPickupBranchID", DraftOperation.Upsert, "true"))).Resolve("token")!;
        var hiddenServices = new ServiceCollection()
            .AddSingleton<ISettingProvider>(hiddenContext.Settings)
            .BuildServiceProvider();
        var hiddenValidation = new DbConfiguredRequired().GetValidationResult(
            null,
            new ValidationContext(new Registration(hiddenContext.Settings), hiddenServices, null)
            {
                MemberName = nameof(Registration.RequestPickupBranchID)
            });
        Assert.AreEqual(ValidationResult.Success, hiddenValidation);

        var displayedContext = CreateResolver(draft: ActiveDraft(
            new SettingMutation("display_preferred_pickup_location", DraftOperation.Upsert, "true"),
            new SettingMutation("label.RequestPickupBranchID", DraftOperation.Upsert, "Preferred pickup location"),
            new SettingMutation("require.RequestPickupBranchID", DraftOperation.Upsert, "true"))).Resolve("token")!;
        var displayedServices = new ServiceCollection()
            .AddSingleton<ISettingProvider>(displayedContext.Settings)
            .BuildServiceProvider();
        var displayedValidation = new DbConfiguredRequired().GetValidationResult(
            null,
            new ValidationContext(new Registration(displayedContext.Settings), displayedServices, null)
            {
                MemberName = nameof(Registration.RequestPickupBranchID)
            });

        Assert.IsNotNull(displayedValidation);
        StringAssert.Contains(displayedValidation!.ErrorMessage, "Preferred pickup location");
    }

    [TestMethod]
    public void PreviewDriverLicense_RejectsInvalidConfiguredFormatInsteadOfUsingMagstripe()
    {
        var draft = ActiveDraft(new SettingMutation("dl_format", DraftOperation.Upsert, "unsupported"));
        var settings = new PreviewSettingProvider(draft, 3, new TestCache(), 1);
        var accessor = new PreviewRequestContextAccessor
        {
            IsPreviewRequest = true,
            PlaintextToken = "preview-token",
            Current = new PreviewRequestContext(Link("Active"), draft, settings)
        };
        var controller = new PreviewController(
            Mock.Of<ISettingsAdministrationRepository>(), accessor, new TestCache(), Mock.Of<IDbHelper>(),
            Mock.Of<IPapiClient>(), Mock.Of<IMelissaRestClient>(), Mock.Of<IEmailSender>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var result = controller.DriverLicense("preview-token", "$unsupported");

        Assert.IsInstanceOfType<BadRequestObjectResult>(result);
    }

    [TestMethod]
    public void LivePreviewLinkAtNewGeneration_IsRejectedByStaleNodeCache()
    {
        // Instance B creates the link after publishing N+1; instance A has
        // not yet observed that publication.
        var instanceA = new TestCache
        {
            Generation = 1,
            SettingsCache =
            [
                new() { OrganizationID = 3, Setting = "registration_text", Value = "generation N" }
            ]
        };
        var instanceB = new TestCache
        {
            Generation = 2,
            SettingsCache =
            [
                new() { OrganizationID = 3, Setting = "registration_text", Value = "generation N+1" }
            ]
        };
        var draft = ActiveDraft();
        var link = Link("Active", allowLiveSubmission: true) with { LiveSettingsGeneration = instanceB.Generation };
        var repository = new Mock<ISettingsAdministrationRepository>();
        repository.Setup(service => service.ResolvePreviewContext(It.IsAny<byte[]>()))
            .Returns(new PreviewContextSnapshot(link, draft, 2));
        repository.Setup(service => service.GetCacheGeneration()).Returns(2);
        var eligibility = new Mock<IPreviewBranchEligibilityService>();
        eligibility.Setup(service => service.IsEligible(3, 3, 1)).Returns(true);
        var resolver = new PreviewContextResolver(repository.Object, new PreviewTokenService(), eligibility.Object,
            instanceA, Options.Create(new SettingsAdministrationOptions()));

        var context = resolver.Resolve("stale-node-token");

        Assert.AreEqual(2, instanceB.GetSnapshot().Generation);
        Assert.IsNull(context);
    }

    [TestMethod]
    public void LivePreviewAfterSynchronization_UsesNewBaselineAndDraftOverlay()
    {
        var cache = new TestCache
        {
            Generation = 2,
            SettingsCache =
            [
                new() { OrganizationID = 3, Setting = "registration_text", Value = "generation N+1" },
                new() { OrganizationID = 3, Setting = "display_responsible_person_field", Value = "false" }
            ]
        };
        var draft = ActiveDraft(new SettingMutation("registration_text", DraftOperation.Upsert, "draft overlay"));
        var link = Link("Active", allowLiveSubmission: true) with { LiveSettingsGeneration = 2 };
        var repository = new Mock<ISettingsAdministrationRepository>();
        repository.Setup(service => service.ResolvePreviewContext(It.IsAny<byte[]>()))
            .Returns(new PreviewContextSnapshot(link, draft, 2));
        repository.Setup(service => service.GetCacheGeneration()).Returns(2);
        var eligibility = new Mock<IPreviewBranchEligibilityService>();
        eligibility.Setup(service => service.IsEligible(3, 3, 1)).Returns(true);
        var resolver = new PreviewContextResolver(repository.Object, new PreviewTokenService(), eligibility.Object,
            cache, Options.Create(new SettingsAdministrationOptions()));

        var context = resolver.Resolve("synchronized-node-token");

        Assert.IsNotNull(context);
        Assert.AreEqual(2, context!.Settings.SnapshotGeneration);
        Assert.AreEqual("draft overlay", context.Settings.RegistrationText);
        Assert.IsFalse(context.Settings.DisplayResponsiblePersonField);
    }

    [TestMethod]
    public void PreviewCacheRefreshFailure_FailsClosedBeforeRegistrationClients()
    {
        var cache = new Mock<ICache>();
        cache.As<IGenerationAwareCacheSnapshotProvider>()
            .Setup(provider => provider.GetSnapshotAtGeneration(2))
            .Throws(new CacheSnapshotConsistencyException("refresh failed"));
        var draft = ActiveDraft();
        var link = Link("Active", allowLiveSubmission: true) with { LiveSettingsGeneration = 2 };
        var repository = new Mock<ISettingsAdministrationRepository>();
        repository.Setup(service => service.ResolvePreviewContext(It.IsAny<byte[]>()))
            .Returns(new PreviewContextSnapshot(link, draft, 2));
        repository.Setup(service => service.GetCacheGeneration()).Returns(2);
        var eligibility = new Mock<IPreviewBranchEligibilityService>();
        eligibility.Setup(service => service.IsEligible(3, 3, 1)).Returns(true);
        var resolver = new PreviewContextResolver(repository.Object, new PreviewTokenService(), eligibility.Object,
            cache.Object, Options.Create(new SettingsAdministrationOptions()));
        var melissa = new Mock<IMelissaRestClient>();
        var papi = new Mock<IPapiClient>();
        var email = new Mock<IEmailSender>();

        var resolved = resolver.Resolve("refresh-failure-token");
        Assert.IsNull(resolved);
        var controller = new PreviewController(repository.Object,
            new PreviewRequestContextAccessor { IsPreviewRequest = true, Current = resolved },
            cache.Object, Mock.Of<IDbHelper>(), papi.Object, melissa.Object, email.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        var result = controller.Submit("refresh-failure-token", null!);

        Assert.IsInstanceOfType<NotFoundObjectResult>(result);
        repository.Verify(service => service.TryAdmitLivePreviewSubmission(It.IsAny<long>(), It.IsAny<long>()), Times.Never);
        Assert.AreEqual(0, melissa.Invocations.Count);
        Assert.AreEqual(0, papi.Invocations.Count);
        Assert.AreEqual(0, email.Invocations.Count);
    }

    [TestMethod]
    public void LivePreviewProviderGenerationMismatch_IsRejectedBeforeAdmissionOrExternalClients()
    {
        var cache = new TestCache { Generation = 1 };
        var draft = ActiveDraft();
        var link = Link("Active", allowLiveSubmission: true) with { LiveSettingsGeneration = 2 };
        var repository = new Mock<ISettingsAdministrationRepository>();
        var melissa = new Mock<IMelissaRestClient>();
        var papi = new Mock<IPapiClient>();
        var email = new Mock<IEmailSender>();
        var context = new PreviewRequestContext(link, draft, new PreviewSettingProvider(draft, 3, cache, 1));
        var controller = new PreviewController(repository.Object,
            new PreviewRequestContextAccessor { IsPreviewRequest = true, Current = context },
            cache, Mock.Of<IDbHelper>(), papi.Object, melissa.Object, email.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var result = controller.Submit("mismatched-generation-token", new Registration(context.Settings));

        Assert.IsInstanceOfType<NotFoundObjectResult>(result);
        repository.Verify(service => service.TryAdmitLivePreviewSubmission(It.IsAny<long>(), It.IsAny<long>()), Times.Never);
        Assert.AreEqual(0, melissa.Invocations.Count);
        Assert.AreEqual(0, papi.Invocations.Count);
        Assert.AreEqual(0, email.Invocations.Count);
    }

    private static PreviewContextResolver CreateResolver(
        SettingDraft? draft = null,
        PreviewLinkRecord? link = null,
        Mock<IPreviewBranchEligibilityService>? eligibility = null)
    {
        draft ??= ActiveDraft();
        link ??= Link("Active");
        var repository = new Mock<ISettingsAdministrationRepository>();
        repository.Setup(service => service.GetCacheGeneration()).Returns(1);
        var validSnapshot = link.RevokedAtUtc is null &&
            (link.ExpiresAtUtc is not { } expiration || expiration > DateTime.UtcNow);
        repository.Setup(service => service.ResolvePreviewContext(It.IsAny<byte[]>()))
            .Returns(validSnapshot && link.DraftStatus == DraftStatus.Active.ToString() && draft.Status == DraftStatus.Active
                ? new PreviewContextSnapshot(link, draft, 1)
                : null);
        if (eligibility is null)
        {
            eligibility = new Mock<IPreviewBranchEligibilityService>();
            eligibility.Setup(service => service.IsEligible(draft.OrganizationId, link.OperationalBranchId, 1)).Returns(true);
        }
        return new PreviewContextResolver(repository.Object, new PreviewTokenService(), eligibility.Object, new TestCache(), Options.Create(new SettingsAdministrationOptions()));
    }

    private static SettingDraft ActiveDraft(params SettingMutation[] changes) =>
        new(7, 3, string.Empty, 0, DraftStatus.Active, changes);

    private static PreviewLinkRecord Link(
        string status,
        DateTime? revokedAt = null,
        DateTime? expiresAt = null,
        bool allowLiveSubmission = false) =>
        new(9, 7, new byte[32], allowLiveSubmission, revokedAt, expiresAt, 3, string.Empty, status, 3)
        {
            LiveSettingsGeneration = allowLiveSubmission ? 1 : null
        };
}
