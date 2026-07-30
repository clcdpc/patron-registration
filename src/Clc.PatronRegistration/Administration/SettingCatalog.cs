using System.Globalization;
using System.Net.Mail;
using System.Text.RegularExpressions;

namespace Clc.PatronRegistration.Administration;

public enum SettingValueType
{
    Boolean,
    Integer,
    NullableInteger,
    Decimal,
    Date,
    NullableDate,
    ShortString,
    LongString,
    Html,
    EmailTemplate,
    EmailAddress,
    Uri,
    Enumeration
}

public enum SettingGroup
{
    Ordinary,
    Alert,
    Label,
    Require
}

public enum SettingCategory
{
    PageAppearanceAndInstructions,
    FormBehaviorAndFields,
    BranchAndPatronDefaults,
    ECardRegistration,
    EmailAndNotices,
    DuplicateChecking,
    AddressVerification,
    PolarisIntegrationAndRecordSets,
    KioskAndSessionBehavior,
    AdvancedIntegrations
}

public static class SettingCategoryPresentation
{
    public static IReadOnlyList<SettingCategory> Ordered { get; } = Enum.GetValues<SettingCategory>();
    public static string DisplayName(this SettingCategory category) => category switch
    {
        SettingCategory.PageAppearanceAndInstructions => "Page appearance and instructions",
        SettingCategory.FormBehaviorAndFields => "Form behavior and fields",
        SettingCategory.BranchAndPatronDefaults => "Branch and patron defaults",
        SettingCategory.ECardRegistration => "E-card registration",
        SettingCategory.EmailAndNotices => "Email and notices",
        SettingCategory.DuplicateChecking => "Duplicate checking",
        SettingCategory.AddressVerification => "Address verification",
        SettingCategory.PolarisIntegrationAndRecordSets => "Polaris integration and record sets",
        SettingCategory.KioskAndSessionBehavior => "Kiosk and session behavior",
        SettingCategory.AdvancedIntegrations => "Advanced integrations",
        _ => throw new ArgumentOutOfRangeException(nameof(category))
    };
}

public sealed record SettingDefinition(string Key, string DisplayName, string Description, SettingValueType ValueType,
    SettingGroup Group = SettingGroup.Ordinary, bool IsSensitive = false, bool AllowEmpty = true,
    IReadOnlyList<string>? AllowedValues = null, int SortOrder = 0, SettingCategory? Category = null)
{
    private static readonly HashSet<string> PositiveIdentifierKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "add_to_record_set_id",
        "registration_logon_user_id",
        "ecard_patron_code_id",
        "teacher_patron_code_id",
        "student_patron_code_id",
        "valid_address_patron_code_id",
        "valid_address_plus_name_patron_code_id",
        "patron_code_id"
    };

    private static readonly HashSet<string> NonNegativeIdentifierKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "mailing_list_record_set_id",
        "valid_address_record_set_id",
        "valid_address_plus_name_record_set_id",
        "invalid_address_record_set_id"
    };

    public string? Validate(string? value)
    {
        if (value is null)
        {
            return "Enter a value for this setting.";
        }
        if (value.Length == 0)
        {
            return AllowEmpty && !IsSensitive
                ? null
                : "An empty value is not valid for this setting. Choose “Use inherited value” instead.";
        }
        if (Group == SettingGroup.Label)
        {
            if (value.Length > 200)
            {
                return "Labels cannot exceed 200 characters.";
            }
            if (value.Contains('<') || value.Contains('>') || value.Any(char.IsControl) ||
                Regex.IsMatch(value, @"\b(?:on[a-z]+|style|src|href)\s*=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                return "Labels must be single-line plain text without markup or control characters.";
            }
        }
        return ValueType switch
        {
            SettingValueType.Boolean when !bool.TryParse(value, out _) => "Enter true or false.",
            SettingValueType.Integer when !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) => "Enter a whole number.",
            SettingValueType.NullableInteger when value.Length > 0 && !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) => "Enter a whole number or leave empty.",
            SettingValueType.Integer or SettingValueType.NullableInteger
                when PositiveIdentifierKeys.Contains(Key) && int.Parse(value, CultureInfo.InvariantCulture) <= 0 =>
                "Configured identifier must be a positive whole number.",
            SettingValueType.Integer
                when NonNegativeIdentifierKeys.Contains(Key) && int.Parse(value, CultureInfo.InvariantCulture) < 0 =>
                "Record-set ID cannot be negative; use zero to disable it.",
            SettingValueType.Decimal when !decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _) => "Enter a decimal number.",
            SettingValueType.Date or SettingValueType.NullableDate when value.Length > 0 && !DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) => "Enter a date as yyyy-MM-dd.",
            SettingValueType.EmailAddress when !MailAddress.TryCreate(value, out _) => "Enter a valid email address.",
            SettingValueType.Uri when !System.Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http") => "Enter an absolute HTTP or HTTPS URL.",
            SettingValueType.Enumeration when AllowedValues?.Contains(value, StringComparer.OrdinalIgnoreCase) != true => "Select a recognized value.",
            _ when value.Length > 100_000 => "Value is too long.",
            _ => null
        };
    }
}

public interface ISettingCatalog
{
    IReadOnlyList<SettingDefinition> All { get; }
    bool TryGet(string key, out SettingDefinition definition);
    IReadOnlyList<string> DynamicFieldSuffixes { get; }
    IReadOnlyList<string> LabelFieldSuffixes { get; }
    IReadOnlyList<string> RequiredFieldSuffixes { get; }
}

public sealed class SettingCatalog : ISettingCatalog
{
    private static readonly string[] BooleanKeys =
    [
        "reset_form",
        "show_dl",
        "hide_gender",
        "enable_age_warning",
        "hide_ereceipt",
        "normalize_to_uppercase",
        "bypass_dupe_check",
        "enable_patron_branch_select_option",
        "block_out_of_state_registrations",
        "enable_legal_name_checkbox",
        "use_legal_name_on_notices",
        "display_ecard_checkbox",
        "display_mailing_list_checkbox",
        "display_sms_notice_information",
        "display_preferred_pickup_location",
        "require_preferred_pickup_location",
        "display_responsible_person_field",
        "perform_papi_duplicate_bypass",
        "use_first_name_for_duplicate_workaround",
        "update_patron_record_with_melissa_address",
        "hide_branch_select_if_only_one_option",
        "disable_branch",
        "force_ecard_remotely"
    ];
    private static readonly string[] IntegerKeys =
    [
        "mailing_list_record_set_id",
        "registration_logon_user_id",
        "ecard_patron_code_id",
        "teacher_patron_code_id",
        "student_patron_code_id",
        "valid_address_patron_code_id",
        "valid_address_plus_name_patron_code_id",
        "valid_address_record_set_id",
        "valid_address_plus_name_record_set_id",
        "invalid_address_record_set_id",
        "reset_seconds"
    ];
    private static readonly string[] NullableIntegerKeys =
    [
        "add_to_record_set_id",
        "expiration_date_years",
        "patron_code_id"
    ];
    private static readonly string[] HtmlKeys =
    [
        "custom_form_footer_html",
        "duplicate_patron_message_html",
        "mailing_list_description_html",
        "sms_notice_information_html"
    ];
    private static readonly string[] TemplateKeys =
    [
        "ecard_welcome_email_template_text",
        "ecard_welcome_email_template_html",
        "welcome_email_template_text",
        "welcome_email_template_html"
    ];
    private static readonly string[] SensitiveKeys =
    [
        "postmark_api_key",
        "melissa_data_api_key"
    ];
    private static readonly string[] OrdinaryKeys =
    [
        "header_image_url",
        "css_file",
        "warning_text",
        "custom_form_footer_html",
        "show_dl_ips",
        "reset_form",
        "show_dl",
        "hide_gender",
        "enable_age_warning",
        "age_warning_text",
        "hide_ereceipt",
        "na_gender_text",
        "normalize_to_uppercase",
        "dl_format",
        "bypass_dupe_check",
        "registration_text",
        "enable_patron_branch_select_option",
        "block_out_of_state_registrations",
        "registration_form_header",
        "duplicate_patron_message_html",
        "enable_legal_name_checkbox",
        "legal_name_checkbox_label",
        "use_legal_name_on_notices",
        "drivers_license_button_text",
        "drivers_license_prompt_text",
        "agreement_confirm_button_text",
        "agreement_cancel_button_text",
        "kiosk_registration_text",
        "kiosk_registration_header",
        "school_info_field_legend",
        "display_ecard_checkbox",
        "ecard_checkbox_label",
        "mailing_list_description_html",
        "display_mailing_list_checkbox",
        "mailing_list_checkbox_label",
        "mailing_list_record_set_id",
        "registration_logon_user_id",
        "ecard_patron_code_id",
        "teacher_patron_code_id",
        "student_patron_code_id",
        "school_info_format",
        "responsible_person_disclaimer",
        "ecard_registration_text",
        "sms_notice_information_html",
        "display_sms_notice_information",
        "ecard_welcome_email_template_text",
        "ecard_welcome_email_template_html",
        "welcome_email_template_text",
        "welcome_email_template_html",
        "welcome_email_from_name",
        "welcome_email_subject",
        "welcome_email_from_address",
        "ecard_welcome_email_subject",
        "postmark_api_key",
        "display_preferred_pickup_location",
        "require_preferred_pickup_location",
        "display_responsible_person_field",
        "perform_papi_duplicate_bypass",
        "use_first_name_for_duplicate_workaround",
        "update_patron_record_with_melissa_address",
        "melissa_data_api_key",
        "valid_address_registration_text",
        "valid_address_plus_name_registration_text",
        "out_of_state_block_message",
        "ecard_barcode_prefix",
        "valid_address_patron_code_id",
        "valid_address_plus_name_patron_code_id",
        "valid_address_record_set_id",
        "valid_address_plus_name_record_set_id",
        "invalid_address_record_set_id",
        "add_to_record_set_id",
        "post_registration_note_text",
        "expiration_date",
        "expiration_date_years",
        "patron_code_id",
        "hide_branch_select_if_only_one_option",
        "disable_branch",
        "reset_seconds",
        "phone_number_format",
        "force_ecard_remotely"
    ];
    private static readonly IReadOnlyDictionary<SettingCategory, string[]> CategoryKeys =
        new Dictionary<SettingCategory, string[]>
        {
            [SettingCategory.PageAppearanceAndInstructions] = ["header_image_url", "css_file", "warning_text", "custom_form_footer_html", "registration_text", "registration_form_header", "school_info_field_legend"],
            [SettingCategory.FormBehaviorAndFields] = ["show_dl_ips", "show_dl", "hide_gender", "enable_age_warning", "age_warning_text", "hide_ereceipt", "na_gender_text", "normalize_to_uppercase", "dl_format", "enable_legal_name_checkbox", "legal_name_checkbox_label", "drivers_license_button_text", "drivers_license_prompt_text", "agreement_confirm_button_text", "agreement_cancel_button_text", "school_info_format", "responsible_person_disclaimer", "display_preferred_pickup_location", "require_preferred_pickup_location", "display_responsible_person_field", "phone_number_format"],
            [SettingCategory.BranchAndPatronDefaults] = ["enable_patron_branch_select_option", "teacher_patron_code_id", "student_patron_code_id", "patron_code_id", "expiration_date", "expiration_date_years", "hide_branch_select_if_only_one_option", "disable_branch"],
            [SettingCategory.ECardRegistration] = ["display_ecard_checkbox", "ecard_checkbox_label", "ecard_patron_code_id", "ecard_registration_text", "ecard_barcode_prefix", "force_ecard_remotely"],
            [SettingCategory.EmailAndNotices] = ["display_mailing_list_checkbox", "mailing_list_checkbox_label", "mailing_list_description_html", "display_sms_notice_information", "sms_notice_information_html", "use_legal_name_on_notices", "ecard_welcome_email_template_text", "ecard_welcome_email_template_html", "welcome_email_template_text", "welcome_email_template_html", "welcome_email_from_name", "welcome_email_subject", "welcome_email_from_address", "ecard_welcome_email_subject", "post_registration_note_text"],
            [SettingCategory.DuplicateChecking] = ["bypass_dupe_check", "duplicate_patron_message_html", "perform_papi_duplicate_bypass", "use_first_name_for_duplicate_workaround"],
            [SettingCategory.AddressVerification] = ["block_out_of_state_registrations", "update_patron_record_with_melissa_address", "valid_address_registration_text", "valid_address_plus_name_registration_text", "out_of_state_block_message", "valid_address_patron_code_id", "valid_address_plus_name_patron_code_id", "valid_address_record_set_id", "valid_address_plus_name_record_set_id", "invalid_address_record_set_id"],
            [SettingCategory.PolarisIntegrationAndRecordSets] = ["mailing_list_record_set_id", "registration_logon_user_id", "add_to_record_set_id"],
            [SettingCategory.KioskAndSessionBehavior] = ["reset_form", "kiosk_registration_text", "kiosk_registration_header", "reset_seconds"],
            [SettingCategory.AdvancedIntegrations] = ["postmark_api_key", "melissa_data_api_key"]
        };
    private static readonly IReadOnlyDictionary<string, string> DisplayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["css_file"] = "CSS file", ["header_image_url"] = "Header image URL",
        ["perform_papi_duplicate_bypass"] = "PAPI duplicate check",
        ["ecard_patron_code_id"] = "E-card patron code", ["display_ecard_checkbox"] = "Show e-card option",
        ["ecard_checkbox_label"] = "E-card option label", ["ecard_registration_text"] = "E-card registration text",
        ["ecard_barcode_prefix"] = "E-card barcode prefix", ["force_ecard_remotely"] = "Require e-card for remote registration",
        ["sms_notice_information_html"] = "Text message information", ["display_sms_notice_information"] = "Show text message information",
        ["add_to_record_set_id"] = "Polaris record set ID", ["registration_logon_user_id"] = "Polaris registration user ID",
        ["postmark_api_key"] = "Postmark API key", ["melissa_data_api_key"] = "Melissa Data API key"
    };
    private static readonly IReadOnlyDictionary<string, string> Descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["show_dl"] = "Shows or hides the driver’s license field on the registration form.",
        ["ecard_patron_code_id"] = "Polaris patron code assigned to successful e-card registrations.",
        ["out_of_state_block_message"] = "Text displayed when registration is blocked because the address is outside the allowed state.",
        ["add_to_record_set_id"] = "Adds newly registered patrons to this Polaris record set. Leave blank to disable this action.",
        ["css_file"] = "CSS file used to style the registration page.",
        ["header_image_url"] = "Web address of the image displayed at the top of the registration page.",
        ["postmark_api_key"] = "Secret API key used to send registration email through Postmark.",
        ["melissa_data_api_key"] = "Secret API key used for Melissa Data address verification.",
        ["perform_papi_duplicate_bypass"] = "Controls whether PAPI is used when bypassing the standard duplicate check."
    };
    public IReadOnlyList<string> DynamicFieldSuffixes { get; } =
    [
        "PatronBranchID", "NameFirst", "NameMiddle", "NameLast", "UseLegalName",
        "LegalNameFirst", "LegalNameMiddle", "LegalNameLast", "Birthdate", "DeliveryOptionId",
        "PhoneVoice1", "PhoneVoice2", "ReceiveEreceipts", "EmailAddress", "AltEmailAddress",
        "StreetOne", "StreetTwo", "City", "State", "PostalCode", "Password", "Password2",
        "RequestPickupBranchID", "User1", "User5", "DeliverCardToSchool", "IsStudent",
        "IsTeacher", "IsECard", "AddToMailingList"
    ];
    public IReadOnlyList<string> LabelFieldSuffixes { get; } =
    [
        "PatronBranchID", "NameFirst", "NameMiddle", "NameLast", "UseLegalName",
        "LegalNameFirst", "LegalNameMiddle", "LegalNameLast", "Birthdate", "DeliveryOptionId",
        "PhoneVoice1", "PhoneVoice2", "ReceiveEreceipts", "EmailAddress", "StreetOne", "StreetTwo",
        "City", "State", "User5", "PostalCode", "Password", "Password2", "RequestPickupBranchID",
        "User1", "DeliverCardToSchool", "IsStudent", "IsTeacher", "IsECard", "AddToMailingList"
    ];
    public IReadOnlyList<string> RequiredFieldSuffixes { get; } =
    [
        "PhoneVoice1", "EmailAddress", "User5"
    ];
    public IReadOnlyList<SettingDefinition> All { get; }
    private readonly Dictionary<string, SettingDefinition> byKey;

    public SettingCatalog()
    {
        var categoriesByKey = CategoryKeys.SelectMany(pair => pair.Value.Select(key => (key, pair.Key)))
            .ToDictionary(item => item.key, item => item.Key, StringComparer.OrdinalIgnoreCase);
        var list = OrdinaryKeys.Select((key, i) =>
        {
            var type = TypeFor(key);
            var displayName = DisplayNames.GetValueOrDefault(key, Friendly(key));
            return new SettingDefinition(
                key,
                displayName,
                Descriptions.GetValueOrDefault(key, DescriptionFor(displayName, type)),
                type,
                IsSensitive: SensitiveKeys.Contains(key),
                AllowEmpty: AllowsEmpty(type),
                SortOrder: i,
                Category: categoriesByKey[key]);
        }).ToList();
        foreach (var suffix in DynamicFieldSuffixes)
        {
            list.Add(new($"alert.{suffix}", $"{Friendly(suffix)} alert", "Stored alert text reserved for future validation-message integration.", SettingValueType.LongString, SettingGroup.Alert));
        }
        foreach (var suffix in LabelFieldSuffixes)
        {
            list.Add(new($"label.{suffix}", $"{Friendly(suffix)} label", "Label shown for this field.", SettingValueType.ShortString, SettingGroup.Label));
        }
        foreach (var suffix in RequiredFieldSuffixes)
        {
            list.Add(new($"require.{suffix}", $"Require {Friendly(suffix)}", "Whether this dynamically validated field is required.", SettingValueType.Boolean, SettingGroup.Require, AllowEmpty: false));
        }
        All = list.OrderBy(x => x.Group).ThenBy(x => x.Category).ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        byKey = list.ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);
    }
    public bool TryGet(string key, out SettingDefinition definition) => byKey.TryGetValue(key, out definition!);
    private static SettingValueType TypeFor(string key)
    {
        if (BooleanKeys.Contains(key))
        {
            return SettingValueType.Boolean;
        }
        if (IntegerKeys.Contains(key))
        {
            return SettingValueType.Integer;
        }
        if (NullableIntegerKeys.Contains(key))
        {
            return SettingValueType.NullableInteger;
        }
        if (key == "expiration_date")
        {
            return SettingValueType.NullableDate;
        }
        if (HtmlKeys.Contains(key))
        {
            return SettingValueType.Html;
        }
        if (TemplateKeys.Contains(key))
        {
            return SettingValueType.EmailTemplate;
        }
        if (key == "welcome_email_from_address")
        {
            return SettingValueType.EmailAddress;
        }
        if (key == "header_image_url")
        {
            return SettingValueType.Uri;
        }
        return key.Contains("text") || key.Contains("html") || key.Contains("disclaimer") || key.Contains("message")
            ? SettingValueType.LongString
            : SettingValueType.ShortString;
    }

    private static bool AllowsEmpty(SettingValueType type) => type is SettingValueType.ShortString
        or SettingValueType.LongString
        or SettingValueType.Html
        or SettingValueType.EmailTemplate
        or SettingValueType.EmailAddress
        or SettingValueType.Uri
        or SettingValueType.NullableInteger
        or SettingValueType.NullableDate;
    private static string Friendly(string key)
    {
        var words = Regex.Replace(key.Replace('_', ' '), "(?<=[a-z0-9])(?=[A-Z])", " ");
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(words)
            .Replace(" Url", " URL", StringComparison.Ordinal).Replace(" Id", " ID", StringComparison.Ordinal)
            .Replace(" Api", " API", StringComparison.Ordinal).Replace(" Sms", " SMS", StringComparison.Ordinal)
            .Replace(" Ecard", "E-card", StringComparison.Ordinal).Replace(" Papi", "PAPI", StringComparison.Ordinal)
            .Replace(" Css", "CSS", StringComparison.Ordinal);
    }

    private static string DescriptionFor(string displayName, SettingValueType type) => type switch
    {
        SettingValueType.Boolean => $"Controls whether {displayName.ToLowerInvariant()} is enabled on registration forms.",
        SettingValueType.Html => $"HTML content used for {displayName.ToLowerInvariant()}; preview it before saving.",
        SettingValueType.EmailTemplate => $"Content used for the {displayName.ToLowerInvariant()} sent after registration.",
        _ => $"Value used for {displayName.ToLowerInvariant()} during registration."
    };
}
