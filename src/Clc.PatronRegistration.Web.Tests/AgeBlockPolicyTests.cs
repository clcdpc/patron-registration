using Clc.PatronRegistration.Configuration;
using Moq;

namespace Clc.PatronRegistration.Tests;

[TestClass]
public sealed class AgeBlockPolicyTests
{
    private static readonly DateOnly AsOf = new(2026, 8, 14);

    [TestMethod]
    public void UnderEighteen_IsBlocked()
    {
        var result = AgeBlockPolicy.Evaluate(Settings().Object,
            AsOf.AddYears(-AgeBlockPolicy.MinimumAge).AddDays(1).ToDateTime(new TimeOnly(23, 59)), AsOf);

        Assert.IsTrue(result.IsBlocked);
        Assert.AreEqual("Too young", result.Message);
    }

    [TestMethod]
    public void ExactlyEighteenToday_IsAllowed()
    {
        var result = AgeBlockPolicy.Evaluate(Settings().Object,
            AsOf.AddYears(-AgeBlockPolicy.MinimumAge).ToDateTime(new TimeOnly(23, 59)), AsOf);

        Assert.IsFalse(result.IsBlocked);
        Assert.AreEqual(string.Empty, result.Message);
    }

    [TestMethod]
    public void OverEighteen_IsAllowed()
    {
        var result = AgeBlockPolicy.Evaluate(Settings().Object,
            AsOf.AddYears(-AgeBlockPolicy.MinimumAge).AddDays(-1).ToDateTime(TimeOnly.MinValue), AsOf);

        Assert.IsFalse(result.IsBlocked);
    }

    [TestMethod]
    public void DisabledFeature_AllowsUnderEighteen()
    {
        var result = AgeBlockPolicy.Evaluate(Settings(false).Object,
            AsOf.AddYears(-AgeBlockPolicy.MinimumAge).AddDays(1).ToDateTime(TimeOnly.MinValue), AsOf);

        Assert.IsFalse(result.IsBlocked);
    }

    [TestMethod]
    public void NullBirthdate_IsNotBlockedByPolicy()
    {
        var result = AgeBlockPolicy.Evaluate(Settings().Object, null, AsOf);

        Assert.IsFalse(result.IsBlocked);
        Assert.AreEqual(string.Empty, result.Message);
    }

    [TestMethod]
    public void FutureBirthdate_IsNotTreatedAsUnderage()
    {
        var result = AgeBlockPolicy.Evaluate(Settings().Object,
            AsOf.AddDays(1).ToDateTime(TimeOnly.MinValue), AsOf);

        Assert.IsFalse(result.IsBlocked);
        Assert.AreEqual(string.Empty, result.Message);
    }

    [TestMethod]
    public void DbSettingProvider_ReadsAgeBlockSettingsUsingExistingSettingLookup()
    {
        var cache = new TestCache
        {
            SettingsCache =
            [
                new() { OrganizationID = 3, Setting = "enable_age_block", Value = "true" },
                new() { OrganizationID = 3, Setting = "age_block_text", Value = "Configured block" }
            ]
        };

        var settings = new DbSettingProvider(3, cache);

        Assert.IsTrue(settings.EnableAgeBlock);
        Assert.AreEqual("Configured block", settings.AgeBlockText);
    }

    private static Mock<ISettingProvider> Settings(bool enabled = true)
    {
        var settings = new Mock<ISettingProvider>();
        settings.SetupGet(value => value.EnableAgeBlock).Returns(enabled);
        settings.SetupGet(value => value.AgeBlockText).Returns("Too young");
        return settings;
    }
}
