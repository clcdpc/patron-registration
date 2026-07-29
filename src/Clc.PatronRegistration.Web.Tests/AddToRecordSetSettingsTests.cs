using Clc.PatronRegistration.Administration;
using Clc.PatronRegistration.Configuration;
using Clc.Polaris.Api;
using Moq;
using NLog;
using NLog.Config;
using NLog.Targets;

namespace Clc.PatronRegistration.Tests;

[TestClass]
public class AddToRecordSetSettingsTests
{
    [DataTestMethod]
    [DataRow(null, IdentifierSettingState.Missing, null)]
    [DataRow("", IdentifierSettingState.Missing, null)]
    [DataRow("0", IdentifierSettingState.Zero, 0)]
    [DataRow("73", IdentifierSettingState.Positive, 73)]
    [DataRow("-2", IdentifierSettingState.Negative, -2)]
    [DataRow("bad", IdentifierSettingState.Malformed, null)]
    public void IdentifierParser_DistinguishesEveryLegacyState(string? value, IdentifierSettingState state, int? parsed)
    {
        var result = IdentifierSettingParser.Parse(value);

        Assert.AreEqual(state, result.State);
        Assert.AreEqual(parsed, result.Value);
    }

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

    [DataTestMethod]
    [DataRow("mailing_list_record_set_id")]
    [DataRow("valid_address_record_set_id")]
    [DataRow("valid_address_plus_name_record_set_id")]
    [DataRow("invalid_address_record_set_id")]
    public void LegacyMalformedNonNullableRecordSetIds_AreSafelyDisabled(string key)
    {
        var cache = new TestCache { SettingsCache = [Setting(3, "not-an-id", key)] };
        var provider = new DbSettingProvider(3, cache);

        var value = key switch
        {
            "mailing_list_record_set_id" => provider.MailingListRecordSetId,
            "valid_address_record_set_id" => provider.ValidAddressRecordSetId,
            "valid_address_plus_name_record_set_id" => provider.ValidAddressPlusNameRecordSetId,
            _ => provider.InvalidAddressRecordSetId
        };
        Assert.AreEqual(0, value);
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

        Assert.IsNotNull(definition.Validate(value));
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("73")]
    public void CatalogAcceptsDisabledOrPositiveRecordSetIds(string value)
    {
        var catalog = new SettingCatalog();
        Assert.IsTrue(catalog.TryGet("add_to_record_set_id", out var definition));

        Assert.IsNull(definition.Validate(value));
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

    [DataTestMethod]
    [DataRow(IdentifierSettingState.Negative, -7)]
    [DataRow(IdentifierSettingState.Malformed, null)]
    public void InvalidOptionalIdentifier_IsSkippedAndDiagnosticContainsOnlyKeyAndCategory(
        IdentifierSettingState state,
        int? parsedValue)
    {
        const string recognizableMalformedValue = "recognizable-malformed-id";
        var previousConfiguration = LogManager.Configuration;
        var target = new MemoryTarget { Layout = "${message}" };
        LogManager.Configuration = new LoggingConfiguration();
        LogManager.Configuration.AddRule(LogLevel.Error, LogLevel.Fatal, target);
        LogManager.ReconfigExistingLoggers();
        var settings = new Mock<ISettingProvider>();
        settings.SetupGet(provider => provider.AddToRecordSetId).Returns(parsedValue);
        settings.As<IIdentifierSettingStateProvider>()
            .Setup(provider => provider.GetIdentifierState("add_to_record_set_id"))
            .Returns(new IdentifierSettingResult(state, parsedValue));
        var papi = new Mock<IPapiClient>();

        try
        {
            new Registration(settings.Object).AddToRecordSet(papi.Object, 123);

            papi.Verify(client => client.RecordSetContentAdd(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
            Assert.IsTrue(target.Logs.Any(message =>
                message.Contains("add_to_record_set_id", StringComparison.Ordinal) &&
                message.Contains(state.ToString(), StringComparison.Ordinal)));
            Assert.IsFalse(target.Logs.Any(message => message.Contains(recognizableMalformedValue, StringComparison.Ordinal)));
        }
        finally
        {
            LogManager.Configuration = previousConfiguration;
            LogManager.ReconfigExistingLoggers();
        }
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

    private static RegistrationFormSetting Setting(int organizationId, string value, string key = "add_to_record_set_id") => new()
    {
        OrganizationID = organizationId,
        FormCode = string.Empty,
        Setting = key,
        Value = value
    };
}
