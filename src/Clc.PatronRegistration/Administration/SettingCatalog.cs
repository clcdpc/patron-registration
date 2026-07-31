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
    private sealed record SettingPresentation(string Key, SettingCategory Category, string DisplayName, string Description);

    private static readonly IReadOnlyList<SettingPresentation> Presentations =
    [
        new("header_image_url", SettingCategory.PageAppearanceAndInstructions, "Header image URL", "Displays this image URL at the top of the registration page."),
        new("css_file", SettingCategory.PageAppearanceAndInstructions, "CSS file", "Selects the CSS file used to style the public registration page."),
        new("warning_text", SettingCategory.PageAppearanceAndInstructions, "Registration agreement content", "Shows this agreement content before a patron confirms registration."),
        new("custom_form_footer_html", SettingCategory.PageAppearanceAndInstructions, "Custom form footer HTML", "Adds this HTML below the registration form; preview changes before publishing."),
        new("registration_text", SettingCategory.PageAppearanceAndInstructions, "Default success message", "Shows this message after a successful registration when no more specific success message applies."),
        new("registration_form_header", SettingCategory.PageAppearanceAndInstructions, "Registration form introduction", "Displays this introductory content above the registration form."),
        new("show_dl", SettingCategory.FormBehaviorAndFields, "Enable driver’s license scanner", "Makes the driver’s-license scanner available for requests identified as on-site."),
        new("hide_gender", SettingCategory.FormBehaviorAndFields, "Hide gender field", "Removes the gender field from the registration form."),
        new("enable_age_warning", SettingCategory.FormBehaviorAndFields, "Show age warning", "Shows an age-related warning during registration using the configured age warning message."),
        new("age_warning_text", SettingCategory.FormBehaviorAndFields, "Age warning message", "Provides the message shown when the age warning is triggered."),
        new("hide_ereceipt", SettingCategory.FormBehaviorAndFields, "Hide e-receipt option", "Removes the e-receipt choice from the registration form."),
        new("na_gender_text", SettingCategory.FormBehaviorAndFields, "N/A gender option text", "Sets the text shown for the not-applicable gender choice."),
        new("normalize_to_uppercase", SettingCategory.FormBehaviorAndFields, "Convert registration data to uppercase", "Converts supported registration text to uppercase before patron creation."),
        new("dl_format", SettingCategory.FormBehaviorAndFields, "Driver’s license scanner format", "Selects the input format used to parse data from the driver’s-license scanner."),
        new("enable_legal_name_checkbox", SettingCategory.FormBehaviorAndFields, "Show legal-name option", "Shows the option for entering a legal name separately from the patron’s preferred name."),
        new("legal_name_checkbox_label", SettingCategory.FormBehaviorAndFields, "Legal-name option label", "Sets the public label for the legal-name option."),
        new("drivers_license_button_text", SettingCategory.FormBehaviorAndFields, "Driver’s license button text", "Sets the text on the button that starts driver’s-license scanning."),
        new("drivers_license_prompt_text", SettingCategory.FormBehaviorAndFields, "Driver’s license prompt text", "Sets the instructions shown while requesting driver’s-license scanner input."),
        new("agreement_confirm_button_text", SettingCategory.FormBehaviorAndFields, "Agreement accept button text", "Sets the button text patrons use to accept the registration agreement."),
        new("agreement_cancel_button_text", SettingCategory.FormBehaviorAndFields, "Agreement decline button text", "Sets the button text patrons use to decline the registration agreement."),
        new("school_info_field_legend", SettingCategory.FormBehaviorAndFields, "School-information heading", "Sets the heading shown above school-related registration controls."),
        new("school_info_format", SettingCategory.FormBehaviorAndFields, "School-registration mode", "Selects how school and teacher or student registration controls are presented."),
        new("responsible_person_disclaimer", SettingCategory.FormBehaviorAndFields, "Responsible-person instructions", "Displays instructions next to the responsible-person field."),
        new("display_responsible_person_field", SettingCategory.FormBehaviorAndFields, "Show responsible-person field", "Shows the responsible-person field on the registration form."),
        new("phone_number_format", SettingCategory.FormBehaviorAndFields, "Phone-number storage format", "Selects how entered phone numbers are formatted before patron creation."),
        new("enable_patron_branch_select_option", SettingCategory.BranchAndPatronDefaults, "Allow patrons to choose a home branch", "Allows patrons to select their home branch instead of always using the current branch."),
        new("display_preferred_pickup_location", SettingCategory.BranchAndPatronDefaults, "Show preferred pickup location", "Shows the preferred pickup location control on the registration form."),
        new("require_preferred_pickup_location", SettingCategory.BranchAndPatronDefaults, "Require preferred pickup location", "Requires a preferred pickup location when that control is shown."),
        new("teacher_patron_code_id", SettingCategory.BranchAndPatronDefaults, "Teacher patron code", "Assigns this Polaris patron code to registrations identified as teachers."),
        new("student_patron_code_id", SettingCategory.BranchAndPatronDefaults, "Student patron code", "Assigns this Polaris patron code to registrations identified as students."),
        new("patron_code_id", SettingCategory.BranchAndPatronDefaults, "Default patron code", "Assigns this Polaris patron code when no more specific patron code applies."),
        new("expiration_date", SettingCategory.BranchAndPatronDefaults, "Fixed expiration date", "Uses this explicit patron expiration date; when an expiration period in years is also configured, the years-based value takes precedence."),
        new("expiration_date_years", SettingCategory.BranchAndPatronDefaults, "Expiration period (years)", "Calculates patron expiration relative to registration by this many years and takes precedence over a configured fixed expiration date."),
        new("hide_branch_select_if_only_one_option", SettingCategory.BranchAndPatronDefaults, "Hide home branch when only one option exists", "Hides the home-branch selector when the patron has only one available branch choice."),
        new("disable_branch", SettingCategory.BranchAndPatronDefaults, "Disable registration for this branch and form", "Prevents registration submissions in the selected branch and form context."),
        new("display_ecard_checkbox", SettingCategory.ECardRegistration, "Show e-card option", "Shows the option to request an e-card on the registration form."),
        new("ecard_checkbox_label", SettingCategory.ECardRegistration, "E-card option label", "Sets the public label for the e-card option."),
        new("ecard_patron_code_id", SettingCategory.ECardRegistration, "E-card patron code", "Assigns this Polaris patron code to successful e-card registrations."),
        new("ecard_registration_text", SettingCategory.ECardRegistration, "E-card success message", "Shows this message after a successful e-card registration."),
        new("ecard_barcode_prefix", SettingCategory.ECardRegistration, "E-card barcode prefix", "Prefixes generated e-card barcodes with this text."),
        new("force_ecard_remotely", SettingCategory.ECardRegistration, "Require e-card for remote registration", "Forces requests not identified as on-site into e-card registration mode."),
        new("display_mailing_list_checkbox", SettingCategory.EmailAndNotices, "Show mailing-list option", "Shows the option to join the mailing list on the registration form."),
        new("mailing_list_checkbox_label", SettingCategory.EmailAndNotices, "Mailing-list option label", "Sets the public label for the mailing-list option."),
        new("mailing_list_description_html", SettingCategory.EmailAndNotices, "Mailing-list description", "Displays this HTML beside the mailing-list option."),
        new("mailing_list_record_set_id", SettingCategory.EmailAndNotices, "Mailing-list record set", "Adds patrons who select the mailing-list option to this Polaris record set."),
        new("display_sms_notice_information", SettingCategory.EmailAndNotices, "Show text-message information", "Shows explanatory information about text-message notices."),
        new("sms_notice_information_html", SettingCategory.EmailAndNotices, "Text-message information", "Provides the HTML displayed with text-message notice choices."),
        new("use_legal_name_on_notices", SettingCategory.EmailAndNotices, "Use legal name on notices", "Uses the entered legal name for patron notices when a legal name is available."),
        new("ecard_welcome_email_template_text", SettingCategory.EmailAndNotices, "E-card welcome email text version", "Provides the plain-text welcome email sent after e-card registration."),
        new("ecard_welcome_email_template_html", SettingCategory.EmailAndNotices, "E-card welcome email HTML version", "Provides the HTML welcome email sent after e-card registration."),
        new("welcome_email_template_text", SettingCategory.EmailAndNotices, "Welcome email text version", "Provides the plain-text welcome email sent after standard registration."),
        new("welcome_email_template_html", SettingCategory.EmailAndNotices, "Welcome email HTML version", "Provides the HTML welcome email sent after standard registration."),
        new("welcome_email_from_name", SettingCategory.EmailAndNotices, "Welcome email sender name", "Sets the sender name displayed on welcome emails."),
        new("welcome_email_subject", SettingCategory.EmailAndNotices, "Welcome email subject", "Sets the subject for standard welcome emails."),
        new("welcome_email_from_address", SettingCategory.EmailAndNotices, "Welcome email sender address", "Sets the sender email address used for welcome emails."),
        new("ecard_welcome_email_subject", SettingCategory.EmailAndNotices, "E-card welcome email subject", "Sets the subject for e-card welcome emails."),
        new("postmark_api_key", SettingCategory.EmailAndNotices, "Postmark API key", "Supplies the secret credential used to send registration email through Postmark; existing values remain hidden."),
        new("bypass_dupe_check", SettingCategory.DuplicateChecking, "Skip preliminary duplicate check", "Skips the application’s preliminary duplicate check before patron creation; Polaris may still perform its own duplicate checking."),
        new("duplicate_patron_message_html", SettingCategory.DuplicateChecking, "Duplicate patron message", "Displays this HTML when the application identifies a possible existing patron."),
        new("perform_papi_duplicate_bypass", SettingCategory.DuplicateChecking, "Attempt PAPI duplicate workaround", "When Polaris rejects registration as a duplicate, allows the application to retry using the configured duplicate-name workaround."),
        new("use_first_name_for_duplicate_workaround", SettingCategory.DuplicateChecking, "Apply duplicate workaround to first name", "Adds the workaround suffix to the first name when enabled; otherwise it is added to the last name."),
        new("block_out_of_state_registrations", SettingCategory.AddressVerification, "Block out-of-state registrations", "Stops registration when address verification identifies an address outside the permitted state."),
        new("update_patron_record_with_melissa_address", SettingCategory.AddressVerification, "Save standardized Melissa address", "Uses the standardized address returned by Melissa when creating the patron record."),
        new("melissa_data_api_key", SettingCategory.AddressVerification, "Melissa Data API key", "Supplies the secret credential used for Melissa address verification; existing values remain hidden."),
        new("valid_address_registration_text", SettingCategory.AddressVerification, "Verified-address success message", "Shows this success message when the address is verified without a name match."),
        new("valid_address_plus_name_registration_text", SettingCategory.AddressVerification, "Address-and-name-match success message", "Shows this success message when address verification also matches the patron’s name."),
        new("out_of_state_block_message", SettingCategory.AddressVerification, "Out-of-state registration message", "Shows this message when an out-of-state registration is blocked."),
        new("valid_address_patron_code_id", SettingCategory.AddressVerification, "Verified-address patron code", "Assigns this Polaris patron code when the address is verified without a name match."),
        new("valid_address_plus_name_patron_code_id", SettingCategory.AddressVerification, "Address-and-name-match patron code", "Assigns this Polaris patron code when both address and name are verified."),
        new("valid_address_record_set_id", SettingCategory.AddressVerification, "Verified-address record set", "Adds patrons with a verified address but no name match to this Polaris record set."),
        new("valid_address_plus_name_record_set_id", SettingCategory.AddressVerification, "Address-and-name-match record set", "Adds patrons whose verified address also matches their name to this Polaris record set."),
        new("invalid_address_record_set_id", SettingCategory.AddressVerification, "Invalid-address record set", "Adds patrons whose address could not be verified to this Polaris record set."),
        new("registration_logon_user_id", SettingCategory.PolarisIntegrationAndRecordSets, "Registration user for unverified addresses", "Uses this Polaris user ID to create registrations whose address is not verified through the address-verification workflow."),
        new("add_to_record_set_id", SettingCategory.PolarisIntegrationAndRecordSets, "Additional post-registration record set", "Adds every successfully created patron to this additional Polaris record set when configured."),
        new("post_registration_note_text", SettingCategory.PolarisIntegrationAndRecordSets, "Patron note added after registration", "Adds this text to the created patron’s Polaris note after successful registration."),
        new("show_dl_ips", SettingCategory.KioskAndSessionBehavior, "On-site IP address prefixes", "A semicolon-separated list of IP address prefixes treated as on-site requests. It affects driver’s-license scanner availability, automatic kiosk resetting, and whether remote registration is forced into e-card mode."),
        new("reset_form", SettingCategory.KioskAndSessionBehavior, "Automatically reset on-site form", "Automatically clears the registration form after an on-site registration succeeds."),
        new("kiosk_registration_text", SettingCategory.KioskAndSessionBehavior, "On-site success message", "Shows this success message after an on-site registration."),
        new("kiosk_registration_header", SettingCategory.KioskAndSessionBehavior, "On-site registration introduction", "Displays this introductory content for on-site registration."),
        new("reset_seconds", SettingCategory.KioskAndSessionBehavior, "Automatic reset delay (seconds)", "Sets how many seconds an on-site success page waits before automatically resetting."),
    ];

    private static readonly IReadOnlyDictionary<string, string> DynamicFieldNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["PatronBranchID"] = "Home branch",
        ["NameFirst"] = "First name",
        ["NameMiddle"] = "Middle name",
        ["NameLast"] = "Last name",
        ["UseLegalName"] = "Legal name option",
        ["LegalNameFirst"] = "Legal first name",
        ["LegalNameMiddle"] = "Legal middle name",
        ["LegalNameLast"] = "Legal last name",
        ["Birthdate"] = "Birth date",
        ["DeliveryOptionId"] = "Notification method",
        ["PhoneVoice1"] = "Primary phone number",
        ["PhoneVoice2"] = "Secondary phone number",
        ["ReceiveEreceipts"] = "E-receipts",
        ["EmailAddress"] = "Email address",
        ["AltEmailAddress"] = "Alternate email address",
        ["StreetOne"] = "Address line 1",
        ["StreetTwo"] = "Address line 2",
        ["City"] = "City",
        ["State"] = "State",
        ["PostalCode"] = "ZIP code",
        ["Password"] = "PIN",
        ["Password2"] = "Confirm PIN",
        ["RequestPickupBranchID"] = "Preferred pickup location",
        ["User1"] = "School",
        ["User5"] = "Responsible person",
        ["DeliverCardToSchool"] = "Deliver card to school",
        ["IsStudent"] = "Student",
        ["IsTeacher"] = "Teacher",
        ["IsECard"] = "E-card option",
        ["AddToMailingList"] = "Mailing-list option",
    };

    public IReadOnlyList<string> DynamicFieldSuffixes { get; } = DynamicFieldNames.Keys.ToList();
    // Purpose-specific settings label the three checkboxes; User1 is populated by school-selection controls.
    public IReadOnlyList<string> LabelFieldSuffixes { get; } =
    [
        "PatronBranchID", "NameFirst", "NameMiddle", "NameLast", "LegalNameFirst",
        "LegalNameMiddle", "LegalNameLast", "Birthdate", "DeliveryOptionId", "PhoneVoice1",
        "PhoneVoice2", "ReceiveEreceipts", "EmailAddress", "StreetOne", "StreetTwo", "City",
        "State", "User5", "PostalCode", "Password", "Password2", "RequestPickupBranchID",
        "DeliverCardToSchool", "IsStudent", "IsTeacher"
    ];
    public IReadOnlyList<string> RequiredFieldSuffixes { get; } = ["PhoneVoice1", "EmailAddress", "User5"];
    public IReadOnlyList<SettingDefinition> All { get; }
    private readonly Dictionary<string, SettingDefinition> byKey;

    public SettingCatalog()
    {
        var list = Presentations.Select((presentation, i) =>
        {
            var type = TypeFor(presentation.Key);
            return new SettingDefinition(presentation.Key, presentation.DisplayName, presentation.Description, type,
                IsSensitive: SensitiveKeys.Contains(presentation.Key), AllowEmpty: AllowsEmpty(type), SortOrder: i,
                Category: presentation.Category);
        }).ToList();
        foreach (var suffix in DynamicFieldSuffixes)
            list.Add(new($"alert.{suffix}", DynamicFieldNames[suffix], "Stored for historical compatibility; runtime validation messages do not currently use this value.", SettingValueType.LongString, SettingGroup.Alert));
        foreach (var suffix in LabelFieldSuffixes)
            list.Add(new($"label.{suffix}", DynamicFieldNames[suffix], $"Sets the label shown for the {DynamicFieldNames[suffix].ToLowerInvariant()} field.", SettingValueType.ShortString, SettingGroup.Label));
        foreach (var suffix in RequiredFieldSuffixes)
            list.Add(new($"require.{suffix}", $"Require {DynamicFieldNames[suffix].ToLowerInvariant()}", $"Makes the {DynamicFieldNames[suffix].ToLowerInvariant()} field required on the registration form.", SettingValueType.Boolean, SettingGroup.Require, AllowEmpty: false));

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
