using Clc.PatronRegistration.Administration;
using Clc.PatronRegistration.Configuration;
using Clc.Polaris.Api;
using Moq;

namespace Clc.PatronRegistration.Tests;

[TestClass]
public class AddToRecordSetSettingsTests
{
    [TestMethod]
    public void MissingAndInheritedMissingValues_AreNull()
    {
        var provider = Provider([]);

        Assert.IsNull(provider.AddToRecordSetId);
    }

    [TestMethod]
    public void ExplicitEmptyOverride_IsNull()
    {
        var provider = Provider([Setting(3, string.Empty)]);

        Assert.IsNull(provider.AddToRecordSetId);
    }

    [TestMethod]
    public void LegacyMalformedOverride_IsSafelyDisabled()
    {
        var provider = Provider([Setting(3, "not-an-id")]);

        Assert.IsNull(provider.AddToRecordSetId);
    }

    [TestMethod]
    public void PositiveOverride_ReturnsConfiguredId()
    {
        var provider = Provider([Setting(1, "41"), Setting(3, "73")]);

        Assert.AreEqual(73, provider.AddToRecordSetId);
    }

    [DataTestMethod]
    [DataRow("0")]
    [DataRow("-1")]
    public void CatalogRejectsNonPositiveRecordSetIds(string value)
    {
        var catalog = new SettingCatalog();
        Assert.IsTrue(catalog.TryGet("add_to_record_set_id", out var definition));

        Assert.IsNotNull(catalog.Validate(definition, value));
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("73")]
    public void CatalogAcceptsDisabledOrPositiveRecordSetIds(string value)
    {
        var catalog = new SettingCatalog();
        Assert.IsTrue(catalog.TryGet("add_to_record_set_id", out var definition));

        Assert.IsNull(catalog.Validate(definition, value));
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(-5)]
    public void DisabledRecordSetId_DoesNotCallPapi(int? recordSetId)
    {
        var settings = new Mock<ISettingProvider>();
        settings.SetupGet(provider => provider.AddToRecordSetId).Returns(recordSetId);
        var papi = new Mock<IPapiClient>();

        new Registration(settings.Object).AddToRecordSet(papi.Object, 123);

        papi.Verify(client => client.RecordSetContentAdd(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [TestMethod]
    public void MissingRecordSetId_DoesNotCallPapi()
    {
        var settings = new Mock<ISettingProvider>();
        settings.SetupGet(provider => provider.AddToRecordSetId).Returns((int?)null);
        var papi = new Mock<IPapiClient>();

        new Registration(settings.Object).AddToRecordSet(papi.Object, 123);

        papi.Verify(client => client.RecordSetContentAdd(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [TestMethod]
    public void PositiveRecordSetId_CallsPapiExactlyOnce()
    {
        var settings = new Mock<ISettingProvider>();
        settings.SetupGet(provider => provider.AddToRecordSetId).Returns(73);
        var papi = new Mock<IPapiClient>();

        new Registration(settings.Object).AddToRecordSet(papi.Object, 123);

        papi.Verify(client => client.RecordSetContentAdd(73, 123, It.IsAny<int>(), It.IsAny<int>()), Times.Once);
    }

    [TestMethod]
    public void AllNullableCatalogSettings_HaveNullableProviderProperties()
    {
        Assert.AreEqual(typeof(int?), typeof(DbSettingProvider).GetProperty(nameof(DbSettingProvider.AddToRecordSetId))!.PropertyType);
        Assert.AreEqual(typeof(int?), typeof(DbSettingProvider).GetProperty(nameof(DbSettingProvider.ExpirationDateYears))!.PropertyType);
        Assert.AreEqual(typeof(int?), typeof(DbSettingProvider).GetProperty(nameof(DbSettingProvider.PatronCodeId))!.PropertyType);
        Assert.AreEqual(typeof(DateTime?), typeof(DbSettingProvider).GetProperty(nameof(DbSettingProvider.ExpirationDate))!.PropertyType);
    }

    private static DbSettingProvider Provider(IEnumerable<RegistrationFormSetting> settings)
    {
        var cache = new TestCache { SettingsCache = settings.ToList() };
        return new DbSettingProvider(3, cache);
    }

    private static RegistrationFormSetting Setting(int organizationId, string value) => new()
    {
        OrganizationID = organizationId,
        FormCode = string.Empty,
        Setting = "add_to_record_set_id",
        Value = value
    };
}
