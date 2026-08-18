using Clc.PatronRegistration.Administration;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace Clc.PatronRegistration.Configuration
{
    public interface ISettingProvider
    {
        int OrganizationId { get; }
        int LibraryId { get; }
        string FormCode { get; }

        [AdminSetting(SettingCategory.PageAppearanceAndInstructions, "Header image", "Displays the uploaded image above the public registration page.", ValueType = SettingValueType.Image)]
        [JsonIgnore]
        int? HeaderImageAssetId { get; }
        [AdminSetting(SettingCategory.PageAppearanceAndInstructions, "Registration agreement content", "Displays this agreement content before the registration form becomes available; blank content skips the agreement step.", ValueType = SettingValueType.LongString)]
        [JsonIgnore]
        string WarningText { get; }
        [AdminSetting(SettingCategory.PageAppearanceAndInstructions, "Custom form footer HTML", "Renders this HTML beneath the public registration form.", ValueType = SettingValueType.Html)]
        [JsonIgnore]
        string CustomFormFooterHtml { get; }
        [AdminSetting(SettingCategory.DuplicateChecking, "Duplicate patron message", "Displays when the preliminary duplicate check finds a patron; [branch_phone] and [branch_id] placeholders are replaced with the selected branch’s values.", ValueType = SettingValueType.Html)]
        [JsonIgnore]
        string DuplicatePatronMessageHtml { get; }
        /// <summary>Compatibility-only. The current public form has no gender field.</summary>
        [JsonIgnore]
        string NaGenderText { get; }
        [AdminSetting(SettingCategory.PageAppearanceAndInstructions, "Default success message", "Displays this default message after successful registration when no on-site, e-card, or address-verification message takes precedence.", ValueType = SettingValueType.LongString)]
        [JsonIgnore]
        string RegistrationText { get; }
        [AdminSetting(SettingCategory.PageAppearanceAndInstructions, "Registration form introduction", "Renders this introductory HTML above the registration form when configured.", Key = "registration_form_header")]
        [JsonIgnore]
        string RegistrationHeader { get; }
        [AdminSetting(SettingCategory.FormBehaviorAndFields, "Age warning message", "Provides the confirmation message shown for a patron under 18 when age warnings are enabled.", ValueType = SettingValueType.LongString)]
        [JsonIgnore]
        string AgeWarningText { get; }
        [AdminSetting(SettingCategory.FormBehaviorAndFields, "Underage registration blocking message", "Displays the message shown when an underage patron is prevented from continuing; the value is inserted as HTML.", ValueType = SettingValueType.Html)]
        [JsonIgnore]
        string AgeBlockText { get; }
        [AdminSetting(SettingCategory.FormBehaviorAndFields, "Driver’s license button text", "Labels the button that starts driver’s-license input on eligible on-site requests.", ValueType = SettingValueType.LongString)]
        [JsonIgnore]
        string DriversLicenseButtonText { get; }
        [AdminSetting(SettingCategory.FormBehaviorAndFields, "Driver’s license prompt text", "Prompts staff to enter or scan driver’s-license data after selecting the driver’s-license button.", ValueType = SettingValueType.LongString)]
        [JsonIgnore]
        string DriversLicensePromptText { get; }
        [AdminSetting(SettingCategory.FormBehaviorAndFields, "Agreement accept button text", "Labels the button that accepts the registration agreement and reveals the form.", ValueType = SettingValueType.LongString)]
        [JsonIgnore]
        string AgreementConfirmButtonText { get; }
        [AdminSetting(SettingCategory.FormBehaviorAndFields, "Agreement decline button text", "Labels the button that declines the registration agreement and leaves the form unavailable.", ValueType = SettingValueType.LongString)]
        [JsonIgnore]
        string AgreementCancelButtonText { get; }
        [AdminSetting(SettingCategory.KioskAndSessionBehavior, "On-site success message", "Replaces the default success message after a successful on-site registration that will automatically reset.", ValueType = SettingValueType.LongString)]
        [JsonIgnore]
        string KioskRegistrationText { get; }
        [AdminSetting(SettingCategory.KioskAndSessionBehavior, "On-site registration introduction", "Retained for compatibility; no current registration-page consumer has been established.")]
        [JsonIgnore]
        string KioskRegistrationHeader { get; }
        [AdminSetting(SettingCategory.FormBehaviorAndFields, "School-information heading", "Displays as the heading above the school, student, teacher, and card-delivery fields.")]
        [JsonIgnore]
        string SchoolInfoFieldLegend { get; }
        [AdminSetting(SettingCategory.PageAppearanceAndInstructions, "CSS file", "Loads this stylesheet on the public registration page.")]
        [JsonIgnore]
        string CssFile { get; }
        [AdminSetting(SettingCategory.EmailAndNotices, "Mailing-list description", "Renders this explanatory HTML beside the mailing-list option when that option is displayed.", ValueType = SettingValueType.Html)]
        [JsonIgnore]
        string MailingListDescriptionHtml { get; }
        [AdminSetting(SettingCategory.ECardRegistration, "E-card success message", "Replaces the default success message after an e-card registration when configured.", ValueType = SettingValueType.LongString)]
        [JsonIgnore]
        string EcardRegistrationText { get; }
        [AdminSetting(SettingCategory.EmailAndNotices, "Text-message information", "Provides the HTML shown when Text Message is selected as the notification method and text-message information is enabled.", ValueType = SettingValueType.Html)]
        [JsonIgnore]
        string SmsNoticeInformationHtml { get; }
        [AdminSetting(SettingCategory.EmailAndNotices, "Show text-message information", "Displays the configured text-message information when Text Message is selected as the notification method.")]
        bool DisplaySmsNoticeInformation { get; }
        [AdminSetting(SettingCategory.EmailAndNotices, "E-card welcome email text version", "Plain-text body for e-card welcome emails; the standard plain-text template is used when this is blank.", ValueType = SettingValueType.EmailTemplate)]
        [JsonIgnore]
        string EcardWelcomeEmailTemplateText { get; }
        [AdminSetting(SettingCategory.EmailAndNotices, "E-card welcome email HTML version", "HTML body for e-card welcome emails; the standard HTML template is used when this is blank.", ValueType = SettingValueType.EmailTemplate)]
        [JsonIgnore]
        string EcardWelcomeEmailTemplateHtml { get; }
        [AdminSetting(SettingCategory.EmailAndNotices, "Welcome email text version", "Plain-text body for the standard welcome email sent after successful registration.", ValueType = SettingValueType.EmailTemplate)]
        [JsonIgnore]
        string WelcomeEmailTemplateText { get; }
        [AdminSetting(SettingCategory.EmailAndNotices, "Welcome email HTML version", "HTML body for the standard welcome email sent after successful registration.", ValueType = SettingValueType.EmailTemplate)]
        [JsonIgnore]
        string WelcomeEmailTemplateHtml { get; }
        [AdminSetting(SettingCategory.EmailAndNotices, "Postmark API key", "Concealed credential used by the Postmark email client to send registration welcome emails.", IsSensitive = true)]
        [JsonIgnore]
        string PostmarkApiKey { get; }
        [AdminSetting(SettingCategory.AddressVerification, "Melissa Data API key", "Concealed credential used by the Melissa client to verify submitted addresses.", IsSensitive = true)]
        [JsonIgnore]
        string MelissaDataApiKey { get; }
        [AdminSetting(SettingCategory.FormBehaviorAndFields, "Responsible-person instructions", "Renders these instructions beside the responsible-person field when that field is displayed.", ValueType = SettingValueType.LongString)]
        [JsonIgnore]
        string ResponsiblePersonDisclaimer { get; }
        [AdminSetting(SettingCategory.EmailAndNotices, "Welcome email sender name", "Sets the display name in the From header of welcome emails.")]
        [JsonIgnore]
        string WelcomeEmailFromName { get; }
        [AdminSetting(SettingCategory.EmailAndNotices, "Welcome email sender address", "Sets the From email address and sender address used for welcome emails.", ValueType = SettingValueType.EmailAddress)]
        [JsonIgnore]
        string WelcomeEmailFromAddress { get; }
        [AdminSetting(SettingCategory.EmailAndNotices, "E-card welcome email subject", "Sets the subject for e-card welcome emails; the standard subject is used when this is blank.")]
        [JsonIgnore]
        string EcardWelcomeEmailSubject { get; }
        [AdminSetting(SettingCategory.EmailAndNotices, "Welcome email subject", "Sets the subject for standard welcome emails.")]
        [JsonIgnore]
        string WelcomeEmailSubject { get; }
        [AdminSetting(SettingCategory.AddressVerification, "Verified-address success message", "Replaces the default success message when Melissa verifies the address and the verified-address patron code remains assigned.", ValueType = SettingValueType.LongString)]
        [JsonIgnore]
        string ValidAddressRegistrationText { get; }
        [AdminSetting(SettingCategory.AddressVerification, "Address-and-name-match success message", "Replaces the default success message when Melissa verifies both the address and name and the matching patron code remains assigned.", ValueType = SettingValueType.LongString)]
        [JsonIgnore]
        string ValidAddressPlusNameRegistrationText { get; }
        [AdminSetting(SettingCategory.AddressVerification, "Out-of-state registration message", "Displays when registration is rejected because the submitted address state is outside Ohio.", ValueType = SettingValueType.LongString)]
        [JsonIgnore]
        string OutOfStateBlockMessage { get; }

        [AdminSetting(SettingCategory.KioskAndSessionBehavior, "On-site IP address prefixes", "Semicolon-separated IP address prefixes treated as on-site requests; these control driver’s-license scanner availability, automatic kiosk resetting, and whether remote registration is forced into e-card mode.", Key = "show_dl_ips", ValueType = SettingValueType.ShortString)]
        IEnumerable<string> DriversLicenseButtonEnabledIpAddresses { get; }
        [AdminSetting(SettingCategory.KioskAndSessionBehavior, "Automatically reset on-site form", "Automatically reloads the registration form after a successful on-site registration.")]
        bool ResetForm { get; }
        [AdminSetting(SettingCategory.KioskAndSessionBehavior, "Automatic reset delay (seconds)", "Sets the delay before the successful on-site registration page automatically reloads.")]
        int ResetSeconds { get; }
        [AdminSetting(SettingCategory.FormBehaviorAndFields, "Enable driver’s license scanner", "Enables the driver’s-license input button for requests whose IP address is recognized as on-site.", Key = "show_dl")]
        bool EnableDriversLicenseSwipe { get; }
        /// <summary>Compatibility-only. The current public form has no gender field to hide.</summary>
        [JsonIgnore]
        bool HideGender { get; }
        [AdminSetting(SettingCategory.FormBehaviorAndFields, "Hide e-receipt option", "Removes the e-receipt preference from the public registration form when enabled.")]
        bool HideEreceipt { get; }
        [AdminSetting(SettingCategory.FormBehaviorAndFields, "Convert registration data to uppercase", "Converts supported name, email, and address values to uppercase before patron creation.")]
        bool NormalizeToUppercase { get; }
        [AdminSetting(SettingCategory.FormBehaviorAndFields, "Driver’s license scanner format", "Selects barcode or magnetic-stripe parsing for scanned driver’s-license data.", Key = "dl_format", ValueType = SettingValueType.Enumeration, AllowedValues = new[] { "barcode", "magstripe" })]
        string DriversLicenseFormat { get; }
        [AdminSetting(SettingCategory.DuplicateChecking, "Skip preliminary duplicate check", "Skips the application’s preliminary duplicate check before patron creation; Polaris may still perform its own duplicate checking.")]
        bool BypassDupeCheck { get; }
        [AdminSetting(SettingCategory.DuplicateChecking, "Attempt PAPI duplicate workaround", "When Polaris rejects registration as a duplicate, allows the application to retry using the configured duplicate-name workaround.", Key = "perform_papi_duplicate_bypass")]
        bool PerformPapiDupeBypass { get; }
        [AdminSetting(SettingCategory.BranchAndPatronDefaults, "Allow patrons to choose a home branch", "Lets patrons choose their home branch instead of assigning the current or default branch.")]
        bool EnablePatronBranchSelectOption { get; }
        [AdminSetting(SettingCategory.AddressVerification, "Block out-of-state registrations", "Blocks registration when the submitted address state is outside Ohio.")]
        bool BlockOutOfStateRegistrations { get; }
        [AdminSetting(SettingCategory.AddressVerification, "Verified-address patron code", "Assigns this Polaris patron code when Melissa verifies the submitted address without a name match.")]
        int ValidAddressPatronCodeId { get; }
        [AdminSetting(SettingCategory.AddressVerification, "Address-and-name-match patron code", "Assigns this Polaris patron code when Melissa verifies the submitted address and finds a name match.")]
        int ValidAddressPlusNamePatronCodeId { get; }
        [AdminSetting(SettingCategory.FormBehaviorAndFields, "Show legal-name option", "Displays the configurable legal-name option and its related legal-name fields on the registration form.")]
        bool EnableLegalNameCheckbox { get; }
        [AdminSetting(SettingCategory.EmailAndNotices, "Use legal name on notices", "Sends the submitted legal name to Polaris for notices when a legal first name is available.")]
        bool UseLegalNameOnNotices { get; }
        [AdminSetting(SettingCategory.FormBehaviorAndFields, "Show age warning", "Shows the configured age-warning confirmation when the entered patron is under 18.")]
        bool EnableAgeWarning { get; }
        [AdminSetting(SettingCategory.FormBehaviorAndFields, "Block registrations for patrons under 18", "Prevents a patron under 18 from continuing after entering a valid birth date.")]
        bool EnableAgeBlock { get; }
        [AdminSetting(SettingCategory.ECardRegistration, "Show e-card option", "Displays the configurable e-card option when remote e-card forcing is not active.", Key = "display_ecard_checkbox")]
        bool DisplayECardCheckbox { get; }
        [AdminSetting(SettingCategory.EmailAndNotices, "Show mailing-list option", "Displays the configurable mailing-list option on the registration form.")]
        bool DisplayMailingListCheckbox { get; }
        [AdminSetting(SettingCategory.BranchAndPatronDefaults, "Show preferred pickup location", "Displays the preferred-pickup-location selector on the registration form.")]
        bool DisplayPreferredPickupLocation { get; }
        [AdminSetting(SettingCategory.FormBehaviorAndFields, "Show responsible-person field", "Displays the responsible-person section on the registration form when enabled.")]
        bool DisplayResponsiblePersonField { get; }
        [AdminSetting(SettingCategory.DuplicateChecking, "Apply duplicate workaround to first name", "Adds the duplicate-workaround suffix to the first name when enabled; otherwise it is added to the last name.")]
        bool UseFirstNameForDuplicateWorkaround { get; }
        [AdminSetting(SettingCategory.AddressVerification, "Save standardized Melissa address", "Replaces submitted address fields with the standardized Melissa response before patron creation for verified addresses.")]
        bool UpdatePatronRecordWithMelissaAddress { get; }
        [AdminSetting(SettingCategory.EmailAndNotices, "Mailing-list record set", "Polaris record set to which patrons are added when they select the mailing-list option.")]
        int MailingListRecordSetId { get; }
        [AdminSetting(SettingCategory.PolarisIntegrationAndRecordSets, "Registration user for unverified addresses", "Polaris user ID used to create registrations whose address was not verified through the address-verification workflow.")]
        int RegistrationLogonUserId { get; }
        [AdminSetting(SettingCategory.ECardRegistration, "E-card patron code", "Assigns this Polaris patron code when e-card registration is selected.")]
        int EcardPatronCodeId { get; }
        [AdminSetting(SettingCategory.BranchAndPatronDefaults, "Teacher patron code", "Assigns this Polaris patron code when the school workflow identifies the registrant as a teacher.")]
        int TeacherPatronCodeId { get; }
        [AdminSetting(SettingCategory.BranchAndPatronDefaults, "Student patron code", "Assigns this Polaris patron code when the school workflow identifies the registrant as a student.")]
        int StudentPatronCodeId { get; }
        [AdminSetting(SettingCategory.AddressVerification, "Verified-address record set", "Adds a successfully created patron to this Polaris record set when the address is verified without a name match.")]
        int ValidAddressRecordSetId { get; }
        [AdminSetting(SettingCategory.AddressVerification, "Address-and-name-match record set", "Adds a successfully created patron to this Polaris record set when the address and name are both verified.")]
        int ValidAddressPlusNameRecordSetId { get; }
        [AdminSetting(SettingCategory.AddressVerification, "Invalid-address record set", "Adds a successfully created patron to this Polaris record set when address verification returns an invalid result.")]
        int InvalidAddressRecordSetId { get; }
        [AdminSetting(SettingCategory.FormBehaviorAndFields, "School-registration mode", "Enables the school, student, and teacher workflow and selects its configured operating mode; blank disables that workflow.")]
        string SchoolInfoFormat { get; }
        [AdminSetting(SettingCategory.ECardRegistration, "E-card barcode prefix", "Prefixes the timestamp-based temporary barcode generated for an e-card registration.")]
        string EcardBarcodePrefix { get; }
        [AdminSetting(SettingCategory.PolarisIntegrationAndRecordSets, "Additional post-registration record set", "Additional Polaris record set to which every successfully created patron is added when configured.")]
        int? AddToRecordSetId { get; }
        [AdminSetting(SettingCategory.PolarisIntegrationAndRecordSets, "Patron note added after registration", "Text added to the created patron’s Polaris note after successful registration.", ValueType = SettingValueType.LongString)]
        string PostRegistrationNoteText { get; }
        [AdminSetting(SettingCategory.BranchAndPatronDefaults, "Fixed expiration date", "Supplies one fixed patron expiration date; a configured years-based expiration takes precedence.")]
        DateTime? ExpirationDate { get; }
        [AdminSetting(SettingCategory.BranchAndPatronDefaults, "Expiration period (years)", "Calculates patron expiration relative to registration and takes precedence over the fixed expiration date.")]
        int? ExpirationDateYears { get; }
        [AdminSetting(SettingCategory.BranchAndPatronDefaults, "Default patron code", "Assigns this Polaris patron code by default before more specific e-card or address-verification codes are applied.")]
        int? PatronCodeId { get; }
        [AdminSetting(SettingCategory.BranchAndPatronDefaults, "Hide home branch when only one option exists", "Replaces the home-branch selector with its single available branch value.", Key = "hide_branch_select_if_only_one_option")]
        bool HideBranchSelectIfOnlyOneBranch { get; }
        [AdminSetting(SettingCategory.BranchAndPatronDefaults, "Disable registration for this branch and form", "Prevents registration submission for this branch and form before registration validation or side effects run.")]
        bool DisableBranch { get; }
        [AdminSetting(SettingCategory.FormBehaviorAndFields, "Phone-number storage format", "Applies this replacement format to the primary phone number before patron creation.")]
        string PhoneNumberFormat { get; }
        [AdminSetting(SettingCategory.ECardRegistration, "Require e-card for remote registration", "Automatically selects e-card mode for requests whose IP address is not recognized as on-site.")]
        bool ForceEcardRemotely { get; }

        List<string> GetRequiredFields();

        string GetFieldLabel(string propertyName);
        string GetFieldErrorMessage(string propertyname);
        bool GetFieldRequired(string propertyName);
    }
}
