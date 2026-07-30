using System.Security.Claims;
using Clc.PatronRegistration.Administration;
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

namespace Clc.PatronRegistration.Tests;

[TestClass]
public class SettingsControllerTests
{
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
        var draft = NonSensitiveDraft();
        repository.Setup(service => service.GetDraft(draft.DraftId)).Returns(draft);
        repository.Setup(service => service.GetPreviewLink(12)).Returns(PreviewLink(draft));
        repository.Setup(service => service.ReplacePreviewLinkMode(12, It.IsAny<byte[]>(), true,
                It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), false, It.IsAny<AuditContext>()))
            .Returns(13);
        var controller = CreateController(repository, LibraryAuthorization());
        var url = new Mock<IUrlHelper>();
        url.Setup(helper => helper.Action(It.IsAny<UrlActionContext>())).Returns("https://example.test/preview/replacement");
        controller.Url = url.Object;

        var result = controller.ReplacePreviewLinkMode(12, true);

        var view = (ViewResult)result;
        Assert.AreEqual("PreviewLinkCreated", view.ViewName);
        Assert.AreEqual("https://example.test/preview/replacement", view.Model);
        repository.Verify(service => service.ReplacePreviewLinkMode(12,
            It.Is<byte[]>(hash => hash.Length == 32), true,
            It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), false, It.IsAny<AuditContext>()), Times.Once);
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
        var draft = new DraftChangesRequest
        {
            OrganizationId = 3,
            Changes = [new SettingMutationInput { Key = "label.NameFirst", Value = maliciousLabel }]
        };

        Assert.IsInstanceOfType<RedirectToActionResult>(controller.DirectSave(direct));
        controller.ModelState.Clear();
        Assert.IsInstanceOfType<RedirectToActionResult>(controller.SaveDraft(24, draft));
        repository.Verify(service => service.DirectSave(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<IReadOnlyList<SettingMutation>>(),
            It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), It.IsAny<AuditContext>()), Times.Never);
        repository.Verify(service => service.SaveDraftChanges(
            It.IsAny<long>(), It.IsAny<IReadOnlyList<SettingMutation>>(),
            It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), It.IsAny<AuditContext>()), Times.Never);
    }

    [TestMethod]
    public void CreateDraft_ConcurrentRepositoryChangeReturnsConflict()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        repository.Setup(service => service.CreateDraft(3, string.Empty, It.IsAny<AuditContext>()))
            .Throws(new System.Data.DBConcurrencyException("The form is changing."));
        var authorization = LibraryAuthorization();
        var controller = CreateController(repository, authorization);

        var result = controller.CreateDraft(3);

        Assert.IsInstanceOfType<ConflictObjectResult>(result);
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
    }

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
            It.IsAny<long>(), It.IsAny<byte[]>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), It.IsAny<bool>(), It.IsAny<AuditContext>()), Times.Never);
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
            new PreviewLinkRequest { OrganizationId = 3, OperationalBranchId = 3 });

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
            Description = "Updated description"
        };

        var result = controller.EditForm("kids", request);

        Assert.IsInstanceOfType<RedirectToActionResult>(result);
        repository.Verify(service => service.SaveFormCode(
            It.Is<FormCodeMetadata>(metadata =>
                metadata.OrganizationId == ownerOrganizationId &&
                metadata.FormCode == "kids" &&
                metadata.DisplayName == "Updated kids form"),
            false,
            It.IsAny<AuditContext>()), Times.Once);
    }

    [TestMethod]
    public void CustomizeForm_UpdatesAnExistingLocalCustomization()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        repository.Setup(service => service.GetFormCodes(2, 1)).Returns(
        [
            new FormCodeMetadata(1, "kids", "System name", null, DateTime.UtcNow, "a", DateTime.UtcNow, "a"),
            new FormCodeMetadata(2, "kids", "Local name", "Local description", DateTime.UtcNow, "a", DateTime.UtcNow, "a")
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
            Description = "Changed locally"
        });

        Assert.IsInstanceOfType<RedirectToActionResult>(result);
        repository.Verify(service => service.SaveFormCode(
            It.Is<FormCodeMetadata>(metadata => metadata.DisplayName == "Changed local name"),
            false,
            It.IsAny<AuditContext>()), Times.Once);
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
            It.IsAny<AuditContext>()), Times.Once);
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

        repository.Verify(service => service.CommitDraft(It.IsAny<long>(), It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), It.IsAny<bool>(), It.IsAny<AuditContext>()), Times.Never);
        repository.Verify(service => service.RemoveDraftChange(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), It.IsAny<bool>(), It.IsAny<AuditContext>()), Times.Never);
    }

    [TestMethod]
    public void LibraryAdministrator_SeesOnlyGenericRestrictedDraftIndicatorNotSensitiveMutation()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
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

        Assert.IsInstanceOfType<RedirectToActionResult>(controller.CommitDraft(draft.DraftId, 3));
        Assert.IsInstanceOfType<RedirectToActionResult>(controller.DiscardDraft(draft.DraftId, 3));
        Assert.IsInstanceOfType<ViewResult>(controller.CreatePreviewLink(draft.DraftId, new PreviewLinkRequest { OrganizationId = 3, OperationalBranchId = 3 }));
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

        Assert.IsInstanceOfType<RedirectToActionResult>(controller.CommitDraft(draft.DraftId, 3));
        Assert.IsInstanceOfType<RedirectToActionResult>(controller.DiscardDraft(draft.DraftId, 3));
    }

    [TestMethod]
    public void RemoveDraftChange_ConcurrencyReturnsConflictInsteadOfUnhandledError()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        var draft = new SettingDraft(23, 3, string.Empty, 0, DraftStatus.Active,
            [new SettingMutation("registration_text", DraftOperation.Upsert, "draft")]);
        repository.Setup(service => service.GetDraft(draft.DraftId)).Returns(draft);
        repository.Setup(service => service.RemoveDraftChange(draft.DraftId, "registration_text", It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), It.IsAny<bool>(), It.IsAny<AuditContext>()))
            .Throws(new System.Data.DBConcurrencyException("The staged draft mutation no longer exists."));
        var controller = CreateController(repository, LibraryAuthorization());

        var result = controller.RemoveDraftChange(draft.DraftId, 3, string.Empty, "registration_text");

        Assert.IsInstanceOfType<ConflictObjectResult>(result);
    }

    [TestMethod]
    public void CommitDraft_SensitiveMutationAddedAfterPrecheck_ReturnsForbid()
    {
        var repository = RaceRepository();
        repository.Setup(service => service.CommitDraft(24, It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), false, It.IsAny<AuditContext>()))
            .Throws(new UnauthorizedAccessException());

        Assert.IsInstanceOfType<ForbidResult>(CreateController(repository, LibraryAuthorization()).CommitDraft(24, 3));
    }

    [TestMethod]
    public void DiscardDraft_SensitiveMutationAddedAfterPrecheck_ReturnsForbid()
    {
        var repository = RaceRepository();
        repository.Setup(service => service.DiscardDraft(24, It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), false, It.IsAny<AuditContext>()))
            .Throws(new UnauthorizedAccessException());

        Assert.IsInstanceOfType<ForbidResult>(CreateController(repository, LibraryAuthorization()).DiscardDraft(24, 3));
    }

    [TestMethod]
    public void PreviewLifecycle_SensitiveMutationAddedAfterPrecheck_ReturnsForbid()
    {
        var repository = RaceRepository();
        repository.Setup(service => service.GetPreviewLink(12)).Returns(PreviewLink(NonSensitiveDraft()));
        repository.Setup(service => service.CreatePreviewLink(24, It.IsAny<byte[]>(), false, 3,
                It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), false, It.IsAny<AuditContext>()))
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
            new PreviewLinkRequest { OrganizationId = 3, OperationalBranchId = 3 }));
        Assert.IsInstanceOfType<ForbidResult>(controller.ReplacePreviewLinkMode(12, true));
        Assert.IsInstanceOfType<ForbidResult>(controller.RevokePreviewLink(12));
    }

    [TestMethod]
    public void SavingAfterAnotherAdministratorDiscardedDraft_ReturnsConflict()
    {
        var repository = RaceRepository();
        repository.Setup(service => service.SaveDraftChanges(24, It.IsAny<IReadOnlyList<SettingMutation>>(),
                It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), It.IsAny<AuditContext>()))
            .Throws(new System.Data.DBConcurrencyException("The shared draft is no longer active."));
        var controller = CreateController(repository, LibraryAuthorization());

        var result = controller.SaveDraft(24, new DraftChangesRequest
        {
            OrganizationId = 3,
            Changes = [new SettingMutationInput { Key = "registration_text", Operation = "Upsert", Value = "new" }]
        });

        Assert.IsInstanceOfType<ConflictObjectResult>(result);
    }

    [TestMethod]
    public void CommittingAfterConcurrentDraftEdit_ReturnsConflict()
    {
        var repository = RaceRepository();
        repository.Setup(service => service.CommitDraft(24, It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), false, It.IsAny<AuditContext>()))
            .Throws(new System.Data.DBConcurrencyException("The draft baseline is stale."));

        var result = CreateController(repository, LibraryAuthorization()).CommitDraft(24, 3);

        Assert.IsInstanceOfType<ConflictObjectResult>(result);
    }

    [TestMethod]
    public void DiscardingOrPreviewingAfterConcurrentCommit_ReturnsConflict()
    {
        var repository = RaceRepository();
        repository.Setup(service => service.DiscardDraft(24, It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), false, It.IsAny<AuditContext>()))
            .Throws(new System.Data.DBConcurrencyException("The shared draft is no longer active."));
        repository.Setup(service => service.CreatePreviewLink(24, It.IsAny<byte[]>(), false, 3,
                It.IsAny<IReadOnlyDictionary<string, SettingDefinition>>(), false, It.IsAny<AuditContext>()))
            .Throws(new System.Data.DBConcurrencyException("The shared draft is no longer active."));
        var controller = CreateController(repository, LibraryAuthorization());

        Assert.IsInstanceOfType<ConflictObjectResult>(controller.DiscardDraft(24, 3));
        Assert.IsInstanceOfType<ConflictObjectResult>(controller.CreatePreviewLink(24,
            new PreviewLinkRequest { OrganizationId = 3, OperationalBranchId = 3 }));
    }

    [TestMethod]
    public void TogglingOrRevokingConcurrentlyChangedLink_ReturnsConflict()
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

        Assert.IsInstanceOfType<ConflictObjectResult>(controller.ReplacePreviewLinkMode(12, true));
        Assert.IsInstanceOfType<ConflictObjectResult>(controller.RevokePreviewLink(12));
    }

    private static Mock<ISettingsAdministrationRepository> RaceRepository()
    {
        var repository = new Mock<ISettingsAdministrationRepository>();
        repository.Setup(service => service.GetDraft(24)).Returns(NonSensitiveDraft());
        return repository;
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

    private static SettingsController CreateController(
        Mock<ISettingsAdministrationRepository> repository,
        Mock<ISettingsAuthorizationService> authorization,
        ICache? suppliedCache = null)
    {
        repository.Setup(service => service.GetCacheGeneration()).Returns(1);
        var invalidator = new Mock<ISettingsCacheInvalidator>();
        var branchEligibility = new Mock<IPreviewBranchEligibilityService>();
        branchEligibility.Setup(service => service.GetEligibleBranches(It.IsAny<int>(), It.IsAny<int>())).Returns([]);
        branchEligibility.Setup(service => service.IsEligible(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
            .Returns((int scopeId, int branchId, int systemId) => branchId == 3);
        var options = Options.Create(new SettingsAdministrationOptions());
        var cache = suppliedCache ?? new TestCache();
        var formCodeAvailability = new FormCodeAvailabilityService(repository.Object, cache, options);
        var controller = new SettingsController(
            authorization.Object,
            repository.Object,
            new SettingCatalog(),
            cache,
            new PreviewTokenService(),
            branchEligibility.Object,
            formCodeAvailability,
            invalidator.Object,
            options);
        controller.ControllerContext = new ControllerContext
        {
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
