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
        var settings = Settings();

        var result = AgeBlockPolicy.Evaluate(settings.Object, AsOf.AddYears(-18).AddDays(1).ToDateTime(TimeOnly.MinValue), AsOf);

        Assert.IsTrue(result.IsBlocked);
        Assert.AreEqual("Too young", result.Message);
    }

    [TestMethod]
    public void ExactlyEighteenToday_IsAllowed()
    {
        var result = AgeBlockPolicy.Evaluate(Settings().Object,
            AsOf.AddYears(-18).ToDateTime(TimeOnly.MinValue), AsOf);

        Assert.IsFalse(result.IsBlocked);
    }

    [TestMethod]
    public void OverEighteen_IsAllowed()
    {
        var result = AgeBlockPolicy.Evaluate(Settings().Object,
            AsOf.AddYears(-18).AddDays(-1).ToDateTime(TimeOnly.MinValue), AsOf);

        Assert.IsFalse(result.IsBlocked);
    }

    [TestMethod]
    public void DisabledFeature_AllowsUnderEighteen()
    {
        var result = AgeBlockPolicy.Evaluate(Settings(false).Object,
            AsOf.AddYears(-18).AddDays(1).ToDateTime(TimeOnly.MinValue), AsOf);

        Assert.IsFalse(result.IsBlocked);
    }

    [TestMethod]
    public void NullBirthdate_IsNotBlockedByPolicy()
    {
        var result = AgeBlockPolicy.Evaluate(Settings().Object, null, AsOf);

        Assert.IsFalse(result.IsBlocked);
    }

    [TestMethod]
    public void FutureBirthdate_IsNotBlockedByPolicy()
    {
        var result = AgeBlockPolicy.Evaluate(Settings().Object,
            AsOf.AddDays(1).ToDateTime(TimeOnly.MinValue), AsOf);

        Assert.IsFalse(result.IsBlocked);
        Assert.AreEqual(string.Empty, result.Message);
    }

    private static Mock<ISettingProvider> Settings(bool enabled = true)
    {
        var settings = new Mock<ISettingProvider>();
        settings.SetupGet(value => value.EnableAgeBlock).Returns(enabled);
        settings.SetupGet(value => value.AgeBlockText).Returns("Too young");
        return settings;
    }
}
