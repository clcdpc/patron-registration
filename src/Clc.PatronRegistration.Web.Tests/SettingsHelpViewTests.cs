namespace Clc.PatronRegistration.Tests;

[TestClass]
public class SettingsHelpViewTests
{
    [TestMethod]
    public void Layout_UsesViewTitleWithRegistrationFallback()
    {
        var layout = ReadWebFile("Views", "Shared", "_Layout.cshtml");

        StringAssert.Contains(layout, "ViewData[\"Title\"] as string");
        StringAssert.Contains(layout, "?? \"New Library Card Registration\"");
        Assert.AreEqual(1, Count(layout, "<title>"));
    }

    [TestMethod]
    public void SettingsViews_ProvideExpectedTitlesAndNavigation()
    {
        var expected = new[]
        {
            ("Index.cshtml", "Registration settings"),
            ("Forms.cshtml", "Form codes"),
            ("Audit.cshtml", "Settings audit"),
            ("Help.cshtml", "Settings help")
        };

        foreach (var (file, title) in expected)
        {
            StringAssert.Contains(ReadWebFile("Views", "Settings", file), $"ViewData[\"Title\"] = \"{title}\"");
        }

        var index = ReadWebFile("Views", "Settings", "Index.cshtml");
        StringAssert.Contains(index, "asp-action=\"Help\"");
        StringAssert.Contains(index, "asp-route-organizationId=\"@Model.OrganizationId\"");
        StringAssert.Contains(index, "asp-route-formCode=\"@Model.FormCode\"");

        foreach (var file in new[] { "Forms.cshtml", "Audit.cshtml" })
        {
            var view = ReadWebFile("Views", "Settings", file);
            StringAssert.Contains(view, "<nav aria-label=");
            StringAssert.Contains(view, "asp-action=\"Help\"");
            StringAssert.Contains(view, "asp-action=\"Index\">Back to settings");
        }
    }

    [TestMethod]
    public void HelpView_HasPrimaryNavigationAndAccurateAuditSearchCopy()
    {
        var help = ReadWebFile("Views", "Settings", "Help.cshtml");

        StringAssert.Contains(help, "aria-label=\"Settings help navigation\"");
        StringAssert.Contains(help, ">Back to settings</a>");
        StringAssert.Contains(help, "asp-action=\"Forms\">Form codes</a>");
        StringAssert.Contains(help, "asp-action=\"Audit\">Audit history</a>");
        StringAssert.Contains(help, "staff member's name, form code, setting key");
        Assert.IsFalse(help.Contains("Search by the setting name, staff member, organization, or form", StringComparison.Ordinal));
    }

    private static string ReadWebFile(params string[] path) =>
        File.ReadAllText(Path.Combine([AppContext.BaseDirectory, "..", "..", "..", "..", "Clc.PatronRegistration.Web", .. path]));

    private static int Count(string value, string term) =>
        (value.Length - value.Replace(term, string.Empty, StringComparison.Ordinal).Length) / term.Length;
}
