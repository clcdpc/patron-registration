using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.AspNetCore.Mvc.Rendering;
using Clc.PatronRegistration.Validators;
using Clc.PatronRegistration.Configuration;
using NLog;
using Clc.Rest;
using Clc.Melissa.Models;

namespace Clc.PatronRegistration
{
    public class RegistrationMetadata
    {
        [DbConfiguredDisplayName]
        public int PatronBranchID { get; set; }

        [DbConfiguredDisplayName]
        [Required]
        public string NameFirst { get; set; } = string.Empty;

        [DbConfiguredDisplayName]
        public string? NameMiddle { get; set; } = string.Empty;

        [DbConfiguredDisplayName]
        [Required]
        public string NameLast { get; set; } = string.Empty;

        [DbConfiguredDisplayName]
        public bool UseLegalName { get; set; }

        [DbConfiguredDisplayName]
        [LegalNameValidator]
        public string? LegalNameFirst { get; set; } = string.Empty;

        [DbConfiguredDisplayName]
        public string? LegalNameMiddle { get; set; } = string.Empty;

        [DbConfiguredDisplayName]
        [LegalNameValidator]
        public string? LegalNameLast { get; set; } = string.Empty;

        [Required]
        [BirthdateNotInFuture]
        [DbConfiguredDisplayName]
        public DateTime? Birthdate { get; set; }

        [DbConfiguredDisplayName]
        [VerifyDeliveryOption]
        public int DeliveryOptionId { get; set; }

        [VerifyDeliveryOption]
        [DbConfiguredRequired]
        [DataType(DataType.PhoneNumber)]
        [DbConfiguredDisplayName]
        [RegularExpression(@"^\(?([0-9]{3})\)?[-. ]?([0-9]{3})[-. ]?([0-9]{4})$", ErrorMessage = "Invalid phone number")]
        public string? PhoneVoice1 { get; set; } = string.Empty;

        [DbConfiguredDisplayName]
        public string PhoneVoice2 { get; set; } = string.Empty;

        public int? TxtPhone { get; set; }

        [DbConfiguredDisplayName]
        [VerifyEmailProvidedForEreceipts]
        public bool ReceiveEreceipts { get; set; }

        [DbConfiguredRequired]
        [EmailAddress]
        [DbConfiguredDisplayName]
        [VerifyDeliveryOption]
        public string? EmailAddress { get; set; } = string.Empty;

        public string? AltEmailAddress { get; set; } = string.Empty;

        [DbConfiguredDisplayName]
        public string StreetOne { get; set; } = string.Empty;

        [DbConfiguredDisplayName]
        public string? StreetTwo { get; set; } = string.Empty;

        [DbConfiguredDisplayName]
        public string City { get; set; } = string.Empty;

        [DbConfiguredDisplayName]
        public string State { get; set; } = string.Empty;

        public string User2 { get; set; } = string.Empty;

        public string User4 { get; set; } = string.Empty;

        [DbConfiguredDisplayName]
        [DbConfiguredRequired]
        public string? User5 { get; set; } = string.Empty;

        [RegularExpression(@"^\d{5}$", ErrorMessage = "Invalid ZIP")]
        [MaxLength(5)]
        [DbConfiguredDisplayName]
        public string PostalCode { get; set; } = string.Empty;

        [Required]
        [DbConfiguredDisplayName]
        [RegularExpression(@"^\w*$", ErrorMessage = "Passwords can include letters (A–Z) and numbers (0–9) only")]
        public string Password { get; set; } = string.Empty;

        [Compare("Password", ErrorMessage = "Passwords must match")]
        [DbConfiguredDisplayName]
        [RegularExpression(@"^\w*$", ErrorMessage = "Passwords can include letters (A–Z) and numbers (0–9) only")]
        public string Password2 { get; set; } = string.Empty;

        [DbConfiguredDisplayName]
        public int? RequestPickupBranchID { get; set; }

        [DbConfiguredDisplayName]
        public string User1 { get; set; } = string.Empty;

        [DbConfiguredDisplayName]
        public bool DeliverCardToSchool { get; set; }

        [DbConfiguredDisplayName]
        public bool IsStudent { get; set; }

        [DbConfiguredDisplayName]
        public bool IsTeacher { get; set; }

        [DbConfiguredDisplayName]
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

        public List<SelectListItem> Genders { get; set; } = new List<SelectListItem>();

        public List<SelectListItem> Months { get; set; } = new List<SelectListItem>();

        public IRestResponse<PersonatorResponse>? MelissaResponse { get; set; }

        [DbConfiguredDisplayName]
        public bool AddToMailingList { get; set; }

        public bool ShowDlButton { get; set; }

        [JsonIgnore]
        public ISettingProvider Settings { get; set; } = default!;
    }
}
