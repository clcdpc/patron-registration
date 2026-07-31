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
    KioskAndSessionBehavior
}

public static class SettingCategoryPresentation
{
    public static IReadOnlyList<SettingCategory> Ordered { get; } = Enum.GetValues<SettingCategory>();
    public static string DisplayName(this SettingCategory category) => category switch
    {
        SettingCategory.PageAppearanceAndInstructions => "Page content and appearance",
        SettingCategory.FormBehaviorAndFields => "Form fields and behavior",
        SettingCategory.BranchAndPatronDefaults => "Branch selection and patron defaults",
        SettingCategory.ECardRegistration => "E-card registration",
        SettingCategory.EmailAndNotices => "Email and communications",
        SettingCategory.DuplicateChecking => "Duplicate detection and workarounds",
        SettingCategory.AddressVerification => "Address verification",
        SettingCategory.PolarisIntegrationAndRecordSets => "Polaris patron creation and follow-up",
        SettingCategory.KioskAndSessionBehavior => "Kiosk and on-site behavior",
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
    private sealed record OrdinaryPresentation(string Key, SettingCategory Category, string DisplayName, string Description);

    private static readonly IReadOnlyList<OrdinaryPresentation> OrdinarySettings =
    [
        new("header_image_url", SettingCategory.PageAppearanceAndInstructions, "Header image URL", "Header image URL used by the registration workflow."),
        new("css_file", SettingCategory.PageAppearanceAndInstructions, "CSS file", "CSS file used by the registration workflow."),
        new("warning_text", SettingCategory.PageAppearanceAndInstructions, "Registration agreement content", "Registration agreement content used by the registration workflow."),
        new("custom_form_footer_html", SettingCategory.PageAppearanceAndInstructions, "Custom form footer HTML", "Custom form footer HTML used by the registration workflow."),
        new("registration_text", SettingCategory.PageAppearanceAndInstructions, "Default success message", "Default success message used by the registration workflow."),
        new("registration_form_header", SettingCategory.PageAppearanceAndInstructions, "Registration form introduction", "Registration form introduction used by the registration workflow."),
        new("show_dl", SettingCategory.FormBehaviorAndFields, "Enable driver’s license scanner", "Enable driver’s license scanner used by the registration workflow."),
        new("hide_gender", SettingCategory.FormBehaviorAndFields, "Hide gender field", "Hide gender field used by the registration workflow."),
        new("enable_age_warning", SettingCategory.FormBehaviorAndFields, "Show age warning", "Show age warning used by the registration workflow."),
        new("age_warning_text", SettingCategory.FormBehaviorAndFields, "Age warning message", "Age warning message used by the registration workflow."),
        new("hide_ereceipt", SettingCategory.FormBehaviorAndFields, "Hide e-receipt option", "Hide e-receipt option used by the registration workflow."),
        new("na_gender_text", SettingCategory.FormBehaviorAndFields, "N/A gender option text", "N/A gender option text used by the registration workflow."),
        new("normalize_to_uppercase", SettingCategory.FormBehaviorAndFields, "Convert registration data to uppercase", "Convert registration data to uppercase used by the registration workflow."),
        new("dl_format", SettingCategory.FormBehaviorAndFields, "Driver’s license scanner format", "Driver’s license scanner format used by the registration workflow."),
        new("enable_legal_name_checkbox", SettingCategory.FormBehaviorAndFields, "Show legal-name option", "Show legal-name option used by the registration workflow."),
        new("drivers_license_button_text", SettingCategory.FormBehaviorAndFields, "Driver’s license button text", "Driver’s license button text used by the registration workflow."),
        new("drivers_license_prompt_text", SettingCategory.FormBehaviorAndFields, "Driver’s license prompt text", "Driver’s license prompt text used by the registration workflow."),
        new("agreement_confirm_button_text", SettingCategory.FormBehaviorAndFields, "Agreement accept button text", "Agreement accept button text used by the registration workflow."),
        new("agreement_cancel_button_text", SettingCategory.FormBehaviorAndFields, "Agreement decline button text", "Agreement decline button text used by the registration workflow."),
        new("school_info_field_legend", SettingCategory.FormBehaviorAndFields, "School-information heading", "School-information heading used by the registration workflow."),
        new("school_info_format", SettingCategory.FormBehaviorAndFields, "School-registration mode", "School-registration mode used by the registration workflow."),
        new("responsible_person_disclaimer", SettingCategory.FormBehaviorAndFields, "Responsible-person instructions", "Responsible-person instructions used by the registration workflow."),
        new("display_responsible_person_field", SettingCategory.FormBehaviorAndFields, "Show responsible-person field", "Show responsible-person field used by the registration workflow."),
        new("phone_number_format", SettingCategory.FormBehaviorAndFields, "Phone-number storage format", "Phone-number storage format used by the registration workflow."),
        new("enable_patron_branch_select_option", SettingCategory.BranchAndPatronDefaults, "Allow patrons to choose a home branch", "Allow patrons to choose a home branch used by the registration workflow."),
        new("display_preferred_pickup_location", SettingCategory.BranchAndPatronDefaults, "Show preferred pickup location", "Show preferred pickup location used by the registration workflow."),
        new("teacher_patron_code_id", SettingCategory.BranchAndPatronDefaults, "Teacher patron code", "Teacher patron code used by the registration workflow."),
        new("student_patron_code_id", SettingCategory.BranchAndPatronDefaults, "Student patron code", "Student patron code used by the registration workflow."),
        new("patron_code_id", SettingCategory.BranchAndPatronDefaults, "Default patron code", "Default patron code used by the registration workflow."),
        new("expiration_date", SettingCategory.BranchAndPatronDefaults, "Fixed expiration date", "Supplies one fixed patron expiration date; a configured years-based expiration takes precedence."),
        new("expiration_date_years", SettingCategory.BranchAndPatronDefaults, "Expiration period (years)", "Calculates patron expiration relative to registration and takes precedence over the fixed expiration date."),
        new("hide_branch_select_if_only_one_option", SettingCategory.BranchAndPatronDefaults, "Hide home branch when only one option exists", "Hide home branch when only one option exists used by the registration workflow."),
        new("disable_branch", SettingCategory.BranchAndPatronDefaults, "Disable registration for this branch and form", "Prevents this branch and form combination from accepting registrations."),
        new("display_ecard_checkbox", SettingCategory.ECardRegistration, "Show e-card option", "Show e-card option used by the registration workflow."),
        new("ecard_patron_code_id", SettingCategory.ECardRegistration, "E-card patron code", "E-card patron code used by the registration workflow."),
        new("ecard_registration_text", SettingCategory.ECardRegistration, "E-card success message", "E-card success message used by the registration workflow."),
        new("ecard_barcode_prefix", SettingCategory.ECardRegistration, "E-card barcode prefix", "E-card barcode prefix used by the registration workflow."),
        new("force_ecard_remotely", SettingCategory.ECardRegistration, "Require e-card for remote registration", "Require e-card for remote registration used by the registration workflow."),
        new("display_mailing_list_checkbox", SettingCategory.EmailAndNotices, "Show mailing-list option", "Show mailing-list option used by the registration workflow."),
        new("mailing_list_description_html", SettingCategory.EmailAndNotices, "Mailing-list description", "Mailing-list description used by the registration workflow."),
        new("mailing_list_record_set_id", SettingCategory.EmailAndNotices, "Mailing-list record set", "Polaris record set to which patrons are added when they select the mailing-list option."),
        new("display_sms_notice_information", SettingCategory.EmailAndNotices, "Show text-message information", "Show text-message information used by the registration workflow."),
        new("sms_notice_information_html", SettingCategory.EmailAndNotices, "Text-message information", "Text-message information used by the registration workflow."),
        new("use_legal_name_on_notices", SettingCategory.EmailAndNotices, "Use legal name on notices", "Use legal name on notices used by the registration workflow."),
        new("ecard_welcome_email_template_text", SettingCategory.EmailAndNotices, "E-card welcome email text version", "E-card welcome email text version used by the registration workflow."),
        new("ecard_welcome_email_template_html", SettingCategory.EmailAndNotices, "E-card welcome email HTML version", "E-card welcome email HTML version used by the registration workflow."),
        new("welcome_email_template_text", SettingCategory.EmailAndNotices, "Welcome email text version", "Welcome email text version used by the registration workflow."),
        new("welcome_email_template_html", SettingCategory.EmailAndNotices, "Welcome email HTML version", "Welcome email HTML version used by the registration workflow."),
        new("welcome_email_from_name", SettingCategory.EmailAndNotices, "Welcome email sender name", "Welcome email sender name used by the registration workflow."),
        new("welcome_email_subject", SettingCategory.EmailAndNotices, "Welcome email subject", "Welcome email subject used by the registration workflow."),
        new("welcome_email_from_address", SettingCategory.EmailAndNotices, "Welcome email sender address", "Welcome email sender address used by the registration workflow."),
        new("ecard_welcome_email_subject", SettingCategory.EmailAndNotices, "E-card welcome email subject", "E-card welcome email subject used by the registration workflow."),
        new("postmark_api_key", SettingCategory.EmailAndNotices, "Postmark API key", "Secret credential used by the application to send welcome emails through Postmark; saved values remain concealed."),
        new("bypass_dupe_check", SettingCategory.DuplicateChecking, "Skip preliminary duplicate check", "Skips the application’s preliminary duplicate check before patron creation; Polaris may still perform its own duplicate checking."),
        new("duplicate_patron_message_html", SettingCategory.DuplicateChecking, "Duplicate patron message", "Duplicate patron message used by the registration workflow."),
        new("perform_papi_duplicate_bypass", SettingCategory.DuplicateChecking, "Attempt PAPI duplicate workaround", "When Polaris rejects registration as a duplicate, allows the application to retry using the configured duplicate-name workaround."),
        new("use_first_name_for_duplicate_workaround", SettingCategory.DuplicateChecking, "Apply duplicate workaround to first name", "Adds the duplicate-workaround suffix to the first name when enabled; otherwise it is added to the last name."),
        new("block_out_of_state_registrations", SettingCategory.AddressVerification, "Block out-of-state registrations", "Blocks registration when the submitted state differs from the branch’s configured state."),
        new("update_patron_record_with_melissa_address", SettingCategory.AddressVerification, "Save standardized Melissa address", "Save standardized Melissa address used by the registration workflow."),
        new("melissa_data_api_key", SettingCategory.AddressVerification, "Melissa Data API key", "Secret credential used to request Melissa Data address verification; saved values remain concealed."),
        new("valid_address_registration_text", SettingCategory.AddressVerification, "Verified-address success message", "Verified-address success message used by the registration workflow."),
        new("valid_address_plus_name_registration_text", SettingCategory.AddressVerification, "Address-and-name-match success message", "Address-and-name-match success message used by the registration workflow."),
        new("out_of_state_block_message", SettingCategory.AddressVerification, "Out-of-state registration message", "Out-of-state registration message used by the registration workflow."),
        new("valid_address_patron_code_id", SettingCategory.AddressVerification, "Verified-address patron code", "Verified-address patron code used by the registration workflow."),
        new("valid_address_plus_name_patron_code_id", SettingCategory.AddressVerification, "Address-and-name-match patron code", "Address-and-name-match patron code used by the registration workflow."),
        new("valid_address_record_set_id", SettingCategory.AddressVerification, "Verified-address record set", "Verified-address record set used by the registration workflow."),
        new("valid_address_plus_name_record_set_id", SettingCategory.AddressVerification, "Address-and-name-match record set", "Address-and-name-match record set used by the registration workflow."),
        new("invalid_address_record_set_id", SettingCategory.AddressVerification, "Invalid-address record set", "Invalid-address record set used by the registration workflow."),
        new("registration_logon_user_id", SettingCategory.PolarisIntegrationAndRecordSets, "Registration user for unverified addresses", "Polaris user ID used to create registrations whose address was not verified through the address-verification workflow."),
        new("add_to_record_set_id", SettingCategory.PolarisIntegrationAndRecordSets, "Additional post-registration record set", "Additional Polaris record set to which every successfully created patron is added when configured."),
        new("post_registration_note_text", SettingCategory.PolarisIntegrationAndRecordSets, "Patron note added after registration", "Text added to the created patron’s Polaris note after successful registration."),
        new("show_dl_ips", SettingCategory.KioskAndSessionBehavior, "On-site IP address prefixes", "Semicolon-separated IP address prefixes treated as on-site requests; these control driver’s-license scanner availability, automatic kiosk resetting, and whether remote registration is forced into e-card mode."),
        new("reset_form", SettingCategory.KioskAndSessionBehavior, "Automatically reset on-site form", "Automatically reset on-site form used by the registration workflow."),
        new("kiosk_registration_text", SettingCategory.KioskAndSessionBehavior, "On-site success message", "On-site success message used by the registration workflow."),
        new("kiosk_registration_header", SettingCategory.KioskAndSessionBehavior, "On-site registration introduction", "On-site registration introduction used by the registration workflow."),
        new("reset_seconds", SettingCategory.KioskAndSessionBehavior, "Automatic reset delay (seconds)", "Automatic reset delay (seconds) used by the registration workflow."),
    ];
    public IReadOnlyList<string> DynamicFieldSuffixes { get; } =
    [
        "PatronBranchID", "NameFirst", "NameMiddle", "NameLast", "UseLegalName",
        "LegalNameFirst", "LegalNameMiddle", "LegalNameLast", "Birthdate", "DeliveryOptionId",
        "PhoneVoice1", "PhoneVoice2", "ReceiveEreceipts", "EmailAddress", "AltEmailAddress",
        "StreetOne", "StreetTwo", "City", "State", "PostalCode", "Password", "Password2",
        "RequestPickupBranchID", "User1", "User5", "DeliverCardToSchool", "IsStudent",
        "IsTeacher", "IsECard", "AddToMailingList"
    ];
    public static IReadOnlyDictionary<string, string> FieldNames { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PatronBranchID"] = "Home branch", ["NameFirst"] = "First name",
            ["NameMiddle"] = "Middle name", ["NameLast"] = "Last name",
            ["UseLegalName"] = "Legal name option", ["LegalNameFirst"] = "Legal first name",
            ["LegalNameMiddle"] = "Legal middle name", ["LegalNameLast"] = "Legal last name",
            ["Birthdate"] = "Birth date", ["DeliveryOptionId"] = "Notification method",
            ["PhoneVoice1"] = "Primary phone number", ["PhoneVoice2"] = "Secondary phone number",
            ["ReceiveEreceipts"] = "E-receipts", ["EmailAddress"] = "Email address",
            ["AltEmailAddress"] = "Alternate email address", ["StreetOne"] = "Address line 1",
            ["StreetTwo"] = "Address line 2", ["City"] = "City", ["State"] = "State",
            ["PostalCode"] = "ZIP code", ["Password"] = "PIN", ["Password2"] = "Confirm PIN",
            ["RequestPickupBranchID"] = "Preferred pickup location", ["User1"] = "School",
            ["User5"] = "Responsible person", ["DeliverCardToSchool"] = "Deliver card to school",
            ["IsStudent"] = "Student", ["IsTeacher"] = "Teacher", ["IsECard"] = "E-card option",
            ["AddToMailingList"] = "Mailing-list option"
        };
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
        var list = OrdinarySettings.Select((setting, i) =>
        {
            var type = TypeFor(setting.Key);
            return new SettingDefinition(setting.Key, setting.DisplayName, setting.Description, type,
                IsSensitive: SensitiveKeys.Contains(setting.Key), AllowEmpty: AllowsEmpty(type), SortOrder: i,
                Category: setting.Category);
        }).ToList();
        foreach (var suffix in DynamicFieldSuffixes)
        {
            list.Add(new($"alert.{suffix}", FieldNames[suffix], $"Validation message for {FieldNames[suffix].ToLowerInvariant()}; reserved for future registration-form integration.", SettingValueType.LongString, SettingGroup.Alert));
        }
        foreach (var suffix in LabelFieldSuffixes)
        {
            list.Add(new($"label.{suffix}", FieldNames[suffix], $"Label displayed for {FieldNames[suffix].ToLowerInvariant()} on the registration form.", SettingValueType.ShortString, SettingGroup.Label));
        }
        foreach (var suffix in RequiredFieldSuffixes)
        {
            list.Add(new($"require.{suffix}", $"Require {FieldNames[suffix].ToLowerInvariant()}", $"Makes {FieldNames[suffix].ToLowerInvariant()} required on the registration form.", SettingValueType.Boolean, SettingGroup.Require, AllowEmpty: false));
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
        var titled = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(words);
        return Regex.Replace(titled, @"\b(?:Html|Css|Url|Id|Api|Sms|Papi|Ecard)\b", match => match.Value switch
        {
            "Html" => "HTML", "Css" => "CSS", "Url" => "URL", "Id" => "ID",
            "Api" => "API", "Sms" => "SMS", "Papi" => "PAPI", "Ecard" => "E-card",
            _ => match.Value
        });
    }

    private static string DescriptionFor(string displayName, SettingValueType type) => type switch
    {
        SettingValueType.Boolean => $"Controls whether {displayName.ToLowerInvariant()} is enabled on registration forms.",
        SettingValueType.Html => $"HTML content used for {displayName.ToLowerInvariant()}; preview it before saving.",
        SettingValueType.EmailTemplate => $"Content used for the {displayName.ToLowerInvariant()} sent after registration.",
        _ => $"Value used for {displayName.ToLowerInvariant()} during registration."
    };
}
