using Clc.PatronRegistration.Data;
using Clc.PatronRegistration.Administration;
using Clc.PatronRegistration.Helpers;
using Clc.Polaris.Api;
using Clc.Polaris.Api.Models;
using Newtonsoft.Json;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using System.Globalization;

namespace Clc.PatronRegistration.Configuration
{
    public class DbSettingProvider : ISettingProvider, IIdentifierSettingStateProvider, IExpirationDateYearsSettingStateProvider
    {
        public int LibraryId { get; protected set; }
        public int OrganizationId { get; }
        [JsonIgnore]
        public ICache Cache { get; }
        public string FormCode { get; } = string.Empty;
        public int SystemOrganizationId { get; }

        public DbSettingProvider(int orgId, ICache cache) : this(orgId, cache, "", 1) { }

        public DbSettingProvider(int orgId, ICache cache, string formCode = "", int systemOrganizationId = 1, int? libraryId = null)
        {
            OrganizationId = orgId;
            FormCode = formCode;
            Cache = cache;
            SystemOrganizationId = systemOrganizationId;
            var branch = cache.OrganizationCache.Single(o => o.OrganizationID == OrganizationId);
            LibraryId = libraryId ?? Cache.OrganizationCache.GetLibrary(orgId).OrganizationID;
        }

        public virtual T GetSetting<T>(string name, T defaultValue = default!)
        {
            var dbValue = new SettingsResolver().Resolve(Cache.SettingsCache, name, OrganizationId, LibraryId, FormCode, SystemOrganizationId).EffectiveValue;
            return ConvertToType(dbValue, defaultValue);
        }

        /// <summary>Reads the database setting associated with the calling provider property.</summary>
        protected T GetPropertySetting<T>([CallerMemberName] string propertyName = "")
        {
            var metadata = SettingPropertyMetadataCache.Get(GetType(), propertyName);
            return GetSetting<T>(metadata.DatabaseKey);
        }

        public virtual IdentifierSettingResult GetIdentifierState(string key) => IdentifierSettingParser.Parse(GetSetting<string>(key));
        public virtual ExpirationDateYearsSettingResult GetExpirationDateYearsState() =>
            ExpirationDateYearsSettingParser.Parse(GetSetting<string>(
                SettingPropertyMetadataCache.Get(GetType(), nameof(ExpirationDateYears)).DatabaseKey));

        private int GetLegacySafeInteger([CallerMemberName] string propertyName = "")
        {
            var metadata = SettingPropertyMetadataCache.Get(GetType(), propertyName);
            return GetIdentifierState(metadata.DatabaseKey).Value.GetValueOrDefault();
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

            // Empty string is a meaningful configured value for strings, but legacy rows
            // containing an empty scalar value are equivalent to an unconfigured scalar.
            if (value.Length == 0 && t != typeof(string))
            {
                return defaultValue;
            }

            var isNullable = t.IsGenericType && t.GetGenericTypeDefinition().Equals(typeof(Nullable<>));
            if (t.IsGenericType && t.GetGenericTypeDefinition().Equals(typeof(Nullable<>)))
            {
                t = Nullable.GetUnderlyingType(t);
            }

            if (isNullable)
            {
                if (t == typeof(int))
                {
                    return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)
                        ? (T)(object)integer
                        : defaultValue;
                }
                if (t == typeof(DateTime))
                {
                    return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date)
                        ? (T)(object)date
                        : defaultValue;
                }
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

        public virtual List<string> GetRequiredFields()
        {
            var resolver = new SettingsResolver();
            return Cache.SettingsCache
                .Where(setting => setting.Setting.StartsWith("require.", StringComparison.OrdinalIgnoreCase))
                .Select(setting => setting.Setting)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(key => bool.TryParse(resolver.Resolve(Cache.SettingsCache, key, OrganizationId, LibraryId, FormCode, SystemOrganizationId).EffectiveValue, out var required) && required)
                .Select(key => key["require.".Length..])
                .ToList();
        }
        [AdminSetting(SettingCategory.PageAppearanceAndInstructions, "Header image URL", "Displays this image in the shared layout above the public registration page.", ValueType = SettingValueType.Uri)]
        public string HeaderImageUrl => GetPropertySetting<string>();
        [AdminSetting(SettingCategory.PageAppearanceAndInstructions, "CSS file", "Loads this stylesheet on the public registration page.")]
        public string CssFile => GetPropertySetting<string>();
        [AdminSetting(SettingCategory.PageAppearanceAndInstructions, "Registration agreement content", "Displays this agreement content before the registration form becomes available; blank content skips the agreement step.", ValueType = SettingValueType.LongString)]
        public string WarningText => GetPropertySetting<string>();
        [AdminSetting(SettingCategory.PageAppearanceAndInstructions, "Custom form footer HTML", "Renders this HTML beneath the public registration form.", ValueType = SettingValueType.Html)]
        public string CustomFormFooterHtml => GetPropertySetting<string>();

        [AdminSetting(SettingCategory.KioskAndSessionBehavior, "On-site IP address prefixes", "Semicolon-separated IP address prefixes treated as on-site requests; these control driver’s-license scanner availability, automatic kiosk resetting, and whether remote registration is forced into e-card mode.", Key = "show_dl_ips", ValueType = SettingValueType.ShortString)]
        public IEnumerable<string> DriversLicenseButtonEnabledIpAddresses
        {
            get
            {
                var value = GetPropertySetting<string>();
                return string.IsNullOrWhiteSpace(value) ? new List<string>() : value.Split(';').ToList();
            }
        }

        [AdminSetting(SettingCategory.KioskAndSessionBehavior, "Automatically reset on-site form", "Automatically reloads the registration form after a successful on-site registration.")]
        public bool ResetForm => GetPropertySetting<bool>();
        [AdminSetting(SettingCategory.FormBehaviorAndFields, "Enable driver’s license scanner", "Enables the driver’s-license input button for requests whose IP address is recognized as on-site.", Key = "show_dl")]
        public bool EnableDriversLicenseSwipe => GetPropertySetting<bool>();
        [AdminSetting(SettingCategory.FormBehaviorAndFields, "Hide gender field", "Removes the gender field from the public registration form when enabled.")]
        public bool HideGender => GetPropertySetting<bool>();
        [AdminSetting(SettingCategory.FormBehaviorAndFields, "Show age warning", "Shows the configured age-warning confirmation when the entered patron is under 18.")]
        public bool EnableAgeWarning => GetPropertySetting<bool>();
        [AdminSetting(SettingCategory.FormBehaviorAndFields, "Age warning message", "Provides the confirmation message shown for a patron under 18 when age warnings are enabled.", ValueType = SettingValueType.LongString)]
        public string AgeWarningText => GetPropertySetting<string>();
        [AdminSetting(SettingCategory.FormBehaviorAndFields, "Block registrations for patrons under 18", "Prevents a patron under 18 from continuing after entering a valid birth date.")]
        public bool EnableAgeBlock => GetPropertySetting<bool>();
        [AdminSetting(SettingCategory.FormBehaviorAndFields, "Underage registration blocking message", "Displays the message shown when an underage patron is prevented from continuing; the value is inserted as HTML.", ValueType = SettingValueType.Html)]
        public string AgeBlockText => GetPropertySetting<string>();
        [AdminSetting(SettingCategory.FormBehaviorAndFields, "Hide e-receipt option", "Removes the e-receipt preference from the public registration form when enabled.")]
        public bool HideEreceipt => GetPropertySetting<bool>();
        [AdminSetting(SettingCategory.FormBehaviorAndFields, "N/A gender option text", "Provides the text for the registration form’s not-applicable gender choice.", ValueType = SettingValueType.LongString)]
        public string NaGenderText => GetPropertySetting<string>();
        [AdminSetting(SettingCategory.FormBehaviorAndFields, "Convert registration data to uppercase", "Converts supported name, email, and address values to uppercase before patron creation.")]
        public bool NormalizeToUppercase => GetPropertySetting<bool>();
        [AdminSetting(SettingCategory.FormBehaviorAndFields, "Driver’s license scanner format", "Selects barcode or magnetic-stripe parsing for scanned driver’s-license data.", Key = "dl_format")]
        public string DriversLicenseFormat => GetPropertySetting<string>();
        [AdminSetting(SettingCategory.DuplicateChecking, "Skip preliminary duplicate check", "Skips the application’s preliminary duplicate check before patron creation; Polaris may still perform its own duplicate checking.")]
        public bool BypassDupeCheck => GetPropertySetting<bool>();
        [AdminSetting(SettingCategory.PageAppearanceAndInstructions, "Default success message", "Displays this default message after successful registration when no on-site, e-card, or address-verification message takes precedence.", ValueType = SettingValueType.LongString)]
        public string RegistrationText => GetPropertySetting<string>();
        [AdminSetting(SettingCategory.BranchAndPatronDefaults, "Allow patrons to choose a home branch", "Lets patrons choose their home branch instead of assigning the current or default branch.")]
        public bool EnablePatronBranchSelectOption => GetPropertySetting<bool>();
        [AdminSetting(SettingCategory.AddressVerification, "Block out-of-state registrations", "Blocks registration when the submitted address state is outside Ohio.")]
        public bool BlockOutOfStateRegistrations => GetPropertySetting<bool>();
        [AdminSetting(SettingCategory.PageAppearanceAndInstructions, "Registration form introduction", "Renders this introductory HTML above the registration form when configured.", Key = "registration_form_header")]
        public string RegistrationHeader => GetPropertySetting<string>();
        [AdminSetting(SettingCategory.DuplicateChecking, "Duplicate patron message", "Displays when the preliminary duplicate check finds a patron; [branch_phone] and [branch_id] placeholders are replaced with the selected branch’s values.", ValueType = SettingValueType.Html)]
        public string DuplicatePatronMessageHtml => GetPropertySetting<string>();
        [AdminSetting(SettingCategory.FormBehaviorAndFields, "Show legal-name option", "Displays the configurable legal-name option and its related legal-name fields on the registration form.")]
        public bool EnableLegalNameCheckbox => GetPropertySetting<bool>();
        public string LegalNameCheckboxLabel => GetSetting<string>("legal_name_checkbox_label");
        [AdminSetting(SettingCategory.EmailAndNotices, "Use legal name on notices", "Sends the submitted legal name to Polaris for notices when a legal first name is available.")]
        public bool UseLegalNameOnNotices => GetPropertySetting<bool>();
        [AdminSetting(SettingCategory.FormBehaviorAndFields, "Driver’s license button text", "Labels the button that starts driver’s-license input on eligible on-site requests.", ValueType = SettingValueType.LongString)]
        public string DriversLicenseButtonText => GetPropertySetting<string>();
        [AdminSetting(SettingCategory.FormBehaviorAndFields, "Driver’s license prompt text", "Prompts staff to enter or scan driver’s-license data after selecting the driver’s-license button.", ValueType = SettingValueType.LongString)]
        public string DriversLicensePromptText => GetPropertySetting<string>();
        [AdminSetting(SettingCategory.FormBehaviorAndFields, "Agreement accept button text", "Labels the button that accepts the registration agreement and reveals the form.", ValueType = SettingValueType.LongString)]
        public string AgreementConfirmButtonText => GetPropertySetting<string>();
        [AdminSetting(SettingCategory.FormBehaviorAndFields, "Agreement decline button text", "Labels the button that declines the registration agreement and leaves the form unavailable.", ValueType = SettingValueType.LongString)]
        public string AgreementCancelButtonText => GetPropertySetting<string>();
        [AdminSetting(SettingCategory.KioskAndSessionBehavior, "On-site success message", "Replaces the default success message after a successful on-site registration that will automatically reset.", ValueType = SettingValueType.LongString)]
        public string KioskRegistrationText => GetPropertySetting<string>();
        [AdminSetting(SettingCategory.KioskAndSessionBehavior, "On-site registration introduction", "Retained for compatibility; no current registration-page consumer has been established.")]
        public string KioskRegistrationHeader => GetPropertySetting<string>();
        [AdminSetting(SettingCategory.FormBehaviorAndFields, "School-information heading", "Displays as the heading above the school, student, teacher, and card-delivery fields.")]
        public string SchoolInfoFieldLegend => GetPropertySetting<string>();
        [AdminSetting(SettingCategory.ECardRegistration, "Show e-card option", "Displays the configurable e-card option when remote e-card forcing is not active.", Key = "display_ecard_checkbox")]
        public bool DisplayECardCheckbox => GetPropertySetting<bool>();
        public string ECardCheckboxLabel => GetSetting<string>("ecard_checkbox_label");
        [AdminSetting(SettingCategory.EmailAndNotices, "Mailing-list description", "Renders this explanatory HTML beside the mailing-list option when that option is displayed.", ValueType = SettingValueType.Html)]
        public string MailingListDescriptionHtml => GetPropertySetting<string>();
        [AdminSetting(SettingCategory.EmailAndNotices, "Show mailing-list option", "Displays the configurable mailing-list option on the registration form.")]
        public bool DisplayMailingListCheckbox => GetPropertySetting<bool>();
        public string MailingListCheckboxLabel => GetSetting<string>("mailing_list_checkbox_label");
        [AdminSetting(SettingCategory.EmailAndNotices, "Mailing-list record set", "Polaris record set to which patrons are added when they select the mailing-list option.")]
        public int MailingListRecordSetId => GetLegacySafeInteger();
        [AdminSetting(SettingCategory.PolarisIntegrationAndRecordSets, "Registration user for unverified addresses", "Polaris user ID used to create registrations whose address was not verified through the address-verification workflow.")]
        public int RegistrationLogonUserId => GetLegacySafeInteger();
        [AdminSetting(SettingCategory.ECardRegistration, "E-card patron code", "Assigns this Polaris patron code when e-card registration is selected.")]
        public int EcardPatronCodeId => GetLegacySafeInteger();
        [AdminSetting(SettingCategory.BranchAndPatronDefaults, "Teacher patron code", "Assigns this Polaris patron code when the school workflow identifies the registrant as a teacher.")]
        public int TeacherPatronCodeId => GetLegacySafeInteger();
        [AdminSetting(SettingCategory.BranchAndPatronDefaults, "Student patron code", "Assigns this Polaris patron code when the school workflow identifies the registrant as a student.")]
        public int StudentPatronCodeId => GetLegacySafeInteger();
        [AdminSetting(SettingCategory.FormBehaviorAndFields, "School-registration mode", "Enables the school, student, and teacher workflow and selects its configured operating mode; blank disables that workflow.")]
        public string SchoolInfoFormat => GetPropertySetting<string>();
        [AdminSetting(SettingCategory.FormBehaviorAndFields, "Responsible-person instructions", "Renders these instructions beside the responsible-person field when that field is displayed.", ValueType = SettingValueType.LongString)]
        public string ResponsiblePersonDisclaimer => GetPropertySetting<string>();
        [AdminSetting(SettingCategory.ECardRegistration, "E-card success message", "Replaces the default success message after an e-card registration when configured.", ValueType = SettingValueType.LongString)]
        public string EcardRegistrationText => GetPropertySetting<string>();
        [AdminSetting(SettingCategory.EmailAndNotices, "Text-message information", "Provides the HTML shown when Text Message is selected as the notification method and text-message information is enabled.", ValueType = SettingValueType.Html)]
        public string SmsNoticeInformationHtml => GetPropertySetting<string>();
        [AdminSetting(SettingCategory.EmailAndNotices, "Show text-message information", "Displays the configured text-message information when Text Message is selected as the notification method.")]
        public bool DisplaySmsNoticeInformation => GetPropertySetting<bool>();
        [AdminSetting(SettingCategory.EmailAndNotices, "E-card welcome email text version", "Plain-text body for e-card welcome emails; the standard plain-text template is used when this is blank.", ValueType = SettingValueType.EmailTemplate)]
        public string EcardWelcomeEmailTemplateText => GetPropertySetting<string>();
        [AdminSetting(SettingCategory.EmailAndNotices, "E-card welcome email HTML version", "HTML body for e-card welcome emails; the standard HTML template is used when this is blank.", ValueType = SettingValueType.EmailTemplate)]
        public string EcardWelcomeEmailTemplateHtml => GetPropertySetting<string>();
        [AdminSetting(SettingCategory.EmailAndNotices, "Welcome email text version", "Plain-text body for the standard welcome email sent after successful registration.", ValueType = SettingValueType.EmailTemplate)]
        public string WelcomeEmailTemplateText => GetPropertySetting<string>();
        [AdminSetting(SettingCategory.EmailAndNotices, "Welcome email HTML version", "HTML body for the standard welcome email sent after successful registration.", ValueType = SettingValueType.EmailTemplate)]
        public string WelcomeEmailTemplateHtml => GetPropertySetting<string>();
        [AdminSetting(SettingCategory.EmailAndNotices, "Welcome email sender name", "Sets the display name in the From header of welcome emails.")]
        public string WelcomeEmailFromName => GetPropertySetting<string>();
        [AdminSetting(SettingCategory.EmailAndNotices, "Welcome email subject", "Sets the subject for standard welcome emails.")]
        public string WelcomeEmailSubject => GetPropertySetting<string>();
        public string WelcomeEmailAddress => GetSetting<string>("welcome_email_from_address");
        [AdminSetting(SettingCategory.EmailAndNotices, "E-card welcome email subject", "Sets the subject for e-card welcome emails; the standard subject is used when this is blank.")]
        public string EcardWelcomeEmailSubject => GetPropertySetting<string>();
        [JsonIgnore]
        [AdminSetting(SettingCategory.EmailAndNotices, "Postmark API key", "Concealed credential used by the Postmark email client to send registration welcome emails.", IsSensitive = true)]
        public string PostmarkApiKey => GetPropertySetting<string>();
        [AdminSetting(SettingCategory.BranchAndPatronDefaults, "Show preferred pickup location", "Displays the preferred-pickup-location selector on the registration form.")]
        public bool DisplayPreferredPickupLocation => GetPropertySetting<bool>();
        public bool RequirePreferredPickupLocation => GetPropertySetting<bool>();
        [AdminSetting(SettingCategory.FormBehaviorAndFields, "Show responsible-person field", "Displays the responsible-person section on the registration form when enabled.")]
        public bool DisplayResponsiblePersonField => GetPropertySetting<bool>();
        [AdminSetting(SettingCategory.DuplicateChecking, "Attempt PAPI duplicate workaround", "When Polaris rejects registration as a duplicate, allows the application to retry using the configured duplicate-name workaround.", Key = "perform_papi_duplicate_bypass")]
        public bool PerformPapiDupeBypass => GetPropertySetting<bool>();
        [AdminSetting(SettingCategory.DuplicateChecking, "Apply duplicate workaround to first name", "Adds the duplicate-workaround suffix to the first name when enabled; otherwise it is added to the last name.")]
        public bool UseFirstNameForDuplicateWorkaround => GetPropertySetting<bool>();
        [AdminSetting(SettingCategory.AddressVerification, "Save standardized Melissa address", "Replaces submitted address fields with the standardized Melissa response before patron creation for verified addresses.")]
        public bool UpdatePatronRecordWithMelissaAddress => GetPropertySetting<bool>();
        [AdminSetting(SettingCategory.EmailAndNotices, "Welcome email sender address", "Sets the From email address and sender address used for welcome emails.", ValueType = SettingValueType.EmailAddress)]
        public string WelcomeEmailFromAddress => GetPropertySetting<string>();
        [JsonIgnore]
        [AdminSetting(SettingCategory.AddressVerification, "Melissa Data API key", "Concealed credential used by the Melissa client to verify submitted addresses.", IsSensitive = true)]
        public string MelissaDataApiKey => GetPropertySetting<string>();
        [AdminSetting(SettingCategory.AddressVerification, "Verified-address success message", "Replaces the default success message when Melissa verifies the address and the verified-address patron code remains assigned.", ValueType = SettingValueType.LongString)]
        public string ValidAddressRegistrationText => GetPropertySetting<string>();
        [AdminSetting(SettingCategory.AddressVerification, "Address-and-name-match success message", "Replaces the default success message when Melissa verifies both the address and name and the matching patron code remains assigned.", ValueType = SettingValueType.LongString)]
        public string ValidAddressPlusNameRegistrationText => GetPropertySetting<string>();
        [AdminSetting(SettingCategory.AddressVerification, "Out-of-state registration message", "Displays when registration is rejected because the submitted address state is outside Ohio.", ValueType = SettingValueType.LongString)]
        public string OutOfStateBlockMessage => GetPropertySetting<string>();
        [AdminSetting(SettingCategory.ECardRegistration, "E-card barcode prefix", "Prefixes the timestamp-based temporary barcode generated for an e-card registration.")]
        public string EcardBarcodePrefix => GetPropertySetting<string>();
        [AdminSetting(SettingCategory.AddressVerification, "Verified-address patron code", "Assigns this Polaris patron code when Melissa verifies the submitted address without a name match.")]
        public int ValidAddressPatronCodeId => GetLegacySafeInteger();
        [AdminSetting(SettingCategory.AddressVerification, "Address-and-name-match patron code", "Assigns this Polaris patron code when Melissa verifies the submitted address and finds a name match.")]
        public int ValidAddressPlusNamePatronCodeId => GetLegacySafeInteger();
        [AdminSetting(SettingCategory.AddressVerification, "Verified-address record set", "Adds a successfully created patron to this Polaris record set when the address is verified without a name match.")]
        public int ValidAddressRecordSetId => GetLegacySafeInteger();
        [AdminSetting(SettingCategory.AddressVerification, "Address-and-name-match record set", "Adds a successfully created patron to this Polaris record set when the address and name are both verified.")]
        public int ValidAddressPlusNameRecordSetId => GetLegacySafeInteger();
        [AdminSetting(SettingCategory.AddressVerification, "Invalid-address record set", "Adds a successfully created patron to this Polaris record set when address verification returns an invalid result.")]
        public int InvalidAddressRecordSetId => GetLegacySafeInteger();
        [AdminSetting(SettingCategory.PolarisIntegrationAndRecordSets, "Additional post-registration record set", "Additional Polaris record set to which every successfully created patron is added when configured.")]
        public int? AddToRecordSetId => GetPropertySetting<int?>();
        [AdminSetting(SettingCategory.PolarisIntegrationAndRecordSets, "Patron note added after registration", "Text added to the created patron’s Polaris note after successful registration.", ValueType = SettingValueType.LongString)]
        public string PostRegistrationNoteText => GetPropertySetting<string>();
        [AdminSetting(SettingCategory.BranchAndPatronDefaults, "Fixed expiration date", "Supplies one fixed patron expiration date; a configured years-based expiration takes precedence.")]
        public DateTime? ExpirationDate => GetPropertySetting<DateTime?>();
        [AdminSetting(SettingCategory.BranchAndPatronDefaults, "Expiration period (years)", "Calculates patron expiration relative to registration and takes precedence over the fixed expiration date.")]
        public int? ExpirationDateYears => GetPropertySetting<int?>();
        [AdminSetting(SettingCategory.BranchAndPatronDefaults, "Default patron code", "Assigns this Polaris patron code by default before more specific e-card or address-verification codes are applied.")]
        public int? PatronCodeId => GetPropertySetting<int?>();
        [AdminSetting(SettingCategory.BranchAndPatronDefaults, "Hide home branch when only one option exists", "Replaces the home-branch selector with its single available branch value.", Key = "hide_branch_select_if_only_one_option")]
        public bool HideBranchSelectIfOnlyOneBranch => GetPropertySetting<bool>();
        [AdminSetting(SettingCategory.BranchAndPatronDefaults, "Disable registration for this branch and form", "Causes registration submission to be skipped for this branch and form through the existing ShouldSkipRegistration check.")]
        public bool DisableBranch => GetPropertySetting<bool>();
        [AdminSetting(SettingCategory.KioskAndSessionBehavior, "Automatic reset delay (seconds)", "Sets the delay before the successful on-site registration page automatically reloads.")]
        public int ResetSeconds => GetPropertySetting<int>();
        [AdminSetting(SettingCategory.FormBehaviorAndFields, "Phone-number storage format", "Applies this replacement format to the primary phone number before patron creation.")]
        public string PhoneNumberFormat => GetPropertySetting<string>();
        [AdminSetting(SettingCategory.ECardRegistration, "Require e-card for remote registration", "Automatically selects e-card mode for requests whose IP address is not recognized as on-site.")]
        public bool ForceEcardRemotely => GetPropertySetting<bool>();
    }
}
