using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using static Dapper.SqlMapper;
using static System.Net.Mime.MediaTypeNames;

namespace Clc.PatronRegistration.Configuration
{
    public interface ISettingProvider
    {
        int OrganizationId { get; }
        int LibraryId { get; }
        string FormCode { get; }

        [JsonIgnore]
        string HeaderImageUrl { get; }
        [JsonIgnore]
        string WarningText { get; }
        [JsonIgnore]
        string CustomFormFooterHtml { get; }
        [JsonIgnore]
        string DuplicatePatronMessageHtml { get; }
        [JsonIgnore]
        string NaGenderText { get; }
        [JsonIgnore]
        string RegistrationText { get; }
        [JsonIgnore]
        string RegistrationHeader { get; }
        [JsonIgnore]
        string AgeWarningText { get; }
        [JsonIgnore]
        string AgeBlockText { get; }
        [JsonIgnore]
        string DriversLicenseButtonText { get; }
        [JsonIgnore]
        string DriversLicensePromptText { get; }
        [JsonIgnore]
        string AgreementConfirmButtonText { get; }
        [JsonIgnore]
        string AgreementCancelButtonText { get; }
        [JsonIgnore]
        string KioskRegistrationText { get; }
        [JsonIgnore]
        string KioskRegistrationHeader { get; }
        [JsonIgnore]
        string SchoolInfoFieldLegend { get; }
        [JsonIgnore]
        string LegalNameCheckboxLabel { get; }
        [JsonIgnore]
        string ECardCheckboxLabel { get; }
        [JsonIgnore]
        string CssFile { get; }
        [JsonIgnore]
        string MailingListCheckboxLabel { get; }
        [JsonIgnore]
        string MailingListDescriptionHtml { get; }
        [JsonIgnore]
        string EcardRegistrationText { get; }
        [JsonIgnore]
        string SmsNoticeInformationHtml { get; }
        bool DisplaySmsNoticeInformation { get; }
        [JsonIgnore]
        string EcardWelcomeEmailTemplateText { get; }
        [JsonIgnore]
        string EcardWelcomeEmailTemplateHtml { get; }
        [JsonIgnore]
        string WelcomeEmailTemplateText { get; }
        [JsonIgnore]
        string WelcomeEmailTemplateHtml { get; }
        [JsonIgnore]
        string PostmarkApiKey { get; }
        [JsonIgnore]
        string MelissaDataApiKey { get; }
        [JsonIgnore]
        string ResponsiblePersonDisclaimer { get; }
        [JsonIgnore]
        string WelcomeEmailFromName { get; }
        [JsonIgnore]
        string WelcomeEmailFromAddress { get; }
        [JsonIgnore]
        string EcardWelcomeEmailSubject { get; }
        [JsonIgnore]
        string WelcomeEmailSubject { get; }
        [JsonIgnore]
        string ValidAddressRegistrationText { get; }
        [JsonIgnore]
        string ValidAddressPlusNameRegistrationText { get; }
        [JsonIgnore]
        string OutOfStateBlockMessage { get; }
        IEnumerable<string> DriversLicenseButtonEnabledIpAddresses { get; }
        bool ResetForm { get; }
        int ResetSeconds { get; }
        bool EnableDriversLicenseSwipe { get; }
        bool HideGender { get; }
        bool HideEreceipt { get; }
        bool NormalizeToUppercase { get; }
        string DriversLicenseFormat { get; }
        bool BypassDupeCheck { get; }
        bool PerformPapiDupeBypass { get; }
        bool EnablePatronBranchSelectOption { get; }
        bool BlockOutOfStateRegistrations { get; }
        int ValidAddressPatronCodeId { get; }
        int ValidAddressPlusNamePatronCodeId { get; }
        bool EnableLegalNameCheckbox { get; }
        bool UseLegalNameOnNotices { get; }
        bool EnableAgeWarning { get; }
        bool EnableAgeBlock { get; }
        bool DisplayECardCheckbox { get; }
        bool DisplayMailingListCheckbox { get; }
        bool DisplayPreferredPickupLocation { get; }
        bool RequirePreferredPickupLocation { get; }
        bool DisplayResponsiblePersonField { get; }
        bool UseFirstNameForDuplicateWorkaround { get; }
        bool UpdatePatronRecordWithMelissaAddress { get; }
        int MailingListRecordSetId { get; }
        int RegistrationLogonUserId { get; }
        int EcardPatronCodeId { get; }
        int TeacherPatronCodeId { get; }
        int StudentPatronCodeId { get; }
        int ValidAddressRecordSetId { get; }
        int ValidAddressPlusNameRecordSetId { get; }
        int InvalidAddressRecordSetId { get; }
        string SchoolInfoFormat { get; }
        string EcardBarcodePrefix { get; }
        int? AddToRecordSetId { get; }
        string PostRegistrationNoteText { get; }
        DateTime? ExpirationDate { get; }
        int? ExpirationDateYears { get; }
        int? PatronCodeId { get; }
        bool HideBranchSelectIfOnlyOneBranch { get; }
        bool DisableBranch { get; }
        string PhoneNumberFormat { get; }
        bool ForceEcardRemotely { get; }

        List<string> GetRequiredFields();

        string GetFieldLabel(string propertyName);
        string GetFieldErrorMessage(string propertyname);
        bool GetFieldRequired(string propertyName);
    }
}
