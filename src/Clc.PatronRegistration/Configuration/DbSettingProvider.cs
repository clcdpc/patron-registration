using Clc.PatronRegistration.Data;
using Clc.PatronRegistration.Helpers;
using Clc.Polaris.Api;
using Clc.Polaris.Api.Models;
using Newtonsoft.Json;
using System.Xml.Linq;
using System.Globalization;

namespace Clc.PatronRegistration.Configuration
{
    public class DbSettingProvider : ISettingProvider
    {
        public int LibraryId { get; }
        public int OrganizationId { get; }
        [JsonIgnore]
        public ICache Cache { get; }
        public string FormCode { get; } = string.Empty;

        public DbSettingProvider(int orgId, ICache cache) : this(orgId, cache, "") { }

        public DbSettingProvider(int orgId, ICache cache, string formCode = "")
        {
            OrganizationId = orgId;
            FormCode = formCode;
            Cache = cache;
            var branch = cache.OrganizationCache.Single(o => o.OrganizationID == OrganizationId);
            LibraryId = Cache.OrganizationCache.GetLibrary(orgId).OrganizationID;
        }

        public T GetSetting<T>(string name, T defaultValue = default!)
        {
            var dbValue = new SettingsResolver().Resolve(Cache.SettingsCache, name, OrganizationId, LibraryId, FormCode).EffectiveValue;
            return ConvertToType(dbValue, defaultValue);
        }

        public static T ConvertToType<T>(string? value, T defaultValue = default!)
        {
            var t = typeof(T);

            if (value is null)
            {
                if (t == typeof(string))
                {
                    defaultValue ??= (T)(object)"";
                }

                return defaultValue;
            }


            if (t.IsGenericType && t.GetGenericTypeDefinition().Equals(typeof(Nullable<>)))
            {
                t = Nullable.GetUnderlyingType(t);
            }

            return Type.GetTypeCode(t) switch
            {
                TypeCode.Int32 => (T)(object)int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture),
                TypeCode.Decimal => (T)(object)decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture),
                TypeCode.Boolean => (T)(object)bool.Parse(value),
                TypeCode.DateTime => (T)(object)DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                TypeCode.String => (T)(object)value,
                _ => throw new NotSupportedException($"Conversion to type {t!.Name} is not supported."),
            };
        }

        public string GetFieldLabel(string propertyName) => GetSetting($"label.{propertyName}", propertyName);
        public string GetFieldErrorMessage(string propertyName) => GetSetting<string>($"alert.{propertyName}");
        public bool GetFieldRequired(string propertyName) => GetSetting<bool>($"require.{propertyName}");

        public List<string> GetRequiredFields()
        {
            var fields = Cache.SettingsCache
                .Where(s => new[] { OrganizationId, LibraryId, 1 }.Contains(s.OrganizationID) && s.Setting.StartsWith("require.", StringComparison.OrdinalIgnoreCase) && new[] { "", FormCode }.Contains(s.FormCode, StringComparer.OrdinalIgnoreCase))
                .GroupBy(s => s.Setting)
                .SelectMany(s => s.OrderByDescending(c => c.OrganizationID).OrderByDescending(c => c.FormCode).Select(c => c.Setting))
                .Select(s => s.Split("require.")[1])
                .ToList();

            return fields;
        }
        public string HeaderImageUrl => GetSetting<string>("header_image_url");
        public string CssFile => GetSetting<string>("css_file");
        public string WarningText => GetSetting<string>("warning_text");
        public string CustomFormFooterHtml => GetSetting<string>("custom_form_footer_html");

        public IEnumerable<string> DriversLicenseButtonEnabledIpAddresses
        {
            get
            {
                var value = GetSetting<string>("show_dl_ips");
                return string.IsNullOrWhiteSpace(value) ? new List<string>() : value.Split(';').ToList();
            }
        }

        public bool ResetForm => GetSetting<bool>("reset_form");
        public bool EnableDriversLicenseSwipe => GetSetting<bool>("show_dl");
        public bool HideGender => GetSetting<bool>("hide_gender");
        public bool EnableAgeWarning => GetSetting<bool>("enable_age_warning");
        public string AgeWarningText => GetSetting<string>("age_warning_text");
        public bool HideEreceipt => GetSetting<bool>("hide_ereceipt");
        public string NaGenderText => GetSetting<string>("na_gender_text");
        public bool NormalizeToUppercase => GetSetting<bool>("normalize_to_uppercase");
        public string DriversLicenseFormat => GetSetting<string>("dl_format");
        public bool BypassDupeCheck => GetSetting<bool>("bypass_dupe_check");
        public string RegistrationText => GetSetting<string>("registration_text");
        public bool EnablePatronBranchSelectOption => GetSetting<bool>("enable_patron_branch_select_option");
        public bool BlockOutOfStateRegistrations => GetSetting<bool>("block_out_of_state_registrations");
        public string RegistrationHeader => GetSetting<string>("registration_form_header");
        public string DuplicatePatronMessageHtml => GetSetting<string>("duplicate_patron_message_html");
        public bool EnableLegalNameCheckbox => GetSetting<bool>("enable_legal_name_checkbox");
        public string LegalNameCheckboxLabel => GetSetting<string>("legal_name_checkbox_label");
        public bool UseLegalNameOnNotices => GetSetting<bool>("use_legal_name_on_notices");
        public string DriversLicenseButtonText => GetSetting<string>("drivers_license_button_text");
        public string DriversLicensePromptText => GetSetting<string>("drivers_license_prompt_text");
        public string AgreementConfirmButtonText => GetSetting<string>("agreement_confirm_button_text");
        public string AgreementCancelButtonText => GetSetting<string>("agreement_cancel_button_text");
        public string KioskRegistrationText => GetSetting<string>("kiosk_registration_text");
        public string KioskRegistrationHeader => GetSetting<string>("kiosk_registration_header");
        public string SchoolInfoFieldLegend => GetSetting<string>("school_info_field_legend");
        public bool DisplayECardCheckbox => GetSetting<bool>("display_ecard_checkbox");
        public string ECardCheckboxLabel => GetSetting<string>("ecard_checkbox_label");
        public string MailingListDescriptionHtml => GetSetting<string>("mailing_list_description_html");
        public bool DisplayMailingListCheckbox => GetSetting<bool>("display_mailing_list_checkbox");
        public string MailingListCheckboxLabel => GetSetting<string>("mailing_list_checkbox_label");
        public int MailingListRecordSetId => GetSetting<int>("mailing_list_record_set_id");
        public int RegistrationLogonUserId => GetSetting<int>("registration_logon_user_id");
        public int EcardPatronCodeId => GetSetting<int>("ecard_patron_code_id");
        public int TeacherPatronCodeId => GetSetting<int>("teacher_patron_code_id");
        public int StudentPatronCodeId => GetSetting<int>("student_patron_code_id");
        public string SchoolInfoFormat => GetSetting<string>("school_info_format");
        public string ResponsiblePersonDisclaimer => GetSetting<string>("responsible_person_disclaimer");
        public string EcardRegistrationText => GetSetting<string>("ecard_registration_text");
        public string SmsNoticeInformationHtml => GetSetting<string>("sms_notice_information_html");
        public bool DisplaySmsNoticeInformation => GetSetting<bool>("display_sms_notice_information");
        public string EcardWelcomeEmailTemplateText => GetSetting<string>("ecard_welcome_email_template_text");
        public string EcardWelcomeEmailTemplateHtml => GetSetting<string>("ecard_welcome_email_template_html");
        public string WelcomeEmailTemplateText => GetSetting<string>("welcome_email_template_text");
        public string WelcomeEmailTemplateHtml => GetSetting<string>("welcome_email_template_html");
        public string WelcomeEmailFromName => GetSetting<string>("welcome_email_from_name");
        public string WelcomeEmailSubject => GetSetting<string>("welcome_email_subject");
        public string WelcomeEmailAddress => GetSetting<string>("welcome_email_from_address");
        public string EcardWelcomeEmailSubject => GetSetting<string>("ecard_welcome_email_subject");
        public string PostmarkApiKey => GetSetting<string>("postmark_api_key");
        public bool DisplayPreferredPickupLocation => GetSetting<bool>("display_preferred_pickup_location");
        public bool RequirePreferredPickupLocation => GetSetting<bool>("require_preferred_pickup_location");
        public bool DisplayResponsiblePersonField => GetSetting<bool>("display_responsible_person_field");
        public bool PerformPapiDupeBypass => GetSetting<bool>("perform_papi_duplicate_bypass");
        public bool UseFirstNameForDuplicateWorkaround => GetSetting<bool>("use_first_name_for_duplicate_workaround");
        public bool UpdatePatronRecordWithMelissaAddress => GetSetting<bool>("update_patron_record_with_melissa_address");
        public string WelcomeEmailFromAddress => GetSetting<string>("welcome_email_from_address");
        public string MelissaDataApiKey => GetSetting<string>("melissa_data_api_key");
        public string ValidAddressRegistrationText => GetSetting<string>("valid_address_registration_text");
        public string ValidAddressPlusNameRegistrationText => GetSetting<string>("valid_address_plus_name_registration_text");
        public string OutOfStateBlockMessage => GetSetting<string>("out_of_state_block_message");
        public string EcardBarcodePrefix => GetSetting<string>("ecard_barcode_prefix");
        public int ValidAddressPatronCodeId => GetSetting<int>("valid_address_patron_code_id");
        public int ValidAddressPlusNamePatronCodeId => GetSetting<int>("valid_address_plus_name_patron_code_id");
        public int ValidAddressRecordSetId => GetSetting<int>("valid_address_record_set_id");
        public int ValidAddressPlusNameRecordSetId => GetSetting<int>("valid_address_plus_name_record_set_id");
        public int InvalidAddressRecordSetId => GetSetting<int>("invalid_address_record_set_id");
        public int? AddToRecordSetId => GetSetting<int>("add_to_record_set_id");
        public string PostRegistrationNoteText => GetSetting<string>("post_registration_note_text");
        public DateTime? ExpirationDate => GetSetting<DateTime?>("expiration_date");
        public int? ExpirationDateYears => GetSetting<int?>("expiration_date_years");
        public int? PatronCodeId => GetSetting<int?>("patron_code_id");
        public bool HideBranchSelectIfOnlyOneBranch => GetSetting<bool>("hide_branch_select_if_only_one_option");
        public bool DisableBranch => GetSetting<bool>("disable_branch");
        public int ResetSeconds => GetSetting<int>("reset_seconds");
        public string PhoneNumberFormat => GetSetting<string>("phone_number_format");
        public bool ForceEcardRemotely => GetSetting<bool>("force_ecard_remotely");
    }
}
