namespace Clc.PatronRegistration.Tests;

[TestClass]
public class SettingsNavigationTests
{
    [DataTestMethod]
    [DataRow("Index.cshtml", "Index")]
    [DataRow("Audit.cshtml", "Audit")]
    [DataRow("Forms.cshtml", "Forms")]
    [DataRow("Help.cshtml", "Help")]
    public void MainSettingsViews_UseSharedNavigationAndMarkCurrentPage(string file, string action)
    {
        var view = ReadWebFile("Views", "Settings", file);
        var navigation = Between(view, "<nav class=\"settings-navigation\"", "</nav>");

        StringAssert.Contains(navigation, $"asp-action=\"{action}\"");
        StringAssert.Contains(navigation, "aria-current=\"page\"");
        StringAssert.Contains(navigation, ">Settings</a>");
        StringAssert.Contains(navigation, ">Audit history</a>");
        StringAssert.Contains(navigation, ">Form codes</a>");
        StringAssert.Contains(navigation, ">Help</a>");
    }

    [DataTestMethod]
    [DataRow("Index.cshtml")]
    [DataRow("Audit.cshtml")]
    [DataRow("Forms.cshtml")]
    [DataRow("Help.cshtml")]
    public void Navigation_FollowsHeadingContent(string file)
    {
        var view = ReadWebFile("Views", "Settings", file);
        var heading = view.IndexOf("<h1", StringComparison.Ordinal);
        var navigation = view.IndexOf("<nav class=\"settings-navigation\"", StringComparison.Ordinal);

        Assert.IsTrue(heading >= 0);
        Assert.IsTrue(navigation > heading);
    }

    [TestMethod]
    public void SettingsAndHelpLinks_PreserveOnlyTheirExistingValidatedContext()
    {
        var indexNavigation = Between(ReadWebFile("Views", "Settings", "Index.cshtml"),
            "<nav class=\"settings-navigation\"", "</nav>");
        var helpNavigation = Between(ReadWebFile("Views", "Settings", "Help.cshtml"),
            "<nav class=\"settings-navigation\"", "</nav>");

        var indexHelp = Link(indexNavigation, "Help");
        StringAssert.Contains(indexHelp, "asp-route-organizationId=\"@Model.OrganizationId\"");
        StringAssert.Contains(indexHelp, "asp-route-formCode=\"@Model.FormCode\"");
        StringAssert.Contains(helpNavigation, "@if (Model.OrganizationId.HasValue)");
        var contextualSettings = Link(helpNavigation, "Settings");
        StringAssert.Contains(contextualSettings, "asp-route-organizationId=\"@Model.OrganizationId\"");
        StringAssert.Contains(contextualSettings, "asp-route-formCode=\"@Model.FormCode\"");
        StringAssert.Contains(helpNavigation, "<a asp-action=\"Index\">Settings</a>");
    }

    [TestMethod]
    public void NavigationCss_IsSimpleWrappingRightAlignedTextLinksAndAvoidsWidthHacks()
    {
        var css = ReadWebFile("wwwroot", "css", "settings.css");
        var navigation = Rule(css, ".settings-navigation");
        var header = Rule(css, ".settings-header");
        var body = Rule(css, "body.settings-administration-page");

        StringAssert.Contains(navigation, "flex-wrap: wrap");
        StringAssert.Contains(navigation, "width: fit-content");
        StringAssert.Contains(navigation, "background: transparent");
        StringAssert.Contains(navigation, "border: 0");
        StringAssert.Contains(header, "align-items: flex-start");
        StringAssert.Contains(header, "justify-content: space-between");
        StringAssert.Contains(body, "padding-inline: clamp(.75rem, 1vw, 1.25rem)");
        StringAssert.Contains(body, "max-width: none");
        Assert.IsFalse(css.Contains("100vw", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(css.Contains("float:", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(navigation.Contains("position: absolute", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(navigation.Contains("background: #f3f3f3", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(css.Contains("margin-left: -", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(css.Contains("margin-right: -", StringComparison.OrdinalIgnoreCase));
    }

    private static string Link(string navigation, string label)
    {
        var labelIndex = navigation.IndexOf($">{label}</a>", StringComparison.Ordinal);
        Assert.IsTrue(labelIndex >= 0);
        var start = navigation.LastIndexOf("<a ", labelIndex, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0);
        return navigation[start..(labelIndex + label.Length + 5)];
    }

    private static string Rule(string css, string selector)
    {
        var start = css.IndexOf(selector + " {", StringComparison.Ordinal);
        Assert.IsTrue(start >= 0);
        var end = css.IndexOf('}', start);
        return css[start..end];
    }

    private static string Between(string value, string start, string end)
    {
        var startIndex = value.IndexOf(start, StringComparison.Ordinal);
        Assert.IsTrue(startIndex >= 0);
        var endIndex = value.IndexOf(end, startIndex, StringComparison.Ordinal);
        Assert.IsTrue(endIndex > startIndex);
        return value[startIndex..endIndex];
    }

    private static string ReadWebFile(params string[] path) =>
        File.ReadAllText(Path.Combine([AppContext.BaseDirectory, "..", "..", "..", "..", "Clc.PatronRegistration.Web", .. path]));
}
