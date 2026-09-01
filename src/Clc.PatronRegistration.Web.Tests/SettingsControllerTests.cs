using System.Security.Claims;
using Clc.PatronRegistration.Administration;
using Clc.PatronRegistration.Configuration;
using Clc.PatronRegistration.Helpers;
using Clc.PatronRegistration.Web.Controllers;
using Clc.PatronRegistration.Web.Models;
using Clc.PatronRegistration.Web.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Mvc.Filters;
using System.Reflection;
using Microsoft.Extensions.Options;
using Moq;
using Clc.Polaris.Api.Models;
using Microsoft.Data.SqlClient;
using System.Text;

namespace Clc.PatronRegistration.Tests;

[TestClass]
public class SettingsControllerTests
{
    [TestMethod]
    public void Help_AllowsManagerAndPreservesAuthorizedDefaultFormWithoutRepositoryAccess()
    {
        var repository = new Mock<ISettingsAdministrationRepository>(MockBehavior.Strict);
        repository.Setup(service => service.GetCacheGeneration()).Returns(1);
        var controller = CreateController(repository, LibraryAuthorization());

        var result = (ViewResult)controller.Help(3, null);
        var model = (SettingsHelpViewModel)result.Model!;

        Assert.AreEqual(3, model.OrganizationId);
        Assert.AreEqual(string.Empty, model.FormCode);
        repository.Verify(service => service.GetCacheGeneration(), Times.Never);
        repository.VerifyNoOtherCalls();
    }

    [TestMethod]
    public void Help_DropsUnauthorizedReturnContext()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        var result = (ViewResult)CreateController(repository, LibraryAuthorization()).Help(99, "kids");
        var model = (SettingsHelpViewModel)result.Model!;

        Assert.IsNull(model.OrganizationId);
        Assert.AreEqual(string.Empty, model.FormCode);
        repository.Verify(service => service.GetFormCodes(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [TestMethod]
    public void Help_DropsUnavailableNamedForm()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        repository.Setup(service => service.GetFormCodes(It.IsAny<int>(), It.IsAny<int>())).Returns([]);
        repository.Setup(service => service.GetLegacyFormCodes()).Returns([]);
        var result = (ViewResult)CreateController(repository, LibraryAuthorization()).Help(3, "missing");
        Assert.IsNull(((SettingsHelpViewModel)result.Model!).OrganizationId);
    }

    [TestMethod]
    public void Help_PreservesAuthorizedNamedFormReturnContext()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        repository.Setup(service => service.GetFormCodes(2, 1)).Returns(
        [
            new FormCodeMetadata(2, "kids", "Children's form", null, DateTime.UtcNow, "admin", DateTime.UtcNow, "admin")
        ]);
        repository.Setup(service => service.GetLegacyFormCodes()).Returns([]);

        var result = (ViewResult)CreateController(repository, LibraryAuthorization()).Help(3, "kids");
        var model = (SettingsHelpViewModel)result.Model!;

        Assert.AreEqual(3, model.OrganizationId);
        Assert.AreEqual("kids", model.FormCode);
    }

    [TestMethod]
    public void Help_ForbidsUserWithoutSettingsRole()
    {
        var authorization = new Mock<ISettingsAuthorizationService>();
        authorization.Setup(service => service.Describe(It.IsAny<ClaimsPrincipal>())).Returns(new SettingsPrincipal(false, 2, false));

        Assert.IsInstanceOfType<ForbidResult>(CreateController(new Mock<ISettingsAdministrationRepository>(), authorization).Help(null));
    }

    [TestMethod]
    public void Help_AllowsGlobalAdministratorWithoutResolvingSentinelOrganization()
    {
        var authorization = new Mock<ISettingsAuthorizationService>();
        authorization.Setup(service => service.Describe(It.IsAny<ClaimsPrincipal>())).Returns(new SettingsPrincipal(true, -1, true));
        var cache = new Mock<ICache>(MockBehavior.Strict);
        var result = CreateController(new Mock<ISettingsAdministrationRepository>(), authorization, cache.Object).Help(null);

        Assert.IsInstanceOfType<ViewResult>(result);
        cache.VerifyNoOtherCalls();
    }

    [DataTestMethod]
    [DataRow(999)]
    [DataRow(-1)]
    public void Help_GlobalAdministratorDropsUnknownAndSentinelReturnOrganizations(int organizationId)
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        var result = (ViewResult)CreateController(repository, GlobalAuthorization(), new TestCache()).Help(organizationId, null);

        Assert.IsNull(((SettingsHelpViewModel)result.Model!).OrganizationId);
        repository.Verify(service => service.GetFormCodes(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [DataTestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public void Help_GlobalAdministratorPreservesRealSystemLibraryAndBranchScopes(int organizationId)
    {
        var result = (ViewResult)CreateController(
            new Mock<ISettingsAdministrationRepository>(), GlobalAuthorization(), new TestCache()).Help(organizationId, null);
        var model = (SettingsHelpViewModel)result.Model!;

        Assert.AreEqual(organizationId, model.OrganizationId);
        Assert.AreEqual(string.Empty, model.FormCode);
    }

    [DataTestMethod]
    [DataRow(2, true)]
    [DataRow(3, true)]
    [DataRow(4, false)]
    public void Help_LibraryAdministratorOnlyPreservesOwnLibraryAndBranches(int organizationId, bool isPreserved)
    {
        var organizations = new List<OrganizationsGetRow>
        {
            new() { OrganizationID = 1, OrganizationCodeID = 1, Name = "System" },
            new() { OrganizationID = 2, OrganizationCodeID = 2, Name = "Own library" },
            new() { OrganizationID = 3, OrganizationCodeID = 3, ParentOrganizationID = 2, Name = "Own branch" },
            new() { OrganizationID = 4, OrganizationCodeID = 2, Name = "Other library" }
        };
        var cache = new Mock<ICache>();
        cache.SetupGet(value => value.OrganizationCache).Returns(organizations);
        cache.Setup(value => value.GetOrg(2)).Returns(organizations[1]);
        cache.Setup(value => value.GetBranches(2)).Returns([organizations[2]]);

        var result = (ViewResult)CreateController(
            new Mock<ISettingsAdministrationRepository>(), LibraryAuthorization(), cache.Object).Help(organizationId, null);

        Assert.AreEqual(isPreserved ? organizationId : null, ((SettingsHelpViewModel)result.Model!).OrganizationId);
    }

    [DataTestMethod]
    [DataRow("2")]
    [DataRow("999")]
    [DataRow("Unknown")]
    public void DirectSave_RejectsUnsupportedOperationWithoutCreatingMutation(string operation)
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        var controller = CreateController(repository, LibraryAuthorization());
        var request = new SaveSettingsRequest
        {
            OrganizationId = 3,
            Changes = [new SettingMutationInput { Key = "registration_text", Operation = operation, Value = "value" }]
        };

        var result = controller.DirectSave(request);

        Assert.IsInstanceOfType<RedirectToActionResult>(result);
        Assert.IsTrue(controller.ModelState.ContainsKey("registration_text"));
        StringAssert.Contains(controller.ModelState["registration_text"]!.Errors.Single().ErrorMessage, "Invalid operation");
        repository.Verify(service => service.DirectSave(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<IReadOnlyList<SettingMutation>>(), It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(),
            It.IsAny<AuditContext>()), Times.Never);
    }

    [TestMethod]
    public void GlobalScopeOptions_AreGroupedSortedAndIncludeParentLibraryNames()
    {
        var organizations = new List<OrganizationsGetRow>
        {
            new() { OrganizationID = 1, OrganizationCodeID = 1, Name = "System" },
            new() { OrganizationID = 8, OrganizationCodeID = 2, Name = "Zulu Library" },
            new() { OrganizationID = 2, OrganizationCodeID = 2, Name = "Alpha Library" },
            new() { OrganizationID = 9, OrganizationCodeID = 3, ParentOrganizationID = 8, Name = "Main Branch" },
            new() { OrganizationID = 3, OrganizationCodeID = 3, ParentOrganizationID = 2, Name = "North Branch" }
        };
        var cache = new Mock<ICache>();
        cache.SetupGet(value => value.OrganizationCache).Returns(organizations);
        var authorization = new Mock<ISettingsAuthorizationService>();
        authorization.Setup(service => service.Describe(It.IsAny<ClaimsPrincipal>()))
            .Returns(new SettingsPrincipal(true, -1, true));
        var controller = CreateController(new Mock<ISettingsAdministrationRepository>(), authorization, cache.Object);
        var method = typeof(SettingsController).GetMethod("GetAuthorizedScopes", BindingFlags.NonPublic | BindingFlags.Instance)!;

        var options = (List<ScopeOption>)method.Invoke(controller, [new SettingsPrincipal(true, -1, true)])!;

        CollectionAssert.AreEqual(new[] { ScopeOptionGroup.System, ScopeOptionGroup.Libraries, ScopeOptionGroup.Libraries,
            ScopeOptionGroup.Branches, ScopeOptionGroup.Branches }, options.Select(option => option.Group).ToArray());
        CollectionAssert.AreEqual(new[] { "System defaults", "Alpha Library", "Zulu Library",
            "Alpha Library — North Branch", "Zulu Library — Main Branch" }, options.Select(option => option.DisplayName).ToArray());
    }

    [TestMethod]
    public void LibraryScopeOptions_ExcludeSystemAndOtherLibraries()
    {
        var controller = CreateController(new Mock<ISettingsAdministrationRepository>(), LibraryAuthorization(), new TestCache());
        var method = typeof(SettingsController).GetMethod("GetAuthorizedScopes", BindingFlags.NonPublic | BindingFlags.Instance)!;

        var options = (List<ScopeOption>)method.Invoke(controller, [new SettingsPrincipal(true, 2, false)])!;

        CollectionAssert.AreEqual(new[] { 2, 3 }, options.Select(option => option.OrganizationId).ToArray());
        Assert.IsFalse(options.Any(option => option.Group == ScopeOptionGroup.System));
        StringAssert.StartsWith(options[1].DisplayName, "Library — ");
    }

    [DataTestMethod]
    [DataRow(true, true)]
    [DataRow(false, false)]
    [DataRow(null, false)]
    public void PreviewLinkMode_NormalizesNullableValuesToSafeDefault(bool? value, bool expected)
    {
        Assert.AreEqual(expected, PreviewLinkMode.AllowsLiveSubmission(value));
    }

    [TestMethod]
    public void GlobalSettingsModel_RemovesEffectiveOverrideAndStagedSecretValues()
    {
        const string effectiveSecret = "effective-postmark-secret-value";
        const string stagedSecret = "staged-postmark-secret-value";
        var cache = new TestCache
        {
            SettingsCache =
            [
                new() { OrganizationID = 1, FormCode = string.Empty, Setting = "postmark_api_key", Value = effectiveSecret }
            ]
        };
        var repository = new Mock<ISettingsAdministrationRepository>();
        repository.Setup(service => service.GetFormCodes(It.IsAny<int>(), It.IsAny<int>())).Returns([]);
        repository.Setup(service => service.GetLegacyFormCodes()).Returns([]);
        repository.Setup(service => service.GetActiveDraft(1, string.Empty)).Returns(new SettingDraft(
            42, 1, string.Empty, 0, DraftStatus.Active,
            [new SettingMutation("postmark_api_key", DraftOperation.Upsert, stagedSecret)]));
        var authorization = new Mock<ISettingsAuthorizationService>();
        authorization.Setup(service => service.Describe(It.IsAny<ClaimsPrincipal>()))
            .Returns(new SettingsPrincipal(true, -1, true));
        authorization.Setup(service => service.CanManage(It.IsAny<ClaimsPrincipal>(), 1, It.IsAny<bool>())).Returns(true);
        var controller = CreateController(repository, authorization, cache);

        var result = (ViewResult)controller.Index(1);
        var model = (SettingsIndexViewModel)result.Model!;
        var row = model.Settings.Single(setting => setting.Definition.Key == "postmark_api_key");

        Assert.IsNull(row.Resolution.EffectiveValue);
        Assert.IsNull(row.Resolution.CurrentOverrideValue);
        Assert.IsNull(row.DraftValue);
        Assert.IsNull(model.ActiveDraft!.Changes.Single(change => change.Key == "postmark_api_key").Value);
        Assert.IsTrue(row.Resolution.SourceOrganizationId.HasValue);
    }

    [TestMethod]
    public void ReplacingPreviewMode_ReturnsOneTimeReplacementUrl()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        var metadataResolved = false;
        var draft = NonSensitiveDraft() with { FormCode = "kids" };
        repository.Setup(service => service.GetDraft(draft.DraftId)).Returns(draft);
        repository.Setup(service => service.GetPreviewLink(12)).Returns(PreviewLink(draft));
        repository.Setup(service => service.ReplacePreviewLinkMode(12, It.IsAny<byte[]>(), true,
                It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), false, It.IsAny<AuditContext>()))
            .Callback(() => Assert.IsTrue(metadataResolved, "Display metadata must be resolved before replacement persistence."))
            .Returns(13);
        repository.Setup(service => service.GetFormCodes(2, 1)).Returns(() =>
        {
            metadataResolved = true;
            return
            [
                new FormCodeMetadata(2, "kids", "Library-customized kids registration", null, DateTime.UtcNow, "a", DateTime.UtcNow, "a"),
                new FormCodeMetadata(1, "kids", "System kids registration", null, DateTime.UtcNow, "a", DateTime.UtcNow, "a")
            ];
        });
        repository.Setup(service => service.GetLegacyFormCodes()).Returns([]);
        var controller = CreateController(repository, LibraryAuthorization());
        var url = new Mock<IUrlHelper>();
        url.Setup(helper => helper.Action(It.IsAny<UrlActionContext>())).Returns("https://example.test/preview/replacement");
        controller.Url = url.Object;

        var result = controller.ReplacePreviewLinkMode(12, true);

        var view = (ViewResult)result;
        Assert.AreEqual("PreviewLinkCreated", view.ViewName);
        var model = (PreviewLinkCreatedViewModel)view.Model!;
        Assert.AreEqual("https://example.test/preview/replacement", model.PreviewUrl);
        Assert.AreEqual(draft.DraftId, model.DraftId);
        Assert.AreEqual(draft.OrganizationId, model.OrganizationId);
        Assert.AreEqual(draft.FormCode, model.FormCode);
        Assert.AreEqual("Library-customized kids registration", model.FormDisplayName);
        Assert.AreEqual(3, model.OperationalBranchId);
        Assert.IsTrue(model.AllowLiveSubmission);
        repository.Verify(service => service.ReplacePreviewLinkMode(12,
            It.Is<byte[]>(hash => hash.Length == 32), true,
            It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), false, It.IsAny<AuditContext>()), Times.Once);
    }

    [TestMethod]
    public void RestorePreviewLink_RedirectsWithStatusWithoutCreatingToken()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        repository.Setup(service => service.GetFormCodes(It.IsAny<int>(), It.IsAny<int>())).Returns([]);
        repository.Setup(service => service.GetLegacyFormCodes()).Returns([]);
        var draft = NonSensitiveDraft();
        repository.Setup(service => service.GetDraft(draft.DraftId)).Returns(draft);
        repository.Setup(service => service.GetPreviewLink(12)).Returns(PreviewLink(draft));
        var tokens = new Mock<IPreviewTokenService>();
        var controller = CreateController(repository, LibraryAuthorization(),
            previewTokenService: tokens.Object,
            administrationOptions: new SettingsAdministrationOptions { PreviewLinkLifetimeHours = 37 });

        var result = (RedirectToActionResult)controller.RestorePreviewLink(12);

        Assert.AreEqual(nameof(SettingsController.Index), result.ActionName);
        Assert.AreEqual(3, result.RouteValues!["organizationId"]);
        Assert.AreEqual(string.Empty, result.RouteValues["formCode"]);
        Assert.AreEqual("Preview link #12 was restored for another 37 hours.", controller.TempData["SettingsStatus"]);
        repository.Verify(service => service.RestorePreviewLink(12, 37,
            It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), false, It.IsAny<AuditContext>()), Times.Once);
        tokens.Verify(service => service.Create(), Times.Never);
        Assert.IsNotInstanceOfType<ViewResult>(result);
    }

    [TestMethod]
    public void DeletePreviewLink_RedirectsWithConsistentRemovalStatus()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        repository.Setup(service => service.GetFormCodes(It.IsAny<int>(), It.IsAny<int>())).Returns([]);
        repository.Setup(service => service.GetLegacyFormCodes()).Returns([]);
        var draft = NonSensitiveDraft();
        repository.Setup(service => service.GetDraft(draft.DraftId)).Returns(draft);
        repository.Setup(service => service.GetPreviewLink(12)).Returns(PreviewLink(draft));
        var controller = CreateController(repository, LibraryAuthorization());

        var result = (RedirectToActionResult)controller.DeletePreviewLink(12);

        Assert.AreEqual(nameof(SettingsController.Index), result.ActionName);
        Assert.AreEqual("Preview link #12 was removed.", controller.TempData["SettingsStatus"]);
        repository.Verify(service => service.DeletePreviewLink(12,
            It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), false, It.IsAny<AuditContext>()), Times.Once);
    }

    [DataTestMethod]
    [DataRow(nameof(SettingsController.RestorePreviewLink))]
    [DataRow(nameof(SettingsController.DeletePreviewLink))]
    public void InactivePreviewLinkActions_RequirePostAndAntiforgery(string action)
    {
        var method = typeof(SettingsController).GetMethod(action)!;
        Assert.IsTrue(method.GetCustomAttributes(typeof(HttpPostAttribute), true).Any());
        Assert.IsTrue(method.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), true).Any());
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void InactivePreviewLinkActions_MissingLinkReturnsConflict(bool restore)
    {
        var controller = CreateController(new Mock<ISettingsAdministrationRepository>(), LibraryAuthorization());
        Assert.IsInstanceOfType<ConflictObjectResult>(restore ? controller.RestorePreviewLink(12) : controller.DeletePreviewLink(12));
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void InactivePreviewLinkActions_UnauthorizedScopeIsForbidden(bool restore)
    {
        var repository = PreviewLifecycleRepository(NonSensitiveDraft());
        var authorization = LibraryAuthorization();
        authorization.Setup(service => service.CanManage(It.IsAny<ClaimsPrincipal>(), 3, It.IsAny<bool>())).Returns(false);

        Assert.IsInstanceOfType<ForbidResult>(restore
            ? CreateController(repository, authorization).RestorePreviewLink(12)
            : CreateController(repository, authorization).DeletePreviewLink(12));
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void InactivePreviewLinkActions_RestrictedDraftRequiresGlobalAdministrator(bool restore)
    {
        var restricted = PreviewLifecycleRepository(SensitiveDraft());
        Assert.IsInstanceOfType<ForbidResult>(restore
            ? CreateController(restricted, LibraryAuthorization()).RestorePreviewLink(12)
            : CreateController(restricted, LibraryAuthorization()).DeletePreviewLink(12));

        var global = PreviewLifecycleRepository(SensitiveDraft());
        Assert.IsInstanceOfType<RedirectToActionResult>(restore
            ? CreateController(global, GlobalAuthorization()).RestorePreviewLink(12)
            : CreateController(global, GlobalAuthorization()).DeletePreviewLink(12));
    }

    [DataTestMethod]
    [DataRow(true, false)]
    [DataRow(false, false)]
    [DataRow(true, true)]
    [DataRow(false, true)]
    public void InactivePreviewLinkActions_RepositoryFailuresUseExistingResponses(bool restore, bool unauthorized)
    {
        var repository = PreviewLifecycleRepository(NonSensitiveDraft());
        if (restore)
            repository.Setup(service => service.RestorePreviewLink(12, It.IsAny<int>(), It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), false, It.IsAny<AuditContext>()))
                .Throws(unauthorized ? new UnauthorizedAccessException() : new System.Data.DBConcurrencyException());
        else
            repository.Setup(service => service.DeletePreviewLink(12, It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), false, It.IsAny<AuditContext>()))
                .Throws(unauthorized ? new UnauthorizedAccessException() : new System.Data.DBConcurrencyException());
        var controller = CreateController(repository, LibraryAuthorization());

        var result = restore ? controller.RestorePreviewLink(12) : controller.DeletePreviewLink(12);

        if (unauthorized) Assert.IsInstanceOfType<ForbidResult>(result);
        else AssertDraftConflictRedirect(controller, result, 3, string.Empty);
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void InactivePreviewLinkActions_DeadlockUsesContextualDraftConflictRedirect(bool restore)
    {
        var repository = PreviewLifecycleRepository(NonSensitiveDraft());
        if (restore)
            repository.Setup(service => service.RestorePreviewLink(12, It.IsAny<int>(), It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), false, It.IsAny<AuditContext>()))
                .Throws(SqlExceptionWithNumber(1205));
        else
            repository.Setup(service => service.DeletePreviewLink(12, It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), false, It.IsAny<AuditContext>()))
                .Throws(SqlExceptionWithNumber(1205));
        var controller = CreateController(repository, LibraryAuthorization());

        AssertDraftConflictRedirect(controller,
            restore ? controller.RestorePreviewLink(12) : controller.DeletePreviewLink(12), 3, string.Empty);
    }

    [TestMethod]
    public void SettingsController_UsesNoStoreResponsePolicy()
    {
        var controller = CreateController(new Mock<ISettingsAdministrationRepository>(), LibraryAuthorization());
        var responseCache = typeof(SettingsController).GetCustomAttribute<ResponseCacheAttribute>();

        Assert.IsNotNull(responseCache);
        Assert.IsTrue(responseCache.NoStore);
        Assert.AreEqual(ResponseCacheLocation.None, responseCache.Location);
        controller.OnActionExecuting(new ActionExecutingContext(
            controller.ControllerContext,
            [],
            new Dictionary<string, object?>(),
            controller));
        Assert.AreEqual("no-store, no-cache, max-age=0", controller.Response.Headers.CacheControl.ToString());
        Assert.AreEqual("no-cache", controller.Response.Headers.Pragma.ToString());
        Assert.AreEqual("no-referrer", controller.Response.Headers["Referrer-Policy"].ToString());
    }

    [DataTestMethod]
    [DataRow(1, "named-form")]
    [DataRow(3, "")]
    public void SettingsController_BrandingUsesAuthenticatedOrganizationNotEditingTarget(int selectedOrganizationId, string selectedFormCode)
    {
        var branding = new SettingsPageBrandingContextAccessor();
        var controller = CreateController(new Mock<ISettingsAdministrationRepository>(), LibraryAuthorization(), brandingAccessor: branding);
        controller.ControllerContext.HttpContext.Request.QueryString =
            new QueryString($"?organizationId={selectedOrganizationId}&formCode={selectedFormCode}");

        controller.OnActionExecuting(new ActionExecutingContext(
            controller.ControllerContext, [], new Dictionary<string, object?>(), controller));

        Assert.AreEqual(new SettingsPageBrandingContext(2, 2), branding.Current);
    }

    [DataTestMethod]
    [DataRow(2, "")]
    [DataRow(3, "kids")]
    public void GlobalAdministrator_UsesSystemBrandingWithoutSentinelCacheOrganization(int editingOrganizationId, string editingFormCode)
    {
        var organizations = new List<OrganizationsGetRow>
        {
            new() { OrganizationID = 1, OrganizationCodeID = 1, Name = "System" },
            new() { OrganizationID = 2, OrganizationCodeID = 2, Name = "Library" },
            new() { OrganizationID = 3, OrganizationCodeID = 3, ParentOrganizationID = 2, Name = "Selected branch" }
        };
        var cache = new Mock<ICache>();
        cache.SetupGet(value => value.OrganizationCache).Returns(organizations);
        cache.SetupGet(value => value.SettingsCache).Returns([]);
        var authorization = new Mock<ISettingsAuthorizationService>();
        authorization.Setup(service => service.Describe(It.IsAny<ClaimsPrincipal>()))
            .Returns(new SettingsPrincipal(true, -1, true));
        var branding = new SettingsPageBrandingContextAccessor();
        var controller = CreateController(new Mock<ISettingsAdministrationRepository>(), authorization, cache.Object, branding);

        var context = new ActionExecutingContext(
            controller.ControllerContext, [], new Dictionary<string, object?>
            {
                ["organizationId"] = editingOrganizationId,
                ["formCode"] = editingFormCode
            }, controller);
        controller.OnActionExecuting(context);

        Assert.IsNull(context.Result);
        Assert.AreEqual(new SettingsPageBrandingContext(1, 1), branding.Current);
        Assert.IsFalse(organizations.Any(organization => organization.OrganizationID == -1));
    }

    [TestMethod]
    public void MissingOrganizationClaim_DoesNotCreateSystemBrandingFallback()
    {
        var authorization = new Mock<ISettingsAuthorizationService>();
        authorization.Setup(service => service.Describe(It.IsAny<ClaimsPrincipal>()))
            .Returns(new SettingsPrincipal(true, null, false));
        var branding = new SettingsPageBrandingContextAccessor();
        var controller = CreateController(new Mock<ISettingsAdministrationRepository>(), authorization, brandingAccessor: branding);

        controller.OnActionExecuting(new ActionExecutingContext(
            controller.ControllerContext, [], new Dictionary<string, object?> { ["organizationId"] = 1 }, controller));

        Assert.IsNull(branding.Current);
    }

    [TestMethod]
    public void BranchAdministrator_UsesBranchAndResolvedLibraryForBranding()
    {
        var authorization = new Mock<ISettingsAuthorizationService>();
        authorization.Setup(service => service.Describe(It.IsAny<ClaimsPrincipal>()))
            .Returns(new SettingsPrincipal(true, 3, false));
        var branding = new SettingsPageBrandingContextAccessor();
        var controller = CreateController(new Mock<ISettingsAdministrationRepository>(), authorization, brandingAccessor: branding);

        controller.OnActionExecuting(new ActionExecutingContext(
            controller.ControllerContext, [], new Dictionary<string, object?> { ["organizationId"] = 1 }, controller));

        Assert.AreEqual(new SettingsPageBrandingContext(3, 2), branding.Current);
    }

    [TestMethod]
    public void InvalidNonGlobalOrganization_IsForbiddenWithoutBranding()
    {
        var authorization = new Mock<ISettingsAuthorizationService>();
        authorization.Setup(service => service.Describe(It.IsAny<ClaimsPrincipal>()))
            .Returns(new SettingsPrincipal(true, 999, false));
        var branding = new SettingsPageBrandingContextAccessor();
        var controller = CreateController(new Mock<ISettingsAdministrationRepository>(), authorization, brandingAccessor: branding);
        var context = new ActionExecutingContext(
            controller.ControllerContext, [], new Dictionary<string, object?>(), controller);

        controller.OnActionExecuting(context);

        Assert.IsInstanceOfType<ForbidResult>(context.Result);
        Assert.IsNull(branding.Current);
    }

    [TestMethod]
    public void PrincipalWithoutSettingsRole_DoesNotEstablishBranding()
    {
        var authorization = new Mock<ISettingsAuthorizationService>();
        authorization.Setup(service => service.Describe(It.IsAny<ClaimsPrincipal>()))
            .Returns(new SettingsPrincipal(false, 2, false));
        var branding = new SettingsPageBrandingContextAccessor();
        var controller = CreateController(new Mock<ISettingsAdministrationRepository>(), authorization, brandingAccessor: branding);

        controller.OnActionExecuting(new ActionExecutingContext(
            controller.ControllerContext, [], new Dictionary<string, object?>(), controller));

        Assert.IsNull(branding.Current);
    }

    [TestMethod]
    public void DirectAndDraftSave_RejectTheSameMarkupLabel()
    {
        const string maliciousLabel = "<img src=x onerror=alert(1)>";
        var repository = new Mock<ISettingsAdministrationRepository>();
        repository.Setup(service => service.GetDraft(24)).Returns(NonSensitiveDraft());
        var controller = CreateController(repository, LibraryAuthorization());
        var direct = new SaveSettingsRequest
        {
            OrganizationId = 3,
            Changes = [new SettingMutationInput { Key = "label.NameFirst", Value = maliciousLabel }]
        };
        var draft = new SaveToSharedDraftRequest
        {
            OrganizationId = 3,
            Changes = [new SettingMutationInput { Key = "label.NameFirst", Value = maliciousLabel }]
        };

        Assert.IsInstanceOfType<RedirectToActionResult>(controller.DirectSave(direct));
        controller.ModelState.Clear();
        Assert.IsInstanceOfType<RedirectToActionResult>(controller.SaveToSharedDraft(draft));
        repository.Verify(service => service.DirectSave(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<IReadOnlyList<SettingMutation>>(),
            It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), It.IsAny<AuditContext>()), Times.Never);
        repository.Verify(service => service.SaveToSharedDraft(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long?>(), It.IsAny<IReadOnlyList<SettingMutation>>(),
            It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), It.IsAny<AuditContext>()), Times.Never);
    }

    [TestMethod]
    public void BackendOnlySchoolInfoFormat_IsRejectedByDirectAndDraftMutationPaths()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        var controller = CreateController(repository, LibraryAuthorization());
        var direct = new SaveSettingsRequest
        {
            OrganizationId = 3,
            Changes = [new SettingMutationInput { Key = "school_info_format", Value = "uapl" }]
        };
        var draft = new SaveToSharedDraftRequest
        {
            OrganizationId = 3,
            Changes = [new SettingMutationInput { Key = "school_info_format", Value = "uapl" }]
        };

        Assert.IsInstanceOfType<RedirectToActionResult>(controller.DirectSave(direct));
        controller.ModelState.Clear();
        Assert.IsInstanceOfType<RedirectToActionResult>(controller.SaveToSharedDraft(draft));

        repository.Verify(service => service.DirectSave(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<IReadOnlyList<SettingMutation>>(),
            It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), It.IsAny<AuditContext>()), Times.Never);
        repository.Verify(service => service.SaveToSharedDraft(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long?>(), It.IsAny<IReadOnlyList<SettingMutation>>(),
            It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), It.IsAny<AuditContext>()), Times.Never);
    }

    [TestMethod]
    public void LibraryAdministrator_DirectAndDraftSavesSanitizeHtmlExecutionSettingsBeforePersistence()
    {
        const string malicious = "<p><strong>Keep this formatting</strong></p><script>alert(1)</script>" +
            "<img src=\"https://example.test/logo.png\" onerror=\"alert(2)\"><a href=\"javascript:alert(3)\">bad</a>";
        IReadOnlyList<SettingMutation>? directChanges = null;
        IReadOnlyList<SettingMutation>? draftChanges = null;
        var repository = new Mock<ISettingsAdministrationRepository>();
        repository.Setup(service => service.DirectSave(
                3, string.Empty, It.IsAny<long>(), It.IsAny<IReadOnlyList<SettingMutation>>(),
                It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), It.IsAny<AuditContext>()))
            .Callback<int, string, long, IReadOnlyList<SettingMutation>, IReadOnlyDictionary<string, SettingDefinition>, AuditContext>(
                (_, _, _, changes, _, _) => directChanges = changes);
        repository.Setup(service => service.SaveToSharedDraft(
                3, string.Empty, It.IsAny<long>(), null, It.IsAny<IReadOnlyList<SettingMutation>>(),
                It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), It.IsAny<AuditContext>()))
            .Callback<int, string, long, long?, IReadOnlyList<SettingMutation>, IReadOnlyDictionary<string, SettingDefinition>, AuditContext>(
                (_, _, _, _, changes, _, _) => draftChanges = changes)
            .Returns(new SaveToDraftResult(25, true));
        var controller = CreateController(repository, LibraryAuthorization());

        var directResult = controller.DirectSave(new SaveSettingsRequest
        {
            OrganizationId = 3,
            Changes = [new SettingMutationInput { Key = "registration_form_header", Value = malicious }]
        });
        controller.ModelState.Clear();
        var draftResult = controller.SaveToSharedDraft(new SaveToSharedDraftRequest
        {
            OrganizationId = 3,
            Changes = [new SettingMutationInput { Key = "registration_form_header", Value = malicious }]
        });

        Assert.IsInstanceOfType<RedirectToActionResult>(directResult);
        Assert.IsInstanceOfType<RedirectToActionResult>(draftResult);
        Assert.IsNotNull(directChanges);
        Assert.IsNotNull(draftChanges);
        foreach (var changes in new[] { directChanges!, draftChanges! })
        {
            var value = changes.Single().Value!;
            StringAssert.Contains(value, "<strong>Keep this formatting</strong>");
            Assert.IsFalse(value.Contains("<script", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(value.Contains("onerror", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(value.Contains("javascript:", StringComparison.OrdinalIgnoreCase));
        }
    }

    [TestMethod]
    public void SaveToSharedDraft_ConcurrentRepositoryChangeUsesFriendlyRecovery()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        repository.Setup(service => service.SaveToSharedDraft(3, string.Empty, It.IsAny<long>(), null,
                It.IsAny<IReadOnlyList<SettingMutation>>(), It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), It.IsAny<AuditContext>()))
            .Throws(new System.Data.DBConcurrencyException("The form is changing."));
        var controller = CreateController(repository, LibraryAuthorization());

        var result = controller.SaveToSharedDraft(new SaveToSharedDraftRequest
        {
            OrganizationId = 3,
            Changes = [new SettingMutationInput { Key = "registration_text", Value = "new" }]
        });

        AssertDraftConflictRedirect(controller, result, 3, string.Empty);
    }

    [DataTestMethod]
    [DataRow("force_ecard_remotely")]
    [DataRow("require.User5")]
    public void DirectSave_WithOnlyLateOrDynamicMutation_PersistsExactlyThatMutation(string key)
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        var authorization = new Mock<ISettingsAuthorizationService>();
        authorization.Setup(service => service.Describe(It.IsAny<ClaimsPrincipal>()))
            .Returns(new SettingsPrincipal(true, 2, false));
        authorization.Setup(service => service.CanManage(It.IsAny<ClaimsPrincipal>(), 3, It.IsAny<bool>()))
            .Returns(true);
        IReadOnlyList<SettingMutation>? captured = null;
        repository.Setup(service => service.DirectSave(
                3,
                string.Empty,
                7,
                It.IsAny<IReadOnlyList<SettingMutation>>(),
                It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(),
                It.IsAny<AuditContext>()))
            .Callback<int, string, long, IReadOnlyList<SettingMutation>, IReadOnlyDictionary<string, SettingDefinition>, AuditContext>(
                (_, _, _, changes, _, _) => captured = changes);
        var controller = CreateController(repository, authorization);
        var request = new SaveSettingsRequest
        {
            OrganizationId = 3,
            ExpectedVersion = 7,
            Changes =
            [
                new SettingMutationInput { Key = key, Operation = "Upsert", Value = "true" }
            ]
        };

        var result = controller.DirectSave(request);

        Assert.IsInstanceOfType<RedirectToActionResult>(result);
        Assert.IsNotNull(captured);
        Assert.AreEqual(1, captured.Count);
        Assert.AreEqual(key, captured[0].Key);
    }

    [TestMethod]
    public void DirectSave_RejectsScopeTampering()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        var authorization = new Mock<ISettingsAuthorizationService>();
        authorization.Setup(service => service.Describe(It.IsAny<ClaimsPrincipal>()))
            .Returns(new SettingsPrincipal(true, 2, false));
        authorization.Setup(service => service.CanManage(It.IsAny<ClaimsPrincipal>(), 99, It.IsAny<bool>()))
            .Returns(false);
        var controller = CreateController(repository, authorization);

        var result = controller.DirectSave(new SaveSettingsRequest { OrganizationId = 99 });

        Assert.IsInstanceOfType<ForbidResult>(result);
        repository.Verify(service => service.DirectSave(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<IReadOnlyList<SettingMutation>>(),
            It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), It.IsAny<AuditContext>()), Times.Never);
    }

    [TestMethod]
    public void DirectSave_ActionRequiresAntiforgery()
    {
        var method = typeof(SettingsController).GetMethod(nameof(SettingsController.DirectSave));

        Assert.IsNotNull(method);
        Assert.IsTrue(method.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), true).Any());
    }

    [TestMethod]
    public void DirectSave_AcceptsAnImageAssetAuthorizedForTheTargetScope()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        IReadOnlyList<SettingMutation>? captured = null;
        repository.Setup(service => service.DirectSave(
                3, string.Empty, 7, It.IsAny<IReadOnlyList<SettingMutation>>(),
                It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), It.IsAny<AuditContext>()))
            .Callback<int, string, long, IReadOnlyList<SettingMutation>, IReadOnlyDictionary<string, SettingDefinition>, AuditContext>(
                (_, _, _, changes, _, _) => captured = changes);
        var assetAuthorization = new Mock<IRegistrationFormAssetAuthorization>();
        assetAuthorization.Setup(service => service.GetAuthorizedMetadata(42, 3, string.Empty))
            .Returns(new RegistrationFormAssetMetadata(42, "header.png", "image/png", "hash", DateTime.UtcNow, DateTime.UtcNow, 3, string.Empty));
        var controller = CreateController(repository, LibraryAuthorization(), suppliedAssetAuthorization: assetAuthorization.Object);

        var result = controller.DirectSave(new SaveSettingsRequest
        {
            OrganizationId = 3,
            ExpectedVersion = 7,
            Changes = [new SettingMutationInput { Key = "header_image_asset_id", Value = "42" }]
        });

        Assert.IsInstanceOfType<RedirectToActionResult>(result);
        Assert.IsTrue(controller.ModelState.IsValid,
            string.Join("; ", controller.ModelState.Values.SelectMany(value => value.Errors).Select(error => error.ErrorMessage)));
        Assert.IsNotNull(captured);
        Assert.AreEqual("42", captured.Single().Value);
        assetAuthorization.Verify(service => service.GetAuthorizedMetadata(42, 3, string.Empty), Times.Once);
    }

    [TestMethod]
    public void DirectSave_RemainsSuccessfulWhenPostCommitCacheRefreshFails()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        var mutationCount = 0;
        repository.Setup(service => service.DirectSave(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<IReadOnlyList<SettingMutation>>(),
                It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), It.IsAny<AuditContext>()))
            .Callback<int, string, long, IReadOnlyList<SettingMutation>, IReadOnlyDictionary<string, SettingDefinition>, AuditContext>(
                (_, _, _, _, _, _) => mutationCount++);
        var cache = new Mock<ICache>();
        cache.Setup(service => service.RebuildCache()).Throws(new InvalidOperationException("simulated refresh failure"));
        var invalidator = new SettingsCacheInvalidator(cache.Object, repository.Object);
        var controller = CreateController(repository, LibraryAuthorization(), suppliedCacheInvalidator: invalidator);

        var result = controller.DirectSave(new SaveSettingsRequest
        {
            OrganizationId = 3,
            ExpectedVersion = 1,
            Changes = [new SettingMutationInput { Key = "label.NameFirst", Value = "First name" }]
        });

        Assert.IsInstanceOfType<RedirectToActionResult>(result);
        Assert.AreEqual(1, mutationCount);
        cache.Verify(service => service.RebuildCache(), Times.Once);
    }

    [TestMethod]
    public void DirectSave_RepositoryFailureStillReturnsConflictWithoutRefreshingCache()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        repository.Setup(service => service.DirectSave(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<IReadOnlyList<SettingMutation>>(),
                It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), It.IsAny<AuditContext>()))
            .Throws(new System.Data.DBConcurrencyException("The settings changed."));
        var invalidator = new Mock<ISettingsCacheInvalidator>(MockBehavior.Strict);
        var controller = CreateController(repository, LibraryAuthorization(), suppliedCacheInvalidator: invalidator.Object);

        var result = controller.DirectSave(new SaveSettingsRequest
        {
            OrganizationId = 3,
            ExpectedVersion = 1,
            Changes = [new SettingMutationInput { Key = "label.NameFirst", Value = "First name" }]
        });

        Assert.IsInstanceOfType<ConflictObjectResult>(result);
        invalidator.Verify(service => service.LiveSettingsChanged(It.IsAny<string?>()), Times.Never);
    }

    [TestMethod]
    public void DirectSave_RejectsMissingImageAssetWithoutCallingRepository()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        var assetAuthorization = new Mock<IRegistrationFormAssetAuthorization>();
        assetAuthorization.Setup(service => service.GetAuthorizedMetadata(999, 3, string.Empty))
            .Returns((RegistrationFormAssetMetadata?)null);
        var controller = CreateController(repository, LibraryAuthorization(), suppliedAssetAuthorization: assetAuthorization.Object);

        var result = controller.DirectSave(new SaveSettingsRequest
        {
            OrganizationId = 3,
            ExpectedVersion = 7,
            Changes = [new SettingMutationInput { Key = "header_image_asset_id", Value = "999" }]
        });

        Assert.IsInstanceOfType<RedirectToActionResult>(result);
        repository.Verify(service => service.DirectSave(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<IReadOnlyList<SettingMutation>>(),
            It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), It.IsAny<AuditContext>()), Times.Never);
    }

    [TestMethod]
    public void DirectSave_RejectsUnpublishedUpstreamImageAssetWithoutCallingRepository()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        var assetAuthorization = new Mock<IRegistrationFormAssetAuthorization>();
        assetAuthorization.Setup(service => service.GetAuthorizedMetadata(42, 3, string.Empty))
            .Returns((RegistrationFormAssetMetadata?)null);
        var controller = CreateController(repository, LibraryAuthorization(), suppliedAssetAuthorization: assetAuthorization.Object);

        var result = controller.DirectSave(new SaveSettingsRequest
        {
            OrganizationId = 3,
            ExpectedVersion = 7,
            Changes = [new SettingMutationInput { Key = "header_image_asset_id", Value = "42" }]
        });

        Assert.IsInstanceOfType<RedirectToActionResult>(result);
        repository.Verify(service => service.DirectSave(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<IReadOnlyList<SettingMutation>>(),
            It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), It.IsAny<AuditContext>()), Times.Never);
    }

    [TestMethod]
    public void SaveToSharedDraft_RejectsMissingImageAssetWithoutCallingRepository()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        var assetAuthorization = new Mock<IRegistrationFormAssetAuthorization>();
        assetAuthorization.Setup(service => service.GetAuthorizedMetadata(999, 3, string.Empty))
            .Returns((RegistrationFormAssetMetadata?)null);
        var controller = CreateController(repository, LibraryAuthorization(), suppliedAssetAuthorization: assetAuthorization.Object);

        var result = controller.SaveToSharedDraft(new SaveToSharedDraftRequest
        {
            OrganizationId = 3,
            ExpectedVersion = 7,
            Changes = [new SettingMutationInput { Key = "header_image_asset_id", Value = "999" }]
        });

        Assert.IsInstanceOfType<RedirectToActionResult>(result);
        repository.Verify(service => service.SaveToSharedDraft(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long?>(),
            It.IsAny<IReadOnlyList<SettingMutation>>(), It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(),
            It.IsAny<AuditContext>()), Times.Never);
    }

    [TestMethod]
    public void SaveToSharedDraft_RejectsUnpublishedUpstreamImageAssetWithoutCallingRepository()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        var assetAuthorization = new Mock<IRegistrationFormAssetAuthorization>();
        assetAuthorization.Setup(service => service.GetAuthorizedMetadata(42, 3, string.Empty))
            .Returns((RegistrationFormAssetMetadata?)null);
        var controller = CreateController(repository, LibraryAuthorization(), suppliedAssetAuthorization: assetAuthorization.Object);

        var result = controller.SaveToSharedDraft(new SaveToSharedDraftRequest
        {
            OrganizationId = 3,
            ExpectedVersion = 7,
            Changes = [new SettingMutationInput { Key = "header_image_asset_id", Value = "42" }]
        });

        Assert.IsInstanceOfType<RedirectToActionResult>(result);
        repository.Verify(service => service.SaveToSharedDraft(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long?>(),
            It.IsAny<IReadOnlyList<SettingMutation>>(), It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(),
            It.IsAny<AuditContext>()), Times.Never);
    }

    [TestMethod]
    public void DirectSave_RejectsCraftedSystemUploadIdBeforePersistence()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        var assets = new Mock<IRegistrationFormAssetRepository>();
        assets.Setup(service => service.GetMetadata(42)).Returns(new RegistrationFormAssetMetadata(
            42, "system.png", "image/png", "system-hash", DateTime.UtcNow, DateTime.UtcNow, 1, string.Empty));
        var assetAuthorization = new RegistrationFormAssetAuthorization(
            assets.Object, new TestCache(), Options.Create(new SettingsAdministrationOptions { SystemOrganizationId = 1 }));
        var controller = CreateController(repository, LibraryAuthorization(), suppliedAssets: assets,
            suppliedAssetAuthorization: assetAuthorization);

        var result = controller.DirectSave(new SaveSettingsRequest
        {
            OrganizationId = 3,
            ExpectedVersion = 7,
            Changes = [new SettingMutationInput { Key = "header_image_asset_id", Value = "42" }]
        });

        Assert.IsInstanceOfType<RedirectToActionResult>(result);
        repository.Verify(service => service.DirectSave(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<IReadOnlyList<SettingMutation>>(),
            It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), It.IsAny<AuditContext>()), Times.Never);
    }

    [TestMethod]
    public void SaveToSharedDraft_RejectsCraftedSystemUploadIdBeforePersistence()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        var assets = new Mock<IRegistrationFormAssetRepository>();
        assets.Setup(service => service.GetMetadata(42)).Returns(new RegistrationFormAssetMetadata(
            42, "system.png", "image/png", "system-hash", DateTime.UtcNow, DateTime.UtcNow, 1, string.Empty));
        var assetAuthorization = new RegistrationFormAssetAuthorization(
            assets.Object, new TestCache(), Options.Create(new SettingsAdministrationOptions { SystemOrganizationId = 1 }));
        var controller = CreateController(repository, LibraryAuthorization(), suppliedAssets: assets,
            suppliedAssetAuthorization: assetAuthorization);

        var result = controller.SaveToSharedDraft(new SaveToSharedDraftRequest
        {
            OrganizationId = 3,
            ExpectedVersion = 7,
            Changes = [new SettingMutationInput { Key = "header_image_asset_id", Value = "42" }]
        });

        Assert.IsInstanceOfType<RedirectToActionResult>(result);
        repository.Verify(service => service.SaveToSharedDraft(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long?>(),
            It.IsAny<IReadOnlyList<SettingMutation>>(), It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(),
            It.IsAny<AuditContext>()), Times.Never);
    }

    [DataTestMethod]
    [DataRow(false, 2, false, "postmark_api_key")]
    [DataRow(false, 2, false, "melissa_data_api_key")]
    [DataRow(true, -1, true, "postmark_api_key")]
    [DataRow(true, -1, true, "melissa_data_api_key")]
    public void Audit_SearchSensitivityMatchesAdministratorScope(bool global, int organizationId, bool includeSensitive, string search)
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        repository.Setup(service => service.SearchAudit(It.IsAny<int?>(), includeSensitive, search))
            .Returns([]);
        var authorization = new Mock<ISettingsAuthorizationService>();
        authorization.Setup(service => service.Describe(It.IsAny<ClaimsPrincipal>()))
            .Returns(new SettingsPrincipal(true, organizationId, global));
        var controller = CreateController(repository, authorization);

        var result = controller.Audit(search);

        Assert.IsInstanceOfType<ViewResult>(result);
        repository.Verify(service => service.SearchAudit(global ? null : 2, includeSensitive, search), Times.Once);
        repository.Verify(service => service.GetFormCodesForLibraries(
            It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<int>()), Times.Never);
    }

    [TestMethod]
    public void Audit_SearchResultPresentsTheRawIdentifierThatMatched()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        repository.Setup(service => service.SearchAudit(2, false, "DirectSave")).Returns(
        [
            new SettingsAuditRow(1, DateTime.UtcNow, "DirectSave", 3, 2, "kids", "registration_text",
                null, null, false, true, "Admin", null, "hidden-correlation", "127.0.0.1")
        ]);
        repository.Setup(service => service.GetFormCodesForLibraries(It.IsAny<IReadOnlyCollection<int>>(), 1))
            .Returns([]);

        var result = (ViewResult)CreateController(repository, LibraryAuthorization()).Audit("DirectSave");
        var entry = ((SettingsAuditViewModel)result.Model!).Events.Single();
        var details = entry.TechnicalDetails.ToDictionary(detail => detail.Label, detail => detail.Value);

        Assert.AreEqual("DirectSave", details["Raw event type"]);
        Assert.AreEqual("registration_text", details["Raw setting key"]);
        Assert.AreEqual("kids", details["Raw form code"]);
        Assert.IsFalse(details.ContainsKey("Correlation ID"));
        Assert.IsFalse(details.ContainsKey("IP address"));
    }

    [TestMethod]
    public void Audit_ResolvesSameFormCodeWithinEachTargetLibraryAndPrefersLocalMetadata()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        repository.Setup(service => service.SearchAudit(null, true, null)).Returns(
        [
            new SettingsAuditRow(1, DateTime.UtcNow, "DirectSave", 3, 2, "kids", null, null, null,
                false, true, "Admin", null, null, null),
            new SettingsAuditRow(2, DateTime.UtcNow, "DirectSave", 5, 4, "kids", null, null, null,
                false, true, "Admin", null, null, null),
            new SettingsAuditRow(3, DateTime.UtcNow, "DirectSave", 7, 6, "kids", null, null, null,
                false, true, "Admin", null, null, null)
        ]);
        repository.Setup(service => service.GetFormCodesForLibraries(
            It.Is<IReadOnlyCollection<int>>(ids => ids.OrderBy(id => id).SequenceEqual(new[] { 2, 4, 6 })), 1)).Returns(
        [
            FormMetadata(2, "kids", "Library two children"),
            FormMetadata(4, "kids", "Library four youth"),
            FormMetadata(1, "kids", "Inherited children")
        ]);

        var result = (ViewResult)CreateController(repository, GlobalAuthorization()).Audit(null);
        var model = (SettingsAuditViewModel)result.Model!;

        Assert.AreEqual("Library two children", model.Events.Single(entry => entry.AuditEventId == 1).Form);
        Assert.AreEqual("Library four youth", model.Events.Single(entry => entry.AuditEventId == 2).Form);
        Assert.AreEqual("Inherited children", model.Events.Single(entry => entry.AuditEventId == 3).Form);
        repository.Verify(service => service.GetFormCodesForLibraries(
            It.IsAny<IReadOnlyCollection<int>>(), 1), Times.Once);
        repository.Verify(service => service.GetFormCodes(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    private static FormCodeMetadata FormMetadata(int organizationId, string code, string displayName) =>
        new(organizationId, code, displayName, null, DateTime.UtcNow, "Admin", DateTime.UtcNow, "Admin");

    [DataTestMethod]
    [DataRow(1, null)]
    [DataRow(1, 999)]
    [DataRow(2, null)]
    [DataRow(2, 999)]
    public void PreviewCreation_RejectsMissingOrUnauthorizedOperationalBranch(int scopeOrganizationId, int? operationalBranchId)
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        repository.Setup(service => service.GetDraft(10))
            .Returns(new SettingDraft(10, scopeOrganizationId, string.Empty, 0, DraftStatus.Active, []));
        var authorization = new Mock<ISettingsAuthorizationService>();
        authorization.Setup(service => service.Describe(It.IsAny<ClaimsPrincipal>()))
            .Returns(new SettingsPrincipal(true, -1, true));
        authorization.Setup(service => service.CanManage(It.IsAny<ClaimsPrincipal>(), scopeOrganizationId, It.IsAny<bool>()))
            .Returns(true);
        var controller = CreateController(repository, authorization);

        var result = controller.CreatePreviewLink(10, new PreviewLinkRequest
        {
            OrganizationId = scopeOrganizationId,
            OperationalBranchId = operationalBranchId
        });

        Assert.IsInstanceOfType<RedirectToActionResult>(result);
        repository.Verify(service => service.CreatePreviewLink(
            It.IsAny<long>(), It.IsAny<byte[]>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), It.IsAny<bool>(), It.IsAny<AuditContext>(), It.IsAny<long?>()), Times.Never);
    }

    [TestMethod]
    public void SuccessfulDraftCreation_ReportsFirstChanges()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        repository.Setup(service => service.SaveToSharedDraft(3, string.Empty, It.IsAny<long>(), null,
            It.IsAny<IReadOnlyList<SettingMutation>>(), It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), It.IsAny<AuditContext>()))
            .Returns(new SaveToDraftResult(14, true));
        var controller = CreateController(repository, LibraryAuthorization());

        var result = controller.SaveToSharedDraft(new SaveToSharedDraftRequest
        {
            OrganizationId = 3, Changes = [new SettingMutationInput { Key = "registration_text", Value = "new" }]
        });

        Assert.IsInstanceOfType<RedirectToActionResult>(result);
        Assert.AreEqual("Shared draft #14 was created with 1 change.", controller.TempData["SettingsStatus"]);
    }

    [DataTestMethod]
    [DataRow(1, "1 change was added to shared draft #24.")]
    [DataRow(2, "2 changes were added to shared draft #24.")]
    public void SaveToSharedDraft_ExistingDraftReportsCorrectCount(int count, string message)
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        repository.Setup(service => service.SaveToSharedDraft(3, string.Empty, It.IsAny<long>(), 24,
                It.IsAny<IReadOnlyList<SettingMutation>>(), It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), It.IsAny<AuditContext>()))
            .Returns(new SaveToDraftResult(24, false));
        var controller = CreateController(repository, LibraryAuthorization());
        var changes = Enumerable.Range(0, count).Select(index => new SettingMutationInput
        {
            Key = index == 0 ? "registration_text" : "warning_text",
            Value = $"value {index}"
        }).ToList();

        var result = controller.SaveToSharedDraft(new SaveToSharedDraftRequest
            { OrganizationId = 3, ExpectedDraftId = 24, Changes = changes });

        Assert.IsInstanceOfType<RedirectToActionResult>(result);
        Assert.AreEqual(message, controller.TempData["SettingsStatus"]);
    }

    [TestMethod]
    public void SaveToSharedDraft_ForwardsExpectedDraftIdAndNull()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        repository.Setup(service => service.SaveToSharedDraft(3, string.Empty, It.IsAny<long>(), It.IsAny<long?>(),
                It.IsAny<IReadOnlyList<SettingMutation>>(), It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), It.IsAny<AuditContext>()))
            .Returns((int _, string _, long expectedVersion, long? expected, IReadOnlyList<SettingMutation> _, IReadOnlyDictionary<string, SettingDefinition> _, AuditContext _) =>
                new SaveToDraftResult(expected ?? 25, !expected.HasValue));
        var controller = CreateController(repository, LibraryAuthorization());
        SaveToSharedDraftRequest Request(long? expected) => new()
        {
            OrganizationId = 3, ExpectedVersion = 37, ExpectedDraftId = expected,
            Changes = [new SettingMutationInput { Key = "registration_text", Value = "value" }]
        };

        controller.SaveToSharedDraft(Request(24));
        controller.ModelState.Clear();
        controller.SaveToSharedDraft(Request(null));

        repository.Verify(service => service.SaveToSharedDraft(3, string.Empty, 37, 24,
            It.IsAny<IReadOnlyList<SettingMutation>>(), It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), It.IsAny<AuditContext>()), Times.Once);
        repository.Verify(service => service.SaveToSharedDraft(3, string.Empty, 37, null,
            It.IsAny<IReadOnlyList<SettingMutation>>(), It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), It.IsAny<AuditContext>()), Times.Once);
    }

    [TestMethod]
    public void SaveToSharedDraft_ForwardsExpectedDraftRevisionWhenEditingSharedDraft()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        repository.Setup(service => service.SaveToSharedDraft(3, string.Empty, 37, 24,
                It.IsAny<IReadOnlyList<SettingMutation>>(), It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(),
                It.IsAny<AuditContext>(), 9))
            .Returns(new SaveToDraftResult(24, false) { DraftRevision = 10 });
        var controller = CreateController(repository, LibraryAuthorization());

        var result = controller.SaveToSharedDraft(new SaveToSharedDraftRequest
        {
            OrganizationId = 3,
            ExpectedVersion = 37,
            ExpectedDraftId = 24,
            ExpectedDraftRevision = 9,
            Changes = [new SettingMutationInput { Key = "registration_text", Value = "value" }]
        });

        Assert.IsInstanceOfType<RedirectToActionResult>(result);
        repository.Verify(service => service.SaveToSharedDraft(3, string.Empty, 37, 24,
            It.IsAny<IReadOnlyList<SettingMutation>>(), It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(),
            It.IsAny<AuditContext>(), 9), Times.Once);
    }

    [TestMethod]
    public async Task UploadHeaderImageAsset_StoresAssetWithoutMutatingTheDraft()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        var assets = new Mock<IRegistrationFormAssetRepository>();
        assets.Setup(service => service.Create("header.png", "image/png", It.IsAny<byte[]>(), 3, string.Empty))
            .Returns(new RegistrationFormAsset(91, "header.png", "image/png", [1], "hash", DateTime.UtcNow, DateTime.UtcNow));
        var controller = CreateController(repository, LibraryAuthorization(), suppliedAssets: assets);
        var content = TestImageData.Create("image/png");
        var file = new FormFile(new MemoryStream(content), 0, content.Length, "file", "header.png")
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/png"
        };

        var result = await controller.UploadHeaderImageAsset(file, 3, string.Empty);

        Assert.IsInstanceOfType<OkObjectResult>(result);
        assets.Verify(service => service.Create("header.png", "image/png", It.Is<byte[]>(bytes => bytes.SequenceEqual(content)), 3, string.Empty), Times.Once);
        repository.Verify(service => service.SaveToSharedDraft(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long?>(),
            It.IsAny<IReadOnlyList<SettingMutation>>(), It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), It.IsAny<AuditContext>()), Times.Never);
    }

    [TestMethod]
    public async Task UploadHeaderImageAsset_RejectsUnauthorizedUploadBeforeAssetStorage()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        var authorization = LibraryAuthorization();
        authorization.Setup(service => service.CanManage(It.IsAny<ClaimsPrincipal>(), 3, It.IsAny<bool>())).Returns(false);
        var assets = new Mock<IRegistrationFormAssetRepository>();
        var controller = CreateController(repository, authorization, suppliedAssets: assets);
        var content = TestImageData.Create("image/png");
        var file = new FormFile(new MemoryStream(content), 0, content.Length, "file", "header.png")
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/png"
        };

        var result = await controller.UploadHeaderImageAsset(file, 3, string.Empty);

        Assert.IsInstanceOfType<ForbidResult>(result);
        assets.Verify(service => service.Create(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<string>()), Times.Never);
        repository.Verify(service => service.SaveToSharedDraft(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long?>(),
            It.IsAny<IReadOnlyList<SettingMutation>>(), It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), It.IsAny<AuditContext>()), Times.Never);
    }

    [TestMethod]
    public async Task UploadHeaderImageAsset_RejectsSignatureMismatchWithoutAssetStorage()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        var assets = new Mock<IRegistrationFormAssetRepository>();
        var controller = CreateController(repository, LibraryAuthorization(), suppliedAssets: assets);
        var file = new FormFile(new MemoryStream(Encoding.UTF8.GetBytes("<svg/>")), 0, 6, "file", "header.png")
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/png"
        };

        var result = await controller.UploadHeaderImageAsset(file, 3, string.Empty);

        Assert.IsInstanceOfType<BadRequestObjectResult>(result);
        assets.Verify(service => service.Create(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<string>()), Times.Never);
    }

    [TestMethod]
    public async Task UploadHeaderImageAsset_LeavesFullImageValidationToRepository()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        var assets = new Mock<IRegistrationFormAssetRepository>();
        assets.Setup(service => service.Create("header.png", "image/png", It.IsAny<byte[]>(), 3, string.Empty))
            .Throws(new ArgumentException("The uploaded file is not a complete, valid image.", "content"));
        var controller = CreateController(repository, LibraryAuthorization(), suppliedAssets: assets);
        var content = new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a };
        var file = new FormFile(new MemoryStream(content), 0, content.Length, "file", "header.png")
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/png"
        };

        var result = await controller.UploadHeaderImageAsset(file, 3, string.Empty);

        Assert.IsInstanceOfType<BadRequestObjectResult>(result);
        assets.Verify(service => service.Create("header.png", "image/png",
            It.Is<byte[]>(bytes => bytes.SequenceEqual(content)), 3, string.Empty), Times.Once);
    }

    [TestMethod]
    public void Index_MarksMissingHeaderAssetWithoutRewritingTheSetting()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        repository.Setup(service => service.GetActiveDraft(3, string.Empty)).Returns((SettingDraft?)null);
        repository.Setup(service => service.GetVersion(3, string.Empty)).Returns(4);
        repository.Setup(service => service.GetFormCodes(2, 1)).Returns([]);
        repository.Setup(service => service.GetLegacyFormCodes()).Returns([]);
        var assets = new Mock<IRegistrationFormAssetRepository>();
        assets.Setup(service => service.GetMetadata(987)).Returns((RegistrationFormAssetMetadata?)null);
        var cache = new TestCache
        {
            SettingsCache =
            [
                new() { OrganizationID = 1, FormCode = string.Empty, Setting = "header_image_asset_id", Value = "987" }
            ]
        };

        var result = (ViewResult)CreateController(repository, LibraryAuthorization(), cache, suppliedAssets: assets).Index(3);
        var model = (SettingsIndexViewModel)result.Model!;
        var row = model.Settings.Single(setting => setting.Definition.Key == "header_image_asset_id");

        Assert.IsTrue(row.EffectiveAssetMissing);
        Assert.IsNull(row.EffectiveAsset);
        repository.Verify(service => service.DirectSave(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<IReadOnlyList<SettingMutation>>(), It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), It.IsAny<AuditContext>()), Times.Never);
    }

    [TestMethod]
    public void Index_InheritedRowsExposeEffectiveValueAsTheInheritanceChoice()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        repository.Setup(service => service.GetVersion(3, string.Empty)).Returns(4);
        repository.Setup(service => service.GetFormCodes(It.IsAny<int>(), It.IsAny<int>())).Returns([]);
        repository.Setup(service => service.GetLegacyFormCodes()).Returns([]);
        var cache = new TestCache
        {
            SettingsCache =
            [
                new() { OrganizationID = 1, FormCode = string.Empty, Setting = "registration_text", Value = "System text" }
            ]
        };

        var result = (ViewResult)CreateController(repository, LibraryAuthorization(), cache).Index(3);
        var row = ((SettingsIndexViewModel)result.Model!).Settings.Single(setting => setting.Definition.Key == "registration_text");

        Assert.IsFalse(row.Resolution.OwnsOverride);
        Assert.IsTrue(row.HasInheritedValue);
        Assert.AreEqual("System text", row.InheritedValue);
        Assert.AreEqual("System defaults", row.InheritedSourceDescription);
    }

    [TestMethod]
    public void Index_HeaderImageRemoveOverridePresentsValidInheritedAsset()
    {
        var repository = ImagePresentationRepository(new SettingDraft(5, 3, string.Empty, 0, DraftStatus.Active,
            [new SettingMutation("header_image_asset_id", DraftOperation.RemoveOverride, null)]));
        var assets = new Mock<IRegistrationFormAssetAuthorization>();
        assets.Setup(service => service.GetAuthorizedMetadata(11, 3, string.Empty)).Returns(ImageMetadata(11, "branch.png"));
        assets.Setup(service => service.GetAuthorizedMetadata(10, 3, string.Empty)).Returns(ImageMetadata(10, "system.png"));
        var cache = ImagePresentationCache(includeInherited: true);

        var result = (ViewResult)CreateController(repository, LibraryAuthorization(), cache,
            suppliedAssetAuthorization: assets.Object).Index(3);
        var row = ((SettingsIndexViewModel)result.Model!).Settings.Single(setting => setting.Definition.Key == "header_image_asset_id");

        Assert.AreEqual(DraftOperation.RemoveOverride, row.DraftOperation);
        Assert.IsTrue(row.HasInheritedValue);
        Assert.IsFalse(row.InheritedAssetMissing);
        Assert.AreEqual("system.png", row.InheritedAsset!.FileName);
        Assert.AreEqual("system.png", row.StagedAsset!.FileName);
    }

    [TestMethod]
    public void Index_HeaderImageRemoveOverridePresentsNoImageWhenNoInheritedSettingExists()
    {
        var repository = ImagePresentationRepository(new SettingDraft(5, 3, string.Empty, 0, DraftStatus.Active,
            [new SettingMutation("header_image_asset_id", DraftOperation.RemoveOverride, null)]));
        var assets = new Mock<IRegistrationFormAssetAuthorization>();
        assets.Setup(service => service.GetAuthorizedMetadata(11, 3, string.Empty)).Returns(ImageMetadata(11, "branch.png"));
        var cache = ImagePresentationCache(includeInherited: false);

        var result = (ViewResult)CreateController(repository, LibraryAuthorization(), cache,
            suppliedAssetAuthorization: assets.Object).Index(3);
        var row = ((SettingsIndexViewModel)result.Model!).Settings.Single(setting => setting.Definition.Key == "header_image_asset_id");

        Assert.IsFalse(row.HasInheritedValue);
        Assert.IsFalse(row.InheritedAssetMissing);
        Assert.IsNull(row.InheritedAsset);
        Assert.IsNull(row.StagedAsset);
    }

    [TestMethod]
    public void Index_HeaderImageRemoveOverrideReportsMissingInheritedAsset()
    {
        var repository = ImagePresentationRepository(new SettingDraft(5, 3, string.Empty, 0, DraftStatus.Active,
            [new SettingMutation("header_image_asset_id", DraftOperation.RemoveOverride, null)]));
        var assets = new Mock<IRegistrationFormAssetAuthorization>();
        assets.Setup(service => service.GetAuthorizedMetadata(11, 3, string.Empty)).Returns(ImageMetadata(11, "branch.png"));
        assets.Setup(service => service.GetAuthorizedMetadata(10, 3, string.Empty))
            .Returns((RegistrationFormAssetMetadata?)null);
        var cache = ImagePresentationCache(includeInherited: true);

        var result = (ViewResult)CreateController(repository, LibraryAuthorization(), cache,
            suppliedAssetAuthorization: assets.Object).Index(3);
        var row = ((SettingsIndexViewModel)result.Model!).Settings.Single(setting => setting.Definition.Key == "header_image_asset_id");

        Assert.IsTrue(row.HasInheritedValue);
        Assert.IsTrue(row.InheritedAssetMissing);
        Assert.IsTrue(row.StagedAssetMissing);
        Assert.IsNull(row.InheritedAsset);
        Assert.IsNull(row.StagedAsset);
    }

    [TestMethod]
    public void UploadHeaderImageAsset_RequiresPostAndAntiforgeryAndDoesNotAcceptDraftParameters()
    {
        var method = typeof(SettingsController).GetMethod(nameof(SettingsController.UploadHeaderImageAsset))!;
        Assert.IsTrue(method.GetCustomAttributes(typeof(HttpPostAttribute), true).Any());
        Assert.IsTrue(method.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), true).Any());
        var sizeLimit = method.GetCustomAttributes(typeof(RequestSizeLimitAttribute), true)
            .Cast<RequestSizeLimitAttribute>().Single();
        Assert.AreEqual(2_200_000, ((Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata)sizeLimit).MaxRequestBodySize);
        Assert.AreEqual(2_200_000, method.GetCustomAttributes(typeof(RequestFormLimitsAttribute), true)
            .Cast<RequestFormLimitsAttribute>().Single().MultipartBodyLengthLimit);
        CollectionAssert.DoesNotContain(method.GetParameters().Select(parameter => parameter.Name).ToArray(), "expectedVersion");
        CollectionAssert.DoesNotContain(method.GetParameters().Select(parameter => parameter.Name).ToArray(), "expectedDraftId");
    }

    [TestMethod]
    public void SettingsForm_PostsRenderedExpectedVersion()
    {
        var view = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../src/Clc.PatronRegistration.Web/Views/Settings/Index.cshtml")));
        StringAssert.Contains(view, "name=\"ExpectedVersion\" value=\"@Model.ScopeVersion\"");
        Assert.AreEqual(4, view.Split("name=\"ExpectedDraftRevision\" value=\"@Model.ActiveDraft.Revision\"").Length - 1);
        var previewFormStart = view.IndexOf("<form asp-action=\"CreatePreviewLink\"", StringComparison.Ordinal);
        var previewFormEnd = view.IndexOf("</form>", previewFormStart, StringComparison.Ordinal);
        Assert.IsTrue(previewFormStart >= 0 && previewFormEnd > previewFormStart);
        StringAssert.Contains(view.Substring(previewFormStart, previewFormEnd - previewFormStart),
            "name=\"ExpectedDraftRevision\" value=\"@Model.ActiveDraft.Revision\"");
    }

    [TestMethod]
    public void DraftLifecycle_ForwardsExpectedRevisionToRepository()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        var draft = NonSensitiveDraft() with { Revision = 7 };
        repository.Setup(service => service.GetDraft(draft.DraftId)).Returns(draft);
        var controller = CreateController(repository, LibraryAuthorization());

        Assert.IsInstanceOfType<RedirectToActionResult>(controller.CommitDraft(
            draft.DraftId, 3, expectedDraftRevision: draft.Revision));
        Assert.IsInstanceOfType<RedirectToActionResult>(controller.DiscardDraft(
            draft.DraftId, 3, expectedDraftRevision: draft.Revision));

        repository.Verify(service => service.CommitDraft(draft.DraftId,
            It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), false, It.IsAny<AuditContext>(), 7), Times.Once);
        repository.Verify(service => service.DiscardDraft(draft.DraftId,
            It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), false, It.IsAny<AuditContext>(), 7), Times.Once);
    }

    [TestMethod]
    public void DraftLifecycle_MissingExpectedRevisionUsesDraftConflictUx()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        var draft = NonSensitiveDraft() with { Revision = 7 };
        repository.Setup(service => service.GetDraft(draft.DraftId)).Returns(draft);
        repository.Setup(service => service.CommitDraft(draft.DraftId,
                It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), false, It.IsAny<AuditContext>(), null))
            .Throws(new System.Data.DBConcurrencyException("revision required"));
        var controller = CreateController(repository, LibraryAuthorization());

        var result = controller.CommitDraft(draft.DraftId, 3);

        AssertDraftConflictRedirect(controller, result, 3, string.Empty);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PreviewCreation_ForwardsConfiguredLifetime(bool allowLiveSubmission)
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        repository.Setup(service => service.GetDraft(10)).Returns(new SettingDraft(10, 3, string.Empty, 0, DraftStatus.Active, []));
        int suppliedLifetime = 0;
        repository.Setup(service => service.CreatePreviewLink(10, It.IsAny<byte[]>(), allowLiveSubmission, 3,
                It.IsAny<int>(), It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), true, It.IsAny<AuditContext>(), 0))
            .Callback((long _, byte[] _, bool _, int _, int lifetime,
                IReadOnlyDictionary<string, SettingDefinition> _, bool _, AuditContext _, long? _) =>
            { suppliedLifetime = lifetime; });
        var controller = CreateController(repository, GlobalAuthorization(), administrationOptions: new SettingsAdministrationOptions { PreviewLinkLifetimeHours = 37 });
        var url = new Mock<IUrlHelper>();
        url.Setup(helper => helper.Action(It.IsAny<UrlActionContext>())).Returns("https://example.test/preview/token");
        controller.Url = url.Object;

        Assert.IsInstanceOfType<ViewResult>(controller.CreatePreviewLink(10,
            new PreviewLinkRequest { OrganizationId = 3, OperationalBranchId = 3, AllowLiveSubmission = allowLiveSubmission, ExpectedDraftRevision = 0 }));
        Assert.AreEqual(37, suppliedLifetime);
    }

    [TestMethod]
    public void PreviewCreation_ForwardsExpectedDraftRevisionToRepository()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        repository.Setup(service => service.GetDraft(10))
            .Returns(new SettingDraft(10, 3, string.Empty, 0, DraftStatus.Active, []) { Revision = 9 });
        long? suppliedRevision = null;
        repository.Setup(service => service.CreatePreviewLink(10, It.IsAny<byte[]>(), false, 3,
                It.IsAny<int>(), It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), true, It.IsAny<AuditContext>(), 9))
            .Callback((long _, byte[] _, bool _, int _, int _, IReadOnlyDictionary<string, SettingDefinition> _,
                bool _, AuditContext _, long? expectedRevision) => suppliedRevision = expectedRevision);
        var controller = CreateController(repository, GlobalAuthorization());
        var url = new Mock<IUrlHelper>();
        url.Setup(helper => helper.Action(It.IsAny<UrlActionContext>())).Returns("https://example.test/preview/token");
        controller.Url = url.Object;

        Assert.IsInstanceOfType<ViewResult>(controller.CreatePreviewLink(10, new PreviewLinkRequest
        {
            OrganizationId = 3, OperationalBranchId = 3, ExpectedDraftRevision = 9
        }));

        Assert.AreEqual(9, suppliedRevision);
    }

    [TestMethod]
    public void PreviewCreation_MissingExpectedDraftRevisionUsesDraftConflictUx()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        repository.Setup(service => service.GetDraft(10))
            .Returns(new SettingDraft(10, 3, string.Empty, 0, DraftStatus.Active, []) { Revision = 9 });
        repository.Setup(service => service.CreatePreviewLink(10, It.IsAny<byte[]>(), false, 3,
                It.IsAny<int>(), It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), true, It.IsAny<AuditContext>(), null))
            .Throws(new System.Data.DBConcurrencyException("revision required"));
        var controller = CreateController(repository, GlobalAuthorization());

        var result = controller.CreatePreviewLink(10, new PreviewLinkRequest
        {
            OrganizationId = 3, OperationalBranchId = 3
        });

        AssertDraftConflictRedirect(controller, result, 3, string.Empty);
    }

    [TestMethod]
    public void SaveToSharedDraft_EmptyChangesUsesValidationRecoveryWithoutRepositoryCall()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        var controller = CreateController(repository, LibraryAuthorization());

        var result = controller.SaveToSharedDraft(new SaveToSharedDraftRequest { OrganizationId = 3, FormCode = "" });

        Assert.IsInstanceOfType<RedirectToActionResult>(result);
        var redirect = (RedirectToActionResult)result;
        Assert.AreEqual(nameof(SettingsController.Index), redirect.ActionName);
        Assert.AreEqual(3, redirect.RouteValues!["organizationId"]);
        StringAssert.Contains((string)controller.TempData["SettingsError"]!, "Submit at least one setting change.");
        repository.Verify(service => service.SaveToSharedDraft(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long?>(),
            It.IsAny<IReadOnlyList<SettingMutation>>(), It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), It.IsAny<AuditContext>()), Times.Never);
    }

    [TestMethod]
    public void SaveToSharedDraft_UnauthorizedSensitiveMutationUsesSafeValidationRecovery()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        var controller = CreateController(repository, LibraryAuthorization());

        var result = controller.SaveToSharedDraft(new SaveToSharedDraftRequest
        {
            OrganizationId = 3,
            Changes = [new SettingMutationInput { Key = "postmark_api_key", Value = "secret" }]
        });

        Assert.IsInstanceOfType<RedirectToActionResult>(result);
        StringAssert.Contains((string)controller.TempData["SettingsError"]!, "unrecognized or inaccessible");
        repository.Verify(service => service.WriteAudit("ValidationFailed", false, It.IsAny<AuditContext>(),
            "Shared draft changes were invalid.", null, null, null), Times.Once);
        repository.Verify(service => service.SaveToSharedDraft(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long?>(),
            It.IsAny<IReadOnlyList<SettingMutation>>(), It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), It.IsAny<AuditContext>()), Times.Never);
    }

    [TestMethod]
    public void PreviewCreation_ReturnsContextualStronglyTypedModel()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        var metadataResolved = false;
        repository.Setup(service => service.GetDraft(10))
            .Returns(new SettingDraft(10, 3, "kids", 0, DraftStatus.Active, []));
        repository.Setup(service => service.GetFormCodes(2, 1)).Returns(() =>
        {
            metadataResolved = true;
            return
            [
                new FormCodeMetadata(2, "kids", "Library-customized kids registration", null, DateTime.UtcNow, "a", DateTime.UtcNow, "a"),
                new FormCodeMetadata(1, "kids", "System kids registration", null, DateTime.UtcNow, "a", DateTime.UtcNow, "a")
            ];
        });
        repository.Setup(service => service.GetLegacyFormCodes()).Returns([]);
        repository.Setup(service => service.CreatePreviewLink(10, It.IsAny<byte[]>(), false, 3,
                It.IsAny<int>(), It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), true, It.IsAny<AuditContext>(), 0))
            .Callback(() => Assert.IsTrue(metadataResolved, "Display metadata must be resolved before preview persistence."));
        var controller = CreateController(repository, GlobalAuthorization());
        var url = new Mock<IUrlHelper>();
        url.Setup(helper => helper.Action(It.IsAny<UrlActionContext>())).Returns("https://example.test/preview/plaintext");
        controller.Url = url.Object;

        var view = (ViewResult)controller.CreatePreviewLink(10, new PreviewLinkRequest
        {
            OrganizationId = 3, FormCode = "kids", OperationalBranchId = 3, AllowLiveSubmission = false, ExpectedDraftRevision = 0
        });

        var model = (PreviewLinkCreatedViewModel)view.Model!;
        Assert.AreEqual("https://example.test/preview/plaintext", model.PreviewUrl);
        Assert.AreEqual(10, model.DraftId);
        Assert.AreEqual("Library-customized kids registration", model.FormDisplayName);
        Assert.AreEqual("Branch", model.OperationalBranchDisplayName);
        Assert.IsFalse(model.AllowLiveSubmission);
    }

    [TestMethod]
    public void PreviewTokenDisplayResponse_DisablesCachingAndReferrers()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        repository.Setup(service => service.GetDraft(10))
            .Returns(new SettingDraft(10, 3, string.Empty, 0, DraftStatus.Active, []));
        var authorization = new Mock<ISettingsAuthorizationService>();
        authorization.Setup(service => service.Describe(It.IsAny<ClaimsPrincipal>()))
            .Returns(new SettingsPrincipal(true, -1, true));
        authorization.Setup(service => service.CanManage(It.IsAny<ClaimsPrincipal>(), 3, It.IsAny<bool>())).Returns(true);
        var controller = CreateController(repository, authorization);
        var url = new Mock<IUrlHelper>();
        url.Setup(helper => helper.Action(It.IsAny<UrlActionContext>())).Returns("https://example.test/preview/one-time-token");
        controller.Url = url.Object;

        var result = controller.CreatePreviewLink(10,
            new PreviewLinkRequest { OrganizationId = 3, OperationalBranchId = 3, ExpectedDraftRevision = 0 });

        Assert.IsInstanceOfType<ViewResult>(result);
        Assert.AreEqual("no-store, no-cache, max-age=0", controller.Response.Headers.CacheControl.ToString());
        Assert.AreEqual("no-cache", controller.Response.Headers.Pragma.ToString());
        Assert.AreEqual("no-referrer", controller.Response.Headers["Referrer-Policy"].ToString());
        Assert.IsNotNull(typeof(SettingsController).GetMethod(nameof(SettingsController.CreatePreviewLink))!
            .GetCustomAttributes(typeof(ResponseCacheAttribute), true)
            .Cast<ResponseCacheAttribute>()
            .Single(attribute => attribute.NoStore && attribute.Location == ResponseCacheLocation.None));
    }

    [DataTestMethod]
    [DataRow(1, true)]
    [DataRow(2, false)]
    public void EditForm_UpdatesSystemOrLibraryMetadataWithoutChangingCode(int ownerOrganizationId, bool global)
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        var authorization = new Mock<ISettingsAuthorizationService>();
        authorization.Setup(service => service.Describe(It.IsAny<ClaimsPrincipal>()))
            .Returns(new SettingsPrincipal(true, global ? -1 : 2, global));
        authorization.Setup(service => service.CanManage(It.IsAny<ClaimsPrincipal>(), ownerOrganizationId, It.IsAny<bool>()))
            .Returns(true);
        var controller = CreateController(repository, authorization);
        var request = new FormCodeRequest
        {
            OrganizationId = ownerOrganizationId,
            FormCode = "kids",
            DisplayName = "Updated kids form",
            Description = "Updated description",
            ExpectedModifiedAtUtc = new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc)
        };

        var result = controller.EditForm("kids", request);

        Assert.IsInstanceOfType<RedirectToActionResult>(result);
        repository.Verify(service => service.SaveFormCode(
            It.Is<FormCodeMetadata>(metadata =>
                metadata.OrganizationId == ownerOrganizationId &&
                metadata.FormCode == "kids" &&
                metadata.DisplayName == "Updated kids form"),
            false,
            It.IsAny<AuditContext>(),
            It.IsAny<DateTime?>()), Times.Once);
    }

    [TestMethod]
    public void EditForm_StaleMetadataTokenUsesExistingSettingsConflictRecovery()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        repository.Setup(service => service.SaveFormCode(
                It.IsAny<FormCodeMetadata>(), false, It.IsAny<AuditContext>(), It.IsAny<DateTime?>()))
            .Throws(new System.Data.DBConcurrencyException("stale metadata"));
        var authorization = GlobalAuthorization();
        var controller = CreateController(repository, authorization);

        var result = controller.EditForm("kids", new FormCodeRequest
        {
            OrganizationId = 1,
            FormCode = "kids",
            DisplayName = "Updated",
            ExpectedModifiedAtUtc = new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc)
        });

        Assert.IsInstanceOfType<RedirectToActionResult>(result);
        Assert.AreEqual(nameof(SettingsController.Index), ((RedirectToActionResult)result).ActionName);
        StringAssert.Contains(controller.TempData["SettingsError"]?.ToString(), "shared draft changed");
    }

    [TestMethod]
    public void CustomizeForm_UpdatesAnExistingLocalCustomization()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        repository.Setup(service => service.GetFormCodes(2, 1)).Returns(
        [
            new FormCodeMetadata(1, "kids", "System name", null, DateTime.UtcNow, "a", DateTime.UtcNow, "a"),
            new FormCodeMetadata(2, "kids", "Local name", "Local description", DateTime.UtcNow, "a", new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc), "a")
        ]);
        var authorization = new Mock<ISettingsAuthorizationService>();
        authorization.Setup(service => service.Describe(It.IsAny<ClaimsPrincipal>()))
            .Returns(new SettingsPrincipal(true, 2, false));
        authorization.Setup(service => service.CanManage(It.IsAny<ClaimsPrincipal>(), 2, It.IsAny<bool>())).Returns(true);
        var controller = CreateController(repository, authorization);

        var result = controller.CustomizeForm("kids", new FormCodeRequest
        {
            OrganizationId = 2,
            FormCode = "kids",
            DisplayName = "Changed local name",
            Description = "Changed locally",
            ExpectedModifiedAtUtc = new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc)
        });

        Assert.IsInstanceOfType<RedirectToActionResult>(result);
        repository.Verify(service => service.SaveFormCode(
            It.Is<FormCodeMetadata>(metadata => metadata.DisplayName == "Changed local name"),
            false,
            It.IsAny<AuditContext>(),
            It.IsAny<DateTime?>()), Times.Once);
    }

    [TestMethod]
    public void Index_CombinesMetadataAndSettingsOnlyLegacyCodesWithoutDuplicates()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        repository.Setup(service => service.GetFormCodes(2, 1)).Returns(
        [
            new FormCodeMetadata(1, "registered", "Registered", null, DateTime.UtcNow, "a", DateTime.UtcNow, "a")
        ]);
        repository.Setup(service => service.GetLegacyFormCodes()).Returns(
        [
            new LegacyFormCodeRow(1, "kiosk"),
            new LegacyFormCodeRow(2, "kids"),
            new LegacyFormCodeRow(3, "kids"),
            new LegacyFormCodeRow(3, "registered"),
            new LegacyFormCodeRow(9, "other-library")
        ]);
        var authorization = new Mock<ISettingsAuthorizationService>();
        authorization.Setup(service => service.Describe(It.IsAny<ClaimsPrincipal>()))
            .Returns(new SettingsPrincipal(true, 2, false));
        authorization.Setup(service => service.CanManage(It.IsAny<ClaimsPrincipal>(), 2, It.IsAny<bool>())).Returns(true);
        var controller = CreateController(repository, authorization);

        var result = controller.Index(2, string.Empty) as ViewResult;
        var model = result?.Model as SettingsIndexViewModel;

        Assert.IsNotNull(model);
        CollectionAssert.AreEquivalent(new[] { string.Empty, "registered", "kiosk", "kids" }, model.FormCodes.Select(form => form.FormCode).ToList());
        Assert.AreEqual(1, model.FormCodes.Count(form => form.FormCode == "kids"));
        Assert.IsFalse(model.FormCodes.Single(form => form.FormCode == "kiosk").IsRegistered);
    }

    [TestMethod]
    public void CreateForm_AdoptsLegacyCodeAsMetadataWithoutCopyingSettings()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        var authorization = new Mock<ISettingsAuthorizationService>();
        authorization.Setup(service => service.Describe(It.IsAny<ClaimsPrincipal>()))
            .Returns(new SettingsPrincipal(true, 2, false));
        authorization.Setup(service => service.CanManage(It.IsAny<ClaimsPrincipal>(), 2, It.IsAny<bool>())).Returns(true);
        var controller = CreateController(repository, authorization);

        var result = controller.CreateForm(new FormCodeRequest
        {
            OrganizationId = 2,
            FormCode = "kiosk",
            DisplayName = "Kiosk"
        });

        Assert.IsInstanceOfType<RedirectToActionResult>(result);
        repository.Verify(service => service.SaveFormCode(
            It.Is<FormCodeMetadata>(metadata => metadata.OrganizationId == 2 && metadata.FormCode == "kiosk"),
            true,
            It.IsAny<AuditContext>(),
            It.IsAny<DateTime?>()), Times.Once);
        repository.Verify(service => service.DirectSave(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<IReadOnlyList<SettingMutation>>(),
            It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), It.IsAny<AuditContext>()), Times.Never);
    }

    [TestMethod]
    public void ConfirmDeleteForm_NonexistentOrUnownedCodeReturnsNotFound()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        repository.Setup(service => service.GetFormCodeDeletionSnapshot(1, "library-only", 1, It.IsAny<IReadOnlyCollection<int>>()))
            .Returns((FormCodeDeletionSnapshot?)null);
        var authorization = new Mock<ISettingsAuthorizationService>();
        authorization.Setup(service => service.Describe(It.IsAny<ClaimsPrincipal>()))
            .Returns(new SettingsPrincipal(true, -1, true));
        var controller = CreateController(repository, authorization);

        var result = controller.ConfirmDeleteForm("library-only", 1);

        Assert.IsInstanceOfType<NotFoundObjectResult>(result);
    }

    [TestMethod]
    public void DeleteForm_OwnershipChangingAfterConfirmationReturnsConflict()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        repository.Setup(service => service.DeleteFormCode(
                It.IsAny<FormCodeDeletionTarget>(), It.IsAny<string>(), 1, It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<AuditContext>()))
            .Throws(new System.Data.DBConcurrencyException("ownership changed"));
        var authorization = new Mock<ISettingsAuthorizationService>();
        authorization.Setup(service => service.Describe(It.IsAny<ClaimsPrincipal>()))
            .Returns(new SettingsPrincipal(true, -1, true));
        var controller = CreateController(repository, authorization);

        var result = controller.DeleteForm("shared", 1, FormCodeDeletionKind.SystemDefinition, false, "fingerprint");

        Assert.IsInstanceOfType<ConflictObjectResult>(result);
    }

    [TestMethod]
    public void DeleteLibraryForm_TargetsOnlyOwningLibraryAndBranches()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        var authorization = new Mock<ISettingsAuthorizationService>();
        authorization.Setup(service => service.Describe(It.IsAny<ClaimsPrincipal>()))
            .Returns(new SettingsPrincipal(true, 2, false));
        authorization.Setup(service => service.CanManage(It.IsAny<ClaimsPrincipal>(), 2, It.IsAny<bool>())).Returns(true);
        var controller = CreateController(repository, authorization);

        var result = controller.DeleteForm("shared", 2, FormCodeDeletionKind.LibraryDefinition, false, "fingerprint");

        Assert.IsInstanceOfType<RedirectToActionResult>(result);
        repository.Verify(service => service.DeleteFormCode(
            It.Is<FormCodeDeletionTarget>(target => target.OwnerOrganizationId == 2 && target.FormCode == "shared"),
            "fingerprint",
            1,
            It.Is<IReadOnlyCollection<int>>(organizations => organizations.Contains(2) && organizations.Contains(3) && !organizations.Contains(1) && !organizations.Contains(9)),
            It.IsAny<AuditContext>()), Times.Once);
    }

    [TestMethod]
    public void DeleteGenuineSystemForm_TargetsAllSystemLibraryAndBranchScopes()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        var authorization = new Mock<ISettingsAuthorizationService>();
        authorization.Setup(service => service.Describe(It.IsAny<ClaimsPrincipal>()))
            .Returns(new SettingsPrincipal(true, -1, true));
        var controller = CreateController(repository, authorization);

        var result = controller.DeleteForm("shared", 1, FormCodeDeletionKind.SystemDefinition, false, "fingerprint");

        Assert.IsInstanceOfType<RedirectToActionResult>(result);
        repository.Verify(service => service.DeleteFormCode(
            It.Is<FormCodeDeletionTarget>(target => target.Kind == FormCodeDeletionKind.SystemDefinition),
            "fingerprint",
            1,
            It.Is<IReadOnlyCollection<int>>(organizations => organizations.Contains(1) && organizations.Contains(2) && organizations.Contains(3)),
            It.IsAny<AuditContext>()), Times.Once);
    }

    [TestMethod]
    public void PreviewOperationalContext_OverridesTamperedBranchAndLibrary()
    {
        var settings = new Mock<Clc.PatronRegistration.Configuration.ISettingProvider>();
        settings.SetupGet(provider => provider.LibraryId).Returns(2);
        var registration = new Registration(settings.Object) { PatronBranchID = 999, LibraryId = 888 };

        PreviewController.ApplyOperationalContext(registration, settings.Object, 3);

        Assert.AreEqual(3, registration.PatronBranchID);
        Assert.AreEqual(2, registration.LibraryId);
    }

    [TestMethod]
    public void LibraryAdministrator_CannotManageSensitiveSharedDraftLifecycleOrCraftedRemoval()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        var draft = SensitiveDraft();
        repository.Setup(service => service.GetDraft(draft.DraftId)).Returns(draft);
        repository.Setup(service => service.GetPreviewLink(12)).Returns(PreviewLink(draft));
        var authorization = LibraryAuthorization();
        var controller = CreateController(repository, authorization);

        Assert.IsInstanceOfType<ForbidResult>(controller.CommitDraft(draft.DraftId, 3));
        Assert.IsInstanceOfType<ForbidResult>(controller.DiscardDraft(draft.DraftId, 3));
        Assert.IsInstanceOfType<ForbidResult>(controller.CreatePreviewLink(draft.DraftId, new PreviewLinkRequest { OrganizationId = 3, OperationalBranchId = 3 }));
        Assert.IsInstanceOfType<ForbidResult>(controller.ReplacePreviewLinkMode(12, true));
        Assert.IsInstanceOfType<ForbidResult>(controller.RevokePreviewLink(12));
        Assert.IsInstanceOfType<ForbidResult>(controller.RemoveDraftChange(draft.DraftId, 3, string.Empty, "postmark_api_key"));

        repository.Verify(service => service.CommitDraft(It.IsAny<long>(), It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), It.IsAny<bool>(), It.IsAny<AuditContext>(), It.IsAny<long?>()), Times.Never);
        repository.Verify(service => service.RemoveDraftChange(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), It.IsAny<bool>(), It.IsAny<AuditContext>()), Times.Never);
    }

    [TestMethod]
    public void LibraryAdministrator_SeesOnlyGenericRestrictedDraftIndicatorNotSensitiveMutation()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        repository.Setup(service => service.GetFormCodes(It.IsAny<int>(), It.IsAny<int>())).Returns([]);
        repository.Setup(service => service.GetLegacyFormCodes()).Returns([]);
        repository.Setup(service => service.GetActiveDraft(3, string.Empty)).Returns(SensitiveDraft());
        var controller = CreateController(repository, LibraryAuthorization());

        var result = (ViewResult)controller.Index(3, string.Empty);
        var model = (SettingsIndexViewModel)result.Model!;

        Assert.IsTrue(model.HasRestrictedDraftChanges);
        Assert.IsFalse(model.CanManageRestrictedDraft);
        Assert.IsFalse(model.Settings.Any(row => row.Definition.IsSensitive));
        Assert.IsFalse(model.Settings.Any(row => row.Definition.Key.Contains("postmark", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(model.ActiveDraft!.Changes.Any(change =>
            change.Key.Contains("postmark", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void GlobalAdministrator_CanManageSensitiveSharedDraftOperations()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        var draft = SensitiveDraft();
        repository.Setup(service => service.GetDraft(draft.DraftId)).Returns(draft);
        repository.Setup(service => service.GetPreviewLink(12)).Returns(PreviewLink(draft));
        var authorization = new Mock<ISettingsAuthorizationService>();
        authorization.Setup(service => service.Describe(It.IsAny<ClaimsPrincipal>())).Returns(new SettingsPrincipal(true, -1, true));
        authorization.Setup(service => service.CanManage(It.IsAny<ClaimsPrincipal>(), It.IsAny<int>(), It.IsAny<bool>())).Returns(true);
        var controller = CreateController(repository, authorization);
        var url = new Mock<IUrlHelper>();
        url.Setup(helper => helper.Action(It.IsAny<UrlActionContext>())).Returns("https://example.test/preview/token");
        controller.Url = url.Object;

        Assert.IsInstanceOfType<RedirectToActionResult>(controller.CommitDraft(draft.DraftId, 3, expectedDraftRevision: draft.Revision));
        Assert.IsInstanceOfType<RedirectToActionResult>(controller.DiscardDraft(draft.DraftId, 3, expectedDraftRevision: draft.Revision));
        Assert.IsInstanceOfType<ViewResult>(controller.CreatePreviewLink(draft.DraftId, new PreviewLinkRequest
        {
            OrganizationId = 3, OperationalBranchId = 3, ExpectedDraftRevision = draft.Revision
        }));
        Assert.IsInstanceOfType<RedirectToActionResult>(controller.ReplacePreviewLinkMode(12, true));
        Assert.IsInstanceOfType<RedirectToActionResult>(controller.RevokePreviewLink(12));
        Assert.IsInstanceOfType<RedirectToActionResult>(controller.RemoveDraftChange(draft.DraftId, 3, string.Empty, "postmark_api_key"));
    }

    [TestMethod]
    public void LibraryAdministrator_CanManageNonSensitiveSharedDraft()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        var draft = new SettingDraft(22, 3, string.Empty, 0, DraftStatus.Active,
            [new SettingMutation("registration_text", DraftOperation.Upsert, "draft")]);
        repository.Setup(service => service.GetDraft(draft.DraftId)).Returns(draft);
        var controller = CreateController(repository, LibraryAuthorization());

        Assert.IsInstanceOfType<RedirectToActionResult>(controller.CommitDraft(draft.DraftId, 3, expectedDraftRevision: draft.Revision));
        Assert.IsInstanceOfType<RedirectToActionResult>(controller.DiscardDraft(draft.DraftId, 3, expectedDraftRevision: draft.Revision));
    }

    [TestMethod]
    public void RemoveDraftChange_ConcurrencyRedirectsWithContext()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        var draft = new SettingDraft(23, 3, string.Empty, 0, DraftStatus.Active,
            [new SettingMutation("registration_text", DraftOperation.Upsert, "draft")]);
        repository.Setup(service => service.GetDraft(draft.DraftId)).Returns(draft);
        repository.Setup(service => service.RemoveDraftChange(draft.DraftId, "registration_text", It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), It.IsAny<bool>(), It.IsAny<AuditContext>()))
            .Throws(new System.Data.DBConcurrencyException("The staged draft mutation no longer exists."));
        var controller = CreateController(repository, LibraryAuthorization());

        var result = controller.RemoveDraftChange(draft.DraftId, 3, string.Empty, "registration_text");

        AssertDraftConflictRedirect(controller, result, 3, string.Empty);
    }

    [TestMethod]
    public void CommitDraft_SensitiveMutationAddedAfterPrecheck_ReturnsForbid()
    {
        var repository = RaceRepository();
        repository.Setup(service => service.CommitDraft(24, It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), false, It.IsAny<AuditContext>(), It.IsAny<long?>()))
            .Throws(new UnauthorizedAccessException());

        Assert.IsInstanceOfType<ForbidResult>(CreateController(repository, LibraryAuthorization()).CommitDraft(24, 3, expectedDraftRevision: 0));
    }

    [TestMethod]
    public void DiscardDraft_SensitiveMutationAddedAfterPrecheck_ReturnsForbid()
    {
        var repository = RaceRepository();
        repository.Setup(service => service.DiscardDraft(24, It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), false, It.IsAny<AuditContext>(), It.IsAny<long?>()))
            .Throws(new UnauthorizedAccessException());

        Assert.IsInstanceOfType<ForbidResult>(CreateController(repository, LibraryAuthorization()).DiscardDraft(24, 3, expectedDraftRevision: 0));
    }

    [TestMethod]
    public void PreviewLifecycle_SensitiveMutationAddedAfterPrecheck_ReturnsForbid()
    {
        var repository = RaceRepository();
        repository.Setup(service => service.GetPreviewLink(12)).Returns(PreviewLink(NonSensitiveDraft()));
        repository.Setup(service => service.CreatePreviewLink(24, It.IsAny<byte[]>(), false, 3, It.IsAny<int>(),
                It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), false, It.IsAny<AuditContext>(), It.IsAny<long?>()))
            .Throws(new UnauthorizedAccessException());
        repository.Setup(service => service.ReplacePreviewLinkMode(12, It.IsAny<byte[]>(), true,
                It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), false, It.IsAny<AuditContext>()))
            .Throws(new UnauthorizedAccessException());
        repository.Setup(service => service.RevokePreviewLink(12,
                It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), false, It.IsAny<AuditContext>()))
            .Throws(new UnauthorizedAccessException());
        var controller = CreateController(repository, LibraryAuthorization());
        var url = new Mock<IUrlHelper>();
        url.Setup(helper => helper.Action(It.IsAny<UrlActionContext>())).Returns("https://example.test/preview");
        controller.Url = url.Object;

        Assert.IsInstanceOfType<ForbidResult>(controller.CreatePreviewLink(24,
            new PreviewLinkRequest { OrganizationId = 3, OperationalBranchId = 3, ExpectedDraftRevision = 0 }));
        Assert.IsInstanceOfType<ForbidResult>(controller.ReplacePreviewLinkMode(12, true));
        Assert.IsInstanceOfType<ForbidResult>(controller.RevokePreviewLink(12));
    }

    [TestMethod]
    public void SavingAfterAnotherAdministratorDiscardedDraft_RedirectsWithContextualConflictMessage()
    {
        var repository = RaceRepository();
        repository.Setup(service => service.SaveToSharedDraft(3, string.Empty, It.IsAny<long>(), 24, It.IsAny<IReadOnlyList<SettingMutation>>(),
                It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), It.IsAny<AuditContext>()))
            .Throws(new System.Data.DBConcurrencyException("The shared draft is no longer active."));
        var controller = CreateController(repository, LibraryAuthorization());

        var result = controller.SaveToSharedDraft(new SaveToSharedDraftRequest
        {
            OrganizationId = 3,
            ExpectedDraftId = 24,
            Changes = [new SettingMutationInput { Key = "registration_text", Operation = "Upsert", Value = "new" }]
        });

        AssertDraftConflictRedirect(controller, result, 3, string.Empty);
    }

    [TestMethod]
    public void CommittingAfterConcurrentDraftEdit_RedirectsWithContextualConflictMessage()
    {
        var repository = RaceRepository();
        repository.Setup(service => service.CommitDraft(24, It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), false, It.IsAny<AuditContext>(), It.IsAny<long?>()))
            .Throws(new System.Data.DBConcurrencyException("The draft baseline is stale."));

        var controller = CreateController(repository, LibraryAuthorization());
        var result = controller.CommitDraft(24, 3, expectedDraftRevision: 0);

        AssertDraftConflictRedirect(controller, result, 3, string.Empty);
    }

    [TestMethod]
    public void DiscardingOrPreviewingAfterConcurrentCommit_RedirectsWithContextualConflictMessage()
    {
        var repository = RaceRepository();
        repository.Setup(service => service.DiscardDraft(24, It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), false, It.IsAny<AuditContext>(), It.IsAny<long?>()))
            .Throws(new System.Data.DBConcurrencyException("The shared draft is no longer active."));
        repository.Setup(service => service.CreatePreviewLink(24, It.IsAny<byte[]>(), false, 3, It.IsAny<int>(),
                It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), false, It.IsAny<AuditContext>(), It.IsAny<long?>()))
            .Throws(new System.Data.DBConcurrencyException("The shared draft is no longer active."));
        var controller = CreateController(repository, LibraryAuthorization());

        AssertDraftConflictRedirect(controller, controller.DiscardDraft(24, 3, expectedDraftRevision: 0), 3, string.Empty);
        AssertDraftConflictRedirect(controller, controller.CreatePreviewLink(24,
            new PreviewLinkRequest { OrganizationId = 3, OperationalBranchId = 3, ExpectedDraftRevision = 0 }), 3, string.Empty);
    }

    [TestMethod]
    public void TogglingOrRevokingConcurrentlyChangedLink_RedirectsWithContextualConflictMessage()
    {
        var repository = RaceRepository();
        repository.Setup(service => service.GetPreviewLink(12)).Returns(PreviewLink(NonSensitiveDraft()));
        repository.Setup(service => service.ReplacePreviewLinkMode(12, It.IsAny<byte[]>(), true,
                It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), false, It.IsAny<AuditContext>()))
            .Throws(new System.Data.DBConcurrencyException("The preview link was revoked."));
        repository.Setup(service => service.RevokePreviewLink(12,
                It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), false, It.IsAny<AuditContext>()))
            .Throws(new System.Data.DBConcurrencyException("The preview link was revoked."));
        var controller = CreateController(repository, LibraryAuthorization());

        AssertDraftConflictRedirect(controller, controller.ReplacePreviewLinkMode(12, true), 3, string.Empty);
        AssertDraftConflictRedirect(controller, controller.RevokePreviewLink(12), 3, string.Empty);
    }

    private static void AssertDraftConflictRedirect(SettingsController controller, IActionResult result, int organizationId, string formCode)
    {
        var redirect = (RedirectToActionResult)result;
        Assert.AreEqual(nameof(SettingsController.Index), redirect.ActionName);
        Assert.AreEqual(organizationId, redirect.RouteValues!["organizationId"]);
        Assert.AreEqual(formCode, redirect.RouteValues["formCode"]);
        StringAssert.Contains((string)controller.TempData["SettingsError"]!, "changed while you were working");
    }

    private static Mock<ISettingsAdministrationRepository> ImagePresentationRepository(SettingDraft draft)
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        repository.Setup(service => service.GetActiveDraft(3, string.Empty)).Returns(draft);
        repository.Setup(service => service.GetPreviewLinks(draft.DraftId)).Returns([]);
        repository.Setup(service => service.GetFormCodes(It.IsAny<int>(), It.IsAny<int>())).Returns([]);
        repository.Setup(service => service.GetLegacyFormCodes()).Returns([]);
        return repository;
    }

    private static TestCache ImagePresentationCache(bool includeInherited) => new()
    {
        SettingsCache =
        [
            new() { OrganizationID = 3, FormCode = string.Empty, Setting = "header_image_asset_id", Value = "11" },
            ..(includeInherited
                ? new[] { new RegistrationFormSetting { OrganizationID = 1, FormCode = string.Empty, Setting = "header_image_asset_id", Value = "10" } }
                : Array.Empty<RegistrationFormSetting>())
        ]
    };

    private static RegistrationFormAssetMetadata ImageMetadata(int assetId, string fileName) =>
        new(assetId, fileName, "image/png", $"hash-{assetId}", DateTime.UtcNow, DateTime.UtcNow,
            assetId == 10 ? 1 : 3, string.Empty);

    [TestMethod]
    public void Index_StaleNodeFailsClosedInsteadOfPairingOldSettingsWithNewVersion()
    {
        // Instance B has published generation N+1 while instance A still has
        // its independently published generation-N snapshot.
        var instanceA = new TestCache
        {
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
        var repository = new Mock<ISettingsAdministrationRepository>();
        repository.Setup(service => service.GetFormCodes(It.IsAny<int>(), It.IsAny<int>())).Returns([]);
        repository.Setup(service => service.GetLegacyFormCodes()).Returns([]);
        repository.Setup(service => service.GetVersion(3, string.Empty)).Returns(2);
        var controller = CreateController(repository, LibraryAuthorization(), instanceA);
        repository.Setup(service => service.GetCacheGeneration()).Returns(2);

        var result = controller.Index(3);

        Assert.AreEqual(2, instanceB.GetSnapshot().Generation);
        var unavailable = result as ObjectResult;
        Assert.IsNotNull(unavailable);
        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, unavailable!.StatusCode);
        Assert.IsFalse(result is ViewResult);
    }

    [TestMethod]
    public void Index_RetriesWhenGenerationChangesBetweenSnapshotAndVersionRead()
    {
        var cache = new TestCache
        {
            SettingsCache =
            [
                new() { OrganizationID = 3, Setting = "registration_text", Value = "generation N" }
            ]
        };
        var repository = new Mock<ISettingsAdministrationRepository>();
        repository.Setup(service => service.GetFormCodes(It.IsAny<int>(), It.IsAny<int>())).Returns([]);
        repository.Setup(service => service.GetLegacyFormCodes()).Returns([]);
        var versionReads = 0;
        repository.Setup(service => service.GetVersion(3, string.Empty)).Returns(() =>
        {
            versionReads++;
            if (versionReads == 1)
            {
                cache.SettingsCache =
                [
                    new() { OrganizationID = 3, Setting = "registration_text", Value = "generation N+1" }
                ];
                cache.Generation = 2;
            }
            return 2;
        });
        var controller = CreateController(repository, LibraryAuthorization(), cache);
        var generationReads = 0;
        repository.Setup(service => service.GetCacheGeneration()).Returns(() =>
        {
            generationReads++;
            return generationReads == 1 ? 1 : 2;
        });

        var result = (ViewResult)controller.Index(3);
        var model = (SettingsIndexViewModel)result.Model!;

        Assert.AreEqual(2, model.ScopeVersion);
        Assert.AreEqual("generation N+1", model.Settings.Single(setting => setting.Definition.Key == "registration_text")
            .Resolution.EffectiveValue);
        Assert.AreEqual(2, versionReads);
        Assert.AreEqual(4, generationReads);
    }

    [TestMethod]
    public void Index_CacheRefreshFailureReturnsServiceUnavailable()
    {
        var cache = new Mock<ICache>();
        cache.As<IGenerationAwareCacheSnapshotProvider>()
            .Setup(provider => provider.GetSnapshotAtGeneration(1))
            .Throws(new CacheSnapshotConsistencyException("refresh failed"));
        var repository = new Mock<ISettingsAdministrationRepository>();
        var controller = CreateController(repository, LibraryAuthorization(), cache.Object);
        repository.Setup(service => service.GetCacheGeneration()).Returns(1);

        var result = controller.Index(3);

        var unavailable = result as ObjectResult;
        Assert.IsNotNull(unavailable);
        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, unavailable!.StatusCode);
        repository.Verify(service => service.GetVersion(It.IsAny<int>(), It.IsAny<string>()), Times.Never);
    }

    private static Mock<ISettingsAdministrationRepository> RaceRepository()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        repository.Setup(service => service.GetDraft(24)).Returns(NonSensitiveDraft());
        return repository;
    }

    private static Mock<ISettingsAdministrationRepository> PreviewLifecycleRepository(SettingDraft draft)
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        repository.Setup(service => service.GetDraft(draft.DraftId)).Returns(draft);
        repository.Setup(service => service.GetPreviewLink(12)).Returns(PreviewLink(draft));
        repository.Setup(service => service.GetFormCodes(It.IsAny<int>(), It.IsAny<int>())).Returns([]);
        repository.Setup(service => service.GetLegacyFormCodes()).Returns([]);
        return repository;
    }

    private static SqlException SqlExceptionWithNumber(int number)
    {
        var errorConstructor = typeof(SqlError).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(constructor => constructor.GetParameters().Any(parameter => parameter.Name == "infoNumber"))
            .OrderBy(constructor => constructor.GetParameters().Length).First();
        var errorArguments = errorConstructor.GetParameters().Select(parameter => parameter.Name == "infoNumber"
            ? (object)number
            : parameter.ParameterType == typeof(string) ? string.Empty
            : parameter.ParameterType == typeof(Exception) ? null
            : Activator.CreateInstance(parameter.ParameterType)).ToArray();
        var error = (SqlError)errorConstructor.Invoke(errorArguments);
        var errors = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), nonPublic: true)!;
        typeof(SqlErrorCollection).GetMethod("Add", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(errors, [error]);
        var factory = typeof(SqlException).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Where(method => method.Name == "CreateException" && method.GetParameters() is var parameters &&
                parameters.Length >= 2 && parameters[0].ParameterType == typeof(SqlErrorCollection) && parameters[1].ParameterType == typeof(string))
            .OrderBy(method => method.GetParameters().Length).First();
        var factoryArguments = factory.GetParameters().Select((parameter, index) => index == 0 ? errors
            : index == 1 ? (object)"test"
            : parameter.ParameterType == typeof(Guid) ? Guid.NewGuid()
            : parameter.ParameterType == typeof(Exception) ? null
            : Activator.CreateInstance(parameter.ParameterType)).ToArray();
        return (SqlException)factory.Invoke(null, factoryArguments)!;
    }

    private static SettingDraft NonSensitiveDraft() => new(24, 3, string.Empty, 0, DraftStatus.Active,
        [new SettingMutation("registration_text", DraftOperation.Upsert, "draft")]);

    private static SettingDraft SensitiveDraft() => new(21, 3, string.Empty, 0, DraftStatus.Active,
        [new SettingMutation("postmark_api_key", DraftOperation.Upsert, new string('s', 32))]);

    private static PreviewLinkRecord PreviewLink(SettingDraft draft) =>
        new(12, draft.DraftId, new byte[32], false, null, null, draft.OrganizationId, draft.FormCode, "Active", 3);

    private static Mock<ISettingsAuthorizationService> LibraryAuthorization()
    {
        var authorization = new Mock<ISettingsAuthorizationService>();
        authorization.Setup(service => service.Describe(It.IsAny<ClaimsPrincipal>())).Returns(new SettingsPrincipal(true, 2, false));
        authorization.Setup(service => service.CanManage(It.IsAny<ClaimsPrincipal>(), 3, It.IsAny<bool>()))
            .Returns((ClaimsPrincipal user, int organizationId, bool sensitive) => !sensitive);
        return authorization;
    }

    private static Mock<ISettingsAuthorizationService> GlobalAuthorization()
    {
        var authorization = new Mock<ISettingsAuthorizationService>();
        authorization.Setup(service => service.Describe(It.IsAny<ClaimsPrincipal>()))
            .Returns(new SettingsPrincipal(true, -1, true));
        authorization.Setup(service => service.CanManage(It.IsAny<ClaimsPrincipal>(), It.IsAny<int>(), It.IsAny<bool>()))
            .Returns(true);
        return authorization;
    }

    private static SettingsController CreateController(
        Mock<ISettingsAdministrationRepository> repository,
        Mock<ISettingsAuthorizationService> authorization,
        ICache? suppliedCache = null,
        ISettingsPageBrandingContextAccessor? brandingAccessor = null,
        SettingsAdministrationOptions? administrationOptions = null,
        IPreviewTokenService? previewTokenService = null,
        Mock<IRegistrationFormAssetRepository>? suppliedAssets = null,
        IRegistrationFormAssetAuthorization? suppliedAssetAuthorization = null,
        ISettingsCacheInvalidator? suppliedCacheInvalidator = null)
    {
        repository.Setup(service => service.GetCacheGeneration()).Returns(1);
        var invalidator = suppliedCacheInvalidator ?? new Mock<ISettingsCacheInvalidator>().Object;
        var branchEligibility = new Mock<IPreviewBranchEligibilityService>();
        branchEligibility.Setup(service => service.GetEligibleBranches(It.IsAny<int>(), It.IsAny<int>())).Returns([]);
        branchEligibility.Setup(service => service.IsEligible(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
            .Returns((int scopeId, int branchId, int systemId) => branchId == 3);
        var options = Options.Create(administrationOptions ?? new SettingsAdministrationOptions());
        var cache = suppliedCache ?? new TestCache();
        var formCodeAvailability = new FormCodeAvailabilityService(repository.Object, cache, options);
        var assets = suppliedAssets ?? new Mock<IRegistrationFormAssetRepository>();
        if (suppliedAssets is null)
        {
            assets.Setup(service => service.GetMetadata(It.IsAny<int>()))
                .Returns((RegistrationFormAssetMetadata?)null);
        }
        var assetAuthorizationMock = suppliedAssetAuthorization is null
            ? new Mock<IRegistrationFormAssetAuthorization>()
            : null;
        if (suppliedAssetAuthorization is null)
        {
            assetAuthorizationMock!.Setup(service => service.GetAuthorizedMetadata(
                    It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>()))
                .Returns((RegistrationFormAssetMetadata?)null);
        }
        var assetAuthorization = suppliedAssetAuthorization ?? assetAuthorizationMock!.Object;
        var controller = new SettingsController(
            authorization.Object,
            repository.Object,
            new SettingCatalog(),
            cache,
            previewTokenService ?? new PreviewTokenService(),
            branchEligibility.Object,
            formCodeAvailability,
            invalidator,
            brandingAccessor ?? new SettingsPageBrandingContextAccessor(),
            assets.Object,
            assetAuthorization,
            options);
        controller.ControllerContext = new ControllerContext
        {
            RouteData = new Microsoft.AspNetCore.Routing.RouteData(),
            ActionDescriptor = new Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor(),
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.Name, "admin@example.org"),
                    new Claim("Clc.OrganizationId", "2")
                ], "test"))
            }
        };
        var tempDataProvider = new Mock<ITempDataProvider>();
        tempDataProvider.Setup(provider => provider.LoadTempData(It.IsAny<HttpContext>()))
            .Returns(new Dictionary<string, object>());
        controller.TempData = new TempDataDictionary(controller.HttpContext, tempDataProvider.Object);
        return controller;
    }
}
