using Clc.PatronRegistration.Administration;
using Clc.PatronRegistration.Configuration;
using Clc.PatronRegistration.Web.Settings;
using Clc.PatronRegistration.Web.Models;
using Clc.PatronRegistration.Validators;
using Clc.PatronRegistration.Helpers;
using Clc.Polaris.Api.Models;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Clc.PatronRegistration.Tests;

[TestClass]
public class SettingsAdministrationTests
{
    // Compatibility contract: this list intentionally remains independent of provider attributes and catalog construction.
    private static readonly string[] ExpectedOrdinaryKeys =
    [
        "header_image_asset_id", "css_file", "warning_text", "custom_form_footer_html", "registration_text", "registration_form_header",
        "show_dl", "enable_age_warning", "age_warning_text", "enable_age_block", "age_block_text", "hide_ereceipt",
        "normalize_to_uppercase", "dl_format", "enable_legal_name_checkbox", "drivers_license_button_text",
        "drivers_license_prompt_text", "agreement_confirm_button_text", "agreement_cancel_button_text", "school_info_field_legend",
        "responsible_person_disclaimer", "display_responsible_person_field", "phone_number_format",
        "enable_patron_branch_select_option", "display_preferred_pickup_location", "teacher_patron_code_id", "student_patron_code_id",
        "patron_code_id", "expiration_date", "expiration_date_years", "hide_branch_select_if_only_one_option", "disable_branch",
        "display_ecard_checkbox", "ecard_patron_code_id", "ecard_registration_text", "ecard_barcode_prefix", "force_ecard_remotely",
        "display_mailing_list_checkbox", "mailing_list_description_html", "mailing_list_record_set_id", "display_sms_notice_information",
        "sms_notice_information_html", "use_legal_name_on_notices", "ecard_welcome_email_template_text",
        "ecard_welcome_email_template_html", "welcome_email_template_text", "welcome_email_template_html", "welcome_email_from_name",
        "welcome_email_subject", "welcome_email_from_address", "ecard_welcome_email_subject", "postmark_api_key",
        "bypass_dupe_check", "duplicate_patron_message_html", "perform_papi_duplicate_bypass", "use_first_name_for_duplicate_workaround",
        "block_out_of_state_registrations", "update_patron_record_with_melissa_address", "melissa_data_api_key",
        "valid_address_registration_text", "valid_address_plus_name_registration_text", "out_of_state_block_message",
        "valid_address_patron_code_id", "valid_address_plus_name_patron_code_id", "valid_address_record_set_id",
        "valid_address_plus_name_record_set_id", "invalid_address_record_set_id", "registration_logon_user_id",
        "add_to_record_set_id", "post_registration_note_text", "show_dl_ips", "reset_form", "kiosk_registration_text", "reset_seconds"
    ];

    // Compatibility contract for editor semantics that cannot be safely inferred from a string CLR type.
    private static readonly IReadOnlyDictionary<string, SettingValueType> ExpectedSemanticValueTypes =
        new Dictionary<string, SettingValueType>(StringComparer.OrdinalIgnoreCase)
        {
            ["header_image_asset_id"] = SettingValueType.Image,
            ["warning_text"] = SettingValueType.LongString,
            ["custom_form_footer_html"] = SettingValueType.Html,
            ["age_warning_text"] = SettingValueType.LongString,
            ["age_block_text"] = SettingValueType.Html,
            ["drivers_license_button_text"] = SettingValueType.LongString,
            ["drivers_license_prompt_text"] = SettingValueType.LongString,
            ["agreement_confirm_button_text"] = SettingValueType.LongString,
            ["agreement_cancel_button_text"] = SettingValueType.LongString,
            ["registration_text"] = SettingValueType.LongString,
            ["duplicate_patron_message_html"] = SettingValueType.Html,
            ["responsible_person_disclaimer"] = SettingValueType.LongString,
            ["ecard_registration_text"] = SettingValueType.LongString,
            ["mailing_list_description_html"] = SettingValueType.Html,
            ["sms_notice_information_html"] = SettingValueType.Html,
            ["ecard_welcome_email_template_text"] = SettingValueType.EmailTemplate,
            ["ecard_welcome_email_template_html"] = SettingValueType.EmailTemplate,
            ["welcome_email_template_text"] = SettingValueType.EmailTemplate,
            ["welcome_email_template_html"] = SettingValueType.EmailTemplate,
            ["welcome_email_from_address"] = SettingValueType.EmailAddress,
            ["valid_address_registration_text"] = SettingValueType.LongString,
            ["valid_address_plus_name_registration_text"] = SettingValueType.LongString,
            ["out_of_state_block_message"] = SettingValueType.LongString,
            ["post_registration_note_text"] = SettingValueType.LongString,
            ["kiosk_registration_text"] = SettingValueType.LongString
        };

    private static readonly string[] ExpectedSensitiveKeys =
    [
        "postmark_api_key", "melissa_data_api_key"
    ];

    [TestMethod]
    public void PreviewLinkView_OffersRestoreAndRemovalWithAntiforgeryForms()
    {
        var root = FindRepositoryRoot();
        var view = File.ReadAllText(Path.Combine(root, "src/Clc.PatronRegistration.Web/Views/Settings/Index.cshtml"));
        static string Form(string source, string action)
        {
            var start = source.IndexOf($"<form asp-action=\"{action}\"", StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"The {action} form was not found.");
            var end = source.IndexOf("</form>", start, StringComparison.Ordinal);
            return source[start..(end + "</form>".Length)];
        }
        var restore = Form(view, "RestorePreviewLink");
        var remove = Form(view, "DeletePreviewLink");

        StringAssert.Contains(restore, ">Restore</button>");
        StringAssert.Contains(remove, ">Remove</button>");
        StringAssert.Contains(restore, "@Html.AntiForgeryToken()");
        StringAssert.Contains(remove, "@Html.AntiForgeryToken()");
        StringAssert.Contains(view, "aria-label=\"Actions for preview link @link.PreviewLinkId\"");
    }

    [DataTestMethod]
    [DataRow(null, null, false, false, true, true)]
    [DataRow(null, -1, false, false, true, true)]
    [DataRow(null, 1, true, true, false, false)]
    [DataRow(0, 1, false, false, false, true)]
    public void PreviewLinkActionPolicy_ProvidesOnlyActionsForCurrentState(
        int? revokedOffsetHours, int? expirationOffsetHours, bool replace, bool revoke, bool restore, bool remove)
    {
        var now = new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var link = new PreviewLinkRecord(42, 7, new byte[32], false,
            revokedOffsetHours.HasValue ? now.AddHours(revokedOffsetHours.Value) : null,
            expirationOffsetHours.HasValue ? now.AddHours(expirationOffsetHours.Value) : null,
            3, "form", "Active", 3);

        Assert.AreEqual(new PreviewLinkActions(replace, revoke, restore, remove), PreviewLinkActionPolicy.For(link, now));
    }
    [DataTestMethod]
    [DataRow(null, true)]
    [DataRow("", true)]
    [DataRow("0", true)]
    [DataRow("1", true)]
    [DataRow("100", true)]
    [DataRow("101", false)]
    [DataRow("-1", false)]
    [DataRow("9999", false)]
    [DataRow("not-a-number", false)]
    public void ExpirationDateYears_UsesBoundedNullableRange(string? value, bool valid)
    {
        var definition = new SettingCatalog().All.Single(item => item.Key == "expiration_date_years");
        Assert.AreEqual(valid, definition.Validate(value ?? string.Empty) is null);
    }

    [DataTestMethod]
    [DataRow("", true)]
    [DataRow("10.0.0.", true)]
    [DataRow("10.0.0.;", false)]
    [DataRow(";10.0.0.", false)]
    [DataRow("10.0.0.;;192.168.", false)]
    [DataRow("10.0.0.; ;192.168.", false)]
    public void DriversLicenseIpPrefixes_RejectEmptySemicolonSegments(string value, bool valid)
    {
        var definition = new SettingCatalog().All.Single(item => item.Key == "show_dl_ips");

        Assert.AreEqual(valid, definition.Validate(value) is null);
    }

    [DataTestMethod]
    [DataRow("0", true)]
    [DataRow("86400", true)]
    [DataRow("-1", false)]
    [DataRow("86401", false)]
    [DataRow("2147483647", false)]
    [DataRow("999999999999999999999", false)]
    public void ResetSeconds_UsesBoundedNonnegativeRange(string value, bool valid)
    {
        var definition = new SettingCatalog().All.Single(item => item.Key == "reset_seconds");

        Assert.AreEqual(valid, definition.Validate(value) is null);
    }

    [TestMethod]
    public void DriversLicenseFormat_IsAnExplicitSupportedEnumeration()
    {
        var definition = new SettingCatalog().All.Single(item => item.Key == "dl_format");

        Assert.AreEqual(SettingValueType.Enumeration, definition.ValueType);
        CollectionAssert.AreEquivalent(new[] { "barcode", "magstripe" }, definition.AllowedValues!.ToArray());
        Assert.IsNull(definition.Validate("barcode"));
        Assert.IsNull(definition.Validate("MAGSTRIPE"));
        Assert.IsNotNull(definition.Validate("magnetic-stripe"));
    }

    [TestMethod]
    public void GenderCompatibilitySettings_AreNotPresentedAsActiveControls()
    {
        var catalog = new SettingCatalog();

        Assert.IsFalse(catalog.TryGet("hide_gender", out _));
        Assert.IsFalse(catalog.TryGet("na_gender_text", out _));
    }

    [TestMethod]
    public void SchoolInfoFormat_RemainsRuntimeOnlyAndExistingRowsStillResolve()
    {
        var property = typeof(ISettingProvider).GetProperty(nameof(ISettingProvider.SchoolInfoFormat));
        Assert.IsNotNull(property);
        Assert.IsNull(property!.GetCustomAttribute<AdminSettingAttribute>(inherit: false));
        Assert.IsFalse(new SettingCatalog().TryGet("school_info_format", out _));

        var provider = new DbSettingProvider(3,
            CacheWith(Setting(3, "school_info_format", "uapl")), string.Empty, 1);

        Assert.AreEqual("uapl", provider.SchoolInfoFormat);
    }

    [TestMethod]
    public void KioskRegistrationHeader_RemainsCompatibilityOnlyAndExistingRowsStillResolve()
    {
        var property = typeof(ISettingProvider).GetProperty(nameof(ISettingProvider.KioskRegistrationHeader));
        Assert.IsNotNull(property);
        Assert.IsNull(property!.GetCustomAttribute<AdminSettingAttribute>(inherit: false));
        Assert.IsFalse(new SettingCatalog().TryGet("kiosk_registration_header", out _));

        var provider = new DbSettingProvider(3,
            CacheWith(Setting(3, "kiosk_registration_header", "legacy kiosk header")), string.Empty, 1);

        Assert.AreEqual("legacy kiosk header", provider.KioskRegistrationHeader);
    }

    [TestMethod]
    public void DbSettingProvider_NormalizesDriverLicenseIpPrefixesAndMalformedResetSeconds()
    {
        var provider = new DbSettingProvider(3, CacheWith(
            Setting(3, "show_dl_ips", " 10.0.; ;192.168.;; "),
            Setting(3, "reset_seconds", "2147483647")));

        CollectionAssert.AreEqual(new[] { "10.0.", "192.168." }, provider.DriversLicenseButtonEnabledIpAddresses.ToArray());
        Assert.AreEqual(0, provider.ResetSeconds);
        Assert.AreEqual(BoundedIntegerSettingState.Invalid, provider.GetResetSecondsState().State);
        Assert.IsTrue(Registration.CheckIp("127.0.0.1", provider.DriversLicenseButtonEnabledIpAddresses));
    }

    [TestMethod]
    public void OrdinaryCatalog_ExactlyMatchesExplicitlyAttributedProviderProperties()
    {
        var catalog = new SettingCatalog();
        var attributed = AdministrationProperties();
        var expected = attributed.Select(SettingKey).ToArray();
        var actual = catalog.All.Where(x => x.Group == SettingGroup.Ordinary).Select(x => x.Key).ToArray();

        Assert.AreEqual(actual.Length, actual.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "Duplicate ordinary key.");
        CollectionAssert.AreEquivalent(expected, actual);
        foreach (var key in expected)
        {
            Assert.AreEqual(1, actual.Count(actualKey => actualKey.Equals(key, StringComparison.OrdinalIgnoreCase)), key);
        }
    }

    [TestMethod]
    public void OrdinaryCatalog_PreservesIndependentCompatibilityKeyContract()
    {
        var actual = new SettingCatalog().All
            .Where(setting => setting.Group == SettingGroup.Ordinary)
            .Select(setting => setting.Key)
            .ToArray();

        Assert.AreEqual(ExpectedOrdinaryKeys.Length, actual.Length, "The ordinary catalog size changed.");
        Assert.AreEqual(actual.Length, actual.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "Duplicate ordinary key.");
        CollectionAssert.AreEquivalent(ExpectedOrdinaryKeys, actual);
        foreach (var key in ExpectedOrdinaryKeys)
        {
            Assert.AreEqual(1, actual.Count(actualKey => actualKey.Equals(key, StringComparison.OrdinalIgnoreCase)), key);
        }
    }

    [TestMethod]
    public void OrdinaryCatalog_PreservesIndependentSemanticValueTypesAndSensitiveKeys()
    {
        var ordinary = new SettingCatalog().All
            .Where(setting => setting.Group == SettingGroup.Ordinary)
            .ToList();

        foreach (var expected in ExpectedSemanticValueTypes)
        {
            var definitions = ordinary.Where(setting => setting.Key.Equals(expected.Key, StringComparison.OrdinalIgnoreCase)).ToArray();
            Assert.AreEqual(1, definitions.Length, expected.Key);
            Assert.AreEqual(expected.Value, definitions[0].ValueType, expected.Key);
        }

        var actualSensitiveKeys = ordinary
            .Where(setting => setting.IsSensitive)
            .Select(setting => setting.Key)
            .ToArray();
        CollectionAssert.AreEquivalent(ExpectedSensitiveKeys, actualSensitiveKeys);
    }

    [TestMethod]
    public void OrdinaryCatalog_UsesAttributedMetadataAndConservativeClrInference()
    {
        var catalog = new SettingCatalog().All.Where(x => x.Group == SettingGroup.Ordinary).ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var property in AdministrationProperties())
        {
            var attribute = property.GetCustomAttribute<AdminSettingAttribute>()!;
            var definition = catalog[SettingKey(property)];
            var expectedType = (int)attribute.ValueType >= 0
                ? attribute.ValueType
                : InferValueType(property.PropertyType);
            Assert.AreEqual(expectedType, definition.ValueType, definition.Key);
            Assert.AreEqual(attribute.IsSensitive, definition.IsSensitive, definition.Key);
            Assert.AreEqual(expectedType is SettingValueType.ShortString or SettingValueType.LongString or SettingValueType.Html
                or SettingValueType.EmailTemplate or SettingValueType.EmailAddress or SettingValueType.Uri
                or SettingValueType.NullableInteger or SettingValueType.NullableDate, definition.AllowEmpty, definition.Key);
            Assert.IsNotNull(definition.Category, definition.Key);
            Assert.IsFalse(string.IsNullOrWhiteSpace(definition.DisplayName), definition.Key);
            Assert.IsFalse(string.IsNullOrWhiteSpace(definition.Description), definition.Key);
        }
    }

    [TestMethod]
    public void AdministrationMetadata_InfersKeysAndPreservesLegacyKeyOverrides()
    {
        var properties = AdministrationProperties().ToDictionary(property => property.Name);
        Assert.AreEqual("enable_age_block", SettingKey(properties[nameof(ISettingProvider.EnableAgeBlock)]));
        Assert.AreEqual("age_block_text", SettingKey(properties[nameof(ISettingProvider.AgeBlockText)]));
        Assert.AreEqual("expiration_date_years", SettingKey(properties[nameof(ISettingProvider.ExpirationDateYears)]));
        Assert.AreEqual("postmark_api_key", SettingKey(properties[nameof(ISettingProvider.PostmarkApiKey)]));
        Assert.AreEqual("show_dl", SettingKey(properties[nameof(ISettingProvider.EnableDriversLicenseSwipe)]));
        Assert.AreEqual("dl_format", SettingKey(properties[nameof(ISettingProvider.DriversLicenseFormat)]));
        Assert.AreEqual("display_ecard_checkbox", SettingKey(properties[nameof(ISettingProvider.DisplayECardCheckbox)]));
        Assert.AreEqual("registration_form_header", SettingKey(properties[nameof(ISettingProvider.RegistrationHeader)]));
        Assert.AreEqual("hide_branch_select_if_only_one_option", SettingKey(properties[nameof(ISettingProvider.HideBranchSelectIfOnlyOneBranch)]));
        Assert.AreEqual("perform_papi_duplicate_bypass", SettingKey(properties[nameof(ISettingProvider.PerformPapiDupeBypass)]));
    }

    [TestMethod]
    public void AdministrationMetadata_UsesExplicitSemanticStringEditorsAndSensitiveMetadata()
    {
        var properties = AdministrationProperties().ToDictionary(property => property.Name);
        var expected = new Dictionary<string, SettingValueType>
        {
            [nameof(ISettingProvider.WarningText)] = SettingValueType.LongString,
            [nameof(ISettingProvider.CustomFormFooterHtml)] = SettingValueType.Html,
            [nameof(ISettingProvider.EcardWelcomeEmailTemplateText)] = SettingValueType.EmailTemplate,
            [nameof(ISettingProvider.WelcomeEmailFromAddress)] = SettingValueType.EmailAddress,
            [nameof(ISettingProvider.AgeBlockText)] = SettingValueType.Html
        };
        foreach (var pair in expected)
        {
            var attribute = properties[pair.Key].GetCustomAttribute<AdminSettingAttribute>()!;
            Assert.AreEqual(pair.Value, attribute.ValueType, pair.Key);
        }

        Assert.IsTrue(properties[nameof(ISettingProvider.PostmarkApiKey)].GetCustomAttribute<AdminSettingAttribute>()!.IsSensitive);
        Assert.IsTrue(properties[nameof(ISettingProvider.MelissaDataApiKey)].GetCustomAttribute<AdminSettingAttribute>()!.IsSensitive);
        Assert.AreEqual(SettingValueType.Html, new SettingCatalog().All.Single(x => x.Key == "age_block_text").ValueType);
        Assert.AreEqual(SettingValueType.Boolean, new SettingCatalog().All.Single(x => x.Key == "enable_age_block").ValueType);
    }

    [TestMethod]
    public void OrdinaryCatalog_DescriptionsAreSpecificAndCriticalDescriptionsAreStable()
    {
        var ordinary = new SettingCatalog().All.Where(x => x.Group == SettingGroup.Ordinary).ToDictionary(x => x.Key);
        var forbidden = new[] { "used by the registration workflow", "value used for", "controls whether", "registration setting" };
        foreach (var definition in ordinary.Values)
            Assert.IsFalse(forbidden.Any(text => definition.Description.Contains(text, StringComparison.OrdinalIgnoreCase)), definition.Key);

        var expected = new Dictionary<string, string>
        {
            ["show_dl_ips"] = "Semicolon-separated IP address prefixes treated as on-site requests; these control driver’s-license scanner availability, automatic kiosk resetting, and whether remote registration is forced into e-card mode.",
            ["bypass_dupe_check"] = "Skips the application’s preliminary duplicate check before patron creation; Polaris may still perform its own duplicate checking.",
            ["perform_papi_duplicate_bypass"] = "When Polaris rejects registration as a duplicate, allows the application to retry using the configured duplicate-name workaround.",
            ["use_first_name_for_duplicate_workaround"] = "Adds the duplicate-workaround suffix to the first name when enabled; otherwise it is added to the last name.",
            ["expiration_date"] = "Supplies one fixed patron expiration date; a configured years-based expiration takes precedence.",
            ["expiration_date_years"] = "Calculates patron expiration relative to registration and takes precedence over the fixed expiration date.",
            ["block_out_of_state_registrations"] = "Blocks registration when the submitted address state is outside Ohio.",
            ["registration_logon_user_id"] = "Polaris user ID used to create registrations whose address was not verified through the address-verification workflow.",
            ["mailing_list_record_set_id"] = "Polaris record set to which patrons are added when they select the mailing-list option.",
            ["add_to_record_set_id"] = "Additional Polaris record set to which every successfully created patron is added when configured.",
            ["post_registration_note_text"] = "Text added to the created patron’s Polaris note after successful registration."
        };
        foreach (var pair in expected) Assert.AreEqual(pair.Value, ordinary[pair.Key].Description, pair.Key);
    }
    [TestMethod]
    public void SettingsAudit_EmptyBatchedFormLookupReturnsWithoutOpeningAConnection()
    {
        var repository = new SettingsAdministrationRepository(Mock.Of<IDbHelperSettings>());

        Assert.AreEqual(0, repository.GetFormCodesForLibraries([], 1).Count);
    }

    [TestMethod]
    public void FormCodeNormalizer_CanonicalizesOnlyNullAndEmptyDefaults()
    {
        Assert.AreEqual(string.Empty, FormCodeNormalizer.Normalize(null));
        Assert.AreEqual(string.Empty, FormCodeNormalizer.Normalize(string.Empty));
        Assert.AreEqual(" Adult Form ", FormCodeNormalizer.Normalize(" Adult Form "));
    }

    [TestMethod]
    public void SettingsRequests_CanonicalizeNullDefaultFormCodes()
    {
        Assert.AreEqual(string.Empty, new SaveSettingsRequest { FormCode = null! }.FormCode);
        Assert.AreEqual(string.Empty, new SaveToSharedDraftRequest { FormCode = null! }.FormCode);
        Assert.AreEqual(string.Empty, new PreviewLinkRequest { FormCode = null! }.FormCode);
        Assert.AreEqual(string.Empty, new FormCodeRequest { FormCode = null! }.FormCode);
    }

    [TestMethod]
    public void AuditContext_CanonicalizesNullDefaultFormCode()
    {
        var audit = new AuditContext(null, null, null, 1, 1, null, null, null);

        Assert.AreEqual(string.Empty, audit.FormCode);
    }

    [TestMethod]
    public void OrdinaryCatalog_HasOrderedStaffFacingPresentationMetadata()
    {
        var ordinary = new SettingCatalog().All.Where(setting => setting.Group == SettingGroup.Ordinary).ToList();
        Assert.IsTrue(ordinary.All(setting => setting.Category.HasValue));
        Assert.IsTrue(ordinary.All(setting => !string.IsNullOrWhiteSpace(setting.DisplayName)));
        Assert.IsTrue(ordinary.All(setting => !string.IsNullOrWhiteSpace(setting.Description)));
        Assert.IsFalse(ordinary.Any(setting => setting.Description == $"Registration setting {setting.Key}."));
        CollectionAssert.AreEqual(Enum.GetValues<SettingCategory>(), SettingCategoryPresentation.Ordered.ToArray());
        foreach (var category in SettingCategoryPresentation.Ordered)
        {
            var names = ordinary.Where(setting => setting.Category == category).Select(setting => setting.DisplayName).ToArray();
            CollectionAssert.AreEqual(names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray(), names);
        }
    }

    [TestMethod]
    public void Catalog_UsesStaffFriendlyAcronymsAndAlphabetizesDynamicGroups()
    {
        var catalog = new SettingCatalog().All;
        foreach (var expected in new[] { "CSS file", "Header image", "Custom form footer HTML", "Additional post-registration record set", "Attempt PAPI duplicate workaround", "E-card patron code" })
            Assert.IsTrue(catalog.Any(setting => setting.DisplayName == expected), expected);
        foreach (var group in new[] { SettingGroup.Alert, SettingGroup.Label, SettingGroup.Require })
        {
            var names = catalog.Where(setting => setting.Group == group).Select(setting => setting.DisplayName).ToArray();
            CollectionAssert.AreEqual(names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray(), names);
        }
    }

    [TestMethod]
    public void OrdinaryCatalog_UsesRequestedCategoriesAndTerminology()
    {
        var catalog = new SettingCatalog().All.ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);
        var expectedCategories = new Dictionary<string, SettingCategory>
        {
            ["school_info_field_legend"] = SettingCategory.FormBehaviorAndFields,
            ["display_preferred_pickup_location"] = SettingCategory.BranchAndPatronDefaults,
            ["mailing_list_record_set_id"] = SettingCategory.EmailAndNotices,
            ["postmark_api_key"] = SettingCategory.EmailAndNotices,
            ["melissa_data_api_key"] = SettingCategory.AddressVerification,
            ["post_registration_note_text"] = SettingCategory.PolarisIntegrationAndRecordSets,
            ["show_dl_ips"] = SettingCategory.KioskAndSessionBehavior
        };
        foreach (var pair in expectedCategories) Assert.AreEqual(pair.Value, catalog[pair.Key].Category, pair.Key);

        CollectionAssert.AreEqual(new[]
        {
            "Page content and appearance", "Form fields and behavior", "Branch selection and patron defaults",
            "E-card registration", "Email and communications", "Duplicate detection and workarounds",
            "Address verification", "Polaris patron creation and follow-up", "Kiosk and on-site behavior"
        }, SettingCategoryPresentation.Ordered.Select(x => x.DisplayName()).ToArray());
        Assert.IsFalse(Enum.GetNames<SettingCategory>().Contains("AdvancedIntegrations"));
        var forbidden = new[] { "Dupe", "DL", "User1", "User5", "Voice1", "Voice2" };
        Assert.IsFalse(catalog.Values.Any(x => forbidden.Any(term => x.DisplayName.Contains(term, StringComparison.Ordinal))),
            "A staff-facing display name exposes an implementation mnemonic.");
    }

    [TestMethod]
    public void DynamicCatalog_PreservesSupportedNamespacesAndCentralizedFieldNames()
    {
        var catalog = new SettingCatalog();
        foreach (var key in new[]
        {
            "alert.NameFirst", "label.NameFirst", "label.UseLegalName", "label.IsECard", "label.AddToMailingList",
            "require.PhoneVoice1", "require.EmailAddress", "require.User5", "require.RequestPickupBranchID"
        }) Assert.IsTrue(catalog.TryGet(key, out _), key);
        foreach (var key in new[]
        {
            "alert.NotAField", "label.NotAField", "require.NotAField", "require.ReceiveEreceipts",
            "legal_name_checkbox_label", "ecard_checkbox_label", "mailing_list_checkbox_label",
            "require_preferred_pickup_location"
        })
            Assert.IsFalse(catalog.TryGet(key, out _), key);

        Assert.AreEqual("First name", catalog.All.Single(x => x.Key == "alert.NameFirst").DisplayName);
        Assert.AreEqual("First name", catalog.All.Single(x => x.Key == "label.NameFirst").DisplayName);
        Assert.AreEqual("Require primary phone number", catalog.All.Single(x => x.Key == "require.PhoneVoice1").DisplayName);
        CollectionAssert.AreEquivalent(new[] { "require.PhoneVoice1", "require.EmailAddress", "require.User5", "require.RequestPickupBranchID" },
            catalog.All.Where(x => x.Group == SettingGroup.Require).Select(x => x.Key).ToArray());
    }

    [TestMethod]
    public void SettingsView_HidesOnlyValidationMessagePresentationBehindOneBoolean()
    {
        var root = FindRepositoryRoot();
        var view = File.ReadAllText(Path.Combine(root, "src/Clc.PatronRegistration.Web/Views/Settings/Index.cshtml"));

        StringAssert.Contains(view, "bool ShowValidationMessageSettings = false;");
        StringAssert.Contains(view, "if (ShowValidationMessageSettings)");
        StringAssert.Contains(view, "groups.Insert(0, (SettingGroup.Alert, \"Validation messages\"));");
        StringAssert.Contains(view, "(SettingGroup.Label, \"Field labels\")");
        StringAssert.Contains(view, "(SettingGroup.Require, \"Required fields\")");
        StringAssert.Contains(view, "if (rows.Count == 0) { continue; }");
        Assert.AreEqual(2, Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains(".Web.Tests", StringComparison.Ordinal)
                && !path.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase)
                && !path.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase))
            .SelectMany(File.ReadLines).Count(line => line.Contains("ShowValidationMessageSettings", StringComparison.Ordinal)) +
            view.Split('\n').Count(line => line.Contains("ShowValidationMessageSettings", StringComparison.Ordinal)),
            "The toggle must remain local to the Razor presentation and occur only in its declaration and conditional.");
    }

    [TestMethod]
    public void RegistrationCheckboxes_UseSupportedRazorMetadataLabelsOnly()
    {
        var root = FindRepositoryRoot();
        var view = File.ReadAllText(Path.Combine(root, "src/Clc.PatronRegistration.Web/Views/Registration/_RegistrationForm.cshtml"));
        foreach (var property in new[] { "UseLegalName", "IsECard", "AddToMailingList" })
        {
            StringAssert.Contains(view, $"<input asp-for=\"{property}\" />");
            StringAssert.Contains(view, $"<label asp-for=\"{property}\"");
        }
        Assert.IsFalse(view.Contains("Html.Raw(Settings.GetFieldLabel", StringComparison.Ordinal));
    }

    [TestMethod]
    public void RegistrationView_RendersSupportedFooterAndSafeBranchAndResetBehavior()
    {
        var root = FindRepositoryRoot();
        var view = File.ReadAllText(Path.Combine(root, "src/Clc.PatronRegistration.Web/Views/Registration/_RegistrationForm.cshtml"));

        StringAssert.Contains(view, "Settings.CustomFormFooterHtml");
        StringAssert.Contains(view, "@Html.Raw(Settings.CustomFormFooterHtml)");
        StringAssert.Contains(view, "branchReloadUrlPattern");
        StringAssert.Contains(view, "resetDelayMilliseconds");
        Assert.IsFalse(view.Contains("Settings.ResetSeconds * 1000", StringComparison.Ordinal));
        Assert.IsFalse(view.Contains("HideGender", StringComparison.Ordinal));
        Assert.IsFalse(view.Contains("NaGenderText", StringComparison.Ordinal));
    }

    [TestMethod]
    public void RetiredFieldProviderMembersAreAbsentAndReplacementLabelsResolveDynamically()
    {
        var retiredMembers = new[]
        {
            "LegalName" + "CheckboxLabel", "ECard" + "CheckboxLabel", "MailingList" + "CheckboxLabel",
            "RequirePreferred" + "PickupLocation"
        };
        var interfaceProperties = typeof(ISettingProvider).GetProperties(BindingFlags.Instance | BindingFlags.Public);
        var providerProperties = typeof(DbSettingProvider).GetProperties(BindingFlags.Instance | BindingFlags.Public);

        foreach (var retiredMember in retiredMembers)
        {
            Assert.IsFalse(interfaceProperties.Any(property => property.Name == retiredMember), retiredMember);
            Assert.IsFalse(providerProperties.Any(property => property.Name == retiredMember), retiredMember);
        }

        var provider = new DbSettingProvider(3, CacheWith(
            Setting(1, "label.UseLegalName", "Use legal name"),
            Setting(1, "label.IsECard", "E-card"),
            Setting(1, "label.AddToMailingList", "Join mailing list")));

        Assert.AreEqual("Use legal name", provider.GetFieldLabel(nameof(Registration.UseLegalName)));
        Assert.AreEqual("E-card", provider.GetFieldLabel(nameof(Registration.IsECard)));
        Assert.AreEqual("Join mailing list", provider.GetFieldLabel(nameof(Registration.AddToMailingList)));

        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection()
            .AddSingleton<ISettingProvider>(provider)
            .BuildServiceProvider();
        new Microsoft.AspNetCore.Http.HttpContextAccessor().HttpContext =
            new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = services };
        try
        {
            Assert.AreEqual("Use legal name", new DbConfiguredDisplayNameAttribute(nameof(Registration.UseLegalName)).DisplayName);
            Assert.AreEqual("E-card", new DbConfiguredDisplayNameAttribute(nameof(Registration.IsECard)).DisplayName);
            Assert.AreEqual("Join mailing list", new DbConfiguredDisplayNameAttribute(nameof(Registration.AddToMailingList)).DisplayName);
        }
        finally
        {
            new Microsoft.AspNetCore.Http.HttpContextAccessor().HttpContext = null;
        }
    }

    [TestMethod]
    public void SettingValuePresentation_FormatsMissingBlankBooleanAndDirectValues()
    {
        var text = new SettingDefinition("text", "Text", "Text", SettingValueType.ShortString);
        var boolean = new SettingDefinition("boolean", "Boolean", "Boolean", SettingValueType.Boolean);

        Assert.AreEqual("Not configured", SettingValuePresentation.Format(text, null, false));
        Assert.AreEqual("Blank", SettingValuePresentation.Format(text, string.Empty, true));
        Assert.AreEqual("Yes", SettingValuePresentation.Format(boolean, "true", true));
        Assert.AreEqual("No", SettingValuePresentation.Format(boolean, "false", true));
        Assert.AreEqual("Leave this page", SettingValuePresentation.Format(text, "Leave this page", true));
    }

    [TestMethod]
    public void SettingValuePresentation_UsesFriendlyDriverLicenseLabelsWithoutChangingTokens()
    {
        var definition = new SettingDefinition("dl_format", "Driver-license format", "Driver-license format", SettingValueType.Enumeration,
            AllowedValues: new[] { "barcode", "magstripe" });

        Assert.AreEqual("Barcode", SettingValuePresentation.FriendlyValue(definition, "barcode"));
        Assert.AreEqual("Magnetic stripe", SettingValuePresentation.FriendlyValue(definition, "magstripe"));
        Assert.AreEqual("barcode-token", SettingValuePresentation.FriendlyValue(definition, "barcode-token"));
        Assert.AreEqual("Blank", SettingValuePresentation.FriendlyValue(definition, string.Empty));
        Assert.AreEqual("Not configured", SettingValuePresentation.FriendlyValue(definition, null, false));

        var resolution = new ResolvedSetting(definition.Key, "barcode", 1, "System", string.Empty, true, "barcode", false);
        var row = new SettingRowViewModel("token", definition, resolution, null, null, null);
        Assert.AreEqual("Barcode", SettingValuePresentation.ForRow(row).Value);
        Assert.AreEqual("Barcode", SettingReviewPresentation.Live(row));
    }

    [TestMethod]
    public void SettingValuePresentation_UsesSafeTypeSpecificSummaries()
    {
        var longText = new SettingDefinition("long", "Long", "Long", SettingValueType.LongString);
        var html = new SettingDefinition("html", "HTML", "HTML", SettingValueType.Html);
        var template = new SettingDefinition("template", "Template", "Template", SettingValueType.EmailTemplate);
        var sensitive = new SettingDefinition("secret", "Secret", "Secret", SettingValueType.ShortString, IsSensitive: true);

        Assert.AreEqual("Several words on one line", SettingValuePresentation.Format(longText, " Several\n words   on\tone line ", true));
        Assert.AreEqual("HTML configured", SettingValuePresentation.Format(html, "<strong>private markup</strong>", true));
        Assert.AreEqual("Email template configured", SettingValuePresentation.Format(template, "Hello {{name}}", true));
        Assert.AreEqual("Hidden", SettingValuePresentation.Format(sensitive, "recognizable-secret", true));
        Assert.AreEqual("Not configured", SettingValuePresentation.Format(sensitive, null, false));
    }

    [TestMethod]
    public void SettingValuePresentation_DraftUpsertUsesStagedValue()
    {
        var definition = new SettingDefinition("message", "Message", "Message", SettingValueType.LongString);
        var resolution = new ResolvedSetting("message", "Live value", 1, "System", string.Empty, false, null, true);
        var row = new SettingRowViewModel("token", definition, resolution, "Staged\n value", DraftOperation.Upsert, 1);

        var presentation = SettingValuePresentation.ForRow(row);
        Assert.AreEqual(SettingPresentationState.DraftChange, presentation.State);
        Assert.AreEqual("Staged value", presentation.Value);
        Assert.AreEqual("Shared draft", presentation.Status);
    }

    [TestMethod]
    public void SettingPresentation_DistinguishesNotSetInheritedCustomizedAndSensitive()
    {
        var text = new SettingDefinition("text", "Text", "Text", SettingValueType.ShortString);
        var secret = new SettingDefinition("secret", "Secret", "Secret", SettingValueType.ShortString, IsSensitive: true);
        SettingRowViewModel Row(SettingDefinition definition, ResolvedSetting resolution) =>
            new("token", definition, resolution, null, null, null);

        var notSet = SettingValuePresentation.ForRow(Row(text,
            new("text", null, null, "Unconfigured", string.Empty, false, null, true)));
        Assert.AreEqual(SettingPresentationState.NotSet, notSet.State);
        Assert.AreEqual("—", notSet.Value);
        Assert.AreEqual("Not set", notSet.Status);
        Assert.AreNotEqual(SettingPresentationState.Inherited, notSet.State);

        var inherited = SettingValuePresentation.ForRow(Row(text,
            new("text", "Parent value", 1, "System", string.Empty, false, null, true)));
        Assert.AreEqual(SettingPresentationState.Inherited, inherited.State);
        Assert.AreEqual("Inherited", inherited.Status);

        var blank = SettingValuePresentation.ForRow(Row(text,
            new("text", string.Empty, 2, "Library", string.Empty, true, string.Empty, false)));
        Assert.AreEqual(SettingPresentationState.Customized, blank.State);
        Assert.AreEqual("Blank", blank.Value);

        var hidden = SettingValuePresentation.ForRow(Row(secret,
            new("secret", null, 1, "System", string.Empty, false, null, true)));
        Assert.AreEqual(SettingPresentationState.Inherited, hidden.State);
        Assert.AreEqual("Hidden", hidden.Value);
    }

    [TestMethod]
    public void SettingPresentation_DraftTakesPrecedenceOverUnsetResolution()
    {
        var definition = new SettingDefinition("text", "Text", "Text", SettingValueType.ShortString);
        var resolution = new ResolvedSetting("text", null, null, "Unconfigured", string.Empty, false, null, true);
        var row = new SettingRowViewModel("token", definition, resolution, "New value", DraftOperation.Upsert, 1);

        Assert.AreEqual(SettingPresentationState.DraftChange, SettingValuePresentation.ForRow(row).State);
    }

    [TestMethod]
    public void SettingsView_HasValueHeadingsAndNoBareConfiguredFallback()
    {
        var root = FindRepositoryRoot();
        var index = File.ReadAllText(Path.Combine(root, "src/Clc.PatronRegistration.Web/Views/Settings/Index.cshtml"));
        var row = File.ReadAllText(Path.Combine(root, "src/Clc.PatronRegistration.Web/Views/Settings/_SettingRow.cshtml"));

        StringAssert.Contains(index, "<span>Setting</span><span>Value</span><span>Status</span>");
        StringAssert.Contains(index, "<form method=\"get\" class=\"settings-context\"");
        StringAssert.Contains(index, "name=\"organizationId\"");
        StringAssert.Contains(index, "name=\"formCode\"");
        StringAssert.Contains(index, "name=\"ExpectedVersion\"");
        Assert.IsFalse(row.Contains(">Configured<", StringComparison.Ordinal));
        StringAssert.Contains(row, "data-presentation-state");
    }

    [TestMethod]
    public void SettingsView_UsesCompactHeaderAndTwoRowControlPanel()
    {
        var root = FindRepositoryRoot();
        var index = File.ReadAllText(Path.Combine(root, "src/Clc.PatronRegistration.Web/Views/Settings/Index.cshtml"));
        var css = File.ReadAllText(Path.Combine(root, "src/Clc.PatronRegistration.Web/wwwroot/css/settings.css"));

        StringAssert.Contains(index, "Choose a scope and form. Uncustomized values inherit from the next applicable level.");
        Assert.IsFalse(index.Contains("Settings for", StringComparison.Ordinal));
        StringAssert.Contains(index, "<section class=\"settings-search settings-controls\" aria-label=\"Settings controls\">");
        StringAssert.Contains(index, "<div class=\"settings-context-row settings-context-primary\">");
        StringAssert.Contains(index, "<label for=\"organization-scope\">Scope <span class=\"visually-hidden\">(system, library, or branch)</span></label>");
        StringAssert.Contains(index, "<label for=\"setting-status-filter\">Status</label>");
        StringAssert.Contains(index, "<option value=\"customized\">Customized here</option>");
        StringAssert.Contains(index, "<div class=\"settings-context-row settings-search-row\">");
        StringAssert.Contains(index, "<label id=\"search-label\" for=\"setting-search\">Search</label>");
        StringAssert.Contains(index, "<p id=\"search-status\" aria-live=\"polite\">@Model.Settings.Count settings</p>");
        Assert.IsFalse(index.Contains("id=\"ordinary-heading\"", StringComparison.Ordinal));
        Assert.IsFalse(index.Contains(">Settings</h2>", StringComparison.Ordinal));

        StringAssert.Contains(css, "justify-content: space-between");
        StringAssert.Contains(css, "background: transparent");
        StringAssert.Contains(css, "--settings-scope-field-width");
        StringAssert.Contains(css, "--settings-form-field-width");
        StringAssert.Contains(css, ".settings-search-row");
        StringAssert.Contains(css, "grid-template-columns: var(--settings-control-label-width) minmax(0, 1fr)");
    }

    [TestMethod]
    public void SettingsSharedDraftMarkup_HasOneLiveResultRegionAndAccessibleReviewContracts()
    {
        var root = FindRepositoryRoot();
        var index = File.ReadAllText(Path.Combine(root, "src/Clc.PatronRegistration.Web/Views/Settings/Index.cshtml"));
        var row = File.ReadAllText(Path.Combine(root, "src/Clc.PatronRegistration.Web/Views/Settings/_SettingRow.cshtml"));
        var css = File.ReadAllText(Path.Combine(root, "src/Clc.PatronRegistration.Web/wwwroot/css/settings.css"));

        Assert.AreEqual(1, index.Split("id=\"search-status\"").Length - 1);
        StringAssert.Contains(index, "id=\"search-status\" aria-live=\"polite\"");
        Assert.IsFalse(index.Contains("id=\"settings-filter-empty\"", StringComparison.Ordinal));
        StringAssert.Contains(index, "<option value=\"draft\">Shared draft changes</option>");
        StringAssert.Contains(index, "if (draftChangeCount > 0)");
        StringAssert.Contains(index, "Review @draftChangeCount shared draft @(draftChangeCount == 1 ? \"change\" : \"changes\")");
        StringAssert.Contains(index, "@if (Model.ActiveDraft is not null)");
        StringAssert.Contains(index, "data-label-template=\"Save {count} {noun} to shared draft\"");
        StringAssert.Contains(index, "data-label-template=\"Add {count} {noun} to shared draft\"");
        StringAssert.Contains(index, "name=\"ExpectedDraftId\"");
        StringAssert.Contains(index, "name=\"ExpectedDraftRevision\"");
        Assert.IsFalse(index.Contains("Create shared draft", StringComparison.Ordinal));
        Assert.IsFalse(index.Contains("No shared draft", StringComparison.Ordinal));
        StringAssert.Contains(css, ".settings-search input[type=\"search\"]");
        Assert.IsFalse(css.Contains(".settings-search input {", StringComparison.Ordinal));
        StringAssert.Contains(row, "$\"Shared draft: {imageSummary}\"");
        StringAssert.Contains(row, "definition.IsSensitive ? string.Empty");
        Assert.IsFalse(row.Contains("<details class=\"setting-row\" open=", StringComparison.Ordinal));
        Assert.IsFalse(row.Contains("tabindex=\"-1\"", StringComparison.Ordinal));
        StringAssert.Contains(index, "bool categoryOpen = rows.Any(row => row.DraftOperation is not null);");
    }

    [TestMethod]
    public void SettingsSharedDraftMarkup_GroupsSummaryPreviewCreationAndPreviewHistory()
    {
        var root = FindRepositoryRoot();
        var index = File.ReadAllText(Path.Combine(root, "src/Clc.PatronRegistration.Web/Views/Settings/Index.cshtml"));

        StringAssert.Contains(index, "<header class=\"draft-summary\">");
        StringAssert.Contains(index, "id=\"draft-panel-title\">Shared draft #");
        StringAssert.Contains(index, "class=\"draft-scope\"");
        StringAssert.Contains(index, "class=\"draft-counts\"");
        StringAssert.Contains(index, "change\" : \"changes\") ·");
        StringAssert.Contains(index, "active preview @(activePreviewCount == 1 ? \"link\" : \"links\")");
        StringAssert.Contains(index, "<div class=\"draft-actions\" role=\"group\" aria-label=\"Draft lifecycle actions\">");
        foreach (var action in new[] { "data-review-draft", ">Publish draft</button>", ">Discard shared draft</button>" })
            StringAssert.Contains(index, action);

        StringAssert.Contains(index, "<details class=\"preview-tools\" aria-labelledby=\"preview-tools-title\">");
        StringAssert.Contains(index, "<summary id=\"preview-tools-title\">Preview tools and links (@activePreviewCount)</summary>");
        var createRow = index.IndexOf("<div class=\"preview-create-row\">", StringComparison.Ordinal);
        Assert.IsTrue(createRow >= 0);
        var branchField = index.IndexOf("<div class=\"preview-branch-field\">", createRow, StringComparison.Ordinal);
        Assert.IsTrue(branchField > createRow);
        var createActions = index.IndexOf("<div class=\"preview-create-actions\" role=\"group\" aria-label=\"Create preview link\">", branchField, StringComparison.Ordinal);
        Assert.IsTrue(createActions > branchField);
        Assert.IsFalse(index.Contains("id=\"preview-create-title\"", StringComparison.Ordinal));
        var safeAction = index.IndexOf("type=\"submit\" name=\"AllowLiveSubmission\" value=\"false\" class=\"preview-create-option preview-create-safe\"", StringComparison.Ordinal);
        var liveAction = index.IndexOf("type=\"submit\" name=\"AllowLiveSubmission\" value=\"true\" class=\"preview-create-option preview-create-live\"", StringComparison.Ordinal);
        Assert.IsTrue(safeAction >= 0);
        Assert.IsTrue(liveAction > safeAction);
        StringAssert.Contains(index, "<span>Cannot create patron records.</span>");
        StringAssert.Contains(index, "<span>Can create real patron records.</span>");
        Assert.IsFalse(index.Contains("type=\"radio\" name=\"AllowLiveSubmission\"", StringComparison.Ordinal));
        Assert.IsFalse(index.Contains(">Create preview link</button>", StringComparison.Ordinal));
        StringAssert.Contains(index, "<section class=\"preview-links\" aria-labelledby=\"preview-links-title\">");
        StringAssert.Contains(index, ">Existing preview links</h4>");
        StringAssert.Contains(index, "<article class=\"preview-link-item\" aria-labelledby=\"preview-link-@link.PreviewLinkId-title\">");
        StringAssert.Contains(index, "<h5 id=\"preview-link-@link.PreviewLinkId-title\" class=\"preview-link-name\">");
        Assert.IsFalse(index.Contains("<h4>Preview link #@link.PreviewLinkId</h4>", StringComparison.Ordinal));
        StringAssert.Contains(index, "<p class=\"preview-link-branch\">");
        StringAssert.Contains(index, "<p class=\"preview-link-status\">");
        StringAssert.Contains(index, "<div class=\"preview-link-actions\" role=\"group\" aria-label=\"Actions for preview link");
    }

    [TestMethod]
    public void SettingsView_UsesStaffStateContextFiltersAndOperationSpecificReviewCopy()
    {
        var root = FindRepositoryRoot();
        var index = File.ReadAllText(Path.Combine(root, "src/Clc.PatronRegistration.Web/Views/Settings/Index.cshtml"));
        var row = File.ReadAllText(Path.Combine(root, "src/Clc.PatronRegistration.Web/Views/Settings/_SettingRow.cshtml"));
        var script = File.ReadAllText(Path.Combine(root, "src/Clc.PatronRegistration.Web/wwwroot/js/settings.js"));

        StringAssert.Contains(index, "id=\"setting-status-filter\"");
        StringAssert.Contains(index, "class=\"settings-actions-context\"");
        StringAssert.Contains(index, "<strong>@Model.OrganizationName</strong>");
        StringAssert.Contains(index, "<strong>@formDisplayName</strong>");
        Assert.IsFalse(index.Contains("data-review-title=\"Add changes to shared draft\"", StringComparison.Ordinal));
        StringAssert.Contains(index, "data-review-title=\"Save changes live\"");
        StringAssert.Contains(index, "MMM d, yyyy, h:mm tt 'UTC'");
        StringAssert.Contains(row, "data-customized-here=\"@resolution.OwnsOverride.ToString().ToLowerInvariant()\"");
        StringAssert.Contains(row, "data-live-summary=\"@liveSummary\"");
        Assert.IsFalse(row.Contains(">Apply</button>", StringComparison.Ordinal));
        StringAssert.Contains(script, "Unsaved in this browser");
        StringAssert.Contains(script, "row.dataset.customizedHere === \"true\"");
    }

    [TestMethod]
    public void FormsView_UsesOrganizationNamesWithIdsAsSecondaryDetails()
    {
        var root = FindRepositoryRoot();
        var forms = File.ReadAllText(Path.Combine(root, "src/Clc.PatronRegistration.Web/Views/Settings/Forms.cshtml"));

        StringAssert.Contains(forms, "Model.OrganizationNames");
        StringAssert.Contains(forms, "class=\"form-owner-name\"");
        StringAssert.Contains(forms, "Organization ID @form.OrganizationId");
        StringAssert.Contains(forms, "Organization ID @legacy.OwnerOrganizationId");
        StringAssert.Contains(forms, "id=\"form-owner-id\" type=\"hidden\"");
        StringAssert.Contains(forms, "<dl class=\"form-owner-display\">");
        StringAssert.Contains(forms, "<dt>Owning organization</dt>");
        StringAssert.Contains(forms, "<dd>");
        Assert.IsFalse(forms.Contains("<label for=\"form-owner-id\">", StringComparison.Ordinal));
        StringAssert.Contains(forms, "<h2 id=\"create-form-heading\">Create a named form</h2>");
        StringAssert.Contains(forms, ">Create named form</button>");
        Assert.IsFalse(forms.Contains("inferred owner @legacy.OwnerOrganizationId", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SettingsHelp_UsesCurrentBrowserAndSharedDraftControlNames()
    {
        var root = FindRepositoryRoot();
        var help = File.ReadAllText(Path.Combine(root, "src/Clc.PatronRegistration.Web/Views/Settings/Help.cshtml"));

        foreach (var current in new[] { "Save N changes live", "Add N changes to shared draft", "Save N changes live instead",
            "Discard unsaved changes", "Shared draft changes", "Review N shared draft changes",
            "Discard shared draft", "Safe preview", "Live-submission preview" })
            StringAssert.Contains(help, current);
        foreach (var obsolete in new[] { "Review and save now", "Save changes to draft", ">Discard draft<",
            "Allow this preview to create real patron records" })
            Assert.IsFalse(help.Contains(obsolete, StringComparison.Ordinal), obsolete);
        StringAssert.Contains(help, "It is not visible to other staff");
        StringAssert.Contains(help, "Shared draft changes are visible to other authorized staff");
    }

    [DataTestMethod]
    [DataRow("<img src=x onerror=alert(1)>")]
    [DataRow("<script>alert(1)</script>")]
    [DataRow("Name onerror=alert(1)")]
    [DataRow("Line one\nLine two")]
    [DataRow("Tab\tlabel")]
    public void LabelCatalog_RejectsMarkupAndControlCharacters(string value)
    {
        var definition = new SettingCatalog().All.Single(setting => setting.Key == "label.NameFirst");

        Assert.IsNotNull(definition.Validate(value));
    }

    [DataTestMethod]
    [DataRow("Children's name & pronouns")]
    [DataRow("Téléphone — résidence")]
    [DataRow("姓・名")]
    public void LabelCatalog_AcceptsPunctuationAndNonAsciiText(string value)
    {
        var definition = new SettingCatalog().All.Single(setting => setting.Key == "label.NameFirst");

        Assert.IsNull(definition.Validate(value));
    }

    [TestMethod]
    public void SensitiveUpsert_RequiresCompleteNonemptyReplacement()
    {
        var definition = new SettingCatalog().All.Single(setting => setting.Key == "postmark_api_key");

        Assert.IsNotNull(definition.Validate(string.Empty));
        Assert.IsNull(definition.Validate("complete-replacement-secret"));
    }

    [TestMethod]
    public void SensitiveEditor_IsAlwaysEmptyAndOnlyRevealsNewlyTypedValue()
    {
        var root = FindRepositoryRoot();
        var partial = File.ReadAllText(Path.Combine(root,
            "src/Clc.PatronRegistration.Web/Views/Settings/_SettingRow.cshtml"));

        StringAssert.Contains(partial, "definition.IsSensitive ? string.Empty");
        StringAssert.Contains(partial, "Enter a new value to replace the existing secret");
        StringAssert.Contains(partial, "Existing values are write-only and cannot be revealed");
    }

    [TestMethod]
    public void ValidationErrorDomSinks_UseTextContent()
    {
        var root = FindRepositoryRoot();
        var registrationView = File.ReadAllText(Path.Combine(root,
            "src/Clc.PatronRegistration.Web/Views/Registration/_RegistrationForm.cshtml"));
        var validationScript = File.ReadAllText(Path.Combine(root,
            "src/Clc.PatronRegistration.Web/wwwroot/js/aspnet-validation.js"));

        StringAssert.Contains(registrationView, "li.textContent = element.value");
        Assert.IsFalse(registrationView.Contains("li.innerHTML = element.value", StringComparison.Ordinal));
        StringAssert.Contains(validationScript, "li.textContent = this.summary[key]");
        StringAssert.Contains(validationScript, "spans[i].textContent = message");
        Assert.IsFalse(validationScript.Contains("li.innerHTML = this.summary[key]", StringComparison.Ordinal));
    }
    [DataTestMethod]
    [DataRow("metadata")]
    [DataRow("setting-added")]
    [DataRow("setting-removed")]
    [DataRow("draft")]
    [DataRow("preview")]
    [DataRow("version")]
    public void DeletionFingerprint_ChangesForEveryConfirmedContentCategory(string changedCategory)
    {
        var target = new FormCodeDeletionTarget(2, "kids", FormCodeDeletionKind.LibraryDefinition, false);
        var metadata = new List<string> { "m|2|time|hash" };
        var settings = new List<string> { "s|2|registration_text|VALUE_HASH" };
        var drafts = new List<string> { "d|10|2|Active|0|time" };
        var previews = new List<string> { "p|20|10|0||time|3" };
        var versions = new List<string> { "v|2|1" };
        var original = FormCodeDeletionFingerprint.Compute(target, [2, 3], metadata, versions, settings, drafts, previews);

        switch (changedCategory)
        {
            case "metadata":
                metadata[0] += "x";
                break;
            case "setting-added":
                settings.Add("s|3|label.NameFirst|OTHER_HASH");
                break;
            case "setting-removed":
                settings.Clear();
                break;
            case "draft":
                drafts[0] += "x";
                break;
            case "preview":
                previews[0] += "x";
                break;
            case "version":
                versions[0] = "v|2|2";
                break;
        }

        var changed = FormCodeDeletionFingerprint.Compute(target, [2, 3], metadata, versions, settings, drafts, previews);
        Assert.AreNotEqual(original, changed);
        Assert.AreEqual(original, FormCodeDeletionFingerprint.Compute(target, [2, 3],
            ["m|2|time|hash"], ["v|2|1"], ["s|2|registration_text|VALUE_HASH"],
            ["d|10|2|Active|0|time"], ["p|20|10|0||time|3"]));
    }

    [TestMethod]
    public void DeletionFingerprint_IsOpaqueAndContainsNoSettingValue()
    {
        const string recognizableSecret = "recognizable-sensitive-setting-value";
        var valueHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(recognizableSecret)));
        var target = new FormCodeDeletionTarget(2, "kids", FormCodeDeletionKind.LibraryDefinition, false);

        var fingerprint = FormCodeDeletionFingerprint.Compute(target, [2], [], [],
            [$"s|2|postmark_api_key|{valueHash}"], [], []);

        Assert.AreEqual(64, fingerprint.Length);
        Assert.IsFalse(fingerprint.Contains(recognizableSecret, StringComparison.Ordinal));
    }

    [TestMethod]
    public void RequireCatalog_ExactlyMatchesDbConfiguredRequiredMetadata()
    {
        var metadataFields = typeof(RegistrationMetadata).GetProperties()
            .Where(property => property.GetCustomAttributes(typeof(DbConfiguredRequired), true).Any())
            .Select(property => property.Name)
            .OrderBy(name => name)
            .ToArray();
        var catalog = new SettingCatalog();
        var catalogFields = catalog.All.Where(definition => definition.Group == SettingGroup.Require)
            .Select(definition => definition.Key["require.".Length..])
            .OrderBy(name => name)
            .ToArray();

        CollectionAssert.AreEqual(new[] { "EmailAddress", "PhoneVoice1", "RequestPickupBranchID", "User5" }, metadataFields);
        CollectionAssert.AreEqual(metadataFields, catalogFields);
        Assert.IsFalse(catalog.TryGet("require.ReceiveEreceipts", out _));
        Assert.IsTrue(catalog.TryGet("label.ReceiveEreceipts", out _));
        Assert.IsTrue(catalog.TryGet("alert.ReceiveEreceipts", out _));
    }

    [TestMethod]
    public void LabelCatalog_ExactlyMatchesDbConfiguredDisplayNameMetadata()
    {
        var metadataFields = typeof(RegistrationMetadata).GetProperties()
            .Where(property => property.GetCustomAttributes(typeof(DbConfiguredDisplayNameAttribute), true).Any())
            .Select(property => property.Name)
            .OrderBy(name => name)
            .ToArray();
        var catalog = new SettingCatalog();
        var catalogFields = catalog.All.Where(definition => definition.Group == SettingGroup.Label)
            .Select(definition => definition.Key["label.".Length..])
            .OrderBy(name => name)
            .ToArray();

        CollectionAssert.AreEqual(metadataFields, catalogFields);
        Assert.IsFalse(catalog.TryGet("label.AltEmailAddress", out _));
        Assert.IsTrue(catalog.TryGet("alert.AltEmailAddress", out _));
    }

    [DataTestMethod]
    [DataRow("added")]
    [DataRow("removed")]
    [DataRow("modified")]
    public void LibraryDeletionFingerprint_TracksContextualSystemMetadata(string change)
    {
        var target = new FormCodeDeletionTarget(2, "kids", FormCodeDeletionKind.LibraryCustomization, false);
        var metadata = new List<string> { "m|1|time-1|hash-1", "m|2|time-2|hash-2" };
        var original = FormCodeDeletionFingerprint.Compute(target, [2, 3], metadata, [], [], [], []);

        if (change == "added")
        {
            metadata[0] = "m|1|time-1|new-system-hash";
        }
        else if (change == "removed")
        {
            metadata.RemoveAt(0);
        }
        else
        {
            metadata[0] = "m|1|time-3|hash-1";
        }

        Assert.AreNotEqual(original, FormCodeDeletionFingerprint.Compute(target, [2, 3], metadata, [], [], [], []));
    }

    [TestMethod]
    public void ConfirmationSnapshotRepositoryPath_IsReadOnly()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root,
            "src/Clc.PatronRegistration.Web/Settings/SettingsAdministrationRepository.cs"));
        var start = source.IndexOf("public FormCodeDeletionSnapshot? GetFormCodeDeletionSnapshot", StringComparison.Ordinal);
        var end = source.IndexOf("public void DeleteFormCode", start, StringComparison.Ordinal);
        var method = source[start..end];

        StringAssert.Contains(method, "BuildDeletionSnapshot(connection, null");
        Assert.IsFalse(method.Contains("EnsureVersionRow", StringComparison.Ordinal));
        Assert.IsFalse(method.Contains("BeginTransaction", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SaveToSharedDraft_LocksDraftRangeBeforeScopeVersion()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root,
            "src/Clc.PatronRegistration.Web/Settings/SettingsAdministrationRepository.cs"));
        var start = source.IndexOf("public SaveToDraftResult SaveToSharedDraft", StringComparison.Ordinal);
        var end = source.IndexOf("public void RemoveDraftChange", start, StringComparison.Ordinal);
        var method = source[start..end];
        var draftLock = method.IndexOf("RegistrationSettingDrafts with(updlock,holdlock)", StringComparison.Ordinal);
        var versionLock = method.IndexOf("EnsureVersionRow", StringComparison.Ordinal);

        Assert.IsTrue(draftLock >= 0);
        Assert.IsTrue(versionLock > draftLock);
    }

    [TestMethod]
    public void GloballyRequiredFields_AreNotExposedAsDynamicRequirements()
    {
        var requiredFields = typeof(RegistrationMetadata).GetProperties()
            .Where(property => property.GetCustomAttributes(typeof(RequiredAttribute), true).Any())
            .Select(property => property.Name)
            .ToList();
        var catalog = new SettingCatalog();

        CollectionAssert.AreEquivalent(new[] { "NameFirst", "NameLast", "Birthdate", "Password" }, requiredFields);
        foreach (var field in requiredFields)
        {
            Assert.IsFalse(catalog.TryGet($"require.{field}", out _));
            Assert.IsTrue(catalog.TryGet($"label.{field}", out _));
            Assert.IsTrue(catalog.TryGet($"alert.{field}", out _));
        }
    }

    [TestMethod]
    public void FormCodeDeletionOwnership_DoesNotInferSystemOwnershipFromOtherLibraries()
    {
        Assert.IsNull(FormCodeDeletionOwnership.Classify(1, "shared", 1, false, false, false));
        Assert.AreEqual(FormCodeDeletionKind.LibraryDefinition,
            FormCodeDeletionOwnership.Classify(2, "shared", 1, true, false, true)!.Kind);
        Assert.AreEqual(FormCodeDeletionKind.LibraryDefinition,
            FormCodeDeletionOwnership.Classify(9, "shared", 1, true, false, true)!.Kind);
    }

    [TestMethod]
    public void FormCodeDeletionOwnership_DistinguishesSystemDefinitionsAndLibraryCustomizations()
    {
        Assert.AreEqual(FormCodeDeletionKind.SystemDefinition,
            FormCodeDeletionOwnership.Classify(1, "shared", 1, true, true, true)!.Kind);
        Assert.AreEqual(FormCodeDeletionKind.LibraryCustomization,
            FormCodeDeletionOwnership.Classify(2, "shared", 1, true, true, true)!.Kind);
    }

    [TestMethod]
    public void FormCodeDeletionLockOrder_IsDraftThenPreviewThenSettingsMetadataThenGeneration()
    {
        CollectionAssert.AreEqual(
            new[]
            {
                FormCodeDeletionLockStep.Drafts,
                FormCodeDeletionLockStep.PreviewLinks,
                FormCodeDeletionLockStep.Metadata,
                FormCodeDeletionLockStep.ScopeVersions,
                FormCodeDeletionLockStep.Settings,
                FormCodeDeletionLockStep.LiveSettingsGeneration
            },
            FormCodeDeletionLockOrder.Required.ToArray());
    }

    [TestMethod]
    public void PreviewRepositoryLockOrder_IsCandidateThenDraftThenLinkThenChangesThenGeneration()
    {
        CollectionAssert.AreEqual(
            new[]
            {
                PreviewLockStep.CandidateLookupOutsideTransaction,
                PreviewLockStep.Draft,
                PreviewLockStep.PreviewLink,
                PreviewLockStep.DraftChanges,
                PreviewLockStep.LiveSettingsGeneration
            },
            PreviewLockOrder.Required.ToArray());
    }

    [TestMethod]
    public void PreviewModeReplacement_UsesDraftThenLinkLockAndNeverUpdatesModeInPlace()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root,
            "src/Clc.PatronRegistration.Web/Settings/SettingsAdministrationRepository.cs"));
        var start = source.IndexOf("public long? ReplacePreviewLinkMode", StringComparison.Ordinal);
        var end = source.IndexOf("public IReadOnlyList<FormCodeMetadata>", start, StringComparison.Ordinal);
        var method = source[start..end];

        var draftAndLinkLock = method.IndexOf("LockPreviewLinkDraft", StringComparison.Ordinal);
        var revoke = method.IndexOf("set RevokedAtUtc=SYSUTCDATETIME()", StringComparison.Ordinal);
        var replacementInsert = method.IndexOf("insert dbo.RegistrationSettingPreviewLinks", StringComparison.Ordinal);
        Assert.IsTrue(draftAndLinkLock >= 0 && revoke > draftAndLinkLock && replacementInsert > revoke);
        Assert.IsFalse(method.Contains("set AllowLiveSubmission=", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(method, "replacementTokenHash");
        StringAssert.Contains(method, "current.OperationalBranchId");
        StringAssert.Contains(method, "current.ExpiresAtUtc");
    }

    [DataTestMethod]
    [DataRow("postmark_api_key", true)]
    [DataRow("melissa_data_api_key", true)]
    [DataRow("registration_text", false)]
    public void DraftChangeRemovalAudit_UsesCatalogSensitivity(string key, bool expectedSensitive)
    {
        var catalog = new SettingCatalog().All.ToDictionary(definition => definition.Key, StringComparer.OrdinalIgnoreCase);

        var definition = DraftChangeAuditClassification.RequireDefinition(key, catalog);

        Assert.AreEqual(expectedSensitive, definition.IsSensitive);
    }

    [DataTestMethod]
    [DataRow(null, "false")]
    [DataRow("", "false")]
    [DataRow("true", "true")]
    [DataRow("false", "false")]
    public void BooleanEditor_DefaultMatchesEffectiveRuntimeBehavior(string? configuredValue, string expected)
    {
        var definition = new SettingDefinition("require.NameFirst", "Name", "", SettingValueType.Boolean);

        Assert.AreEqual(expected, SettingEditorDefaults.ValueFor(definition, configuredValue));
    }

    [DataTestMethod]
    [DataRow("registration_logon_user_id")]
    [DataRow("ecard_patron_code_id")]
    [DataRow("teacher_patron_code_id")]
    [DataRow("student_patron_code_id")]
    [DataRow("valid_address_patron_code_id")]
    [DataRow("valid_address_plus_name_patron_code_id")]
    [DataRow("patron_code_id")]
    public void ConfiguredPolarisIdentifiers_MustBePositive(string key)
    {
        var catalog = new SettingCatalog();
        Assert.IsTrue(catalog.TryGet(key, out var definition));

        Assert.IsNotNull(definition.Validate("0"));
        Assert.IsNotNull(definition.Validate("-1"));
        Assert.IsNull(definition.Validate("1"));
    }

    [DataTestMethod]
    [DataRow("mailing_list_record_set_id")]
    [DataRow("valid_address_record_set_id")]
    [DataRow("valid_address_plus_name_record_set_id")]
    [DataRow("invalid_address_record_set_id")]
    public void OptionalRecordSetIdentifiers_AllowZeroButRejectNegative(string key)
    {
        var catalog = new SettingCatalog();
        Assert.IsTrue(catalog.TryGet(key, out var definition));

        Assert.IsNull(definition.Validate("0"));
        Assert.IsNotNull(definition.Validate("-1"));
        Assert.IsNull(definition.Validate("1"));
    }

    [TestMethod]
    public void FirstSensitiveDraftMutation_IsARevocationTransitionOnlyOnce()
    {
        var catalog = new SettingCatalog().All.ToDictionary(definition => definition.Key, StringComparer.OrdinalIgnoreCase);

        Assert.IsTrue(SensitiveDraftPolicy.BecameSensitive(
            ["registration_text"], ["registration_text", "postmark_api_key"], catalog));
        Assert.IsFalse(SensitiveDraftPolicy.BecameSensitive(
            ["postmark_api_key"], ["postmark_api_key", "melissa_data_api_key"], catalog));
        Assert.IsFalse(SensitiveDraftPolicy.BecameSensitive(
            ["postmark_api_key"], ["registration_text"], catalog));
    }

    [DataTestMethod]
    [DataRow("postmark_api_key")]
    [DataRow("melissa_data_api_key")]
    public void SensitiveAuditRows_AreOmittedForLibraryButVisibleToGlobalAdministrator(string settingKey)
    {
        var row = new SettingsAuditRow(
            1, DateTime.UtcNow, "DraftChangeRemoved", 2, 2, string.Empty, settingKey,
            null, null, true, true, "global@example.org", null, null, null);

        Assert.AreEqual(0, SettingsAuditVisibility.ForAdministrator([row], false).Count());
        Assert.AreSame(row, SettingsAuditVisibility.ForAdministrator([row], true).Single());
    }

    [TestMethod]
    public void OrdinaryDraftChangeRemovalAudit_RemainsVisibleToLibraryAdministrator()
    {
        var row = new SettingsAuditRow(
            1, DateTime.UtcNow, "DraftChangeRemoved", 2, 2, string.Empty, "registration_text",
            null, null, false, true, "library@example.org", null, null, null);

        Assert.AreSame(row, SettingsAuditVisibility.ForAdministrator([row], false).Single());
    }

    [TestMethod]
    public void Resolver_UsesAllSixExplicitLevels()
    {
        var precedence = SettingsResolver.BuildPrecedence(3, 2, 1, "kids");

        CollectionAssert.AreEqual(
            new[] { (3, "kids"), (3, ""), (2, "kids"), (2, ""), (1, "kids"), (1, "") },
            precedence.Select(source => (source.OrganizationId, source.FormCode)).ToArray());
    }

    [TestMethod]
    public void Resolver_ResolvesNamedAndDefaultValuesAtEveryPrecedenceLevel()
    {
        var rows = new[]
        {
            Setting(3, "branch_named", "branch named", "kids"),
            Setting(3, "branch_default", "branch default"),
            Setting(2, "library_named", "library named", "kids"),
            Setting(2, "library_default", "library default"),
            Setting(1, "system_named", "system named", "kids"),
            Setting(1, "system_default", "system default")
        };
        var snapshot = new SettingsResolverSnapshot(rows);
        var resolver = new SettingsResolver();

        foreach (var expected in new[]
        {
            (Key: "branch_named", Value: "branch named", Source: "Branch", Form: "kids"),
            (Key: "branch_default", Value: "branch default", Source: "Branch", Form: ""),
            (Key: "library_named", Value: "library named", Source: "Library", Form: "kids"),
            (Key: "library_default", Value: "library default", Source: "Library", Form: ""),
            (Key: "system_named", Value: "system named", Source: "System", Form: "kids"),
            (Key: "system_default", Value: "system default", Source: "System", Form: "")
        })
        {
            var result = resolver.Resolve(snapshot, expected.Key, 3, 2, "kids", 1);
            Assert.AreEqual(expected.Value, result.EffectiveValue, expected.Key);
            Assert.AreEqual(expected.Source, result.SourceOrganizationType, expected.Key);
            Assert.AreEqual(expected.Form, result.SourceFormCode, expected.Key);
        }
    }

    [TestMethod]
    public void Resolver_IndexUsesCaseInsensitiveKeysAndFormCodes()
    {
        var snapshot = new SettingsResolverSnapshot(
            [Setting(3, "MixedKey", "configured", "KidsForm")]);

        var result = new SettingsResolver().Resolve(snapshot, "mixedkey", 3, 2, "kidsform", 1);

        Assert.AreEqual("configured", result.EffectiveValue);
        Assert.IsTrue(result.OwnsOverride);
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
    public void DbSettingProvider_CapturesOneCacheGenerationForAllSettingReads()
    {
        var cache = new TestCache
        {
            OrganizationCache =
            [
                new() { OrganizationID = 1, OrganizationCodeID = 1, Name = "System" },
                new() { OrganizationID = 2, OrganizationCodeID = 2, Name = "Generation N library", ParentOrganizationID = 1 },
                new() { OrganizationID = 3, OrganizationCodeID = 3, Name = "Generation N branch", ParentOrganizationID = 2 }
            ],
            SettingsCache =
            [
                Setting(3, "registration_text", "generation N", "kids"),
                Setting(3, "display_responsible_person_field", "true", "kids"),
                Setting(3, "require.User5", "true", "kids"),
                Setting(3, "registration_logon_user_id", "42", "kids")
            ]
        };

        var generationN = new DbSettingProvider(3, cache, "kids", 1);
        var generationNSnapshot = cache.GetSnapshot();
        Assert.AreSame(generationNSnapshot.IndexedSettings, generationN.ResolutionSnapshot);

        cache.OrganizationCache =
        [
            new() { OrganizationID = 1, OrganizationCodeID = 1, Name = "System" },
            new() { OrganizationID = 4, OrganizationCodeID = 2, Name = "Generation N+1 library", ParentOrganizationID = 1 },
            new() { OrganizationID = 3, OrganizationCodeID = 3, Name = "Generation N+1 branch", ParentOrganizationID = 4 }
        ];
        cache.SettingsCache =
        [
            Setting(3, "registration_text", "generation N+1", "kids"),
            Setting(3, "display_responsible_person_field", "false", "kids"),
            Setting(3, "require.User5", "false", "kids"),
            Setting(3, "registration_logon_user_id", "84", "kids")
        ];

        Assert.AreEqual(2, generationN.LibraryId);
        Assert.AreEqual("generation N", generationN.RegistrationText);
        Assert.IsTrue(generationN.DisplayResponsiblePersonField);
        Assert.IsTrue(generationN.GetFieldRequired("User5"));
        CollectionAssert.AreEquivalent(new[] { "User5" }, generationN.GetRequiredFields());
        Assert.AreEqual(42, generationN.RegistrationLogonUserId);

        var generationN1 = new DbSettingProvider(3, cache, "kids", 1);

        Assert.AreEqual(4, generationN1.LibraryId);
        Assert.AreEqual("generation N+1", generationN1.RegistrationText);
        Assert.IsFalse(generationN1.DisplayResponsiblePersonField);
        Assert.IsFalse(generationN1.GetFieldRequired("User5"));
        CollectionAssert.AreEquivalent(Array.Empty<string>(), generationN1.GetRequiredFields());
        Assert.AreEqual(84, generationN1.RegistrationLogonUserId);
    }

    [TestMethod]
    public void DbSettingProvider_UsesPublishedIndexWithoutPerProviderGlobalScanOrCopy()
    {
        var rows = Enumerable.Range(0, 20_000)
            .Select(index => Setting(3, $"unused_{index}", index.ToString(), "kids"))
            .Append(Setting(3, "registration_text", "indexed value", "kids"))
            .ToList();
        var guardedSettings = new GuardedSettingsList(rows);
        var snapshot = new CacheSnapshot(guardedSettings,
        [
            new() { OrganizationID = 1, OrganizationCodeID = 1, Name = "System" },
            new() { OrganizationID = 2, OrganizationCodeID = 2, Name = "Library", ParentOrganizationID = 1 },
            new() { OrganizationID = 3, OrganizationCodeID = 3, Name = "Branch", ParentOrganizationID = 2 }
        ]);
        var cache = new SnapshotOnlyCache(snapshot);

        // Building the generation is allowed to enumerate the complete input
        // once. Every provider and read after publication must use the index.
        guardedSettings.ThrowOnEnumeration = true;
        const int providerCount = 250;
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        DbSettingProvider? last = null;
        for (var i = 0; i < providerCount; i++)
        {
            last = new DbSettingProvider(3, cache, "kids", 1);
            Assert.AreEqual("indexed value", last.RegistrationText);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.IsNotNull(last);
        Assert.AreSame(snapshot.IndexedSettings, last!.ResolutionSnapshot);
        Assert.AreEqual(providerCount, cache.SnapshotCalls);
        Assert.AreEqual(0, cache.SettingsPropertyReads);
        Assert.IsTrue(allocated < 10_000_000,
            $"Provider construction/read allocation unexpectedly scaled with the global settings collection: {allocated} bytes.");
    }

    [TestMethod]
    public void PreviewSettingProvider_UsesTheCapturedCacheGenerationForItsOverlay()
    {
        var cache = new TestCache
        {
            SettingsCache =
            [
                Setting(3, "registration_text", "generation N", "kids"),
                Setting(3, "display_responsible_person_field", "true", "kids"),
                Setting(3, "require.User5", "false", "kids")
            ]
        };
        var draft = Draft(3, new SettingMutation("require.User5", DraftOperation.Upsert, "true"));
        draft = draft with { FormCode = "kids" };

        var preview = new PreviewSettingProvider(draft, 3, cache, 1);

        cache.SettingsCache =
        [
            Setting(3, "registration_text", "generation N+1", "kids"),
            Setting(3, "display_responsible_person_field", "false", "kids"),
            Setting(3, "require.User5", "false", "kids")
        ];

        Assert.AreEqual("generation N", preview.RegistrationText);
        Assert.IsTrue(preview.DisplayResponsiblePersonField);
        Assert.IsTrue(preview.GetFieldRequired("User5"));
        CollectionAssert.AreEquivalent(new[] { "User5" }, preview.GetRequiredFields());
    }

    [TestMethod]
    public void CatalogKeys_AreUniqueAndRejectArbitrarySuffixes()
    {
        var catalog = new SettingCatalog();

        Assert.AreEqual(
            catalog.All.Count,
            catalog.All.Select(setting => setting.Key.ToLowerInvariant()).Distinct().Count());
        Assert.IsTrue(catalog.TryGet("require.PhoneVoice1", out _));
        Assert.IsFalse(catalog.TryGet("require.NameFirst", out _));
        Assert.IsFalse(catalog.TryGet("require.DropTable", out _));
    }

    [TestMethod]
    public void SqlSettingAllowlistsMatchEveryPersistableCatalogKey()
    {
        var expected = new SettingCatalog().All.Select(setting => setting.Key).ToArray();
        foreach (var compatibilityOnlyKey in new[]
        {
            "school_info_format", "hide_gender", "na_gender_text", "kiosk_registration_header"
        })
        {
            Assert.IsFalse(expected.Contains(compatibilityOnlyKey, StringComparer.OrdinalIgnoreCase), compatibilityOnlyKey);
        }
        var root = FindRepositoryRoot();
        foreach (var relativePath in new[] { "database/settings-administration.sql" })
        {
            var source = File.ReadAllText(Path.Combine(root, relativePath));
            const string begin = "/* BEGIN SETTING_CATALOG_ALLOWLIST */";
            const string end = "/* END SETTING_CATALOG_ALLOWLIST */";
            var start = source.IndexOf(begin, StringComparison.Ordinal);
            var finish = source.IndexOf(end, start + begin.Length, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0 && finish > start, $"The catalog allowlist markers are missing from {relativePath}.");
            var section = source[(start + begin.Length)..finish];
            var actual = Regex.Matches(section, @"\('([^']+)'\)")
                .Select(match => match.Groups[1].Value)
                .ToArray();

            Assert.AreEqual(expected.Length, actual.Length, $"The catalog allowlist count drifted in {relativePath}.");
            Assert.AreEqual(actual.Length, actual.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                $"The catalog allowlist contains duplicate keys in {relativePath}.");
            CollectionAssert.AreEquivalent(expected, actual, relativePath);
        }
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

    [TestMethod]
    public void EmptyScalarConversions_ReturnCallerDefaults_WhileStringRemainsEmpty()
    {
        var date = new DateTime(2025, 2, 3, 4, 5, 6, DateTimeKind.Utc);

        Assert.IsTrue(DbSettingProvider.ConvertToType(string.Empty, true));
        Assert.AreEqual(30, DbSettingProvider.ConvertToType(string.Empty, 30));
        Assert.AreEqual(1.5m, DbSettingProvider.ConvertToType(string.Empty, 1.5m));
        Assert.AreEqual(date, DbSettingProvider.ConvertToType(string.Empty, date));
        Assert.AreEqual(7, DbSettingProvider.ConvertToType<int?>(string.Empty, 7));
        Assert.AreEqual(date, DbSettingProvider.ConvertToType<DateTime?>(string.Empty, date));
        Assert.AreEqual(string.Empty, DbSettingProvider.ConvertToType(string.Empty, "fallback"));
    }

    [TestMethod]
    public void NullAndNonemptyScalarConversions_PreserveExistingBehavior()
    {
        var date = new DateTime(2025, 2, 3, 4, 5, 6, DateTimeKind.Utc);

        Assert.AreEqual(42, DbSettingProvider.ConvertToType<int>(null, 42));
        Assert.AreEqual(string.Empty, DbSettingProvider.ConvertToType<string>(null));
        Assert.IsTrue(DbSettingProvider.ConvertToType("true", false));
        Assert.AreEqual(31, DbSettingProvider.ConvertToType("31", 0));
        Assert.AreEqual(2.75m, DbSettingProvider.ConvertToType("2.75", 0m));
        Assert.AreEqual(date, DbSettingProvider.ConvertToType(date.ToString("O"), DateTime.MinValue));
        Assert.ThrowsException<FormatException>(() => DbSettingProvider.ConvertToType("not-a-boolean", false));
        Assert.ThrowsException<FormatException>(() => DbSettingProvider.ConvertToType("not-an-integer", 0));
    }

    [TestMethod]
    public void EmptyLegacySettingRow_IsConsumedUsingConfiguredScalarDefault()
    {
        var cache = new TestCache
        {
            SettingsCache = [new() { OrganizationID = 3, FormCode = string.Empty, Setting = "legacy_bool", Value = string.Empty }]
        };
        var provider = new DbSettingProvider(3, cache);

        Assert.IsTrue(provider.GetSetting("legacy_bool", true));
    }

    [TestMethod]
    public void DraftOperationValidation_RejectsUndefinedValuesBeforeRepositoryWork()
    {
        foreach (var operation in new[] { (DraftOperation)2, (DraftOperation)999 })
        {
            Assert.ThrowsException<InvalidOperationException>(() => DraftOperationValidation.RequireSupported(
                [new SettingMutation("registration_text", operation, "value")]));
        }

        DraftOperationValidation.RequireSupported(
            [new SettingMutation("registration_text", DraftOperation.Upsert, "value"),
             new SettingMutation("warning_text", DraftOperation.RemoveOverride, null)]);
    }

    [DataTestMethod]
    [DataRow("Upsert", true, DraftOperation.Upsert)]
    [DataRow("RemoveOverride", true, DraftOperation.RemoveOverride)]
    [DataRow("2", false, DraftOperation.Upsert)]
    [DataRow("999", false, DraftOperation.Upsert)]
    [DataRow("Unknown", false, DraftOperation.Upsert)]
    public void DraftOperationParsing_AcceptsOnlySupportedNames(string input, bool expected, DraftOperation expectedOperation)
    {
        Assert.AreEqual(expected, DraftOperationValidation.TryParseSupported(input, out var operation));
        if (expected) Assert.AreEqual(expectedOperation, operation);
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
    public void SettingsSnapshot_OmitsCatalogSensitiveValuesButKeepsUsefulSettings()
    {
        const string postmarkSecret = "recognizable-postmark-secret-123";
        const string melissaSecret = "recognizable-melissa-secret-456";
        var cache = CacheWith(
            Setting(1, "postmark_api_key", postmarkSecret),
            Setting(1, "melissa_data_api_key", melissaSecret),
            Setting(1, "dl_format", "Useful public text"));
        var provider = new DbSettingProvider(3, cache, string.Empty, 1);

        var snapshot = SettingsSnapshotSerializer.Serialize(provider);

        Assert.IsFalse(snapshot.Contains(postmarkSecret, StringComparison.Ordinal));
        Assert.IsFalse(snapshot.Contains(melissaSecret, StringComparison.Ordinal));
        StringAssert.Contains(snapshot, "Useful public text");
    }

    [DataTestMethod]
    [DataRow(SettingValueType.Html)]
    [DataRow(SettingValueType.EmailTemplate)]
    public void LongNonSensitiveAuditValues_ArePreservedInFull(SettingValueType type)
    {
        var value = new string('x', 25_000);
        var definition = new SettingDefinition("long", "Long", "Long value", type);

        Assert.IsNull(definition.Validate(value));
        Assert.AreEqual(value, AuditValueFormatter.Format(value, definition.IsSensitive));
        Assert.AreEqual(25_000, AuditValueFormatter.Format(value, false)!.Length);
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

        var provider = new PreviewSettingProvider(draft, 3, cache, 1);

        Assert.AreEqual("draft", provider.RegistrationText);
        Assert.AreEqual(string.Empty, provider.WarningText);
    }

    [TestMethod]
    public void NonDefaultSystemOrganization_IsUsedByLiveAdministrationAndPreviewResolution()
    {
        const int systemOrganizationId = 42;
        var cache = new TestCache
        {
            SettingsCache = [Setting(systemOrganizationId, "registration_text", "configured system")]
        };

        var live = new DbSettingProvider(3, cache, string.Empty, systemOrganizationId);
        var administration = new SettingsResolver().Resolve(
            cache.SettingsCache, "registration_text", 3, 2, string.Empty, systemOrganizationId);
        var draft = new SettingDraft(8, 3, string.Empty, 0, DraftStatus.Active, []);
        var preview = new PreviewSettingProvider(draft, 3, cache, systemOrganizationId);

        Assert.AreEqual("configured system", live.RegistrationText);
        Assert.AreEqual("configured system", administration.EffectiveValue);
        Assert.AreEqual("configured system", preview.RegistrationText);
    }

    [TestMethod]
    public void BranchDraft_ResolvesAtItsOperationalBranch()
    {
        var cache = CacheWith(Setting(1, "registration_text", "system"), Setting(2, "registration_text", "library"));
        var draft = Draft(3, new SettingMutation("registration_text", DraftOperation.Upsert, "branch draft"));

        var preview = new PreviewSettingProvider(draft, 3, cache, 1);

        Assert.AreEqual(3, preview.OrganizationId);
        Assert.AreEqual(2, preview.LibraryId);
        Assert.AreEqual("branch draft", preview.RegistrationText);
    }

    [TestMethod]
    public void LibraryDraft_IsMaskedByOperationalBranchOverride()
    {
        var cache = CacheWith(Setting(1, "registration_text", "system"), Setting(3, "registration_text", "branch"));
        var draft = Draft(2, new SettingMutation("registration_text", DraftOperation.Upsert, "library draft"));

        var preview = new PreviewSettingProvider(draft, 3, cache, 1);

        Assert.AreEqual("branch", preview.RegistrationText);
    }

    [TestMethod]
    public void SystemDraft_IsMaskedByLibraryAndBranchOverrides()
    {
        var cache = CacheWith(
            Setting(1, "registration_text", "system"),
            Setting(2, "registration_text", "library"),
            Setting(3, "registration_text", "branch"));
        var draft = Draft(1, new SettingMutation("registration_text", DraftOperation.Upsert, "system draft"));

        Assert.AreEqual("branch", new PreviewSettingProvider(draft, 3, cache, 1).RegistrationText);
    }

    [TestMethod]
    public void SystemDraft_IsVisibleWhenNoLowerOverrideMasksIt()
    {
        var cache = CacheWith(Setting(1, "registration_text", "system"));
        var draft = Draft(1, new SettingMutation("registration_text", DraftOperation.Upsert, "system draft"));

        Assert.AreEqual("system draft", new PreviewSettingProvider(draft, 3, cache, 1).RegistrationText);
    }

    [DataTestMethod]
    [DataRow(3, "library")]
    [DataRow(2, "branch")]
    [DataRow(1, "branch")]
    public void RemoveOverride_RemovesOnlyTheDraftScope(int draftOrganizationId, string expected)
    {
        var rows = new List<RegistrationFormSetting> { Setting(1, "registration_text", "system") };
        if (draftOrganizationId != 2)
        {
            rows.Add(Setting(2, "registration_text", "library"));
        }
        if (draftOrganizationId != 3)
        {
            rows.Add(Setting(3, "registration_text", "branch"));
        }
        var cache = CacheWith(rows.ToArray());
        var draft = Draft(draftOrganizationId, new SettingMutation("registration_text", DraftOperation.RemoveOverride, null));

        Assert.AreEqual(expected, new PreviewSettingProvider(draft, 3, cache, 1).RegistrationText);
    }

    [TestMethod]
    public void LibraryCustomizationDeletion_InvalidatesLibraryAndEveryBranchScope()
    {
        var affectedScopes = SettingsAdministrationRepository.AffectedVersionScopes(new[] { 2, 3, 4, 3 });

        CollectionAssert.AreEquivalent(new[] { 2, 3, 4 }, affectedScopes.ToList());
    }

    [TestMethod]
    public void SettingsEditor_UsesDirectSemanticEditorContract()
    {
        var root = FindRepositoryRoot();
        var index = File.ReadAllText(Path.Combine(root, "src/Clc.PatronRegistration.Web/Views/Settings/Index.cshtml"));
        var partial = File.ReadAllText(Path.Combine(root, "src/Clc.PatronRegistration.Web/Views/Settings/_SettingRow.cshtml"));
        var batch = File.ReadAllText(Path.Combine(root, "src/Clc.PatronRegistration.Web/Views/Settings/_BatchSettingRow.cshtml"));
        var script = File.ReadAllText(Path.Combine(root, "src/Clc.PatronRegistration.Web/wwwroot/js/settings.js"));
        var css = File.ReadAllText(Path.Combine(root, "src/Clc.PatronRegistration.Web/wwwroot/css/settings.css"));

        Assert.IsFalse(partial.Contains("<select id=\"@operationId\"", StringComparison.Ordinal));
        StringAssert.Contains(partial, "class=\"operation\" type=\"hidden\"");
        StringAssert.Contains(partial, "class=\"image-setting\"");
        StringAssert.Contains(partial, "class=\"image-upload-trigger\"");
        StringAssert.Contains(partial, "class=\"image-choose-another\"");
        StringAssert.Contains(partial, "class=\"image-undo-pending\"");
        StringAssert.Contains(partial, "class=\"setting-mode-group\"");
        StringAssert.Contains(partial, "value=\"inherit\" data-mode=\"inherit\"");
        StringAssert.Contains(partial, "value=\"customize\" data-mode=\"customize\"");
        StringAssert.Contains(partial, "boolean-mode-group");
        StringAssert.Contains(partial, "class=\"plain-text-preview\"");
        StringAssert.Contains(index, "class=\"batch-settings");
        StringAssert.Contains(index, "id=\"batch-@batchHeadingPrefix-field\"");
        StringAssert.Contains(batch, "class=\"batch-browser-status\"");
        StringAssert.Contains(batch, "headers=\"batch-@columnHeadingPrefix-effective\"");
        StringAssert.Contains(partial, "bool.TryParse(Model.InheritedValue");
        StringAssert.Contains(batch, "bool.TryParse(Model.InheritedValue");
        StringAssert.Contains(partial, "Model.InheritedSourceDescription");
        StringAssert.Contains(batch, "Model.InheritedSourceDescription");
        StringAssert.Contains(css, "clip-path: inset(50%)");
        Assert.IsFalse(css.Contains(".batch-settings thead { display: none", StringComparison.Ordinal));

        Assert.IsFalse(partial.Contains("data-candidate-operation", StringComparison.Ordinal));
        Assert.IsFalse(partial.Contains("Keep change", StringComparison.Ordinal));
        Assert.IsFalse(partial.Contains("class=\"edit-setting\"", StringComparison.Ordinal));
        Assert.IsFalse(script.Contains("data-candidate-operation", StringComparison.Ordinal));
        Assert.IsFalse(script.Contains("function beginEdit", StringComparison.Ordinal));
        Assert.IsFalse(script.Contains("function applyEdit", StringComparison.Ordinal));
        Assert.IsFalse(script.Contains("function cancelEdit", StringComparison.Ordinal));
        StringAssert.Contains(script, "function proposedState(row)");
        StringAssert.Contains(script, "function baselineState(row)");
        StringAssert.Contains(script, "const dirty = !sameState(row, state, baseline)");
        StringAssert.Contains(script, "row.dataset.dirty = dirty.toString()");
        StringAssert.Contains(script, "function initializeImageRow(row, settingsForm)");
        StringAssert.Contains(script, "function initializeStandardRow(row, settingsForm)");
        StringAssert.Contains(script, "function disableDirtyMutations()");
        StringAssert.Contains(script, "submitter?.dataset?.submitKind === \"draft\"");
    }

    [TestMethod]
    public void InheritanceAndReviewMessages_DoNotExposeSensitiveValues()
    {
        var root = FindRepositoryRoot();
        var partial = File.ReadAllText(Path.Combine(root, "src/Clc.PatronRegistration.Web/Views/Settings/_SettingRow.cshtml"));
        var script = File.ReadAllText(Path.Combine(root, "src/Clc.PatronRegistration.Web/wwwroot/js/settings.js"));

        Assert.IsFalse(partial.Contains("inheritance-message", StringComparison.Ordinal));
        StringAssert.Contains(script, "row.dataset.sensitive === \"true\"");
        StringAssert.Contains(script, "row.dataset.valueType === \"image\"");
        StringAssert.Contains(script, "image-pending-file-name");
        StringAssert.Contains(script, "Use inherited image");
        StringAssert.Contains(partial, "data-image-inherited-missing=");
        StringAssert.Contains(script, "mode === \"inherit\" ? \"RemoveOverride\" : \"Upsert\"");
    }

    [TestMethod]
    public void SettingReviewPresentation_UsesSafeImageFilenamesAndInheritanceStates()
    {
        var image = new SettingDefinition("header_image_asset_id", "Header image", "Header image", SettingValueType.Image);
        var resolution = new ResolvedSetting("header_image_asset_id", "42", 2, "Main Library", string.Empty, true, "42", false);
        var currentAsset = new SettingAssetPresentation(42, "old-header.webp", "/settings/assets/42");
        var stagedAsset = new SettingAssetPresentation(43, "branch-header.webp", "/settings/assets/43");

        var live = new SettingRowViewModel("image-live", image, resolution, null, null, null,
            SourceDescription: "Main Library", EffectiveAsset: currentAsset);
        Assert.AreEqual("old-header.webp", SettingReviewPresentation.Live(live));

        var replacement = live with
        {
            DraftValue = "43",
            DraftOperation = DraftOperation.Upsert,
            DraftId = 7,
            StagedAsset = stagedAsset
        };
        Assert.AreEqual("branch-header.webp", SettingReviewPresentation.Proposed(replacement));

        var inherited = live with
        {
            DraftOperation = DraftOperation.RemoveOverride,
            DraftValue = null,
            InheritedValue = "44",
            HasInheritedValue = true,
            InheritedSourceDescription = "Main Library"
        };
        Assert.AreEqual("use inherited image from Main Library", SettingReviewPresentation.Proposed(inherited));

        var noInherited = live with
        {
            DraftOperation = DraftOperation.RemoveOverride,
            DraftValue = null,
            InheritedValue = null,
            HasInheritedValue = false
        };
        Assert.AreEqual("no configured image", SettingReviewPresentation.Proposed(noInherited));

        var missingEffective = live with { EffectiveAsset = null, EffectiveAssetMissing = true };
        Assert.AreEqual("missing configured image", SettingReviewPresentation.Live(missingEffective));

        var missingStaged = replacement with { StagedAsset = null, StagedAssetMissing = true };
        Assert.AreEqual("missing staged image", SettingReviewPresentation.Proposed(missingStaged));

        foreach (var summary in new[]
        {
            SettingReviewPresentation.Live(live),
            SettingReviewPresentation.Proposed(replacement),
            SettingReviewPresentation.Proposed(inherited),
            SettingReviewPresentation.Proposed(noInherited),
            SettingReviewPresentation.Live(missingEffective),
            SettingReviewPresentation.Proposed(missingStaged)
        })
        {
            Assert.IsFalse(summary.Contains("42", StringComparison.Ordinal));
            Assert.IsFalse(summary.Contains("43", StringComparison.Ordinal));
            Assert.IsFalse(summary.Contains("44", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public void Catalog_ExplicitAllowEmptyOverrideIsHonored()
    {
        var definition = TestCatalog<MetadataInferenceContract>(nameof(MetadataInferenceContract.RequiredString))
            .All.Single(x => x.Key == "required_string");

        Assert.IsFalse(definition.AllowEmpty);
        Assert.IsNotNull(definition.Validate(string.Empty));
    }

    [TestMethod]
    public void Catalog_DoesNotExposeUnattributedProviderProperties()
    {
        var catalog = new SettingCatalog();

        Assert.IsFalse(catalog.TryGet("require_preferred_pickup_location", out _));
        Assert.IsFalse(catalog.TryGet("welcome_email_address", out _));
        Assert.IsTrue(typeof(FutureProviderImplementation).GetProperty(nameof(FutureProviderImplementation.ImplementationOnly))!
            .IsDefined(typeof(AdminSettingAttribute), inherit: false));
        Assert.IsFalse(catalog.TryGet("implementation_only", out _));
    }

    [TestMethod]
    public void Catalog_InfersDecimalDateAndNullableTypesFromProviderClrTypes()
    {
        var catalog = TestCatalog<MetadataInferenceContract>(
            nameof(MetadataInferenceContract.DecimalSetting),
            nameof(MetadataInferenceContract.DateSetting),
            nameof(MetadataInferenceContract.NullableIntegerSetting),
            nameof(MetadataInferenceContract.LegacyProperty));

        Assert.AreEqual(SettingValueType.Decimal, catalog.All.Single(x => x.Key == "decimal_setting").ValueType);
        Assert.AreEqual(SettingValueType.Date, catalog.All.Single(x => x.Key == "date_setting").ValueType);
        Assert.AreEqual(SettingValueType.NullableInteger, catalog.All.Single(x => x.Key == "nullable_integer_setting").ValueType);
        Assert.AreEqual("legacy_setting", catalog.All.Single(x => x.Key == "legacy_setting").Key);
    }

    [TestMethod]
    public void Catalog_RejectsDuplicateAttributedDatabaseKeysClearly()
    {
        var exception = Assert.ThrowsException<InvalidOperationException>(() => TestCatalog<DuplicateKeyContract>(
            nameof(DuplicateKeyContract.SameKey), nameof(DuplicateKeyContract.ExplicitSameKey)));

        StringAssert.Contains(exception.Message, "Duplicate administration setting database key 'same_key'");
        StringAssert.Contains(exception.Message, nameof(DuplicateKeyContract.SameKey));
        StringAssert.Contains(exception.Message, nameof(DuplicateKeyContract.ExplicitSameKey));
    }

    [TestMethod]
    public void Catalog_RejectsUnsupportedAttributedClrTypesClearly()
    {
        var exception = Assert.ThrowsException<InvalidOperationException>(() => TestCatalog<UnsupportedTypeContract>(
            nameof(UnsupportedTypeContract.Unsupported)));

        StringAssert.Contains(exception.Message, nameof(UnsupportedTypeContract.Unsupported));
        StringAssert.Contains(exception.Message, typeof(Guid).FullName!);
    }

    [TestMethod]
    public void AdministrationMetadata_IsDeclaredOnISettingProviderAndCachedFromThatContract()
    {
        var interfaceProperties = typeof(ISettingProvider).GetProperties(BindingFlags.Instance | BindingFlags.Public);
        var providerProperties = typeof(DbSettingProvider).GetProperties(BindingFlags.Instance | BindingFlags.Public);

        Assert.AreEqual(74, AdministrationProperties().Length);
        Assert.IsFalse(providerProperties.Any(property => property.GetCustomAttribute<AdminSettingAttribute>() is not null));
        Assert.IsTrue(SettingPropertyMetadataCache.GetAll().Where(metadata => metadata.Administration is not null)
            .All(metadata => metadata.Property.DeclaringType == typeof(ISettingProvider)));
        Assert.IsNotNull(interfaceProperties.Single(property => property.Name == nameof(ISettingProvider.EnableAgeBlock))
            .GetCustomAttribute<AdminSettingAttribute>());

        Assert.IsFalse(interfaceProperties.Any(property => property.Name == "HeaderImageUrl"));
        Assert.IsFalse(new SettingCatalog().TryGet("header_image_url", out _));
        Assert.IsFalse(SettingPropertyMetadataCache.GetAll().Any(metadata => metadata.Property.Name == "HeaderImageUrl"));
        Assert.AreEqual("header_image_asset_id", SettingPropertyMetadataCache.Get(nameof(ISettingProvider.HeaderImageAssetId)).DatabaseKey);
        Assert.AreEqual(SettingValueType.Image, new SettingCatalog().All.Single(setting => setting.Key == "header_image_asset_id").ValueType);
    }

    [TestMethod]
    public void SettingsPage_VersionsSettingsJavaScript()
    {
        var root = FindRepositoryRoot();
        var index = File.ReadAllText(Path.Combine(root, "src/Clc.PatronRegistration.Web/Views/Settings/Index.cshtml"));
        var previewLink = File.ReadAllText(Path.Combine(root, "src/Clc.PatronRegistration.Web/Views/Settings/PreviewLinkCreated.cshtml"));

        StringAssert.Contains(index, "<script src=\"~/js/settings.js\" asp-append-version=\"true\"></script>");
        StringAssert.Contains(previewLink, "<script src=\"~/js/settings.js\" asp-append-version=\"true\"></script>");
    }

    [TestMethod]
    public void SettingPropertyMetadataCache_ResolvesInferredAndExceptionalInterfaceKeys()
    {
        var expected = new Dictionary<string, string>
        {
            [nameof(ISettingProvider.EnableAgeBlock)] = "enable_age_block",
            [nameof(ISettingProvider.AgeBlockText)] = "age_block_text",
            [nameof(ISettingProvider.HeaderImageAssetId)] = "header_image_asset_id",
            [nameof(ISettingProvider.ExpirationDateYears)] = "expiration_date_years",
            [nameof(ISettingProvider.EnableDriversLicenseSwipe)] = "show_dl",
            [nameof(ISettingProvider.DriversLicenseFormat)] = "dl_format",
            [nameof(ISettingProvider.DisplayECardCheckbox)] = "display_ecard_checkbox",
            [nameof(ISettingProvider.RegistrationHeader)] = "registration_form_header",
            [nameof(ISettingProvider.PerformPapiDupeBypass)] = "perform_papi_duplicate_bypass",
            [nameof(ISettingProvider.HideBranchSelectIfOnlyOneBranch)] = "hide_branch_select_if_only_one_option"
        };

        foreach (var pair in expected)
        {
            var metadata = SettingPropertyMetadataCache.Get(pair.Key);
            Assert.AreEqual(pair.Value, metadata.DatabaseKey, pair.Key);
            Assert.AreEqual(typeof(ISettingProvider), metadata.Property.DeclaringType, pair.Key);
        }
    }

    [TestMethod]
    public void Catalog_IsIdenticalForConcreteProvidersAndIgnoresImplementationAttributes()
    {
        var expected = new SettingCatalog().All.Select(setting => $"{setting.Key}:{setting.ValueType}:{setting.IsSensitive}").ToArray();
        _ = new DbSettingProvider(3, new TestCache());
        _ = new PreviewSettingProvider(Draft(3), 3, new TestCache(), 1);
        _ = new ForcedKioskModeDbSettingProvider(3, new TestCache());

        var actual = new SettingCatalog().All.Select(setting => $"{setting.Key}:{setting.ValueType}:{setting.IsSensitive}").ToArray();
        CollectionAssert.AreEqual(expected, actual);
        Assert.IsFalse(new SettingCatalog().TryGet("implementation_only", out _));
    }

    [TestMethod]
    public void PreviewSettingProvider_UsesGetSettingOverrideForPropertyMetadataReads()
    {
        var draft = Draft(3,
            new SettingMutation("enable_age_block", DraftOperation.Upsert, "true"),
            new SettingMutation("header_image_asset_id", DraftOperation.Upsert, "42"),
            new SettingMutation("display_ecard_checkbox", DraftOperation.Upsert, "true"));
        var preview = new PreviewSettingProvider(draft, 3, new TestCache(), 1);

        Assert.IsTrue(preview.EnableAgeBlock);
        Assert.AreEqual(42, preview.HeaderImageAssetId);
        Assert.IsTrue(preview.DisplayECardCheckbox);
        Assert.IsTrue(new SettingCatalog().TryGet("display_ecard_checkbox", out _));
    }

    [TestMethod]
    public void ForcedKioskProvider_UsesInterfaceMetadataDespiteHiddenResetImplementation()
    {
        var catalog = new SettingCatalog();
        Assert.IsTrue(catalog.TryGet("reset_form", out var resetForm));
        Assert.AreEqual(SettingValueType.Boolean, resetForm.ValueType);
        Assert.IsTrue(catalog.TryGet("show_dl", out _));
        Assert.AreEqual(typeof(ISettingProvider), SettingPropertyMetadataCache.Get(nameof(ISettingProvider.ResetForm)).Property.DeclaringType);

        var provider = new ForcedKioskModeDbSettingProvider(3, CacheWith(
            Setting(1, "enable_age_block", "true"),
            Setting(1, "display_ecard_checkbox", "true")));

        Assert.IsTrue(provider.ResetForm);
        Assert.IsTrue(provider.EnableAgeBlock);
        Assert.IsTrue(provider.DisplayECardCheckbox);
    }

    private static PropertyInfo[] AdministrationProperties() =>
        typeof(ISettingProvider).GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.GetCustomAttribute<AdminSettingAttribute>() is not null)
            .OrderBy(property => property.MetadataToken)
            .ToArray();

    private static string SettingKey(PropertyInfo property) =>
        property.GetCustomAttribute<AdminSettingAttribute>()!.Key
        ?? JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name);

    private static SettingValueType InferValueType(Type propertyType) => propertyType switch
    {
        _ when propertyType == typeof(bool) => SettingValueType.Boolean,
        _ when propertyType == typeof(int) => SettingValueType.Integer,
        _ when propertyType == typeof(int?) => SettingValueType.NullableInteger,
        _ when propertyType == typeof(decimal) => SettingValueType.Decimal,
        _ when propertyType == typeof(DateTime) => SettingValueType.Date,
        _ when propertyType == typeof(DateTime?) => SettingValueType.NullableDate,
        _ when propertyType == typeof(string) => SettingValueType.ShortString,
        _ => throw new InvalidOperationException($"Unsupported test type {propertyType.FullName}.")
    };

    private static SettingCatalog TestCatalog<T>(params string[] propertyNames) =>
        new(propertyNames.Select(propertyName =>
        {
            var property = typeof(T).GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)
                ?? throw new InvalidOperationException($"Test metadata property '{propertyName}' was not found.");
            var administration = property.GetCustomAttribute<AdminSettingAttribute>()
                ?? throw new InvalidOperationException($"Test metadata property '{propertyName}' is not attributed.");
            var databaseKey = administration.Key ?? JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name);
            return new SettingPropertyMetadata(property, administration, databaseKey);
        }).ToArray());

    private interface MetadataInferenceContract
    {
        [AdminSetting(SettingCategory.FormBehaviorAndFields, "Decimal setting", "Decimal setting.")]
        decimal DecimalSetting { get; }

        [AdminSetting(SettingCategory.FormBehaviorAndFields, "Date setting", "Date setting.")]
        DateTime DateSetting { get; }

        [AdminSetting(SettingCategory.FormBehaviorAndFields, "Nullable integer setting", "Nullable integer setting.")]
        int? NullableIntegerSetting { get; }

        [AdminSetting(SettingCategory.FormBehaviorAndFields, "HTML setting", "HTML setting.", ValueType = SettingValueType.Html)]
        string HtmlSetting { get; }

        [AdminSetting(SettingCategory.FormBehaviorAndFields, "Email template setting", "Email template setting.", ValueType = SettingValueType.EmailTemplate)]
        string TemplateSetting { get; }

        [AdminSetting(SettingCategory.FormBehaviorAndFields, "Email address setting", "Email address setting.", ValueType = SettingValueType.EmailAddress)]
        string EmailSetting { get; }

        [AdminSetting(SettingCategory.FormBehaviorAndFields, "Required string", "Required string.", AllowEmpty = false)]
        string RequiredString { get; }

        [AdminSetting(SettingCategory.FormBehaviorAndFields, "Legacy setting", "Legacy setting.", Key = "legacy_setting")]
        bool LegacyProperty { get; }
    }

    private sealed class FutureProviderImplementation : DbSettingProvider
    {
        private FutureProviderImplementation() : base(0, null!) { }

        [AdminSetting(SettingCategory.FormBehaviorAndFields, "Implementation only", "Implementation-only metadata must not change the catalog.")]
        public bool ImplementationOnly => true;
    }

    private interface DuplicateKeyContract
    {
        [AdminSetting(SettingCategory.FormBehaviorAndFields, "Same key", "Same key.")]
        bool SameKey { get; }

        [AdminSetting(SettingCategory.FormBehaviorAndFields, "Explicit same key", "Explicit same key.", Key = "same_key")]
        bool ExplicitSameKey { get; }
    }

    private interface UnsupportedTypeContract
    {
        [AdminSetting(SettingCategory.FormBehaviorAndFields, "Unsupported", "Unsupported.")]
        Guid Unsupported { get; }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src/Clc.PatronRegistration.Web")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private static RegistrationFormSetting Setting(int organizationId, string key, string value, string formCode = "") => new()
    {
        OrganizationID = organizationId,
        Setting = key,
        Value = value,
        FormCode = formCode
    };

    private static TestCache CacheWith(params RegistrationFormSetting[] settings) => new() { SettingsCache = settings.ToList() };

    private static SettingDraft Draft(int organizationId, params SettingMutation[] changes) =>
        new(20, organizationId, string.Empty, 0, DraftStatus.Active, changes);

    private sealed class GuardedSettingsList(IReadOnlyList<RegistrationFormSetting> rows) : IReadOnlyList<RegistrationFormSetting>
    {
        public bool ThrowOnEnumeration { get; set; }
        public int Count => rows.Count;
        public RegistrationFormSetting this[int index] => rows[index];

        public IEnumerator<RegistrationFormSetting> GetEnumerator()
        {
            if (ThrowOnEnumeration)
            {
                throw new InvalidOperationException("The published settings snapshot was enumerated after indexing.");
            }
            return rows.GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class SnapshotOnlyCache(CacheSnapshot snapshot) : ICache, ICacheSnapshotProvider
    {
        public int SnapshotCalls { get; private set; }
        public int SettingsPropertyReads { get; private set; }
        public bool IsInitialized => true;
        public List<RegistrationFormSetting> SettingsCache
        {
            get
            {
                SettingsPropertyReads++;
                throw new InvalidOperationException("Provider used the mutable cache property instead of the published snapshot.");
            }
        }
        public List<OrganizationsGetRow> OrganizationCache => throw new InvalidOperationException();
        public CacheSnapshot GetSnapshot()
        {
            SnapshotCalls++;
            return snapshot;
        }
        public void RebuildCache() => throw new InvalidOperationException();
        public OrganizationsGetRow GetOrg(int orgId) => throw new InvalidOperationException();
        public List<OrganizationsGetRow> GetBranches(int orgId) => throw new InvalidOperationException();
    }
}
