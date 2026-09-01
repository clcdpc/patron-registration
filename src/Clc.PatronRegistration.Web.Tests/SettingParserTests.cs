using Clc.PatronRegistration.Configuration;

namespace Clc.PatronRegistration.Tests;

[TestClass]
public sealed class SettingParserTests
{
    [DataTestMethod]
    [DataRow(null, BoundedIntegerSettingState.Unconfigured, null)]
    [DataRow("", BoundedIntegerSettingState.Unconfigured, null)]
    [DataRow("0", BoundedIntegerSettingState.Valid, 0)]
    [DataRow("86400", BoundedIntegerSettingState.Valid, 86400)]
    [DataRow("-1", BoundedIntegerSettingState.Invalid, null)]
    [DataRow("86401", BoundedIntegerSettingState.Invalid, null)]
    [DataRow("2147483647", BoundedIntegerSettingState.Invalid, null)]
    [DataRow("999999999999999999999", BoundedIntegerSettingState.Invalid, null)]
    public void ResetSecondsParser_HandlesLegacyAndBoundedValues(
        string? raw, BoundedIntegerSettingState expectedState, int? expectedValue)
    {
        var result = ResetSecondsSettingParser.Parse(raw);

        Assert.AreEqual(expectedState, result.State);
        Assert.AreEqual(expectedValue, result.Value);
    }

    [TestMethod]
    public void ResetSecondsParser_ConvertsOnlySafeValuesToJavaScriptMilliseconds()
    {
        Assert.AreEqual(86_400_000L, ResetSecondsSettingParser.ToJavaScriptMilliseconds(86_400));
        Assert.AreEqual(0L, ResetSecondsSettingParser.ToJavaScriptMilliseconds(int.MaxValue));
        Assert.AreEqual(0L, ResetSecondsSettingParser.ToJavaScriptMilliseconds(-1));
    }

    [DataTestMethod]
    [DataRow(null, DriversLicenseFormatSettingState.Unconfigured)]
    [DataRow("", DriversLicenseFormatSettingState.Unconfigured)]
    [DataRow("barcode", DriversLicenseFormatSettingState.Barcode)]
    [DataRow("MAGSTRIPE", DriversLicenseFormatSettingState.Magstripe)]
    [DataRow("magnetic-stripe", DriversLicenseFormatSettingState.Invalid)]
    [DataRow("unexpected", DriversLicenseFormatSettingState.Invalid)]
    public void DriversLicenseFormatParser_RejectsUnsupportedLegacyValues(
        string? raw, DriversLicenseFormatSettingState expectedState)
    {
        Assert.AreEqual(expectedState, DriversLicenseFormatSettingParser.Parse(raw).State);
    }
}
