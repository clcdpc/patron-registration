using System.Security.Claims;
using Clc.PatronRegistration.Administration;
using Clc.PatronRegistration.Helpers;
using Clc.PatronRegistration.Web.Controllers;
using Clc.PatronRegistration.Web.Models;
using Clc.PatronRegistration.Web.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Moq;

namespace Clc.PatronRegistration.Tests;

[TestClass]
public class SettingsControllerTests
{
    [DataTestMethod]
    [DataRow("force_ecard_remotely")]
    [DataRow("require.AddToMailingList")]
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
            It.IsAny<long>(), It.IsAny<byte[]>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<AuditContext>()), Times.Never);
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

    private static SettingsController CreateController(
        Mock<ISettingsAdministrationRepository> repository,
        Mock<ISettingsAuthorizationService> authorization)
    {
        repository.Setup(service => service.GetCacheGeneration()).Returns(1);
        var invalidator = new Mock<ISettingsCacheInvalidator>();
        var controller = new SettingsController(
            authorization.Object,
            repository.Object,
            new SettingCatalog(),
            new TestCache(),
            new PreviewTokenService(),
            invalidator.Object,
            Options.Create(new SettingsAdministrationOptions()));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.Name, "admin@example.org"),
                    new Claim("organization", "2")
                ], "test"))
            }
        };
        return controller;
    }
}
