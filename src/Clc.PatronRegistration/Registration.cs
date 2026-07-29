using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc;
using Clc.PatronRegistration.Configuration;
using Clc.Melissa.Models;
using Clc.Rest;
using Newtonsoft.Json;
using Clc.PatronRegistration.Helpers;
using System.Globalization;
using Clc.Melissa;
using Clc.Polaris.Api;
using Clc.Polaris.Api.Models;
using Clc.PatronRegistration.Data;
using Clc.PatronRegistration.Web.Models;
using NLog;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Clc.PatronRegistration.Validators;
using Clc.Rest.Models;
using System.Text.RegularExpressions;
using System.Windows.Markup;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Clc.PatronRegistration.Administration;

namespace Clc.PatronRegistration
{
    [ModelMetadataType(typeof(RegistrationMetadata))]
    public partial class Registration
    {
        [JsonIgnore]
        private static readonly ILogger logger = LogManager.GetCurrentClassLogger();

        public int PatronBranchID { get; set; }
        public string NameFirst { get; set; } = string.Empty;
        public string? NameMiddle { get; set; } = string.Empty;
        public string NameLast { get; set; } = string.Empty;
        public bool UseLegalName { get; set; }
        public string? LegalNameFirst { get; set; } = string.Empty;
        public string? LegalNameMiddle { get; set; } = string.Empty;
        public string? LegalNameLast { get; set; } = string.Empty;
        public DateTime? Birthdate { get; set; }
        public int DeliveryOptionId { get; set; }
        public string? PhoneVoice1 { get; set; } = string.Empty;
        public string PhoneVoice2 { get; set; } = string.Empty;
        public int? TxtPhoneNumber { get; set; }
        public bool ReceiveEreceipts { get; set; }
        public string? EmailAddress { get; set; } = string.Empty;
        public string? AltEmailAddress { get; set; } = string.Empty;
        public string StreetOne { get; set; } = string.Empty;
        public string? StreetTwo { get; set; } = string.Empty;
        public string City { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public string User1 { get; set; } = string.Empty;
        public string User2 { get; set; } = string.Empty;
        public string User4 { get; set; } = string.Empty;
        public string User5 { get; set; } = string.Empty;
        public string PostalCode { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string Password2 { get; set; } = string.Empty;
        public int? RequestPickupBranchID { get; set; }
        public bool DeliverCardToSchool { get; set; }
        public bool IsStudent { get; set; }
        public bool IsTeacher { get; set; }
        public bool IsECard { get; set; }
        public bool EnableSMS { get; set; }
        public int LogonUserID { get; set; }
        public int LibraryId { get; set; }
        public int Phone1CarrierID { get; set; }
        public int Phone2CarrierID { get; set; }
        public int EReceiptOptionID { get; set; }
        public bool UseLegalNameOnNotices { get; set; }
        public string Barcode { get; set; } = string.Empty;
        public int? PatronCode { get; set; }
        public AddressVerificationStatus AddressVerificationStatus { get; set; }
        public string a_password { get; set; } = string.Empty;
        public bool ZipMismatchRetry { get; set; }
        public SelectList Branches { get; set; } = new SelectList(Array.Empty<string>());
        public SelectList PickupBranches { get; set; } = new SelectList(Array.Empty<string>());
        public List<SelectListItem> Genders { get; set; } = [];
        public List<SelectListItem> Months { get; set; } = [];
        [JsonIgnore]
        public IRestResponse<PersonatorResponse>? MelissaResponse { get; set; }
        public bool AddToMailingList { get; set; }
        public bool ShowDlButton { get; set; }
        [JsonIgnore]
        public ISettingProvider Settings { get; protected set; }

        public void UseSettings(ISettingProvider settings)
        {
            Settings = settings;
        }

        public bool BypassAgreement { get; set; } = false;
        public bool ShouldDisplayAgreement => !string.IsNullOrWhiteSpace(Settings?.WarningText) && !BypassAgreement;

        public List<KeyValuePair<string, string>> ModelErrors { get; set; } = [];

        public PatronRegistrationParams ConvertToPatronRegistrationParams() => JsonConvert.DeserializeObject<PatronRegistrationParams>(JsonConvert.SerializeObject(this))!;


        public Registration() : this(new HttpContextAccessor().HttpContext!.RequestServices.GetRequiredService<ISettingProvider>())
        {

        }
        public Registration(ISettingProvider settings)
        {
            Settings = settings;
        }


        [GeneratedRegex("\\(?(\\d{3}).*(\\d{3}).*(\\d{4})")]
        public static partial Regex PhoneNumber();

        public void SetPatronCode()
        {
            var configuredId = Settings.PatronCodeId;
            PatronCode = configuredId is > 0 ? configuredId : null;
            if (configuredId is < 0)
            {
                logger.Error("Skipping invalid negative patron_code_id configuration.");
            }
        }

        public void HandleSmsSettings()
        {
            if (DeliveryOptionId == 8)
            {
                TxtPhoneNumber = 1;
                Phone1CarrierID = 23;
            }
        }

        public DupeCheckResult DupeCheck(IDbHelper db, IPapiClient papi)
        {
            if (Settings.BypassDupeCheck) { return DupeCheckResult.False(); }
            if (IsTeacher) { return DupeCheckResult.False(); }

            var lastname = NameLast;

            if (Settings.FormCode.Equals("kids", StringComparison.OrdinalIgnoreCase) && Settings.LibraryId == 28)
            {
                lastname = "C-" + lastname;
            }           

            var isDuplicate = db.CheckPatronIsDuplicate(Settings.LibraryId, NameFirst, lastname, Birthdate.GetValueOrDefault());
            if (isDuplicate) { return DupeCheckResult.True(DuplicateMessage(papi)); }

            return DupeCheckResult.False();
        }

        void FormatRegistration()
        {
            NameFirst = NameFirst.Trim();
            NameLast = NameLast.Trim();
            if (!string.IsNullOrWhiteSpace(PhoneVoice1))
            {
                PhoneVoice1 = PhoneNumber().Replace(PhoneVoice1, Settings.PhoneNumberFormat);
            }
        }

        public RegistrationAttempt CreateRegistration(string ip, ModelStateDictionary modelState, ISettingProvider settings, IDbHelper db, IPapiClient papi, IMelissaRestClient melissa, IEmailSender emailSender)
        {
            Settings = settings;
            if (!modelState.IsValid)
            {
                ModelErrors = RegistrationAttempt.ErrorsFromModelState(modelState);
                return new RegistrationAttempt
                {
                    Status = RegistrationStatus.Error,
                    Message = "Please correct the validation errors and try again.",
                    Errors = ModelErrors
                };
            }
            HandleSmsSettings(); // might need to go back above ValidateRegistration

            ApplyForceEcardSetting(ip);

            if (!ValidateRegistration(db, papi))
            {
                AddHistoryEntry(ip);
                return new RegistrationAttempt { Status = RegistrationStatus.Error, Errors = ModelErrors };
            }

            FormatRegistration();
            VerifyAndFixAddress(melissa);
            SetPatronCode();
            HandleLibrarySettings();
            HandleMailingList();
            HandleEReceipts();
            NormalizeRegistration();
            SetLegalNameOnNotices();
            HandleECardSettings();
            HandleSchoolInfo();
            SetLogonUserID();

            if (!ValidateRegistration(db, papi))
            {
                AddHistoryEntry(ip);
                return new RegistrationAttempt { Status = RegistrationStatus.Error, Errors = ModelErrors };
            }

            logger.Trace("Submitting validated patron registration for branch {0}.", PatronBranchID);
            var registrationParams = ConvertToPatronRegistrationParams();
            HandleExpirationDate(registrationParams);

            var papiResponse = papi.PatronRegistrationCreate(registrationParams);
            logger.Trace(papiResponse.Data.ToJson());

            if (!BypassPapiDupeCheck(registrationParams, papiResponse, papi, out papiResponse))
            {
                AddHistoryEntry(ip, papiResponse, "duplicate that cannot be bypassed");
                return new RegistrationAttempt { Status = RegistrationStatus.Duplicate, Message = DuplicateMessage(papi) };
            }

            if ((papiResponse?.Data?.PatronID).GetValueOrDefault(0) <= 0)
            {
                AddHistoryEntry(ip, papiResponse, $"{papiResponse.Data?.PAPIErrorCode} - {papiResponse.Data?.ErrorMessage}");
                return HandlePapiRegistrationCreateError(papiResponse);
            }

            AddHistoryEntry(ip, papiResponse);
            return FinalizeRegistration(ip, papiResponse, db, papi, emailSender);
        }



        public bool ValidateRegistration(IDbHelper db, IPapiClient papi)
        {
            if (!Birthdate.HasValue)
            {
                ModelErrors.Add(new("", "Please enter a valid birth date."));
            }

            var dupeCheckResult = DupeCheck(db, papi);
            if (dupeCheckResult.IsDupe)
            {
                ModelErrors.Add(new("", dupeCheckResult.Message));
            }

            if (DeliveryOptionId == 2 && string.IsNullOrWhiteSpace(EmailAddress))
            {
                ModelErrors.Add(new("", "Please enter an email address or choose a different notification type."));
            }

            if (DeliveryOptionId == 3 && string.IsNullOrWhiteSpace(PhoneVoice1))
            {
                ModelErrors.Add(new("", "Please enter a phone number or choose a different notification type."));
            }

            if (DeliveryOptionId == 8 && !TxtPhoneNumber.HasValue)
            {
                ModelErrors.Add(new("", "Please select a TXT phone number or choose a different notification type."));
            }

            if (DeliveryOptionId == 8 && ((TxtPhoneNumber.GetValueOrDefault() == 1 && string.IsNullOrWhiteSpace(PhoneVoice1)) || (TxtPhoneNumber.GetValueOrDefault() == 2 && string.IsNullOrWhiteSpace(PhoneVoice2))))
            {
                ModelErrors.Add(new("", "Please enter a TXT phone number or choose a different notification type."));
            }

            if (ReceiveEreceipts == true && string.IsNullOrEmpty(EmailAddress))
            {
                ModelErrors.Add(new("", "If you'd like to receive eReceipts please enter a valid email address."));
            }

            if ((IsTeacher || IsStudent) && string.IsNullOrWhiteSpace(User1))
            {
                ModelErrors.Add(new("", "Please select a school"));
            }

            if (string.IsNullOrWhiteSpace(EmailAddress)) { EmailAddress = null; }
            if (string.IsNullOrWhiteSpace(AltEmailAddress)) { AltEmailAddress = null; }

            if (State.Trim().ToLower().Equals("ohio", StringComparison.OrdinalIgnoreCase))
            {
                State = "OH";
            }

            if (!string.Equals(State, "oh", StringComparison.InvariantCultureIgnoreCase) && Settings.BlockOutOfStateRegistrations)
            {
                ModelErrors.Add(new("OutOfState", Settings.OutOfStateBlockMessage));
            }

            return ModelErrors.Count == 0;
        }

        public void HandleLibrarySettings()
        {
            if (LibraryId == 6)
            {
                User4 = "GHPA";
            }
        }

        public void HandleMailingList()
        {
            if (AddToMailingList)
            {
                User2 = "Newsletter";
            }
        }

        public void HandleEReceipts()
        {
            if (ReceiveEreceipts == true && !string.IsNullOrWhiteSpace(EmailAddress))
            {
                EReceiptOptionID = 2;
            }
        }

        public void NormalizeRegistration()
        {
            if (Settings.NormalizeToUppercase)
            {
                NormalizeToUppercase();
            }
        }

        public void SetLegalNameOnNotices()
        {
            UseLegalNameOnNotices = Settings.UseLegalNameOnNotices && !string.IsNullOrWhiteSpace(LegalNameFirst);
        }

        public void HandleECardSettings()
        {
            if (Settings.DisplayECardCheckbox && IsECard)
            {
                Barcode = $"{Settings.EcardBarcodePrefix}{DateTimeOffset.Now.ToUnixTimeSeconds()}";
                PatronCode = PositivePatronCodeOrCurrent(Settings.EcardPatronCodeId, "ecard_patron_code_id");
            }
        }

        public void HandleSchoolInfo()
        {
            if (Settings.SchoolInfoFormat == "uapl" && IsECard)
            {
                User1 = "";
            }

            if (!string.IsNullOrWhiteSpace(Settings.SchoolInfoFormat))
            {
                if (IsTeacher)
                {
                    PatronCode = PositivePatronCodeOrCurrent(Settings.TeacherPatronCodeId, "teacher_patron_code_id");
                }
                if (IsStudent)
                {
                    PatronCode = PositivePatronCodeOrCurrent(Settings.StudentPatronCodeId, "student_patron_code_id");
                }
            }
        }
        public bool ShouldSkipRegistration() => !string.IsNullOrWhiteSpace(a_password) || Settings.DisableBranch;

        public void SetLogonUserID()
        {
            if (AddressVerificationStatus == AddressVerificationStatus.Valid || AddressVerificationStatus == AddressVerificationStatus.ValidPlusNameMatch)
            {
                LogonUserID = 1;
            }
            else
            {
                var configuredId = Settings.RegistrationLogonUserId;
                LogonUserID = configuredId > 0 ? configuredId : 0;
                if (configuredId < 0)
                {
                    logger.Error("Skipping invalid negative registration_logon_user_id configuration.");
                }
            }
        }

        private int? PositivePatronCodeOrCurrent(int configuredId, string settingKey)
        {
            if (configuredId > 0)
            {
                return configuredId;
            }
            if (configuredId < 0)
            {
                logger.Error($"Skipping invalid negative {settingKey} configuration.");
            }
            return PatronCode;
        }

        public void HandleExpirationDate(PatronRegistrationParams registrationParams)
        {
            if (Settings.ExpirationDate.HasValue)
            {
                registrationParams.ExpirationDate = Settings.ExpirationDate.Value;
            }

            if (Settings.ExpirationDateYears.HasValue)
            {
                registrationParams.ExpirationDate = DateTime.Now.AddYears(Settings.ExpirationDateYears.Value);
            }
        }
        public void NormalizeToUppercase()
        {
            NameFirst = NameFirst.ToUpper();
            NameLast = NameLast.ToUpper();
            NameMiddle = NameMiddle?.ToUpper();
            LegalNameFirst = LegalNameFirst?.ToUpper();
            LegalNameMiddle = LegalNameMiddle?.ToUpper();
            LegalNameLast = LegalNameLast?.ToUpper();
            EmailAddress = EmailAddress?.ToUpper();
            AltEmailAddress = AltEmailAddress?.ToUpper();
            StreetOne = StreetOne.ToUpper();
            StreetTwo = StreetTwo?.ToUpper();
            City = City.ToUpper();
            State = State.ToUpper();
        }

        public Registration VerifyAndFixAddress(IMelissaRestClient melissa)
        {
            var status = AddressVerificationStatus.None;
            var response = melissa.PersonatorRequest(new PersonatorRequestRecord
            {
                FirstName = NameFirst,
                LastName = NameLast,
                AddressLine1 = StreetOne,
                AddressLine2 = StreetTwo,
                City = City,
                State = State,
                PostalCode = PostalCode
            });

            MelissaResponse = response;

            var record = response?.Data?.Records?.FirstOrDefault();

            var results = record?.ParsedResults;
            if (results == null) { AddressVerificationStatus = AddressVerificationStatus.Invalid; return this; }
            if (results.Contains(MelissaStatus.VR01))
            {
                status = AddressVerificationStatus.ValidPlusNameMatch;
            }
            else if (results.Contains(MelissaStatus.AS01))
            {
                status = AddressVerificationStatus.Valid;
            }
            else
            {
                status = AddressVerificationStatus.Invalid;
            }

            AddressVerificationStatus = status;

            if (new[] { AddressVerificationStatus.Valid, AddressVerificationStatus.ValidPlusNameMatch }.Contains(AddressVerificationStatus) && Settings.UpdatePatronRecordWithMelissaAddress)
            {
                TextInfo textInfo = new CultureInfo("en-US", false).TextInfo;

                if (record != null)
                {
                    StreetOne = textInfo.ToTitleCase(record.AddressLine1);
                    StreetTwo = textInfo.ToTitleCase(record.AddressLine2);
                    City = textInfo.ToTitleCase(record.City);
                    State = record.State.Length == 2 ? record.State.ToUpper() : textInfo.ToTitleCase(record.State);
                    PostalCode = record.PostalCode.Split('-')[0];
                }
            }

            if (PatronCode == null)
            {
                switch (status)
                {
                    case AddressVerificationStatus.Valid:
                        HandleValidAddressPreReg();
                        break;
                    case AddressVerificationStatus.ValidPlusNameMatch:
                        HandleValidAddressPlusNamePreReg();
                        break;
                    case AddressVerificationStatus.Invalid:
                        HandleInvalidAddressPreReg();
                        break;
                    default:
                        HandleNoAddressVerificationInfoPreReg();
                        break;
                }
            }

            return this;
        }

        public Registration HandleValidAddressPreReg()
        {
            if (Settings.ValidAddressPatronCodeId > 0) { PatronCode = Settings.ValidAddressPatronCodeId; }
            return this;
        }

        public Registration HandleValidAddressPlusNamePreReg()
        {
            if (Settings.ValidAddressPlusNamePatronCodeId > 0) { PatronCode = Settings.ValidAddressPlusNamePatronCodeId; }
            return this;
        }

        public Registration HandleInvalidAddressPreReg()
        {
            return this;
        }

        public Registration HandleNoAddressVerificationInfoPreReg()
        {
            return this;
        }

        public bool BypassPapiDupeCheck(PatronRegistrationParams registrationParams, IRestResponse<PatronRegistrationCreateResult> papiResponse, IPapiClient papi, out IRestResponse<PatronRegistrationCreateResult> papiResponseOut)
        {
            papiResponseOut = papiResponse;

            if (papiResponse.Data?.PAPIErrorCode == -3528)
            {
                if (Settings.PerformPapiDupeBypass)
                {
                    var parentOrgId = CacheHelper.OrganizationCache.Single(o => o.OrganizationID == PatronBranchID).ParentOrganizationID;
                    var parentOrg = CacheHelper.OrganizationCache.Single(o => o.OrganizationID == parentOrgId.GetValueOrDefault(1));

                    if (Settings.UseFirstNameForDuplicateWorkaround)
                    {
                        registrationParams.NameFirst += "-" + parentOrg.Abbreviation;
                    }
                    else
                    {
                        registrationParams.NameLast += "-" + parentOrg.Abbreviation;
                    }

                    var _papiResponse = papi.PatronRegistrationCreate(registrationParams);
                    papiResponseOut = _papiResponse;

                    if (_papiResponse.Data?.PAPIErrorCode == -3528)
                    {
                        logger.Info($"Patron {NameFirst} {NameLast} is a duplicate that cannot be bypassed");

                        return false;
                    }
                }
            }
            return true;
        }

        public RegistrationAttempt HandlePapiRegistrationCreateError(IRestResponse<PatronRegistrationCreateResult> papiResponse)
        {
            if (new[] { -3510, -3511, -3512, -3513, -3514, -3515, -3516, -3517 }.Contains(papiResponse.Data?.PAPIErrorCode ?? 0))
            {
                if (ZipMismatchRetry)
                {
                    return new RegistrationAttempt { Status = RegistrationStatus.ZipMismatch, Message = "ZIP mismatch. Please double-check your address information and try again." };
                }
                ZipMismatchRetry = true;
                return new RegistrationAttempt { Status = RegistrationStatus.ZipMismatchRetry };
            }
            logger.Error($"Error message: {papiResponse?.Data?.ErrorMessage}\r\nRegistration Data: {JsonConvert.SerializeObject(papiResponse)}");

            return new RegistrationAttempt { Status = RegistrationStatus.Error, Message = $"An error occurred during your registration. If this problem persists, please contact the library.\r\n\r\nError Code: {papiResponse?.Data?.PAPIErrorCode}\r\nError Message:{papiResponse?.Data?.ErrorMessage}" };
        }

        public RegistrationAttempt FinalizeRegistration(string ip, IRestResponse<PatronRegistrationCreateResult> papiResponse, IDbHelper db, IPapiClient papi, IEmailSender emailSender)
        {
            Barcode = papiResponse.Data.Barcode;

            HandleAddToMailingList(papi, papiResponse.Data.PatronID);
            AddToRecordSet(papi, papiResponse.Data.PatronID);
            AddPostRegistrationNote(papi);
            SendWelcomeEmail(emailSender);
            HandleSchoolDelivery(papi);
            HandleAddressValidationPostReg(papi, papiResponse.Data.PatronID);

            return new RegistrationAttempt { Status = RegistrationStatus.Success, Message = GetRegistrationSuccessText(ip) };
        }

        public void AddHistoryEntry(string ip, IRestResponse<PatronRegistrationCreateResult> papiResponse = null!, string status = "")
        {
            if (string.IsNullOrWhiteSpace(status))
            {
                if (ModelErrors != null && ModelErrors.Any()) { status = string.Join(",", ModelErrors.Select(e => e.Value)); }
                else if (papiResponse != null)
                {
                    status = $"{papiResponse.Data.PAPIErrorCode} - {papiResponse.Data.ErrorMessage}";
                }
            }

            var settingsSnapshot = "";
            try { settingsSnapshot = SettingsSnapshotSerializer.Serialize(Settings); }
            catch (JsonSerializationException) { settingsSnapshot = ""; }

            var papiResponseJson = "";
            try { papiResponseJson = papiResponse?.ToJson() ?? ""; }
            catch (JsonSerializationException) { papiResponseJson = ""; }


            var entry = new RegistrationHistoryEntry(ip, status, this) { Result = status, SettingsSnapshot = settingsSnapshot, PapiResponse = papiResponseJson };
            DbHelper.Global.AddRegistrationHistoryEntry(entry);
        }
        string GetRegistrationSuccessText(string ip)
        {
            var registrationText = Settings.RegistrationText;

            if (ShouldAutoReset(ip) && !string.IsNullOrWhiteSpace(Settings.KioskRegistrationText))
            {
                registrationText = Settings.KioskRegistrationText;
            }

            if ((IsECard) && !string.IsNullOrWhiteSpace(Settings.EcardRegistrationText)) { registrationText = Settings.EcardRegistrationText; }
            else
            {
                // there might end up being a better way to handle this once libraries start testing
                if (AddressVerificationStatus == AddressVerificationStatus.Valid && PatronCode.GetValueOrDefault(-1) == Settings.ValidAddressPatronCodeId && !string.IsNullOrWhiteSpace(Settings.ValidAddressRegistrationText)) { registrationText = Settings.ValidAddressRegistrationText; }
                if (AddressVerificationStatus == AddressVerificationStatus.ValidPlusNameMatch && PatronCode.GetValueOrDefault(-1) == Settings.ValidAddressPlusNamePatronCodeId && !string.IsNullOrWhiteSpace(Settings.ValidAddressPlusNameRegistrationText)) { registrationText = Settings.ValidAddressPlusNameRegistrationText; }
            }

            registrationText = this.FormatTemplate(registrationText);

            return registrationText;
        }
        public bool ShouldAutoReset(string ip) => Settings.ResetForm && CheckIp(ip, Settings.DriversLicenseButtonEnabledIpAddresses);

        public static bool CheckIp(string ipToCheck, IEnumerable<string> whitelist)
        {
            whitelist = whitelist.Concat(["127", "::1"]);
            return whitelist.Any(i => ipToCheck.StartsWith(i));
        }

        public void HandleAddToMailingList(IPapiClient papi, int patronId)
        {
            var recordSetId = Settings.MailingListRecordSetId;
            if (AddToMailingList && patronId > 0 && recordSetId > 0)
            {
                papi.RecordSetContentAdd(recordSetId, patronId);
            }
            else if (AddToMailingList && (patronId <= 0 || recordSetId < 0))
            {
                logger.Error("Skipping mailing-list record-set update because a required identifier is invalid.");
            }
        }

        public void AddToRecordSet(IPapiClient papi, int patronId)
        {
            var recordSetId = Settings.AddToRecordSetId;
            if (patronId > 0 && recordSetId is > 0)
            {
                papi.RecordSetContentAdd(recordSetId.Value, patronId);
            }
            else if (patronId <= 0 || recordSetId is < 0)
            {
                logger.Error("Skipping configured record-set update because a required identifier is invalid.");
            }
        }

        public void AddPostRegistrationNote(IPapiClient papi)
        {
            if (!string.IsNullOrWhiteSpace(Settings.PostRegistrationNoteText))
            {
                papi.UpdatePatronNotesData(Barcode, Settings.PostRegistrationNoteText);
            }
        }

        public void HandleSchoolDelivery(IPapiClient papi)
        {
            if (DeliverCardToSchool && !string.IsNullOrWhiteSpace(Settings.SchoolInfoFormat) && (IsTeacher || IsStudent) && !string.IsNullOrWhiteSpace(User1))
            {
                papi.UpdatePatronNotesData(Barcode, "School Delivery Requested");
            }
        }

        public void SendWelcomeEmail(IEmailSender emailSender)
        {
            var subject = IsECard && !string.IsNullOrWhiteSpace(Settings.EcardWelcomeEmailSubject) ? Settings.EcardWelcomeEmailSubject : Settings.WelcomeEmailSubject;
            var textTemplate = IsECard && !string.IsNullOrWhiteSpace(Settings.EcardWelcomeEmailTemplateText) ? Settings.EcardWelcomeEmailTemplateText : Settings.WelcomeEmailTemplateText;
            var htmlTemplate = IsECard && !string.IsNullOrWhiteSpace(Settings.EcardWelcomeEmailTemplateHtml) ? Settings.EcardWelcomeEmailTemplateHtml : Settings.WelcomeEmailTemplateHtml;

            if (string.IsNullOrWhiteSpace(EmailAddress) || (string.IsNullOrWhiteSpace(textTemplate) && string.IsNullOrWhiteSpace(htmlTemplate))) return;

            var textBody = this.FormatTemplate(textTemplate);
            var htmlBody = this.FormatTemplate(htmlTemplate);
            _ = Task.Run(() => { emailSender.Send(EmailAddress, $@"""{Settings.WelcomeEmailFromName}"" {Settings.WelcomeEmailFromAddress}", Settings.WelcomeEmailFromAddress, subject, htmlBody, textBody); });
        }

        public void HandleAddressValidationPostReg(IPapiClient papi, int patronId)
        {
            switch (AddressVerificationStatus)
            {
                case AddressVerificationStatus.Valid:
                    AddPatronToRecordSet(patronId, Settings.ValidAddressRecordSetId, papi);
                    break;
                case AddressVerificationStatus.ValidPlusNameMatch:
                    AddPatronToRecordSet(patronId, Settings.ValidAddressPlusNameRecordSetId, papi);
                    break;
                case AddressVerificationStatus.Invalid:
                    AddPatronToRecordSet(patronId, Settings.InvalidAddressRecordSetId, papi);
                    break;
                case AddressVerificationStatus.None:
                    break;
            }
        }
        public void AddPatronToRecordSet(int patronId, int recordSetId, IPapiClient papi)
        {
            if (patronId <= 0 || recordSetId <= 0)
            {
                if (patronId < 0 || recordSetId < 0)
                {
                    logger.Error("Skipping address-validation record-set update because a required identifier is invalid.");
                }
                return;
            }

            var response = papi.RecordSetContentAdd(recordSetId, patronId);
            if (response.Data.PAPIErrorCode < 0)
            {
                logger.Error($"Error adding patron {patronId} to record set {recordSetId}: {response.Data.PAPIErrorCode} - {response.Data.ErrorMessage}");
            }
        }

        public string DuplicateMessage(IPapiClient papi)
        {
            if (PatronBranchID == 0) { PatronBranchID = CacheHelper.GetBranches(Settings.OrganizationId).First().OrganizationID; }
            var branchPhone = papi.SA_GetValueByOrg("orgphone1", PatronBranchID).Data?.Value;

            var message = Settings.DuplicatePatronMessageHtml;
            message = message.Replace("[branch_phone]", string.IsNullOrWhiteSpace(branchPhone) ? "" : $"at {branchPhone}")
                .Replace("[branch_id]", PatronBranchID.ToString());

            return message;
        }

        public static Registration BuildBaseRegistration(int orgId, bool forceDl, string ip, ISettingProvider settings, IDbHelper db)
        {
            var p = new Registration(settings)
            {
                State = "OH",
                Genders = db.GetGendersToOrganizations(orgId).Select(g => new SelectListItem { Value = g.GenderID.ToString(), Text = g.Description }).ToList(),
                ShowDlButton = forceDl || settings.EnableDriversLicenseSwipe && CheckIp(ip, settings.DriversLicenseButtonEnabledIpAddresses),
                IsECard = settings.ForceEcardRemotely && !CheckIp(ip, settings.DriversLicenseButtonEnabledIpAddresses)
            };

            if (settings.DisplayMailingListCheckbox) { p.AddToMailingList = true; }

            var org = CacheHelper.OrganizationCache.Single(o => o.OrganizationID == orgId);

            if (settings.EnablePatronBranchSelectOption)
            {
                p.PatronBranchID = 0;
            }
            else
            {
                p.PatronBranchID = org.OrganizationCodeID == 3 ? org.OrganizationID : (db.GetSelfRegistrationBranches(org.OrganizationID).MinBy(b => b.OrganizationID)?.OrganizationID).GetValueOrDefault();
                p.RequestPickupBranchID = p.PatronBranchID;
            }

            p.LibraryId = org.OrganizationCodeID == 3 ? org.ParentOrganizationID.GetValueOrDefault(p.PatronBranchID) : org.OrganizationID;

            p.Branches = new SelectList(db.GetSelfRegistrationBranches(p.LibraryId), "OrganizationID", "DisplayName");
            p.PickupBranches = new SelectList(db.GetPickupBranches(p.LibraryId), "OrganizationID", "DisplayName");

            return p;
        }

        public void ApplyForceEcardSetting(string ip)
        {
            if (!Settings.ForceEcardRemotely)
            {
                return;
            }

            IsECard = !CheckIp(ip, Settings.DriversLicenseButtonEnabledIpAddresses);
        }
    }
}
