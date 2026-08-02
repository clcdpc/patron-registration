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
    public const int MaximumExpirationDateYears = 100;
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
            SettingValueType.NullableInteger when Key.Equals("expiration_date_years", StringComparison.OrdinalIgnoreCase) &&
                (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var expirationYears) ||
                 expirationYears < 0 || expirationYears > MaximumExpirationDateYears) =>
                $"Enter a whole number from 0 through {MaximumExpirationDateYears}, or leave empty.",
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
        new("header_image_url", SettingCategory.PageAppearanceAndInstructions, "Header image URL", "Displays this image in the shared layout above the public registration page."),
        new("css_file", SettingCategory.PageAppearanceAndInstructions, "CSS file", "Loads this stylesheet on the public registration page."),
        new("warning_text", SettingCategory.PageAppearanceAndInstructions, "Registration agreement content", "Displays this agreement content before the registration form becomes available; blank content skips the agreement step."),
        new("custom_form_footer_html", SettingCategory.PageAppearanceAndInstructions, "Custom form footer HTML", "Renders this HTML beneath the public registration form."),
        new("registration_text", SettingCategory.PageAppearanceAndInstructions, "Default success message", "Displays this default message after successful registration when no on-site, e-card, or address-verification message takes precedence."),
        new("registration_form_header", SettingCategory.PageAppearanceAndInstructions, "Registration form introduction", "Renders this introductory HTML above the registration form when configured."),
        new("show_dl", SettingCategory.FormBehaviorAndFields, "Enable driver’s license scanner", "Enables the driver’s-license input button for requests whose IP address is recognized as on-site."),
        new("hide_gender", SettingCategory.FormBehaviorAndFields, "Hide gender field", "Removes the gender field from the public registration form when enabled."),
        new("enable_age_warning", SettingCategory.FormBehaviorAndFields, "Show age warning", "Shows the configured age-warning confirmation when the entered patron is under 18."),
        new("age_warning_text", SettingCategory.FormBehaviorAndFields, "Age warning message", "Provides the confirmation message shown for a patron under 18 when age warnings are enabled."),
        new("hide_ereceipt", SettingCategory.FormBehaviorAndFields, "Hide e-receipt option", "Removes the e-receipt preference from the public registration form when enabled."),
        new("na_gender_text", SettingCategory.FormBehaviorAndFields, "N/A gender option text", "Provides the text for the registration form’s not-applicable gender choice."),
        new("normalize_to_uppercase", SettingCategory.FormBehaviorAndFields, "Convert registration data to uppercase", "Converts supported name, email, and address values to uppercase before patron creation."),
        new("dl_format", SettingCategory.FormBehaviorAndFields, "Driver’s license scanner format", "Selects barcode or magnetic-stripe parsing for scanned driver’s-license data."),
        new("enable_legal_name_checkbox", SettingCategory.FormBehaviorAndFields, "Show legal-name option", "Displays the configurable legal-name option and its related legal-name fields on the registration form."),
        new("drivers_license_button_text", SettingCategory.FormBehaviorAndFields, "Driver’s license button text", "Labels the button that starts driver’s-license input on eligible on-site requests."),
        new("drivers_license_prompt_text", SettingCategory.FormBehaviorAndFields, "Driver’s license prompt text", "Prompts staff to enter or scan driver’s-license data after selecting the driver’s-license button."),
        new("agreement_confirm_button_text", SettingCategory.FormBehaviorAndFields, "Agreement accept button text", "Labels the button that accepts the registration agreement and reveals the form."),
        new("agreement_cancel_button_text", SettingCategory.FormBehaviorAndFields, "Agreement decline button text", "Labels the button that declines the registration agreement and leaves the form unavailable."),
        new("school_info_field_legend", SettingCategory.FormBehaviorAndFields, "School-information heading", "Displays as the heading above the school, student, teacher, and card-delivery fields."),
        new("school_info_format", SettingCategory.FormBehaviorAndFields, "School-registration mode", "Enables the school, student, and teacher workflow and selects its configured operating mode; blank disables that workflow."),
        new("responsible_person_disclaimer", SettingCategory.FormBehaviorAndFields, "Responsible-person instructions", "Renders these instructions beside the responsible-person field when that field is displayed."),
        new("display_responsible_person_field", SettingCategory.FormBehaviorAndFields, "Show responsible-person field", "Displays the responsible-person section on the registration form when enabled."),
        new("phone_number_format", SettingCategory.FormBehaviorAndFields, "Phone-number storage format", "Applies this replacement format to the primary phone number before patron creation."),
        new("enable_patron_branch_select_option", SettingCategory.BranchAndPatronDefaults, "Allow patrons to choose a home branch", "Lets patrons choose their home branch instead of assigning the current or default branch."),
        new("display_preferred_pickup_location", SettingCategory.BranchAndPatronDefaults, "Show preferred pickup location", "Displays the preferred-pickup-location selector on the registration form."),
        new("teacher_patron_code_id", SettingCategory.BranchAndPatronDefaults, "Teacher patron code", "Assigns this Polaris patron code when the school workflow identifies the registrant as a teacher."),
        new("student_patron_code_id", SettingCategory.BranchAndPatronDefaults, "Student patron code", "Assigns this Polaris patron code when the school workflow identifies the registrant as a student."),
        new("patron_code_id", SettingCategory.BranchAndPatronDefaults, "Default patron code", "Assigns this Polaris patron code by default before more specific e-card or address-verification codes are applied."),
        new("expiration_date", SettingCategory.BranchAndPatronDefaults, "Fixed expiration date", "Supplies one fixed patron expiration date; a configured years-based expiration takes precedence."),
        new("expiration_date_years", SettingCategory.BranchAndPatronDefaults, "Expiration period (years)", "Calculates patron expiration relative to registration and takes precedence over the fixed expiration date."),
        new("hide_branch_select_if_only_one_option", SettingCategory.BranchAndPatronDefaults, "Hide home branch when only one option exists", "Replaces the home-branch selector with its single available branch value."),
        new("disable_branch", SettingCategory.BranchAndPatronDefaults, "Disable registration for this branch and form", "Causes registration submission to be skipped for this branch and form through the existing ShouldSkipRegistration check."),
        new("display_ecard_checkbox", SettingCategory.ECardRegistration, "Show e-card option", "Displays the configurable e-card option when remote e-card forcing is not active."),
        new("ecard_patron_code_id", SettingCategory.ECardRegistration, "E-card patron code", "Assigns this Polaris patron code when e-card registration is selected."),
        new("ecard_registration_text", SettingCategory.ECardRegistration, "E-card success message", "Replaces the default success message after an e-card registration when configured."),
        new("ecard_barcode_prefix", SettingCategory.ECardRegistration, "E-card barcode prefix", "Prefixes the timestamp-based temporary barcode generated for an e-card registration."),
        new("force_ecard_remotely", SettingCategory.ECardRegistration, "Require e-card for remote registration", "Automatically selects e-card mode for requests whose IP address is not recognized as on-site."),
        new("display_mailing_list_checkbox", SettingCategory.EmailAndNotices, "Show mailing-list option", "Displays the configurable mailing-list option on the registration form."),
        new("mailing_list_description_html", SettingCategory.EmailAndNotices, "Mailing-list description", "Renders this explanatory HTML beside the mailing-list option when that option is displayed."),
        new("mailing_list_record_set_id", SettingCategory.EmailAndNotices, "Mailing-list record set", "Polaris record set to which patrons are added when they select the mailing-list option."),
        new("display_sms_notice_information", SettingCategory.EmailAndNotices, "Show text-message information", "Displays the configured text-message information when Text Message is selected as the notification method."),
        new("sms_notice_information_html", SettingCategory.EmailAndNotices, "Text-message information", "Provides the HTML shown when Text Message is selected as the notification method and text-message information is enabled."),
        new("use_legal_name_on_notices", SettingCategory.EmailAndNotices, "Use legal name on notices", "Sends the submitted legal name to Polaris for notices when a legal first name is available."),
        new("ecard_welcome_email_template_text", SettingCategory.EmailAndNotices, "E-card welcome email text version", "Plain-text body for e-card welcome emails; the standard plain-text template is used when this is blank."),
        new("ecard_welcome_email_template_html", SettingCategory.EmailAndNotices, "E-card welcome email HTML version", "HTML body for e-card welcome emails; the standard HTML template is used when this is blank."),
        new("welcome_email_template_text", SettingCategory.EmailAndNotices, "Welcome email text version", "Plain-text body for the standard welcome email sent after successful registration."),
        new("welcome_email_template_html", SettingCategory.EmailAndNotices, "Welcome email HTML version", "HTML body for the standard welcome email sent after successful registration."),
        new("welcome_email_from_name", SettingCategory.EmailAndNotices, "Welcome email sender name", "Sets the display name in the From header of welcome emails."),
        new("welcome_email_subject", SettingCategory.EmailAndNotices, "Welcome email subject", "Sets the subject for standard welcome emails."),
        new("welcome_email_from_address", SettingCategory.EmailAndNotices, "Welcome email sender address", "Sets the From email address and sender address used for welcome emails."),
        new("ecard_welcome_email_subject", SettingCategory.EmailAndNotices, "E-card welcome email subject", "Sets the subject for e-card welcome emails; the standard subject is used when this is blank."),
        new("postmark_api_key", SettingCategory.EmailAndNotices, "Postmark API key", "Concealed credential used by the Postmark email client to send registration welcome emails."),
        new("bypass_dupe_check", SettingCategory.DuplicateChecking, "Skip preliminary duplicate check", "Skips the application’s preliminary duplicate check before patron creation; Polaris may still perform its own duplicate checking."),
        new("duplicate_patron_message_html", SettingCategory.DuplicateChecking, "Duplicate patron message", "Displays when the preliminary duplicate check finds a patron; [branch_phone] and [branch_id] placeholders are replaced with the selected branch’s values."),
        new("perform_papi_duplicate_bypass", SettingCategory.DuplicateChecking, "Attempt PAPI duplicate workaround", "When Polaris rejects registration as a duplicate, allows the application to retry using the configured duplicate-name workaround."),
        new("use_first_name_for_duplicate_workaround", SettingCategory.DuplicateChecking, "Apply duplicate workaround to first name", "Adds the duplicate-workaround suffix to the first name when enabled; otherwise it is added to the last name."),
        new("block_out_of_state_registrations", SettingCategory.AddressVerification, "Block out-of-state registrations", "Blocks registration when the submitted address state is outside Ohio."),
        new("update_patron_record_with_melissa_address", SettingCategory.AddressVerification, "Save standardized Melissa address", "Replaces submitted address fields with the standardized Melissa response before patron creation for verified addresses."),
        new("melissa_data_api_key", SettingCategory.AddressVerification, "Melissa Data API key", "Concealed credential used by the Melissa client to verify submitted addresses."),
        new("valid_address_registration_text", SettingCategory.AddressVerification, "Verified-address success message", "Replaces the default success message when Melissa verifies the address and the verified-address patron code remains assigned."),
        new("valid_address_plus_name_registration_text", SettingCategory.AddressVerification, "Address-and-name-match success message", "Replaces the default success message when Melissa verifies both the address and name and the matching patron code remains assigned."),
        new("out_of_state_block_message", SettingCategory.AddressVerification, "Out-of-state registration message", "Displays when registration is rejected because the submitted address state is outside Ohio."),
        new("valid_address_patron_code_id", SettingCategory.AddressVerification, "Verified-address patron code", "Assigns this Polaris patron code when Melissa verifies the submitted address without a name match."),
        new("valid_address_plus_name_patron_code_id", SettingCategory.AddressVerification, "Address-and-name-match patron code", "Assigns this Polaris patron code when Melissa verifies the submitted address and finds a name match."),
        new("valid_address_record_set_id", SettingCategory.AddressVerification, "Verified-address record set", "Adds a successfully created patron to this Polaris record set when the address is verified without a name match."),
        new("valid_address_plus_name_record_set_id", SettingCategory.AddressVerification, "Address-and-name-match record set", "Adds a successfully created patron to this Polaris record set when the address and name are both verified."),
        new("invalid_address_record_set_id", SettingCategory.AddressVerification, "Invalid-address record set", "Adds a successfully created patron to this Polaris record set when address verification returns an invalid result."),
        new("registration_logon_user_id", SettingCategory.PolarisIntegrationAndRecordSets, "Registration user for unverified addresses", "Polaris user ID used to create registrations whose address was not verified through the address-verification workflow."),
        new("add_to_record_set_id", SettingCategory.PolarisIntegrationAndRecordSets, "Additional post-registration record set", "Additional Polaris record set to which every successfully created patron is added when configured."),
        new("post_registration_note_text", SettingCategory.PolarisIntegrationAndRecordSets, "Patron note added after registration", "Text added to the created patron’s Polaris note after successful registration."),
        new("show_dl_ips", SettingCategory.KioskAndSessionBehavior, "On-site IP address prefixes", "Semicolon-separated IP address prefixes treated as on-site requests; these control driver’s-license scanner availability, automatic kiosk resetting, and whether remote registration is forced into e-card mode."),
        new("reset_form", SettingCategory.KioskAndSessionBehavior, "Automatically reset on-site form", "Automatically reloads the registration form after a successful on-site registration."),
        new("kiosk_registration_text", SettingCategory.KioskAndSessionBehavior, "On-site success message", "Replaces the default success message after a successful on-site registration that will automatically reset."),
        new("kiosk_registration_header", SettingCategory.KioskAndSessionBehavior, "On-site registration introduction", "Retained for compatibility; no current registration-page consumer has been established."),
        new("reset_seconds", SettingCategory.KioskAndSessionBehavior, "Automatic reset delay (seconds)", "Sets the delay before the successful on-site registration page automatically reloads."),
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
}
