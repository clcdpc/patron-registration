using Clc.PatronRegistration.Administration;
using Clc.PatronRegistration.Configuration;
using Clc.PatronRegistration;

namespace Clc.PatronRegistration.Tests;

[TestClass]
public sealed class SafeHtmlPolicyTests
{
    [TestMethod]
    public void AdministratorHtmlPolicyRetainsFormattingButRemovesActiveContent()
    {
        var definition = new SettingDefinition(
            "registration_form_header", "Registration header", "Header", SettingValueType.ShortString,
            IsHtmlExecutionContext: true);
        var input = "<p><strong>Welcome</strong> <a href=\"https://example.test/help\" target=\"_blank\">help</a></p>" +
            "<script>alert(1)</script><img src=\"https://example.test/logo.png\" onerror=\"alert(2)\">" +
            "<a href=\"javascript:alert(3)\" onclick=\"alert(4)\">bad</a>" +
            "<iframe src=\"https://example.test/frame\"></iframe><object data=\"x\"></object>" +
            "<style>body{background:url(javascript:alert(5))}</style><div id=\"clobber\">text</div>";

        var output = SafeHtmlPolicy.SanitizeForSetting(definition, input);
        var lower = output.ToLowerInvariant();

        StringAssert.Contains(output, "<strong>Welcome</strong>");
        StringAssert.Contains(output, "https://example.test/help");
        StringAssert.Contains(output, "target=\"_blank\"");
        StringAssert.Contains(output, "https://example.test/logo.png");
        Assert.IsFalse(lower.Contains("<script", StringComparison.Ordinal));
        Assert.IsFalse(lower.Contains("onerror", StringComparison.Ordinal));
        Assert.IsFalse(lower.Contains("onclick", StringComparison.Ordinal));
        Assert.IsFalse(lower.Contains("javascript:", StringComparison.Ordinal));
        Assert.IsFalse(lower.Contains("<iframe", StringComparison.Ordinal));
        Assert.IsFalse(lower.Contains("<object", StringComparison.Ordinal));
        Assert.IsFalse(lower.Contains("<style", StringComparison.Ordinal));
        Assert.IsFalse(lower.Contains(" id=", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CatalogMarksNonHtmlSettingsThatReachHtmlExecutionContexts()
    {
        var catalog = new SettingCatalog();

        Assert.AreEqual(SettingValueType.LongString, catalog.All.Single(setting => setting.Key == "warning_text").ValueType);
        foreach (var key in new[] { "warning_text", "registration_text", "registration_form_header", "age_block_text" })
        {
            var definition = catalog.All.Single(setting => setting.Key == key);
            Assert.IsTrue(definition.IsHtmlExecutionContext);
            Assert.IsTrue(SafeHtmlPolicy.IsHtmlExecutionContext(definition));
        }
        Assert.IsFalse(SafeHtmlPolicy.IsHtmlExecutionContext(catalog.All.Single(setting => setting.Key == "age_warning_text")));
    }

    [TestMethod]
    public void PublicSettingProviderSanitizesLegacyPersistedHtmlBeforeRawPageRendering()
    {
        const string malicious = "<p><strong>Keep formatting</strong></p><script>alert(1)</script>" +
            "<img src=\"https://example.test/logo.png\" onerror=\"alert(2)\">";
        var provider = new DbSettingProvider(3, new TestCache
        {
            SettingsCache =
            [
                new RegistrationFormSetting
                {
                    OrganizationID = 3,
                    FormCode = string.Empty,
                    Setting = "registration_form_header",
                    Value = malicious
                }
            ]
        });

        var output = provider.RegistrationHeader;

        StringAssert.Contains(output, "<strong>Keep formatting</strong>");
        Assert.IsFalse(output.Contains("<script", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(output.Contains("onerror", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void JavascriptStringEncodingCannotCloseTheContainingScriptElement()
    {
        var encoded = "</script><script>alert(1)</script>\"'&<".ToJavascriptString();

        Assert.IsFalse(encoded.Contains("</script>", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(encoded, "\\u003C");
    }

    [TestMethod]
    public void StylesheetReferencePolicyRejectsExecutableSchemes()
    {
        Assert.IsTrue(SafeHtmlPolicy.IsSafeStylesheetReference("/css/custom.css"));
        Assert.IsTrue(SafeHtmlPolicy.IsSafeStylesheetReference("https://cdn.example.test/custom.css"));
        Assert.IsFalse(SafeHtmlPolicy.IsSafeStylesheetReference("javascript:alert(1)"));
        Assert.IsFalse(SafeHtmlPolicy.IsSafeStylesheetReference("data:text/css,body{}"));
        Assert.IsFalse(SafeHtmlPolicy.IsSafeStylesheetReference("//cdn.example.test/custom.css"));
    }
}
