using System.Text;
using System.Reflection;
using System.Diagnostics;
using Clc.Melissa;
using Clc.PatronRegistration.Administration;
using Clc.PatronRegistration.Configuration;
using Clc.PatronRegistration.Data;
using Clc.PatronRegistration.Web.Controllers;
using Clc.PatronRegistration.Web.Models;
using Clc.PatronRegistration.Web.Settings;
using Clc.Polaris.Api;
using Clc.Polaris.Api.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Clc.PatronRegistration.Tests;

[TestClass]
public sealed class RegistrationBranchFragmentRenderingTests
{
    [TestMethod]
    public async Task ChangeBranch_RendersOnlyTheLayoutFreeFragmentWithSelectedBranding()
    {
        using var fixture = BranchFixture.Create(headerImageAssetId: 42, cssFile: "~/custom/branch.css");

        var result = fixture.Controller.ChangeBranch(
            fixture.SubmittedRegistration(), 2, forceDl: true, agreementAccepted: true);

        Assert.IsInstanceOfType<PartialViewResult>(result);
        var view = (PartialViewResult)result;
        Assert.AreEqual("_RegistrationForm", view.ViewName);
        var model = (Registration)view.Model!;
        Assert.IsTrue(model.BypassAgreement);
        Assert.IsFalse(model.ShouldDisplayAgreement);
        var markup = await fixture.RenderAsync(view);

        AssertLayoutFree(markup);
        StringAssert.Contains(markup, "id=\"registration-form-fragment\"");
        StringAssert.Contains(markup, "data-registration-css-url=\"https://example.test/custom/branch.css\"");
        StringAssert.Contains(markup, "data-registration-header-image-url=\"https://example.test/assets/42\"");
        Assert.IsFalse(markup.Contains("id=\"registration-configured-stylesheet\"", StringComparison.Ordinal));
        Assert.IsFalse(markup.Contains("id=\"registration-header-image\"", StringComparison.Ordinal));
        Assert.IsFalse(markup.Contains("1234", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ChangeBranch_RendersEmptyBrandingMetadataWhenSelectedBranchHasNoBranding()
    {
        using var fixture = BranchFixture.Create(headerImageAssetId: null, cssFile: string.Empty);

        var result = fixture.Controller.ChangeBranch(
            fixture.SubmittedRegistration(), 2, agreementAccepted: true);

        Assert.IsInstanceOfType<PartialViewResult>(result);
        var view = (PartialViewResult)result;
        var markup = await fixture.RenderAsync(view);

        StringAssert.Contains(markup, "data-registration-css-url=\"\"");
        StringAssert.Contains(markup, "data-registration-header-image-url=\"\"");
        Assert.IsFalse(markup.Contains("id=\"registration-configured-stylesheet\"", StringComparison.Ordinal));
        Assert.IsFalse(markup.Contains("id=\"registration-header-image\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ChangeBranch_RendersSelectedBranchRequirednessAndLabel()
    {
        using var fixture = BranchFixture.Create(
            headerImageAssetId: null,
            cssFile: string.Empty,
            selectedUser5Required: true);

        var result = fixture.Controller.ChangeBranch(
            fixture.SubmittedRegistration(), 2, agreementAccepted: true);

        var markup = await fixture.RenderAsync((PartialViewResult)result);

        StringAssert.Contains(markup, "Branch responsible person");
        StringAssert.Contains(markup, "name=\"User5\"");
        StringAssert.Contains(markup, "aria-required=\"true\"");
    }

    [TestMethod]
    public async Task ChangeBranch_RendersPersistedTeacherAndSchoolState()
    {
        using var fixture = BranchFixture.Create(
            headerImageAssetId: null,
            cssFile: string.Empty,
            schoolInfoFormat: "uapl");
        var registration = fixture.SubmittedRegistration();
        registration.IsTeacher = true;
        registration.User1 = "Barrington Elementary School";

        var result = fixture.Controller.ChangeBranch(registration, 2, agreementAccepted: true);

        var view = (PartialViewResult)result;
        var model = (Registration)view.Model!;
        Assert.IsTrue(model.IsTeacher);
        Assert.AreEqual("Barrington Elementary School", model.User1);

        var markup = await fixture.RenderAsync(view);

        StringAssert.Contains(markup, "name=\"IsTeacher\"");
        StringAssert.Contains(markup, "id=\"User1\" name=\"User1\" type=\"hidden\" value=\"Barrington Elementary School\"");
        StringAssert.Contains(markup, "id=\"IsTeacher\" name=\"IsTeacher\"");
        StringAssert.Contains(markup, "checked=\"checked\"");
    }

    [TestMethod]
    public async Task SettingRow_SensitiveSharedDraftRemoveOverrideUsesInheritedPresenceWithoutRenderingSecrets()
    {
        using var fixture = BranchFixture.Create(headerImageAssetId: null, cssFile: string.Empty);
        var definition = new SettingCatalog().All.Single(setting => setting.Key == "postmark_api_key");
        const string liveSecret = "live-local-secret";
        const string inheritedSecret = "inherited-secret";
        SettingRowViewModel Row(bool hasInherited) => new(
            "setting-sensitive-draft",
            definition,
            new ResolvedSetting(definition.Key, liveSecret, 3, "Branch", string.Empty, true, liveSecret, false),
            null,
            DraftOperation.RemoveOverride,
            99,
            SourceDescription: "Branch",
            InheritedValue: hasInherited ? inheritedSecret : null,
            HasInheritedValue: hasInherited,
            InheritedSourceDescription: hasInherited ? "Main Library" : null);

        var noInheritedMarkup = await fixture.RenderSettingRowAsync(Row(hasInherited: false));
        var inheritedMarkup = await fixture.RenderSettingRowAsync(Row(hasInherited: true));
        var noInheritedText = System.Net.WebUtility.HtmlDecode(noInheritedMarkup);
        var inheritedText = System.Net.WebUtility.HtmlDecode(inheritedMarkup);

        StringAssert.Contains(noInheritedMarkup, "<span class=\"summary-value\" title=\"Not configured\">Not configured</span>");
        StringAssert.Contains(inheritedMarkup, "<span class=\"summary-value\" title=\"Configured\">Configured</span>");
        foreach (var text in new[] { noInheritedText, inheritedText })
        {
            StringAssert.Contains(text, "Shared draft — use inherited value");
        }
        foreach (var markup in new[] { noInheritedMarkup, inheritedMarkup })
        {
            Assert.IsFalse(markup.Contains(liveSecret, StringComparison.Ordinal));
            Assert.IsFalse(markup.Contains(inheritedSecret, StringComparison.Ordinal));
        }
    }

    private static void AssertLayoutFree(string markup)
    {
        Assert.IsFalse(markup.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(markup.Contains("<html", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(markup.Contains("<head", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(markup.Contains("<body", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(markup.Contains("id=\"regFormContainer\"", StringComparison.Ordinal));
        Assert.AreEqual(1, markup.Split("id=\"registration-form-fragment\"", StringSplitOptions.None).Length - 1);
    }

    private sealed class BranchFixture : IDisposable
    {
        private readonly ServiceProvider provider;
        private readonly ISettingProvider routeSettings;
        private readonly ISettingProvider selectedSettings;

        public RegistrationController Controller { get; }
        public Mock<IDbHelper> Db { get; }

        private BranchFixture(
            ServiceProvider provider,
            ISettingProvider routeSettings,
            ISettingProvider selectedSettings,
            RegistrationController controller,
            Mock<IDbHelper> db)
        {
            this.provider = provider;
            this.routeSettings = routeSettings;
            this.selectedSettings = selectedSettings;
            Controller = controller;
            Db = db;
        }

        public static BranchFixture Create(
            int? headerImageAssetId,
            string cssFile,
            bool selectedUser5Required = false,
            string schoolInfoFormat = "")
        {
            var routeSettings = Settings(2, branchSelectionEnabled: true);
            var selectedSettings = Settings(4, branchSelectionEnabled: false);
            selectedSettings.SetupGet(value => value.HeaderImageAssetId).Returns(headerImageAssetId);
            selectedSettings.SetupGet(value => value.CssFile).Returns(cssFile);
            selectedSettings.SetupGet(value => value.SchoolInfoFormat).Returns(schoolInfoFormat);
            selectedSettings.SetupGet(value => value.DisplayResponsiblePersonField).Returns(selectedUser5Required);
            selectedSettings.Setup(value => value.GetFieldRequired(nameof(Registration.User5)))
                .Returns(selectedUser5Required);
            selectedSettings.Setup(value => value.GetFieldLabel(nameof(Registration.User5)))
                .Returns("Branch responsible person");

            var organizations = new List<OrganizationsGetRow>
            {
                Organization(1, null, 1),
                Organization(2, 1, 2),
                Organization(3, 2),
                Organization(4, 2)
            };

            var db = new Mock<IDbHelper>();
            db.Setup(value => value.GetSelfRegistrationOrganizations(null)).Returns(organizations);
            db.Setup(value => value.GetGendersToOrganizations(4)).Returns([]);
            db.Setup(value => value.GetPickupBranches(2)).Returns([]);

            var scopeResolver = new Mock<IRegistrationScopeResolver>();
            scopeResolver.Setup(value => value.ResolveForSubmission(
                    It.IsAny<HttpContext>(), routeSettings.Object, 4))
                .Returns(new RegistrationScopeResolution(true, selectedSettings.Object));

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddControllersWithViews()
                .AddApplicationPart(typeof(RegistrationController).Assembly);
            var diagnostics = new DiagnosticListener("RegistrationBranchFragmentRenderingTests");
            services.AddSingleton<DiagnosticSource>(diagnostics);
            services.AddSingleton<DiagnosticListener>(diagnostics);
            services.AddHttpContextAccessor();
            services.AddSingleton<ISettingProvider>(routeSettings.Object);
            services.AddSingleton<IPreviewRequestContextAccessor>(new PreviewRequestContextAccessor());

            var httpContext = new DefaultHttpContext();
            httpContext.Request.Scheme = "https";
            httpContext.Request.Host = new HostString("example.test");
            httpContext.Request.Method = HttpMethods.Post;
            httpContext.Request.RouteValues["orgId"] = 2;
            httpContext.Request.RouteValues["formCode"] = string.Empty;

            var actionContext = new ActionContext(
                httpContext,
                new RouteData(),
                new ControllerActionDescriptor
                {
                    ControllerTypeInfo = typeof(RegistrationController).GetTypeInfo(),
                    ActionName = nameof(RegistrationController.ChangeBranch)
                },
                new ModelStateDictionary());
            var urlHelper = UrlHelper(actionContext);
            var urlHelperFactory = new Mock<IUrlHelperFactory>();
            urlHelperFactory.Setup(value => value.GetUrlHelper(It.IsAny<ActionContext>()))
                .Returns(urlHelper.Object);
            services.AddSingleton(urlHelperFactory.Object);

            var provider = services.BuildServiceProvider();
            httpContext.RequestServices = provider;
            provider.GetRequiredService<IHttpContextAccessor>().HttpContext = httpContext;

            var controller = new RegistrationController(
                Mock.Of<IPapiClient>(), db.Object, routeSettings.Object,
                Mock.Of<IEmailSenderFactory>(), Mock.Of<IMelissaClientFactory>(),
                provider.GetRequiredService<Microsoft.AspNetCore.Mvc.ModelBinding.Validation.IObjectModelValidator>(),
                scopeResolver.Object)
            {
                ControllerContext = new ControllerContext(actionContext)
            };

            return new BranchFixture(provider, routeSettings.Object, selectedSettings.Object, controller, db);
        }

        public Registration SubmittedRegistration() => new(routeSettings)
        {
            PatronBranchID = 4,
            NameFirst = "Earlier",
            NameLast = "Patron",
            EmailAddress = "earlier@example.test",
            Birthdate = new DateTime(1990, 1, 1),
            StreetOne = "1 Main St",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            Password = "1234",
            Password2 = "1234"
        };

        public async Task<string> RenderAsync(PartialViewResult result)
        {
            var actionContext = Controller.ControllerContext;
            var viewEngine = provider.GetRequiredService<IRazorViewEngine>();
            var viewLookup = viewEngine.GetView(
                executingFilePath: null,
                viewPath: $"~/Views/Registration/{result.ViewName}.cshtml",
                isMainPage: false);
            Assert.IsTrue(viewLookup.Success,
                $"Could not find view: {string.Join(" | ", viewLookup.SearchedLocations ?? [])}");

            var viewData = new ViewDataDictionary<Registration>(
                provider.GetRequiredService<IModelMetadataProvider>(), actionContext.ModelState)
            {
                Model = (Registration)result.Model!
            };
            var tempData = new TempDataDictionary(
                actionContext.HttpContext,
                provider.GetRequiredService<ITempDataProvider>());
            await using var output = new MemoryStream();
            await using var writer = new StreamWriter(output, Encoding.UTF8, leaveOpen: true);
            var viewContext = new ViewContext(
                actionContext, viewLookup.View!, viewData, tempData, writer, new HtmlHelperOptions());

            await viewLookup.View!.RenderAsync(viewContext);
            await writer.FlushAsync();
            return Encoding.UTF8.GetString(output.ToArray());
        }

        public async Task<string> RenderSettingRowAsync(SettingRowViewModel model)
        {
            var actionContext = Controller.ControllerContext;
            var viewEngine = provider.GetRequiredService<IRazorViewEngine>();
            var viewLookup = viewEngine.GetView(null, "~/Views/Settings/_SettingRow.cshtml", isMainPage: false);
            Assert.IsTrue(viewLookup.Success,
                $"Could not find view: {string.Join(" | ", viewLookup.SearchedLocations ?? [])}");

            var viewData = new ViewDataDictionary<SettingRowViewModel>(
                provider.GetRequiredService<IModelMetadataProvider>(), actionContext.ModelState)
            {
                Model = model
            };
            var tempData = new TempDataDictionary(
                actionContext.HttpContext,
                provider.GetRequiredService<ITempDataProvider>());
            await using var output = new MemoryStream();
            await using var writer = new StreamWriter(output, Encoding.UTF8, leaveOpen: true);
            var viewContext = new ViewContext(
                actionContext, viewLookup.View!, viewData, tempData, writer, new HtmlHelperOptions());

            await viewLookup.View!.RenderAsync(viewContext);
            await writer.FlushAsync();
            return Encoding.UTF8.GetString(output.ToArray());
        }

        public void Dispose() => provider.Dispose();

        private static Mock<IUrlHelper> UrlHelper(ActionContext actionContext)
        {
            var helper = new Mock<IUrlHelper>();
            helper.SetupGet(value => value.ActionContext).Returns(actionContext);
            helper.Setup(value => value.Content(It.IsAny<string>()))
                .Returns((string path) => path.StartsWith("~/", StringComparison.Ordinal) ? path[1..] : path);
            helper.Setup(value => value.Action(It.IsAny<UrlActionContext>()))
                .Returns((UrlActionContext context) => $"/{context.Controller}/{context.Action}");
            helper.Setup(value => value.RouteUrl(It.IsAny<UrlRouteContext>()))
                .Returns((UrlRouteContext context) =>
                {
                    var values = new RouteValueDictionary(context.Values);
                    return context.RouteName == "RegistrationFormAsset"
                        ? $"https://example.test/assets/{values["id"]}"
                        : null;
                });
            return helper;
        }

        private static Mock<ISettingProvider> Settings(
            int organizationId, bool branchSelectionEnabled)
        {
            var settings = new Mock<ISettingProvider>();
            settings.SetupGet(value => value.OrganizationId).Returns(organizationId);
            settings.SetupGet(value => value.LibraryId).Returns(2);
            settings.SetupGet(value => value.FormCode).Returns(string.Empty);
            settings.SetupGet(value => value.EnablePatronBranchSelectOption).Returns(branchSelectionEnabled);
            settings.SetupGet(value => value.DisableBranch).Returns(false);
            settings.SetupGet(value => value.WarningText).Returns(string.Empty);
            settings.SetupGet(value => value.CssFile).Returns(string.Empty);
            settings.SetupGet(value => value.HeaderImageAssetId).Returns((int?)null);
            settings.SetupGet(value => value.ResetSeconds).Returns(0);
            settings.SetupGet(value => value.DriversLicenseButtonEnabledIpAddresses).Returns([]);
            settings.SetupGet(value => value.PhoneNumberFormat).Returns("($1) $2-$3");
            settings.SetupGet(value => value.MelissaDataApiKey).Returns(string.Empty);
            settings.SetupGet(value => value.PostmarkApiKey).Returns(string.Empty);
            settings.Setup(value => value.GetRequiredFields()).Returns([]);
            settings.Setup(value => value.GetFieldRequired(It.IsAny<string>())).Returns(false);
            settings.Setup(value => value.GetFieldLabel(It.IsAny<string>())).Returns("Field");
            return settings;
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
    }
}
