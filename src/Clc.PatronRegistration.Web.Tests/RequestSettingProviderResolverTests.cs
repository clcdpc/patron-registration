using System.Security.Claims;
using Clc.PatronRegistration.Configuration;
using Clc.PatronRegistration.Helpers;
using Clc.PatronRegistration.Web.Settings;
using Clc.Polaris.Api.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Moq;

namespace Clc.PatronRegistration.Tests;

[TestClass]
public class RequestSettingProviderResolverTests
{
    [TestMethod]
    public void SettingsAdmin_UsesSelectedScopeAndFormForEffectivePageSettings()
    {
        var cache = new TestCache
        {
            SettingsCache =
            [
                new() { OrganizationID = 2, FormCode = string.Empty, Setting = "header_image_asset_id", Value = "20" },
                new() { OrganizationID = 2, FormCode = string.Empty, Setting = "css_file", Value = "library.css" },
                new() { OrganizationID = 3, FormCode = string.Empty, Setting = "header_image_asset_id", Value = "30" },
                new() { OrganizationID = 3, FormCode = "kids", Setting = "header_image_asset_id", Value = "31" },
                new() { OrganizationID = 3, FormCode = "kids", Setting = "css_file", Value = "kids.css" }
            ]
        };
        var branding = new SettingsPageBrandingContextAccessor();
        branding.Set(2, 2);
        var authorization = new Mock<ISettingsAuthorizationService>();
        authorization.Setup(service => service.CanManage(It.IsAny<ClaimsPrincipal>(), 3, false)).Returns(true);
        var forms = new Mock<IFormCodeAvailabilityService>();
        forms.Setup(service => service.IsAvailable(3, "kids")).Returns(true);
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?organizationId=3&formCode=kids");
        var resolver = CreateResolver(cache, branding, authorization.Object, forms.Object);

        var settings = resolver.Resolve(context);

        Assert.AreEqual(3, settings.OrganizationId);
        Assert.AreEqual(2, settings.LibraryId);
        Assert.AreEqual("kids", settings.FormCode);
        Assert.AreEqual(31, settings.HeaderImageAssetId);
        Assert.AreEqual("kids.css", settings.CssFile);
    }

    [TestMethod]
    public void SettingsAdmin_DoesNotUseUnauthorizedSelectedScope()
    {
        var cache = new TestCache
        {
            SettingsCache =
            [
                new() { OrganizationID = 2, FormCode = string.Empty, Setting = "header_image_asset_id", Value = "20" },
                new() { OrganizationID = 3, FormCode = string.Empty, Setting = "header_image_asset_id", Value = "30" }
            ]
        };
        var branding = new SettingsPageBrandingContextAccessor();
        branding.Set(2, 2);
        var authorization = new Mock<ISettingsAuthorizationService>();
        authorization.Setup(service => service.CanManage(It.IsAny<ClaimsPrincipal>(), 3, false)).Returns(false);
        var forms = new Mock<IFormCodeAvailabilityService>(MockBehavior.Strict);
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?organizationId=3");
        var resolver = CreateResolver(cache, branding, authorization.Object, forms.Object);

        var settings = resolver.Resolve(context);

        Assert.AreEqual(2, settings.OrganizationId);
        Assert.AreEqual(string.Empty, settings.FormCode);
        Assert.AreEqual(20, settings.HeaderImageAssetId);
        forms.VerifyNoOtherCalls();
    }

    private static RequestSettingProviderResolver CreateResolver(
        ICache cache,
        ISettingsPageBrandingContextAccessor branding,
        ISettingsAuthorizationService authorization,
        IFormCodeAvailabilityService forms)
    {
        return new RequestSettingProviderResolver(
            new PreviewRequestContextAccessor(),
            branding,
            authorization,
            forms,
            cache,
            Options.Create(new SettingsAdministrationOptions { SystemOrganizationId = 1 }),
            new Mock<IRegistrationConfiguration>().Object);
    }
}
