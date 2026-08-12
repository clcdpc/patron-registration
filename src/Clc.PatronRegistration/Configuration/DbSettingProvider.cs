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
            var metadata = SettingPropertyMetadataCache.Get(propertyName);
            return GetSetting<T>(metadata.DatabaseKey);
        }

        public virtual IdentifierSettingResult GetIdentifierState(string key) => IdentifierSettingParser.Parse(GetSetting<string>(key));
        public virtual ExpirationDateYearsSettingResult GetExpirationDateYearsState() =>
            ExpirationDateYearsSettingParser.Parse(GetSetting<string>(
                SettingPropertyMetadataCache.Get(nameof(ExpirationDateYears)).DatabaseKey));

        private int GetLegacySafeInteger([CallerMemberName] string propertyName = "")
        {
            var metadata = SettingPropertyMetadataCache.Get(propertyName);
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
        public string HeaderImageUrl => GetPropertySetting<string>();
        public string CssFile => GetPropertySetting<string>();
        public string WarningText => GetPropertySetting<string>();
        public string CustomFormFooterHtml => GetPropertySetting<string>();

        public IEnumerable<string> DriversLicenseButtonEnabledIpAddresses
        {
            get
            {
                var value = GetPropertySetting<string>();
                return string.IsNullOrWhiteSpace(value) ? new List<string>() : value.Split(';').ToList();
            }
        }

        public bool ResetForm => GetPropertySetting<bool>();
        public bool EnableDriversLicenseSwipe => GetPropertySetting<bool>();
        public bool HideGender => GetPropertySetting<bool>();
        public bool EnableAgeWarning => GetPropertySetting<bool>();
        public string AgeWarningText => GetPropertySetting<string>();
        public bool EnableAgeBlock => GetPropertySetting<bool>();
        public string AgeBlockText => GetPropertySetting<string>();
        public bool HideEreceipt => GetPropertySetting<bool>();
        public string NaGenderText => GetPropertySetting<string>();
        public bool NormalizeToUppercase => GetPropertySetting<bool>();
        public string DriversLicenseFormat => GetPropertySetting<string>();
        public bool BypassDupeCheck => GetPropertySetting<bool>();
        public string RegistrationText => GetPropertySetting<string>();
        public bool EnablePatronBranchSelectOption => GetPropertySetting<bool>();
        public bool BlockOutOfStateRegistrations => GetPropertySetting<bool>();
        public string RegistrationHeader => GetPropertySetting<string>();
        public string DuplicatePatronMessageHtml => GetPropertySetting<string>();
        public bool EnableLegalNameCheckbox => GetPropertySetting<bool>();
        public string LegalNameCheckboxLabel => GetSetting<string>("legal_name_checkbox_label");
        public bool UseLegalNameOnNotices => GetPropertySetting<bool>();
        public string DriversLicenseButtonText => GetPropertySetting<string>();
        public string DriversLicensePromptText => GetPropertySetting<string>();
        public string AgreementConfirmButtonText => GetPropertySetting<string>();
        public string AgreementCancelButtonText => GetPropertySetting<string>();
        public string KioskRegistrationText => GetPropertySetting<string>();
        public string KioskRegistrationHeader => GetPropertySetting<string>();
        public string SchoolInfoFieldLegend => GetPropertySetting<string>();
        public bool DisplayECardCheckbox => GetPropertySetting<bool>();
        public string ECardCheckboxLabel => GetSetting<string>("ecard_checkbox_label");
        public string MailingListDescriptionHtml => GetPropertySetting<string>();
        public bool DisplayMailingListCheckbox => GetPropertySetting<bool>();
        public string MailingListCheckboxLabel => GetSetting<string>("mailing_list_checkbox_label");
        public int MailingListRecordSetId => GetLegacySafeInteger();
        public int RegistrationLogonUserId => GetLegacySafeInteger();
        public int EcardPatronCodeId => GetLegacySafeInteger();
        public int TeacherPatronCodeId => GetLegacySafeInteger();
        public int StudentPatronCodeId => GetLegacySafeInteger();
        public string SchoolInfoFormat => GetPropertySetting<string>();
        public string ResponsiblePersonDisclaimer => GetPropertySetting<string>();
        public string EcardRegistrationText => GetPropertySetting<string>();
        public string SmsNoticeInformationHtml => GetPropertySetting<string>();
        public bool DisplaySmsNoticeInformation => GetPropertySetting<bool>();
        public string EcardWelcomeEmailTemplateText => GetPropertySetting<string>();
        public string EcardWelcomeEmailTemplateHtml => GetPropertySetting<string>();
        public string WelcomeEmailTemplateText => GetPropertySetting<string>();
        public string WelcomeEmailTemplateHtml => GetPropertySetting<string>();
        public string WelcomeEmailFromName => GetPropertySetting<string>();
        public string WelcomeEmailSubject => GetPropertySetting<string>();
        public string WelcomeEmailAddress => GetSetting<string>("welcome_email_from_address");
        public string EcardWelcomeEmailSubject => GetPropertySetting<string>();
        [JsonIgnore]
        public string PostmarkApiKey => GetPropertySetting<string>();
        public bool DisplayPreferredPickupLocation => GetPropertySetting<bool>();
        public bool RequirePreferredPickupLocation => GetPropertySetting<bool>();
        public bool DisplayResponsiblePersonField => GetPropertySetting<bool>();
        public bool PerformPapiDupeBypass => GetPropertySetting<bool>();
        public bool UseFirstNameForDuplicateWorkaround => GetPropertySetting<bool>();
        public bool UpdatePatronRecordWithMelissaAddress => GetPropertySetting<bool>();
        public string WelcomeEmailFromAddress => GetPropertySetting<string>();
        [JsonIgnore]
        public string MelissaDataApiKey => GetPropertySetting<string>();
        public string ValidAddressRegistrationText => GetPropertySetting<string>();
        public string ValidAddressPlusNameRegistrationText => GetPropertySetting<string>();
        public string OutOfStateBlockMessage => GetPropertySetting<string>();
        public string EcardBarcodePrefix => GetPropertySetting<string>();
        public int ValidAddressPatronCodeId => GetLegacySafeInteger();
        public int ValidAddressPlusNamePatronCodeId => GetLegacySafeInteger();
        public int ValidAddressRecordSetId => GetLegacySafeInteger();
        public int ValidAddressPlusNameRecordSetId => GetLegacySafeInteger();
        public int InvalidAddressRecordSetId => GetLegacySafeInteger();
        public int? AddToRecordSetId => GetPropertySetting<int?>();
        public string PostRegistrationNoteText => GetPropertySetting<string>();
        public DateTime? ExpirationDate => GetPropertySetting<DateTime?>();
        public int? ExpirationDateYears => GetPropertySetting<int?>();
        public int? PatronCodeId => GetPropertySetting<int?>();
        public bool HideBranchSelectIfOnlyOneBranch => GetPropertySetting<bool>();
        public bool DisableBranch => GetPropertySetting<bool>();
        public int ResetSeconds => GetPropertySetting<int>();
        public string PhoneNumberFormat => GetPropertySetting<string>();
        public bool ForceEcardRemotely => GetPropertySetting<bool>();
    }
}
