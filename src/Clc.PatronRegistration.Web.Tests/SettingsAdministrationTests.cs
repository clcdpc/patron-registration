using Clc.PatronRegistration.Administration;
using Clc.PatronRegistration.Configuration;

namespace Clc.PatronRegistration.Tests;

[TestClass]
public class SettingsAdministrationTests
{
    [TestMethod]
    public void Resolver_UsesAllSixExplicitLevels()
    {
        var precedence = SettingsResolver.BuildPrecedence(3, 2, 1, "kids");

        CollectionAssert.AreEqual(
            new[] { (3, "kids"), (3, ""), (2, "kids"), (2, ""), (1, "kids"), (1, "") },
            precedence.Select(source => (source.OrganizationId, source.FormCode)).ToArray());
    }

    [TestMethod]
    public void Resolver_PreservesExplicitEmptyOverride()
    {
        var rows = new[]
        {
            Setting(1, "x", "system"),
            Setting(2, "x", string.Empty)
        };

        var result = new SettingsResolver().Resolve(rows, "x", 2, 2, string.Empty, 1);

        Assert.IsTrue(result.OwnsOverride);
        Assert.AreEqual(string.Empty, result.EffectiveValue);
        Assert.IsFalse(result.IsInherited);
    }

    [TestMethod]
    public void Resolver_RemoveExposesInheritedValue()
    {
        var rows = new[]
        {
            Setting(1, "x", "system"),
            Setting(2, "x", "local")
        };
        var removed = new HashSet<(int, string, string)> { (2, string.Empty, "x") };

        var result = new SettingsResolver().Resolve(rows, "x", 2, 2, string.Empty, 1, removed);

        Assert.AreEqual("system", result.EffectiveValue);
    }

    [TestMethod]
    public void GetRequiredFields_UsesEffectiveBooleanAndExplicitFalse()
    {
        var cache = new TestCache
        {
            SettingsCache =
            [
                Setting(1, "require.NameFirst", "true"),
                Setting(1, "require.EmailAddress", "true"),
                Setting(2, "require.EmailAddress", "false"),
                Setting(2, "require.Birthdate", "true", "kids"),
                Setting(3, "require.Birthdate", "false", "kids")
            ]
        };
        var provider = new DbSettingProvider(3, cache, "kids", 1);

        var required = provider.GetRequiredFields();

        CollectionAssert.AreEquivalent(new[] { "NameFirst" }, required);
    }

    [TestMethod]
    public void CatalogKeys_AreUniqueAndRejectArbitrarySuffixes()
    {
        var catalog = new SettingCatalog();

        Assert.AreEqual(
            catalog.All.Count,
            catalog.All.Select(setting => setting.Key.ToLowerInvariant()).Distinct().Count());
        Assert.IsTrue(catalog.TryGet("require.NameFirst", out _));
        Assert.IsFalse(catalog.TryGet("require.DropTable", out _));
    }

    [TestMethod]
    public void DynamicFieldCatalog_ContainsDeliberatelySupportedRegistrationFields()
    {
        var expected = new[]
        {
            "UseLegalName", "ReceiveEreceipts", "User5", "Password2", "User1",
            "DeliverCardToSchool", "IsStudent", "IsTeacher", "IsECard", "AddToMailingList"
        };
        var catalog = new SettingCatalog();

        foreach (var field in expected)
        {
            CollectionAssert.Contains(catalog.DynamicFieldSuffixes.ToList(), field);
            Assert.IsTrue(catalog.TryGet($"alert.{field}", out _));
            Assert.IsTrue(catalog.TryGet($"label.{field}", out _));
            Assert.IsTrue(catalog.TryGet($"require.{field}", out _));
        }
    }

    [DataTestMethod]
    [DataRow(SettingValueType.Boolean)]
    [DataRow(SettingValueType.Integer)]
    [DataRow(SettingValueType.Decimal)]
    [DataRow(SettingValueType.Date)]
    [DataRow(SettingValueType.Enumeration)]
    public void NonStringNonNullableTypes_RejectEmptyOverrides(SettingValueType type)
    {
        var definition = new SettingDefinition("test", "Test", "Test", type, AllowEmpty: false);

        Assert.IsNotNull(definition.Validate(string.Empty));
    }

    [DataTestMethod]
    [DataRow(SettingValueType.ShortString)]
    [DataRow(SettingValueType.LongString)]
    [DataRow(SettingValueType.Html)]
    [DataRow(SettingValueType.EmailTemplate)]
    [DataRow(SettingValueType.EmailAddress)]
    [DataRow(SettingValueType.Uri)]
    [DataRow(SettingValueType.NullableInteger)]
    [DataRow(SettingValueType.NullableDate)]
    public void ExplicitlyEmptyTypes_HaveDefinedEmptyStorageSemantics(SettingValueType type)
    {
        var definition = new SettingDefinition("test", "Test", "Test", type, AllowEmpty: true);

        Assert.IsNull(definition.Validate(string.Empty));
    }

    [TestMethod]
    public void NullableConversion_MapsEmptyStorageToNull()
    {
        Assert.IsNull(DbSettingProvider.ConvertToType<int?>(string.Empty));
        Assert.IsNull(DbSettingProvider.ConvertToType<DateTime?>(string.Empty));
    }

    [DataTestMethod]
    [DataRow("a")]
    [DataRow("secret")]
    [DataRow("abcd1234wxyz5678")]
    public void SensitiveMasking_NeverRetainsTheWholeSecret(string secret)
    {
        var masked = SensitiveValueMasker.Mask(secret);

        Assert.AreNotEqual(secret, masked);
        Assert.IsTrue(masked.Contains('…'));
        Assert.IsTrue(masked.Replace("…", string.Empty).Length <= secret.Length / 2);
    }

    [TestMethod]
    public void PreviewTokens_Have256BitsAndUrlSafeEncoding()
    {
        var service = new PreviewTokenService();
        var token = service.Create();

        Assert.AreEqual(32, token.Hash.Length);
        Assert.IsTrue(service.Matches(token.Plaintext, token.Hash));
        Assert.IsFalse(service.Matches(token.Plaintext + "x", token.Hash));
        Assert.IsFalse(token.Plaintext.Contains('+'));
        Assert.IsFalse(token.Plaintext.Contains('/'));
    }

    [TestMethod]
    public void PreviewOverlay_ReflectsLatestUpsertAndRemoveOperations()
    {
        var cache = new TestCache
        {
            SettingsCache =
            [
                Setting(1, "registration_text", "system"),
                Setting(2, "registration_text", "library"),
                Setting(3, "warning_text", "branch")
            ]
        };
        var draft = new SettingDraft(4, 3, string.Empty, 0, DraftStatus.Active,
        [
            new SettingMutation("registration_text", DraftOperation.Upsert, "draft"),
            new SettingMutation("warning_text", DraftOperation.RemoveOverride, null)
        ]);

        var provider = new PreviewSettingProvider(draft, cache, 1);

        Assert.AreEqual("draft", provider.RegistrationText);
        Assert.AreEqual(string.Empty, provider.WarningText);
    }

    private static RegistrationFormSetting Setting(int organizationId, string key, string value, string formCode = "") => new()
    {
        OrganizationID = organizationId,
        Setting = key,
        Value = value,
        FormCode = formCode
    };
}
