namespace Clc.PatronRegistration.Tests;

[TestClass]
public class SettingsAuditViewTests
{
    [TestMethod]
    public void CollapsedSummary_HasSixHeadingsInUsefulOrder()
    {
        var headings = Between(View(), "<div class=\"audit-column-headings\"", "</div>");

        AssertOrdered(headings, ">When<", ">Staff member<", ">Activity<", ">Setting<", ">Target<", ">Result<");
        Assert.AreEqual(6, Count(headings, "<span>"));
        Assert.IsFalse(headings.Contains("Previous", StringComparison.Ordinal));
        Assert.IsFalse(headings.Contains("New", StringComparison.Ordinal));
        Assert.IsFalse(headings.Contains("Request", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CollapsedSummary_PresentsSettingAndAccessibleNotApplicableFallback()
    {
        var summary = Between(View(), "<summary class=\"audit-event-summary\">", "</summary>");

        AssertOrdered(summary, "data-label=\"Activity\"", "data-label=\"Setting\"", "data-label=\"Target\"");
        StringAssert.Contains(summary, "@entry.Setting");
        StringAssert.Contains(summary, "aria-label=\"Not applicable\">—</span>");
    }

    [TestMethod]
    public void ValuesAndRequestMetadata_AreNotPermanentSummaryFields()
    {
        var view = View();
        var summary = Between(view, "<summary class=\"audit-event-summary\">", "</summary>");
        var expanded = Between(view, "<div class=\"audit-event-content\">", "</details>");

        Assert.IsFalse(summary.Contains("Previous value", StringComparison.Ordinal));
        Assert.IsFalse(summary.Contains("New value", StringComparison.Ordinal));
        Assert.IsFalse(summary.Contains("Correlation ID", StringComparison.Ordinal));
        Assert.IsFalse(summary.Contains("IP address", StringComparison.Ordinal));
        StringAssert.Contains(expanded, "<dt>Previous value</dt>");
        StringAssert.Contains(expanded, "<dt>New value</dt>");
        StringAssert.Contains(expanded, "<summary>Technical details</summary>");
    }

    [TestMethod]
    public void SearchPlaceholder_AccuratelyDescribesRepositorySearchFields()
    {
        var view = View();

        StringAssert.Contains(view, "placeholder=\"Search staff, event, form, or setting key\"");
        Assert.IsFalse(view.Contains("setting, or value", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Styles_DefineSixDesktopColumnsAndResponsiveSettingLabels()
    {
        var css = ReadWebFile("wwwroot", "css", "settings.css");
        var columns = Between(css, "--audit-columns:", ";");

        Assert.AreEqual(6, Count(columns, "minmax(") + Count(columns, "5.5rem"));
        StringAssert.Contains(css, "content: attr(data-label)");
        StringAssert.Contains(css, "@media (max-width: 72rem)");
        StringAssert.Contains(css, "grid-template-columns: minmax(0,1fr) minmax(0,1fr)");
        StringAssert.Contains(css, "@media (max-width: 36rem)");
    }

    private static string View() => ReadWebFile("Views", "Settings", "Audit.cshtml");

    private static string ReadWebFile(params string[] path) =>
        File.ReadAllText(Path.Combine([AppContext.BaseDirectory, "..", "..", "..", "..", "Clc.PatronRegistration.Web", .. path]));

    private static string Between(string value, string start, string end)
    {
        var startIndex = value.IndexOf(start, StringComparison.Ordinal);
        Assert.IsTrue(startIndex >= 0, $"Expected to find {start}");
        var endIndex = value.IndexOf(end, startIndex, StringComparison.Ordinal);
        Assert.IsTrue(endIndex >= 0, $"Expected to find {end}");
        return value[startIndex..endIndex];
    }

    private static void AssertOrdered(string value, params string[] terms)
    {
        var previous = -1;
        foreach (var term in terms)
        {
            var current = value.IndexOf(term, StringComparison.Ordinal);
            Assert.IsTrue(current > previous, $"Expected {term} in order.");
            previous = current;
        }
    }

    private static int Count(string value, string term) =>
        (value.Length - value.Replace(term, string.Empty, StringComparison.Ordinal).Length) / term.Length;
}
