using Clc.PatronRegistration.Web.Settings;

namespace Clc.PatronRegistration.Tests;

[TestClass]
public sealed class SettingsAdministrationOptionsTests
{
    [DataTestMethod]
    [DataRow(1, true)]
    [DataRow(24, true)]
    [DataRow(168, true)]
    [DataRow(0, false)]
    [DataRow(-1, false)]
    [DataRow(169, false)]
    public void PreviewLifetime_IsBounded(int hours, bool valid) =>
        Assert.AreEqual(valid, SettingsAdministrationOptions.IsValidPreviewLinkLifetime(hours));

    [TestMethod]
    public void PreviewLifetime_DefaultIs24AndStartupValidationIsConfigured()
    {
        Assert.AreEqual(24, new SettingsAdministrationOptions().PreviewLinkLifetimeHours);
        StringAssert.Contains(File.ReadAllText(Path.Combine(RepositoryRoot(), "src/Clc.PatronRegistration.Web/Program.cs")), ".ValidateOnStart()");
    }

    private static string RepositoryRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
}
