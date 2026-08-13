using System.Text;
using Clc.PatronRegistration.Administration;
using Clc.PatronRegistration.Configuration;
using Clc.PatronRegistration.Helpers;
using Clc.PatronRegistration.Web.Controllers;
using Clc.PatronRegistration.Web.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Moq;
using Clc.Polaris.Api.Models;

namespace Clc.PatronRegistration.Tests;

[TestClass]
public sealed class RegistrationFormAssetTests
{
    [TestMethod]
    public void UploadValidation_AcceptsCompletePngJpegAndWebpImages()
    {
        foreach (var contentType in new[] { "image/png", "image/jpeg", "image/webp" })
        {
            var content = TestImageData.Create(contentType);
            var fileName = contentType[("image/".Length)..] switch { "jpeg" => "header.jpg", var extension => $"header.{extension}" };
            Assert.IsTrue(RegistrationFormAssetUploadValidation.TryValidate(contentType, content, fileName,
                out var sanitized, out var error), contentType);
            Assert.AreEqual(fileName, sanitized);
            Assert.IsNull(error, contentType);
        }
    }

    [TestMethod]
    public void UploadValidation_RejectsTruncatedHeadersEmptyOversizeSvgUnsupportedAndMismatchedFiles()
    {
        Assert.IsFalse(RegistrationFormAssetUploadValidation.TryValidate("image/png", [], "empty.png", out _, out _));
        Assert.IsFalse(RegistrationFormAssetUploadValidation.TryValidate(
            "image/png", new byte[RegistrationFormAssetUploadValidation.MaximumUploadBytes + 1], "large.png", out _, out _));
        Assert.IsFalse(RegistrationFormAssetUploadValidation.TryValidate("image/svg+xml", Encoding.UTF8.GetBytes("<svg/>"), "logo.svg", out _, out _));
        Assert.IsFalse(RegistrationFormAssetUploadValidation.TryValidate("image/png", Encoding.UTF8.GetBytes("<svg/>"), "logo.png", out _, out _));
        Assert.IsFalse(RegistrationFormAssetUploadValidation.TryValidate("image/gif", [0x47, 0x49, 0x46, 0x38], "logo.gif", out _, out _));
        Assert.IsFalse(RegistrationFormAssetUploadValidation.TryValidate("image/png",
            [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a], "logo.png", out _, out _));
        Assert.IsFalse(RegistrationFormAssetUploadValidation.TryValidate("image/jpeg", [0xff, 0xd8, 0xff], "logo.jpg", out _, out _));
        Assert.IsFalse(RegistrationFormAssetUploadValidation.TryValidate("image/webp",
            "RIFFxxxxWEBP"u8.ToArray(), "logo.webp", out _, out _));
        Assert.IsFalse(RegistrationFormAssetUploadValidation.TryValidate("image/jpeg",
            TestImageData.Create("image/png"), "logo.jpg", out _, out _));
    }

    [TestMethod]
    public void UploadValidation_SanitizesDisplayNameAndComputesLowercaseSha256()
    {
        var content = new byte[] { 1, 2, 3 };
        Assert.IsTrue(RegistrationFormAssetUploadValidation.TryValidate("image/png",
            TestImageData.Create("image/png"), "..\\nested\\header.png", out var name, out _));
        Assert.AreEqual("header.png", name);
        Assert.AreEqual("039058c6f2c0cb492c533b0a4d14ef77cc0f78abccced5287d84a1a2011cfb81",
            RegistrationFormAssetUploadValidation.ComputeContentHash(content));
    }

    [TestMethod]
    public void ImageDefinitionRequiresPositiveAssetId()
    {
        var definition = new SettingCatalog().All.Single(setting => setting.Key == "header_image_asset_id");
        Assert.AreEqual(SettingValueType.Image, definition.ValueType);
        Assert.AreEqual(SettingCategory.PageAppearanceAndInstructions, definition.Category);
        Assert.AreEqual("Header image", definition.DisplayName);
        StringAssert.Contains(definition.Description, "uploaded image");
        Assert.IsNotNull(definition.Validate("0"));
        Assert.IsNotNull(definition.Validate("not-an-id"));
        Assert.IsNull(definition.Validate("42"));
        Assert.IsNotNull(definition.Validate(string.Empty));
    }

    [TestMethod]
    public void AssetEndpoint_ReturnsBytesTypeEtagAndImmutableCachingHeaders()
    {
        var content = new byte[] { 1, 2, 3 };
        var repository = new Mock<IRegistrationFormAssetRepository>();
        repository.Setup(item => item.IsPubliclyReferenced(42)).Returns(true);
        repository.Setup(item => item.GetMetadata(42)).Returns(new RegistrationFormAssetMetadata(
            42, "header.png", "image/png", "abc123", DateTime.UtcNow, DateTime.UtcNow));
        repository.Setup(item => item.Get(42)).Returns(new RegistrationFormAsset(
            42, "header.png", "image/png", content,
            "abc123", DateTime.UtcNow, DateTime.UtcNow));
        var controller = new RegistrationFormAssetsController(repository.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var result = (FileContentResult)controller.Get(42);

        CollectionAssert.AreEqual(content, result.FileContents);
        Assert.AreEqual("image/png", result.ContentType);
        Assert.AreEqual("\"abc123\"", controller.Response.Headers.ETag.ToString());
        Assert.AreEqual("public, max-age=31536000, immutable", controller.Response.Headers.CacheControl.ToString());
        Assert.AreEqual("nosniff", controller.Response.Headers["X-Content-Type-Options"].ToString());
    }

    [TestMethod]
    public void AssetEndpoint_ReturnsNotFoundForUnknownAsset()
    {
        var repository = new Mock<IRegistrationFormAssetRepository>();
        repository.Setup(item => item.Get(42)).Returns((RegistrationFormAsset?)null);
        var controller = new RegistrationFormAssetsController(repository.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        Assert.IsInstanceOfType(controller.Get(42), typeof(NotFoundResult));
    }

    [TestMethod]
    public void AssetEndpoint_DoesNotExposeDraftOnlyOrphanedAsset()
    {
        var repository = new Mock<IRegistrationFormAssetRepository>();
        repository.Setup(item => item.IsPubliclyReferenced(42)).Returns(false);
        repository.Setup(item => item.GetMetadata(42)).Returns(new RegistrationFormAssetMetadata(
            42, "draft.png", "image/png", "abc123", DateTime.UtcNow, DateTime.UtcNow));
        var controller = new RegistrationFormAssetsController(repository.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        Assert.IsInstanceOfType(controller.Get(42), typeof(NotFoundResult));
        repository.Verify(item => item.GetMetadata(42), Times.Never);
        repository.Verify(item => item.Get(42), Times.Never);
    }

    [TestMethod]
    public void SettingsAssetEndpoint_AllowsAuthorizedAdministratorToPreviewKnownAsset()
    {
        var authorization = new Mock<ISettingsAuthorizationService>();
        authorization.Setup(item => item.Describe(It.IsAny<System.Security.Claims.ClaimsPrincipal>()))
            .Returns(new SettingsPrincipal(true, 2, false));
        authorization.Setup(item => item.CanManage(It.IsAny<System.Security.Claims.ClaimsPrincipal>(), 3, false)).Returns(true);
        var forms = new Mock<IFormCodeAvailabilityService>();
        forms.Setup(item => item.IsAvailable(3, string.Empty)).Returns(true);
        var repository = new Mock<IRegistrationFormAssetRepository>();
        repository.Setup(item => item.GetMetadata(42)).Returns(new RegistrationFormAssetMetadata(
            42, "draft.png", "image/png", "abc123", DateTime.UtcNow, DateTime.UtcNow));
        repository.Setup(item => item.Get(42)).Returns(new RegistrationFormAsset(
            42, "draft.png", "image/png", [1, 2], "abc123", DateTime.UtcNow, DateTime.UtcNow));
        var assetAuthorization = new Mock<IRegistrationFormAssetAuthorization>();
        assetAuthorization.Setup(item => item.GetAuthorizedMetadata(42, 3, string.Empty))
            .Returns(repository.Object.GetMetadata(42));
        var controller = new SettingsRegistrationFormAssetsController(
            authorization.Object, forms.Object, repository.Object, assetAuthorization.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var result = (FileContentResult)controller.Get(42, 3);

        CollectionAssert.AreEqual(new byte[] { 1, 2 }, result.FileContents);
        Assert.AreEqual("private, max-age=31536000, immutable", controller.Response.Headers.CacheControl.ToString());
    }

    [TestMethod]
    public void SettingsAssetEndpoint_DeniesAnotherLibrarysUnpublishedAsset()
    {
        var organizations = new List<OrganizationsGetRow>
        {
            new() { OrganizationID = 1, OrganizationCodeID = 1, Name = "System" },
            new() { OrganizationID = 2, OrganizationCodeID = 2, Name = "Own library" },
            new() { OrganizationID = 3, OrganizationCodeID = 3, ParentOrganizationID = 2, Name = "Own branch" },
            new() { OrganizationID = 8, OrganizationCodeID = 2, Name = "Other library" },
            new() { OrganizationID = 9, OrganizationCodeID = 3, ParentOrganizationID = 8, Name = "Other branch" }
        };
        var cache = new Mock<ICache>();
        cache.SetupGet(item => item.OrganizationCache).Returns(organizations);
        var repository = new Mock<IRegistrationFormAssetRepository>();
        repository.Setup(item => item.GetMetadata(42)).Returns(new RegistrationFormAssetMetadata(
            42, "other-library.png", "image/png", "abc123", DateTime.UtcNow, DateTime.UtcNow, 8, string.Empty));
        var assetAuthorization = new RegistrationFormAssetAuthorization(
            repository.Object, cache.Object, Options.Create(new SettingsAdministrationOptions { SystemOrganizationId = 1 }));
        var authorization = new Mock<ISettingsAuthorizationService>();
        authorization.Setup(item => item.Describe(It.IsAny<System.Security.Claims.ClaimsPrincipal>()))
            .Returns(new SettingsPrincipal(true, 2, false));
        authorization.Setup(item => item.CanManage(It.IsAny<System.Security.Claims.ClaimsPrincipal>(), 3, false)).Returns(true);
        var forms = new Mock<IFormCodeAvailabilityService>();
        forms.Setup(item => item.IsAvailable(3, string.Empty)).Returns(true);
        var controller = new SettingsRegistrationFormAssetsController(
            authorization.Object, forms.Object, repository.Object, assetAuthorization)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        Assert.IsInstanceOfType(controller.Get(42, 3), typeof(NotFoundResult));
        repository.Verify(item => item.Get(It.IsAny<int>()), Times.Never);
    }

    [TestMethod]
    public void AssetAuthorization_AllowsAnAssetUploadedAtTheExactTargetScopeBeforeSave()
    {
        var fixture = CreateAssetAuthorizationFixture();
        fixture.Repository.Setup(item => item.GetMetadata(42)).Returns(new RegistrationFormAssetMetadata(
            42, "branch.png", "image/png", "branch-hash", DateTime.UtcNow, DateTime.UtcNow, 3, "kids"));

        Assert.IsNotNull(fixture.Authorization.GetAuthorizedMetadata(42, 3, "kids"));
        fixture.Repository.Verify(item => item.IsReferencedBySettings(
            It.IsAny<int>(), It.IsAny<IReadOnlyList<SettingSource>>()), Times.Never);
    }

    [TestMethod]
    public void AssetAuthorization_AllowsExactScopeUploadWithoutHierarchyLookup()
    {
        var cache = new Mock<ICache>();
        cache.SetupGet(item => item.OrganizationCache).Returns([]);
        var repository = new Mock<IRegistrationFormAssetRepository>();
        repository.Setup(item => item.GetMetadata(42)).Returns(new RegistrationFormAssetMetadata(
            42, "branch.png", "image/png", "branch-hash", DateTime.UtcNow, DateTime.UtcNow, 99, "kids"));
        var authorization = new RegistrationFormAssetAuthorization(
            repository.Object, cache.Object, Options.Create(new SettingsAdministrationOptions { SystemOrganizationId = 1 }));

        Assert.IsNotNull(authorization.GetAuthorizedMetadata(42, 99, "kids"));
    }

    [TestMethod]
    public void AssetAuthorization_DeniesAnUnpublishedAssetUploadedAtAnotherLibrary()
    {
        var fixture = CreateAssetAuthorizationFixture();
        fixture.Repository.Setup(item => item.GetMetadata(42)).Returns(new RegistrationFormAssetMetadata(
            42, "other-library.png", "image/png", "other-hash", DateTime.UtcNow, DateTime.UtcNow, 8, string.Empty));

        Assert.IsNull(fixture.Authorization.GetAuthorizedMetadata(42, 3, string.Empty));
    }

    [TestMethod]
    public void AssetAuthorization_DeniesAnUnpublishedSystemAssetToALibrary()
    {
        var fixture = CreateAssetAuthorizationFixture();
        fixture.Repository.Setup(item => item.GetMetadata(42)).Returns(new RegistrationFormAssetMetadata(
            42, "system.png", "image/png", "system-hash", DateTime.UtcNow, DateTime.UtcNow, 1, string.Empty));

        Assert.IsNull(fixture.Authorization.GetAuthorizedMetadata(42, 2, string.Empty));
        Assert.IsNull(fixture.Authorization.GetAuthorizedMetadata(42, 3, "kids"));
    }

    [TestMethod]
    public void AssetAuthorization_DeniesAnUnpublishedLibraryAssetToADownstreamBranchForm()
    {
        var fixture = CreateAssetAuthorizationFixture();
        fixture.Repository.Setup(item => item.GetMetadata(42)).Returns(new RegistrationFormAssetMetadata(
            42, "library.png", "image/png", "library-hash", DateTime.UtcNow, DateTime.UtcNow, 2, "kids"));

        Assert.IsNull(fixture.Authorization.GetAuthorizedMetadata(42, 3, "kids"));
    }

    [TestMethod]
    public void AssetAuthorization_AllowsPersistedSystemAndLibraryAssetsThroughInheritance()
    {
        var fixture = CreateAssetAuthorizationFixture();
        fixture.Repository.Setup(item => item.GetMetadata(42)).Returns(new RegistrationFormAssetMetadata(
            42, "system.png", "image/png", "system-hash", DateTime.UtcNow, DateTime.UtcNow, 1, string.Empty));
        fixture.Repository.Setup(item => item.GetMetadata(43)).Returns(new RegistrationFormAssetMetadata(
            43, "library.png", "image/png", "library-hash", DateTime.UtcNow, DateTime.UtcNow, 2, "kids"));
        fixture.Repository.Setup(item => item.IsReferencedBySettings(
                42, It.IsAny<IReadOnlyList<SettingSource>>())).Returns(true);
        fixture.Repository.Setup(item => item.IsReferencedBySettings(
                43, It.IsAny<IReadOnlyList<SettingSource>>())).Returns(true);

        Assert.IsNotNull(fixture.Authorization.GetAuthorizedMetadata(42, 3, "kids"));
        Assert.IsNotNull(fixture.Authorization.GetAuthorizedMetadata(43, 3, "kids"));
    }

    [TestMethod]
    public void AssetAuthorization_DoesNotInheritAnUpstreamActiveDraft()
    {
        var fixture = CreateAssetAuthorizationFixture();
        fixture.Repository.Setup(item => item.GetMetadata(42)).Returns(new RegistrationFormAssetMetadata(
            42, "system.png", "image/png", "system-hash", DateTime.UtcNow, DateTime.UtcNow, 1, string.Empty));
        fixture.Repository.Setup(item => item.IsReferencedByActiveDraft(42, 1, string.Empty)).Returns(true);

        Assert.IsNull(fixture.Authorization.GetAuthorizedMetadata(42, 2, string.Empty));
        fixture.Repository.Verify(item => item.IsReferencedByActiveDraft(42, 2, string.Empty), Times.Once);
    }

    [TestMethod]
    public void AssetAuthorization_AllowsAnAssetReferencedByTheTargetActiveDraft()
    {
        var fixture = CreateAssetAuthorizationFixture();
        fixture.Repository.Setup(item => item.GetMetadata(42)).Returns(new RegistrationFormAssetMetadata(
            42, "system.png", "image/png", "system-hash", DateTime.UtcNow, DateTime.UtcNow, 1, string.Empty));
        fixture.Repository.Setup(item => item.IsReferencedByActiveDraft(42, 3, "kids")).Returns(true);

        Assert.IsNotNull(fixture.Authorization.GetAuthorizedMetadata(42, 3, "kids"));
    }

    [TestMethod]
    public void AssetAuthorization_PreservesLegacyAssetsOnlyWhenEffectivelyReferenced()
    {
        var fixture = CreateAssetAuthorizationFixture();
        fixture.Repository.Setup(item => item.GetMetadata(42)).Returns(new RegistrationFormAssetMetadata(
            42, "legacy-effective.png", "image/png", "legacy-effective-hash", DateTime.UtcNow, DateTime.UtcNow));
        fixture.Repository.Setup(item => item.GetMetadata(43)).Returns(new RegistrationFormAssetMetadata(
            43, "legacy-draft.png", "image/png", "legacy-draft-hash", DateTime.UtcNow, DateTime.UtcNow));
        fixture.Repository.Setup(item => item.GetMetadata(44)).Returns(new RegistrationFormAssetMetadata(
            44, "legacy-orphan.png", "image/png", "legacy-orphan-hash", DateTime.UtcNow, DateTime.UtcNow));
        fixture.Repository.Setup(item => item.IsReferencedBySettings(
                42, It.IsAny<IReadOnlyList<SettingSource>>())).Returns(true);
        fixture.Repository.Setup(item => item.IsReferencedByActiveDraft(43, 3, "kids")).Returns(true);

        Assert.IsNotNull(fixture.Authorization.GetAuthorizedMetadata(42, 3, "kids"));
        Assert.IsNotNull(fixture.Authorization.GetAuthorizedMetadata(43, 3, "kids"));
        Assert.IsNull(fixture.Authorization.GetAuthorizedMetadata(44, 3, "kids"));
    }

    [TestMethod]
    public void AssetEndpoint_ReturnsNotModifiedForMatchingEtag()
    {
        var repository = new Mock<IRegistrationFormAssetRepository>();
        repository.Setup(item => item.IsPubliclyReferenced(42)).Returns(true);
        repository.Setup(item => item.GetMetadata(42)).Returns(new RegistrationFormAssetMetadata(
            42, "header.png", "image/png", "abc123", DateTime.UtcNow, DateTime.UtcNow));
        repository.Setup(item => item.Get(42)).Returns(new RegistrationFormAsset(
            42, "header.png", "image/png", [1], "abc123", DateTime.UtcNow, DateTime.UtcNow));
        var context = new DefaultHttpContext();
        context.Request.Headers.IfNoneMatch = "W/\"abc123\"";
        var controller = new RegistrationFormAssetsController(repository.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };

        var result = (StatusCodeResult)controller.Get(42);

        Assert.AreEqual(StatusCodes.Status304NotModified, result.StatusCode);
        repository.Verify(item => item.Get(42), Times.Never);
    }

    [TestMethod]
    public void SharedLayoutUsesAssetRouteWithoutEmbeddingContent()
    {
        var root = FindRepositoryRoot();
        var layout = File.ReadAllText(Path.Combine(root, "src/Clc.PatronRegistration.Web/Views/Shared/_Layout.cshtml"));

        StringAssert.Contains(layout, "Settings?.HeaderImageAssetId");
        StringAssert.Contains(layout, "RegistrationFormAsset");
        StringAssert.Contains(layout, "PreviewRegistrationFormAsset");
        Assert.IsFalse(layout.Contains("Convert.ToBase64String", StringComparison.Ordinal));
        Assert.IsFalse(layout.Contains("HeaderImageUrl", StringComparison.Ordinal));
        Assert.IsFalse(layout.Contains("LegacyUrl", StringComparison.Ordinal));
    }

    [TestMethod]
    public void PreviewProvider_ResolvesStagedHeaderAssetThroughNormalSettingOverlay()
    {
        var draft = new SettingDraft(7, 3, string.Empty, 0, DraftStatus.Active,
            [new SettingMutation("header_image_asset_id", DraftOperation.Upsert, "42")]);
        var preview = new PreviewSettingProvider(draft, 3, new TestCache(), 1);

        Assert.AreEqual(42, preview.HeaderImageAssetId);
    }

    [TestMethod]
    public void SettingsImageRow_ReportsMissingDatabaseAssetWithoutLegacyFallback()
    {
        var root = FindRepositoryRoot();
        var row = File.ReadAllText(Path.Combine(root, "src/Clc.PatronRegistration.Web/Views/Settings/_SettingRow.cshtml"));

        StringAssert.Contains(row, "The configured uploaded image is missing.");
        StringAssert.Contains(row, "The staged uploaded image is missing.");
        StringAssert.Contains(row, "class=\"image-setting\"");
        StringAssert.Contains(row, "class=\"image-upload-trigger\"");
        StringAssert.Contains(row, "class=\"image-choose-another\"");
        StringAssert.Contains(row, "class=\"image-undo-pending\"");
        StringAssert.Contains(row, "data-image-inherited-missing=");
        Assert.AreEqual(1, row.Split("data-image-current", StringSplitOptions.None).Length - 1);
        Assert.IsFalse(row.Contains("image-value-editor", StringComparison.Ordinal));
        Assert.IsFalse(row.Contains("image-edit-current", StringComparison.Ordinal));
        Assert.IsFalse(row.Contains("legacy", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(row.Contains("LegacyImageUrl", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SettingsImageRow_DistinguishesInheritedMissingAssetFromNoInheritedImage()
    {
        var root = FindRepositoryRoot();
        var row = File.ReadAllText(Path.Combine(root, "src/Clc.PatronRegistration.Web/Views/Settings/_SettingRow.cshtml"));

        StringAssert.Contains(row, "Model.HasInheritedValue && Model.InheritedAssetMissing");
        StringAssert.Contains(row, "The inherited uploaded image is missing.");
        StringAssert.Contains(row, "Use the inherited image setting.");
        StringAssert.Contains(row, "No image will be configured.");
        Assert.IsTrue(row.IndexOf("The inherited uploaded image is missing.", StringComparison.Ordinal)
            < row.IndexOf("No image will be configured.", StringComparison.Ordinal));
    }

    [TestMethod]
    public void AssetEndpointIsAnonymousButDoesNotExposeAdministrativeOperations()
    {
        var endpoint = typeof(RegistrationFormAssetsController).GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), true);
        Assert.AreEqual(1, endpoint.Length);
        Assert.IsFalse(typeof(RegistrationFormAssetsController).GetMethods()
            .Any(method => method.Name.Contains("Upload", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void SettingsAssetEndpointIsAuthenticatedAndPreviewAssetRouteIsScopedToThePreviewSetting()
    {
        Assert.IsTrue(typeof(SettingsRegistrationFormAssetsController).GetCustomAttributes(
            typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true).Length > 0);
        var previewMethod = typeof(PreviewController).GetMethod(nameof(PreviewController.Asset))!;
        Assert.IsNotNull(previewMethod.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute), true).SingleOrDefault());
    }

    private static (RegistrationFormAssetAuthorization Authorization, Mock<IRegistrationFormAssetRepository> Repository)
        CreateAssetAuthorizationFixture()
    {
        var organizations = new List<OrganizationsGetRow>
        {
            new() { OrganizationID = 1, OrganizationCodeID = 1, Name = "System" },
            new() { OrganizationID = 2, OrganizationCodeID = 2, Name = "Own library" },
            new() { OrganizationID = 3, OrganizationCodeID = 3, ParentOrganizationID = 2, Name = "Own branch" },
            new() { OrganizationID = 8, OrganizationCodeID = 2, Name = "Other library" }
        };
        var cache = new Mock<ICache>();
        cache.SetupGet(item => item.OrganizationCache).Returns(organizations);
        var repository = new Mock<IRegistrationFormAssetRepository>();
        var authorization = new RegistrationFormAssetAuthorization(
            repository.Object, cache.Object, Options.Create(new SettingsAdministrationOptions { SystemOrganizationId = 1 }));
        return (authorization, repository);
    }

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
