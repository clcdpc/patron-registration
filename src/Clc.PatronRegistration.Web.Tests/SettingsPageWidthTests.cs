namespace Clc.PatronRegistration.Tests;

[TestClass]
public class SettingsPageWidthTests
{
    [TestMethod]
    public void SettingsViewStart_AssignsBodyClassLayoutAndStylesheetForAllSettingsViews()
    {
        var viewStart = ReadWebFile("Views", "Settings", "_ViewStart.cshtml");

        StringAssert.Contains(viewStart, "Layout = \"_Layout\"");
        StringAssert.Contains(viewStart, "ViewData[\"BodyClass\"] = \"settings-administration-page\"");
        StringAssert.Contains(viewStart, "ViewData[\"PageStylesheet\"] = \"~/css/settings.css\"");
    }

    [TestMethod]
    public void SharedLayout_RendersOptionalEncodedBodyClassWithoutRouteChecks()
    {
        var layout = ReadWebFile("Views", "Shared", "_Layout.cshtml");

        StringAssert.Contains(layout, "var bodyClass = ViewData[\"BodyClass\"] as string;");
        StringAssert.Contains(layout, "<body class=\"@bodyClass\" aria-live=\"polite\">");
        Assert.IsFalse(layout.Contains("Html.Raw", StringComparison.Ordinal));
        Assert.IsFalse(layout.Contains("SettingsController", StringComparison.Ordinal));
        Assert.IsFalse(layout.Contains("ControllerName", StringComparison.Ordinal));
    }


    [TestMethod]
    public void SharedLayout_LoadsPageStylesheetAfterCustomStylesheetAndBeforeBody()
    {
        var layout = ReadWebFile("Views", "Shared", "_Layout.cshtml");
        var customStylesheet = layout.IndexOf("Settings.CssFile", StringComparison.Ordinal);
        var pageStylesheet = layout.IndexOf("href=\"@pageStylesheet\"", StringComparison.Ordinal);
        var renderBody = layout.IndexOf("@RenderBody()", StringComparison.Ordinal);

        Assert.IsTrue(customStylesheet >= 0);
        Assert.IsTrue(pageStylesheet > customStylesheet);
        Assert.IsTrue(renderBody > pageStylesheet);
    }

    [TestMethod]
    public void OrdinaryAndRegistrationViews_DoNotAssignSettingsBodyClass()
    {
        var rootViewStart = ReadWebFile("Views", "_ViewStart.cshtml");
        var registration = ReadWebFile("Views", "Registration", "Create.cshtml");

        Assert.IsFalse(rootViewStart.Contains("settings-administration-page", StringComparison.Ordinal));
        Assert.IsFalse(registration.Contains("settings-administration-page", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SettingsCss_RemovesMaximumOnlyWithinSettingsBodyAndAvoidsWidthHacks()
    {
        var css = ReadWebFile("wwwroot", "css", "settings.css");
        var bodyRule = Rule(css, "body.settings-administration-page");
        var containerRule = Rule(css, "body.settings-administration-page #regFormContainer");

        StringAssert.Contains(bodyRule, "max-width: none");
        StringAssert.Contains(bodyRule, "padding-inline: clamp(.75rem, 1vw, 1.25rem)");
        StringAssert.Contains(containerRule, "max-width: none");
        StringAssert.Contains(containerRule, "min-width: 0");
        StringAssert.Contains(containerRule, "width: 100%");
        Assert.IsFalse(css.Contains("100vw", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(css.Contains("margin-left: -", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(css.Contains("margin-right: -", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void GlobalBodyLimitAndHelpReadingWidthRemainUnchanged()
    {
        var siteCss = ReadWebFile("wwwroot", "css", "site.css");
        var settingsCss = ReadWebFile("wwwroot", "css", "settings.css");

        StringAssert.Contains(Rule(siteCss, "body"), "max-width: 1200px");
        StringAssert.Contains(Rule(settingsCss, ".settings-help"), "max-width: 58rem");
        Assert.IsFalse(Rule(settingsCss, ".settings-audit").Contains("max-width", StringComparison.Ordinal));
    }

    [TestMethod]
    public void AuditResponsiveLayoutsRemainAvailable()
    {
        var css = ReadWebFile("wwwroot", "css", "settings.css");

        StringAssert.Contains(css, "@media (max-width: 72rem)");
        StringAssert.Contains(css, "grid-template-columns: minmax(0,1fr) minmax(0,1fr)");
        StringAssert.Contains(css, "@media (max-width: 36rem)");
    }

    private static string Rule(string css, string selector)
    {
        var start = css.IndexOf(selector + " {", StringComparison.Ordinal);
        Assert.IsTrue(start >= 0, $"Expected CSS rule for {selector}.");
        var end = css.IndexOf('}', start);
        Assert.IsTrue(end > start);
        return css[start..end];
    }

    private static string ReadWebFile(params string[] path) =>
        File.ReadAllText(Path.Combine([AppContext.BaseDirectory, "..", "..", "..", "..", "Clc.PatronRegistration.Web", .. path]));
}
