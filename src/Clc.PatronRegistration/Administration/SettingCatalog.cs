using System.Globalization;
using System.Net.Mail;

namespace Clc.PatronRegistration.Administration;

public enum SettingValueType { Boolean, Integer, NullableInteger, Decimal, Date, NullableDate, ShortString, LongString, Html, EmailTemplate, EmailAddress, Uri, Enumeration }
public enum SettingGroup { Ordinary, Alert, Label, Require }

public sealed record SettingDefinition(string Key, string DisplayName, string Description, SettingValueType ValueType,
    SettingGroup Group = SettingGroup.Ordinary, bool IsSensitive = false, bool AllowEmpty = true,
    IReadOnlyList<string>? AllowedValues = null, int SortOrder = 0)
{
    public string? Validate(string? value)
    {
        if (value is null) return "A value is required for an upsert operation.";
        if (value.Length == 0 && AllowEmpty) return null;
        return ValueType switch
        {
            SettingValueType.Boolean when !bool.TryParse(value, out _) => "Enter true or false.",
            SettingValueType.Integer when !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) => "Enter a whole number.",
            SettingValueType.NullableInteger when value.Length > 0 && !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) => "Enter a whole number or leave empty.",
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
}

public sealed class SettingCatalog : ISettingCatalog
{
    private static readonly string[] BooleanKeys = ["reset_form","show_dl","hide_gender","enable_age_warning","hide_ereceipt","normalize_to_uppercase","bypass_dupe_check","enable_patron_branch_select_option","block_out_of_state_registrations","enable_legal_name_checkbox","use_legal_name_on_notices","display_ecard_checkbox","display_mailing_list_checkbox","display_sms_notice_information","display_preferred_pickup_location","require_preferred_pickup_location","display_responsible_person_field","perform_papi_duplicate_bypass","use_first_name_for_duplicate_workaround","update_patron_record_with_melissa_address","hide_branch_select_if_only_one_option","disable_branch","force_ecard_remotely"];
    private static readonly string[] IntegerKeys = ["mailing_list_record_set_id","registration_logon_user_id","ecard_patron_code_id","teacher_patron_code_id","student_patron_code_id","valid_address_patron_code_id","valid_address_plus_name_patron_code_id","valid_address_record_set_id","valid_address_plus_name_record_set_id","invalid_address_record_set_id","reset_seconds"];
    private static readonly string[] NullableIntegerKeys = ["add_to_record_set_id","expiration_date_years","patron_code_id"];
    private static readonly string[] HtmlKeys = ["custom_form_footer_html","duplicate_patron_message_html","mailing_list_description_html","sms_notice_information_html"];
    private static readonly string[] TemplateKeys = ["ecard_welcome_email_template_text","ecard_welcome_email_template_html","welcome_email_template_text","welcome_email_template_html"];
    private static readonly string[] SensitiveKeys = ["postmark_api_key","melissa_data_api_key"];
    private static readonly string[] OrdinaryKeys = ["header_image_url","css_file","warning_text","custom_form_footer_html","show_dl_ips","reset_form","show_dl","hide_gender","enable_age_warning","age_warning_text","hide_ereceipt","na_gender_text","normalize_to_uppercase","dl_format","bypass_dupe_check","registration_text","enable_patron_branch_select_option","block_out_of_state_registrations","registration_form_header","duplicate_patron_message_html","enable_legal_name_checkbox","legal_name_checkbox_label","use_legal_name_on_notices","drivers_license_button_text","drivers_license_prompt_text","agreement_confirm_button_text","agreement_cancel_button_text","kiosk_registration_text","kiosk_registration_header","school_info_field_legend","display_ecard_checkbox","ecard_checkbox_label","mailing_list_description_html","display_mailing_list_checkbox","mailing_list_checkbox_label","mailing_list_record_set_id","registration_logon_user_id","ecard_patron_code_id","teacher_patron_code_id","student_patron_code_id","school_info_format","responsible_person_disclaimer","ecard_registration_text","sms_notice_information_html","display_sms_notice_information","ecard_welcome_email_template_text","ecard_welcome_email_template_html","welcome_email_template_text","welcome_email_template_html","welcome_email_from_name","welcome_email_subject","welcome_email_from_address","ecard_welcome_email_subject","postmark_api_key","display_preferred_pickup_location","require_preferred_pickup_location","display_responsible_person_field","perform_papi_duplicate_bypass","use_first_name_for_duplicate_workaround","update_patron_record_with_melissa_address","melissa_data_api_key","valid_address_registration_text","valid_address_plus_name_registration_text","out_of_state_block_message","ecard_barcode_prefix","valid_address_patron_code_id","valid_address_plus_name_patron_code_id","valid_address_record_set_id","valid_address_plus_name_record_set_id","invalid_address_record_set_id","add_to_record_set_id","post_registration_note_text","expiration_date","expiration_date_years","patron_code_id","hide_branch_select_if_only_one_option","disable_branch","reset_seconds","phone_number_format","force_ecard_remotely"];
    public IReadOnlyList<string> DynamicFieldSuffixes { get; } = ["PatronBranchID","NameFirst","NameMiddle","NameLast","LegalNameFirst","LegalNameMiddle","LegalNameLast","Birthdate","DeliveryOptionId","PhoneVoice1","PhoneVoice2","EmailAddress","AltEmailAddress","StreetOne","StreetTwo","City","State","PostalCode","Password","RequestPickupBranchID"];
    public IReadOnlyList<SettingDefinition> All { get; }
    private readonly Dictionary<string, SettingDefinition> byKey;

    public SettingCatalog()
    {
        var list = OrdinaryKeys.Select((key, i) => new SettingDefinition(key, Friendly(key), $"Registration setting {key}.", TypeFor(key), IsSensitive: SensitiveKeys.Contains(key), SortOrder: i)).ToList();
        foreach (var suffix in DynamicFieldSuffixes)
        {
            list.Add(new($"alert.{suffix}", $"{Friendly(suffix)} alert", "Validation message shown for this field.", SettingValueType.LongString, SettingGroup.Alert));
            list.Add(new($"label.{suffix}", $"{Friendly(suffix)} label", "Label shown for this field.", SettingValueType.ShortString, SettingGroup.Label));
            list.Add(new($"require.{suffix}", $"Require {Friendly(suffix)}", "Whether this field is required.", SettingValueType.Boolean, SettingGroup.Require, AllowEmpty: false));
        }
        All = list;
        byKey = list.ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);
    }
    public bool TryGet(string key, out SettingDefinition definition) => byKey.TryGetValue(key, out definition!);
    private static SettingValueType TypeFor(string key) => BooleanKeys.Contains(key) ? SettingValueType.Boolean : IntegerKeys.Contains(key) ? SettingValueType.Integer : NullableIntegerKeys.Contains(key) ? SettingValueType.NullableInteger : key == "expiration_date" ? SettingValueType.NullableDate : HtmlKeys.Contains(key) ? SettingValueType.Html : TemplateKeys.Contains(key) ? SettingValueType.EmailTemplate : key == "welcome_email_from_address" ? SettingValueType.EmailAddress : key == "header_image_url" ? SettingValueType.Uri : key.Contains("text") || key.Contains("html") || key.Contains("disclaimer") || key.Contains("message") ? SettingValueType.LongString : SettingValueType.ShortString;
    private static string Friendly(string key) => string.Concat(key.Replace('_', ' ').Select((c, i) => i == 0 || key.Replace('_',' ')[i - 1] == ' ' ? char.ToUpperInvariant(c) : c));
}
