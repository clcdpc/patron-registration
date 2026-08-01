using Clc.Melissa;
using Clc.PatronRegistration.Configuration;
using Clc.PatronRegistration.Data;
using Clc.PatronRegistration.Web.Controllers;
using Clc.PatronRegistration;
using Clc.Polaris.Api;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using Clc.Polaris.Api.Models;
using Clc.Rest;
using Clc.Rest.Models;
using Clc.PatronRegistration.Helpers;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc;
using System.Reflection;

namespace Clc.PatronRegistration.Tests
{
    [TestClass]
    public class RegistrationTests : IDisposable
    {
        private Mock<ISettingProvider> _mockSettings;
        private Mock<IDbHelper> _mockDbHelper;
        private Mock<IPapiClient> _mockPapiClient;
        private Mock<IMelissaRestClient> _mockMelissaClient;
        private Mock<IEmailSender> _mockEmailSender;
        private Mock<ICache> _mockCache;

        [TestInitialize]
        public void Setup()
        {
            _mockSettings = new Mock<ISettingProvider>();
            _mockDbHelper = new Mock<IDbHelper>();
            _mockPapiClient = new Mock<IPapiClient>();
            _mockMelissaClient = new Mock<IMelissaRestClient>();
            _mockEmailSender = new Mock<IEmailSender>();
            _mockCache = new Mock<ICache>();

            CacheHelper.Configure(new TestCache());

            _mockSettings.Setup(s => s.FormCode).Returns("");
            _mockSettings.Setup(s => s.LibraryId).Returns(2);
        }

        [TestMethod]
        public void Test_SetPatronCode_Default()
        {
            _mockSettings.Setup(s => s.PatronCodeId).Returns(1);
            var registration = new Registration(_mockSettings.Object)
            {
                PatronBranchID = 1
            };
            registration.SetPatronCode();
            Assert.AreEqual(_mockSettings.Object.PatronCodeId, registration.PatronCode);
        }

        [DataTestMethod]
        [DataRow("AS01", false, 20)]
        [DataRow("VR01", true, 30)]
        public void CreateRegistration_PassesAddressSpecificPatronCodeToPolaris(
            string melissaResult, bool plusNameMatch, int expectedPatronCode)
        {
            _mockSettings.Setup(s => s.PatronCodeId).Returns(10);
            _mockSettings.Setup(s => s.ValidAddressPatronCodeId).Returns(20);
            _mockSettings.Setup(s => s.ValidAddressPlusNamePatronCodeId).Returns(30);
            _mockSettings.Setup(s => s.RegistrationText).Returns("Registration complete");
            _mockSettings.Setup(s => s.DriversLicenseButtonEnabledIpAddresses).Returns([]);
            _mockDbHelper.Setup(db => db.CheckPatronIsDuplicate(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>())).Returns(false);
            DbHelper.Global = _mockDbHelper.Object;
            _mockMelissaClient.Setup(client => client.PersonatorRequest(It.IsAny<Clc.Melissa.Models.PersonatorRequestRecord>()))
                .Returns(new RestResponse<Clc.Melissa.Models.PersonatorResponse>
                {
                    Data = new Clc.Melissa.Models.PersonatorResponse
                    {
                        Records =
                        [
                            new Clc.Melissa.Models.Record
                            {
                                Results = melissaResult,
                                AddressLine1 = "123 Main Street",
                                AddressLine2 = "",
                                City = "Columbus",
                                State = "OH",
                                PostalCode = "43215"
                            }
                        ]
                    }
                });
            PatronRegistrationParams captured = null;
            _mockPapiClient.Setup(client => client.PatronRegistrationCreate(It.IsAny<PatronRegistrationParams>()))
                .Callback<PatronRegistrationParams>(value => captured = value)
                .Returns(new RestResponse<PatronRegistrationCreateResult>
                {
                    Data = new PatronRegistrationCreateResult
                    {
                        PatronID = 123,
                        Barcode = "2000000000123",
                        PAPIErrorCode = 0
                    }
                });
            var registration = new Registration(_mockSettings.Object)
            {
                NameFirst = "Pat",
                NameLast = "Reader",
                Birthdate = new DateTime(1990, 1, 1),
                PatronBranchID = 1,
                StreetOne = "123 Main Street",
                StreetTwo = "",
                City = "Columbus",
                State = "OH",
                PostalCode = "43215"
            };

            var result = registration.CreateRegistration(
                "203.0.113.1", new ModelStateDictionary(), _mockSettings.Object, _mockDbHelper.Object,
                _mockPapiClient.Object, _mockMelissaClient.Object, _mockEmailSender.Object);

            Assert.AreEqual(RegistrationStatus.Success, result.Status);
            Assert.IsNotNull(captured);
            Assert.AreEqual(expectedPatronCode, captured.PatronCode);
            Assert.AreEqual(plusNameMatch ? AddressVerificationStatus.ValidPlusNameMatch : AddressVerificationStatus.Valid,
                registration.AddressVerificationStatus);
        }

        [TestMethod]
        public void DefaultPatronCode_RemainsWhenAddressVerificationHasNoResult()
        {
            _mockSettings.Setup(s => s.PatronCodeId).Returns(10);
            var registration = new Registration(_mockSettings.Object);

            registration.SetPatronCode();
            registration.ApplyAddressVerificationPatronCode(AddressVerificationStatus.None);

            Assert.AreEqual(10, registration.PatronCode);
        }

        [TestMethod]
        public void VerifiedAddressPatronCode_ReplacesDefault()
        {
            _mockSettings.Setup(s => s.PatronCodeId).Returns(10);
            _mockSettings.Setup(s => s.ValidAddressPatronCodeId).Returns(20);
            var registration = new Registration(_mockSettings.Object);

            registration.SetPatronCode();
            registration.ApplyAddressVerificationPatronCode(AddressVerificationStatus.Valid);

            Assert.AreEqual(20, registration.PatronCode);
        }

        [TestMethod]
        public void AddressAndNameMatchPatronCode_ReplacesDefault()
        {
            _mockSettings.Setup(s => s.PatronCodeId).Returns(10);
            _mockSettings.Setup(s => s.ValidAddressPlusNamePatronCodeId).Returns(30);
            var registration = new Registration(_mockSettings.Object);

            registration.SetPatronCode();
            registration.ApplyAddressVerificationPatronCode(AddressVerificationStatus.ValidPlusNameMatch);

            Assert.AreEqual(30, registration.PatronCode);
        }

        [TestMethod]
        public void AddressVerification_DoesNotReplaceAnAlreadySpecificPatronCode()
        {
            _mockSettings.Setup(s => s.PatronCodeId).Returns(10);
            _mockSettings.Setup(s => s.ValidAddressPatronCodeId).Returns(20);
            var registration = new Registration(_mockSettings.Object) { PatronCode = 99 };

            registration.ApplyAddressVerificationPatronCode(AddressVerificationStatus.Valid);

            Assert.AreEqual(99, registration.PatronCode);
        }

        [DataTestMethod]
        [DataRow(false, 0)]
        [DataRow(false, -1)]
        [DataRow(true, 0)]
        [DataRow(true, -1)]
        public void InvalidAddressSpecificPatronCode_DoesNotEraseDefault(bool plusNameMatch, int addressCode)
        {
            _mockSettings.Setup(s => s.PatronCodeId).Returns(10);
            _mockSettings.Setup(s => s.ValidAddressPatronCodeId).Returns(addressCode);
            _mockSettings.Setup(s => s.ValidAddressPlusNamePatronCodeId).Returns(addressCode);
            var registration = new Registration(_mockSettings.Object);

            registration.SetPatronCode();
            registration.ApplyAddressVerificationPatronCode(plusNameMatch ? AddressVerificationStatus.ValidPlusNameMatch : AddressVerificationStatus.Valid);

            Assert.AreEqual(10, registration.PatronCode);
        }

        [DataTestMethod]
        [DataRow(false, AddressVerificationStatus.Valid, 20, "Verified success")]
        [DataRow(true, AddressVerificationStatus.ValidPlusNameMatch, 30, "Name-match success")]
        public void AddressSpecificSuccessMessage_IsSelectedAfterCodeIsApplied(
            bool plusNameMatch, AddressVerificationStatus status, int addressCode, string expectedMessage)
        {
            _mockSettings.Setup(s => s.RegistrationText).Returns("Default success");
            _mockSettings.Setup(s => s.PatronCodeId).Returns(10);
            _mockSettings.Setup(s => s.ValidAddressPatronCodeId).Returns(20);
            _mockSettings.Setup(s => s.ValidAddressPlusNamePatronCodeId).Returns(30);
            _mockSettings.Setup(s => s.ValidAddressRegistrationText).Returns("Verified success");
            _mockSettings.Setup(s => s.ValidAddressPlusNameRegistrationText).Returns("Name-match success");
            _mockSettings.Setup(s => s.DriversLicenseButtonEnabledIpAddresses).Returns([]);
            var registration = new Registration(_mockSettings.Object) { AddressVerificationStatus = status };
            registration.SetPatronCode();
            registration.ApplyAddressVerificationPatronCode(plusNameMatch ? AddressVerificationStatus.ValidPlusNameMatch : AddressVerificationStatus.Valid);

            Assert.AreEqual(addressCode, registration.PatronCode);
            Assert.AreEqual(expectedMessage, RegistrationSuccessText(registration));
        }

        [DataTestMethod]
        [DataRow(false, AddressVerificationStatus.Valid)]
        [DataRow(true, AddressVerificationStatus.ValidPlusNameMatch)]
        public void AddressSpecificSuccessMessage_IsNotSelectedWhenCodeCannotBeApplied(bool plusNameMatch, AddressVerificationStatus status)
        {
            _mockSettings.Setup(s => s.RegistrationText).Returns("Default success");
            _mockSettings.Setup(s => s.PatronCodeId).Returns(10);
            _mockSettings.Setup(s => s.ValidAddressRegistrationText).Returns("Verified success");
            _mockSettings.Setup(s => s.ValidAddressPlusNameRegistrationText).Returns("Name-match success");
            _mockSettings.Setup(s => s.DriversLicenseButtonEnabledIpAddresses).Returns([]);
            var registration = new Registration(_mockSettings.Object) { AddressVerificationStatus = status };
            registration.SetPatronCode();
            registration.ApplyAddressVerificationPatronCode(plusNameMatch ? AddressVerificationStatus.ValidPlusNameMatch : AddressVerificationStatus.Valid);

            Assert.AreEqual(10, registration.PatronCode);
            Assert.AreEqual("Default success", RegistrationSuccessText(registration));
        }

        [TestMethod]
        public void LaterSpecificPatronCodes_StillOverrideDefaultAndAddressCodes()
        {
            _mockSettings.Setup(s => s.PatronCodeId).Returns(10);
            _mockSettings.Setup(s => s.ValidAddressPatronCodeId).Returns(20);
            _mockSettings.Setup(s => s.DisplayECardCheckbox).Returns(true);
            _mockSettings.Setup(s => s.EcardPatronCodeId).Returns(30);
            _mockSettings.Setup(s => s.RegistrationText).Returns("Default success");
            _mockSettings.Setup(s => s.ValidAddressRegistrationText).Returns("Verified success");
            _mockSettings.Setup(s => s.EcardRegistrationText).Returns("E-card success");
            _mockSettings.Setup(s => s.DriversLicenseButtonEnabledIpAddresses).Returns([]);
            _mockSettings.Setup(s => s.SchoolInfoFormat).Returns("configured");
            _mockSettings.Setup(s => s.TeacherPatronCodeId).Returns(40);
            _mockSettings.Setup(s => s.StudentPatronCodeId).Returns(50);
            var registration = new Registration(_mockSettings.Object) { IsECard = true };

            registration.SetPatronCode();
            registration.ApplyAddressVerificationPatronCode(AddressVerificationStatus.Valid);
            Assert.AreEqual(20, registration.PatronCode);
            registration.HandleECardSettings();
            Assert.AreEqual(30, registration.PatronCode);
            Assert.AreEqual("E-card success", RegistrationSuccessText(registration));
            registration.IsTeacher = true;
            registration.HandleSchoolInfo();
            Assert.AreEqual(40, registration.PatronCode);
            registration.IsTeacher = false;
            registration.IsStudent = true;
            registration.HandleSchoolInfo();
            Assert.AreEqual(50, registration.PatronCode);
        }

        [TestMethod]
        public void SettingsContract_HasNoForcedPatronCodeSetting()
        {
            Assert.IsFalse(typeof(ISettingProvider).GetProperties().Any(property =>
                property.Name.Contains("Force", StringComparison.OrdinalIgnoreCase) &&
                property.Name.Contains("PatronCode", StringComparison.OrdinalIgnoreCase)));
        }

        private static string RegistrationSuccessText(Registration registration)
        {
            var method = typeof(Registration).GetMethod("GetRegistrationSuccessText", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(method);
            return (string)method.Invoke(registration, ["203.0.113.1"])!;
        }

        [TestMethod]
        public void HandleSmsSettings_DeliveryOptionIdIs8_SetsTxtPhoneAndPhone1CarrierID()
        {
            var registration = new Registration(_mockSettings.Object)
            {
                DeliveryOptionId = 8
            };

            registration.HandleSmsSettings();

            Assert.AreEqual(1, registration.TxtPhoneNumber);
            Assert.AreEqual(23, registration.Phone1CarrierID);
        }

        [TestMethod]
        public void HandleSmsSettings_DeliveryOptionIdIsNot8_DoesNotSetTxtPhoneAndPhone1CarrierID()
        {
            var registration = new Registration(_mockSettings.Object)
            {
                DeliveryOptionId = 1
            };

            registration.HandleSmsSettings();

            Assert.IsNull(registration.TxtPhoneNumber);
            Assert.AreNotEqual(23, registration.Phone1CarrierID);
        }


        [TestMethod]
        public void Test_DupeCheck_NoDuplicate()
        {
            _mockDbHelper.Setup(db => db.CheckPatronIsDuplicate(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>())).Returns(false);

            var registration = new Registration(_mockSettings.Object)
            {
                NameFirst = "John",
                NameLast = "Doe",
                Birthdate = new DateTime(2000, 1, 1),
                PatronBranchID = 1
            };

            var result = registration.DupeCheck(_mockDbHelper.Object, _mockPapiClient.Object);
            Assert.IsFalse(result.IsDupe);
        }

        [TestMethod]
        public void Test_DupeCheck_Duplicate()
        {
            _mockSettings.Setup(s => s.DuplicatePatronMessageHtml).Returns("dupe message [branch_id] [branch_phone]");
            _mockDbHelper.Setup(db => db.CheckPatronIsDuplicate(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>())).Returns(true);
            _mockPapiClient.Setup(p => p.SA_GetValueByOrg(It.IsAny<string>(), It.IsAny<int>())).Returns(new RestResponse<StringResult> { Data = new StringResult { Value = "(123) 456-7890" } });

            var registration = new Registration(_mockSettings.Object)
            {
                NameFirst = "John",
                NameLast = "Doe",
                Birthdate = new DateTime(2000, 1, 1),
                PatronBranchID = 1
            };

            var result = registration.DupeCheck(_mockDbHelper.Object, _mockPapiClient.Object);
            Assert.IsTrue(result.IsDupe);
            Assert.AreEqual(result.Message, "dupe message 1 at (123) 456-7890");
        }

        [TestMethod]
        public void Test_DupeCheck_SWPL_Kids_Card_Last_Name_Addition()
        {
            _mockSettings.Setup(s => s.LibraryId).Returns(28);
            _mockSettings.Setup(s => s.FormCode).Returns("kids");
            _mockSettings.Setup(s => s.DuplicatePatronMessageHtml).Returns("dupe message [branch_id] [branch_phone]");
            _mockDbHelper.Setup(db => db.CheckPatronIsDuplicate(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>())).Returns(false);
            _mockDbHelper.Setup(db => db.CheckPatronIsDuplicate(It.IsAny<int>(), It.IsAny<string>(), It.Is<string>(s => s.StartsWith("C-")), It.IsAny<DateTime>())).Returns(true);
            _mockPapiClient.Setup(p => p.SA_GetValueByOrg(It.IsAny<string>(), It.IsAny<int>())).Returns(new RestResponse<StringResult> { Data = new StringResult { Value = "(123) 456-7890" } });

            var registration = new Registration(_mockSettings.Object)
            {
                NameFirst = "John",
                NameLast = "Doe",
                Birthdate = new DateTime(2000, 1, 1),
                PatronBranchID = 1
            };

            var result = registration.DupeCheck(_mockDbHelper.Object, _mockPapiClient.Object);
            Assert.IsTrue(result.IsDupe);
            Assert.AreEqual(result.Message, "dupe message 1 at (123) 456-7890");
        }

        [TestMethod]
        public void ShouldAutoReset_ShouldReturnTrue_WhenResetFormIsTrueAndIpIsInList()
        {
            _mockSettings.Setup(s => s.ResetForm).Returns(true);
            _mockSettings.Setup(s => s.DriversLicenseButtonEnabledIpAddresses).Returns(new List<string> { "127.0.0.1" });
            var registration = new Registration(_mockSettings.Object);

            var result = registration.ShouldAutoReset("127.0.0.1");

            Assert.IsTrue(result);
        }

        [TestMethod]
        public void ShouldAutoReset_ShouldReturnFalse_WhenResetFormIsFalse()
        {
            _mockSettings.Setup(s => s.ResetForm).Returns(false);
            _mockSettings.Setup(s => s.DriversLicenseButtonEnabledIpAddresses).Returns(new List<string> { "127.0.0.1" });
            var registration = new Registration(_mockSettings.Object);

            var result = registration.ShouldAutoReset("127.0.0.1");

            Assert.IsFalse(result);
        }

        [TestMethod]
        public void ShouldAutoReset_ShouldReturnFalse_WhenIpIsNotInList()
        {
            _mockSettings.Setup(s => s.ResetForm).Returns(true);
            _mockSettings.Setup(s => s.DriversLicenseButtonEnabledIpAddresses).Returns(new List<string> { "127.0.0.1" });
            var registration = new Registration(_mockSettings.Object);

            var result = registration.ShouldAutoReset("192.168.0.1");

            Assert.IsFalse(result);
        }

        [TestMethod]
        public void ApplyForceEcardSetting_DoesNotOverrideSelection_WhenForceEcardRemotelyIsFalse()
        {
            _mockSettings.Setup(s => s.ForceEcardRemotely).Returns(false);
            _mockSettings.Setup(s => s.DriversLicenseButtonEnabledIpAddresses).Returns(new List<string> { "127.0.0.1" });
            var registration = new Registration(_mockSettings.Object)
            {
                IsECard = true
            };

            registration.ApplyForceEcardSetting("192.168.0.1");

            Assert.IsTrue(registration.IsECard);
        }

        [TestMethod]
        public void ApplyForceEcardSetting_ForcesEcardForRemoteIp_WhenForceEcardRemotelyIsTrue()
        {
            _mockSettings.Setup(s => s.ForceEcardRemotely).Returns(true);
            _mockSettings.Setup(s => s.DriversLicenseButtonEnabledIpAddresses).Returns(new List<string> { "127.0.0.1" });
            var registration = new Registration(_mockSettings.Object)
            {
                IsECard = false
            };

            registration.ApplyForceEcardSetting("192.168.0.1");

            Assert.IsTrue(registration.IsECard);
        }

        [TestMethod]
        public void ApplyForceEcardSetting_ForcesStandardCardForLocalIp_WhenForceEcardRemotelyIsTrue()
        {
            _mockSettings.Setup(s => s.ForceEcardRemotely).Returns(true);
            _mockSettings.Setup(s => s.DriversLicenseButtonEnabledIpAddresses).Returns(new List<string> { "127.0.0.1" });
            var registration = new Registration(_mockSettings.Object)
            {
                IsECard = true
            };

            registration.ApplyForceEcardSetting("127.0.0.1");

            Assert.IsFalse(registration.IsECard);
        }

        [TestMethod]
        public void HandleAddToMailingList_AddsToMailingList_WhenConditionsAreMet()
        {
            _mockSettings.Setup(s => s.MailingListRecordSetId).Returns(1);

            var registration = new Registration(_mockSettings.Object)
            {
                AddToMailingList = true
            };

            registration.HandleAddToMailingList(_mockPapiClient.Object, 123);

            _mockPapiClient.Verify(p => p.RecordSetContentAdd(1, 123, It.IsAny<int>(), It.IsAny<int>()), Times.Once);
        }

        [TestMethod]
        public void HandleAddToMailingList_DoesNotAddToMailingList_WhenAddToMailingListIsFalse()
        {
            _mockSettings.Setup(s => s.MailingListRecordSetId).Returns(1);

            var registration = new Registration(_mockSettings.Object)
            {
                AddToMailingList = false
            };

            registration.HandleAddToMailingList(_mockPapiClient.Object, 123);

            _mockPapiClient.Verify(p => p.RecordSetContentAdd(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
        }

        [TestMethod]
        public void HandleAddToMailingList_DoesNotAddToMailingList_WhenMailingListRecordSetIdIsZero()
        {


            _mockSettings.Setup(s => s.MailingListRecordSetId).Returns(0);

            var registration = new Registration(_mockSettings.Object)
            {
                AddToMailingList = true
            };

            registration.HandleAddToMailingList(_mockPapiClient.Object, 123);

            _mockPapiClient.Verify(p => p.RecordSetContentAdd(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
        }

        [DataTestMethod]
        [DataRow(-1, 123)]
        [DataRow(1, 0)]
        [DataRow(1, -1)]
        public void HandleAddToMailingList_InvalidLegacyIdentifiersDoNotCallPapi(int recordSetId, int patronId)
        {
            _mockSettings.Setup(s => s.MailingListRecordSetId).Returns(recordSetId);
            var registration = new Registration(_mockSettings.Object) { AddToMailingList = true };

            registration.HandleAddToMailingList(_mockPapiClient.Object, patronId);

            _mockPapiClient.Verify(p => p.RecordSetContentAdd(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
        }

        [TestMethod]
        public void AddToRecordSet_SettingsHasValue_CallsRecordSetContentAdd()
        {
            _mockSettings.Setup(s => s.AddToRecordSetId).Returns(1);

            var registration = new Registration(_mockSettings.Object);

            registration.AddToRecordSet(_mockPapiClient.Object, 123);

            _mockPapiClient.Verify(p => p.RecordSetContentAdd(1, 123, It.IsAny<int>(), It.IsAny<int>()), Times.Once);
        }

        [TestMethod]
        public void AddToRecordSet_SettingsHasNoValue_DoesNotCallRecordSetContentAdd()
        {
            _mockSettings.Setup(s => s.AddToRecordSetId).Returns((int?)null);

            var registration = new Registration(_mockSettings.Object);

            registration.AddToRecordSet(_mockPapiClient.Object, 123);

            _mockPapiClient.Verify(p => p.RecordSetContentAdd(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
        }

        [DataTestMethod]
        [DataRow(0, 123)]
        [DataRow(-1, 123)]
        [DataRow(1, 0)]
        [DataRow(1, -1)]
        public void AddPatronToRecordSet_RequiresPositiveIdentifiers(int recordSetId, int patronId)
        {
            var registration = new Registration(_mockSettings.Object);

            registration.AddPatronToRecordSet(patronId, recordSetId, _mockPapiClient.Object);

            _mockPapiClient.Verify(p => p.RecordSetContentAdd(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
        }

        [TestMethod]
        public void NegativeLegacyPatronCodeAndLogonUserAreNotApplied()
        {
            _mockSettings.Setup(s => s.PatronCodeId).Returns(-10);
            _mockSettings.Setup(s => s.RegistrationLogonUserId).Returns(-20);
            var registration = new Registration(_mockSettings.Object)
            {
                AddressVerificationStatus = AddressVerificationStatus.Invalid
            };

            registration.SetPatronCode();
            registration.SetLogonUserID();

            Assert.IsNull(registration.PatronCode);
            Assert.AreEqual(0, registration.LogonUserID);
        }

        [TestMethod]
        public void AddPostRegistrationNote_WithValidNoteText_CallsUpdatePatronNotesData()
        {
            _mockSettings.Setup(s => s.PostRegistrationNoteText).Returns("Test Note");

            var registration = new Registration(_mockSettings.Object) { Barcode = "12345" };
            registration.AddPostRegistrationNote(_mockPapiClient.Object);

            _mockPapiClient.Verify(p => p.UpdatePatronNotesData("12345", _mockSettings.Object.PostRegistrationNoteText, It.IsAny<string>(), It.IsAny<UpdateNoteMode>(), It.IsAny<int?>()), Times.Once);
        }

        [TestMethod]
        public void AddPostRegistrationNote_WithEmptyNoteText_DoesNotCallUpdatePatronNotesData()
        {
            _mockSettings.Setup(s => s.PostRegistrationNoteText).Returns(string.Empty);

            var registration = new Registration(_mockSettings.Object);
            registration.AddPostRegistrationNote(_mockPapiClient.Object);

            _mockPapiClient.Verify(p => p.UpdatePatronNotesData(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<UpdateNoteMode>(), It.IsAny<int>()), Times.Never);
        }
        [TestMethod]
        public void HandleSchoolDelivery_DeliverCardToSchoolTrue_UpdatesPatronNotesData()
        {
            _mockSettings.Setup(s => s.SchoolInfoFormat).Returns("SomeFormat");

            var registration = new Registration(_mockSettings.Object)
            {
                DeliverCardToSchool = true,
                IsTeacher = true,
                User1 = "SomeUser",
                Barcode = "12345"
            };

            registration.HandleSchoolDelivery(_mockPapiClient.Object);

            _mockPapiClient.Verify(p => p.UpdatePatronNotesData("12345", "School Delivery Requested", It.IsAny<string>(), It.IsAny<UpdateNoteMode>(), It.IsAny<int?>()), Times.Once);
        }

        [TestMethod]
        public void HandleSchoolDelivery_DeliverCardToSchoolFalse_DoesNotUpdatePatronNotesData()
        {
            var registration = new Registration(_mockSettings.Object)
            {
                DeliverCardToSchool = false
            };

            registration.HandleSchoolDelivery(_mockPapiClient.Object);

            _mockPapiClient.Verify(p => p.UpdatePatronNotesData(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<UpdateNoteMode>(), It.IsAny<int>()), Times.Never);
        }

        [TestMethod]
        public void HandleSchoolDelivery_SchoolInfoFormatEmpty_DoesNotUpdatePatronNotesData()
        {
            _mockSettings.Setup(s => s.SchoolInfoFormat).Returns(string.Empty);

            var registration = new Registration(_mockSettings.Object)
            {
                DeliverCardToSchool = true,
                IsTeacher = true,
                User1 = "SomeUser",
                Barcode = "12345"
            };

            registration.HandleSchoolDelivery(_mockPapiClient.Object);

            _mockPapiClient.Verify(p => p.UpdatePatronNotesData(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<UpdateNoteMode>(), It.IsAny<int>()), Times.Never);
        }

        [TestMethod]
        public void HandleSchoolDelivery_User1Empty_DoesNotUpdatePatronNotesData()
        {
            _mockSettings.Setup(s => s.SchoolInfoFormat).Returns("SomeFormat");

            var registration = new Registration(_mockSettings.Object)
            {
                DeliverCardToSchool = true,
                IsTeacher = true,
                User1 = string.Empty,
                Barcode = "12345"
            };

            registration.HandleSchoolDelivery(_mockPapiClient.Object);

            _mockPapiClient.Verify(p => p.UpdatePatronNotesData(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<UpdateNoteMode>(), It.IsAny<int>()), Times.Never);
        }
        [TestMethod]
        public void SendWelcomeEmail_ShouldSendEmail_WhenEmailAddressAndTemplatesAreValid()
        {
            var emailSent = new ManualResetEvent(false);
            _mockEmailSender.Setup(s => s.Send(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).Callback(() => emailSent.Set());

            _mockSettings.Setup(s => s.WelcomeEmailSubject).Returns("Welcome");
            _mockSettings.Setup(s => s.WelcomeEmailTemplateText).Returns("Welcome, {{NameFirst}}");
            _mockSettings.Setup(s => s.WelcomeEmailTemplateHtml).Returns("<p>Welcome, {{NameFirst}}</p>");
            _mockSettings.Setup(s => s.WelcomeEmailFromName).Returns("Library");
            _mockSettings.Setup(s => s.WelcomeEmailFromAddress).Returns("library@example.com");

            var registration = new Registration(_mockSettings.Object)
            {
                EmailAddress = "user@example.com",
                NameFirst = "John"
            };

            registration.SendWelcomeEmail(_mockEmailSender.Object);

            Assert.IsTrue(emailSent.WaitOne(TimeSpan.FromSeconds(3)), "Send was never called");
            _mockEmailSender.Verify(e => e.Send(
                "user@example.com",
                "\"Library\" library@example.com",
                "library@example.com",
                "Welcome",
                "<p>Welcome, John</p>",
                "Welcome, John"
            ), Times.Once);
        }

        [TestMethod]
        public void SendWelcomeEmail_ShouldNotSendEmail_WhenEmailAddressIsEmpty()
        {
            var mockEmailSender = new Mock<IEmailSender>();


            var registration = new Registration(_mockSettings.Object)
            {
                EmailAddress = ""
            };

            registration.SendWelcomeEmail(mockEmailSender.Object);

            mockEmailSender.Verify(e => e.Send(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>()
            ), Times.Never);
        }

        [TestMethod]
        public void SendWelcomeEmail_ShouldNotSendEmail_WhenTemplatesAreEmpty()
        {
            var mockEmailSender = new Mock<IEmailSender>();

            _mockSettings.Setup(s => s.WelcomeEmailTemplateText).Returns("");
            _mockSettings.Setup(s => s.WelcomeEmailTemplateHtml).Returns("");

            var registration = new Registration(_mockSettings.Object)
            {
                EmailAddress = "user@example.com"
            };

            registration.SendWelcomeEmail(mockEmailSender.Object);

            mockEmailSender.Verify(e => e.Send(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>()
            ), Times.Never);
        }

        [TestMethod]
        public void SendWelcomeEmail_ShouldUseECardTemplate_WhenIsECardIsTrue()
        {
            var emailSent = new ManualResetEvent(false);
            _mockEmailSender.Setup(s => s.Send(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).Callback(() => emailSent.Set());

            _mockSettings.Setup(s => s.EcardWelcomeEmailSubject).Returns("E-Card Welcome");
            _mockSettings.Setup(s => s.EcardWelcomeEmailTemplateText).Returns("E-Card Welcome, {{NameFirst}}");
            _mockSettings.Setup(s => s.EcardWelcomeEmailTemplateHtml).Returns("<p>E-Card Welcome, {{NameFirst}}</p>");
            _mockSettings.Setup(s => s.WelcomeEmailFromName).Returns("Library");
            _mockSettings.Setup(s => s.WelcomeEmailFromAddress).Returns("library@example.com");

            var registration = new Registration(_mockSettings.Object)
            {
                EmailAddress = "user@example.com",
                NameFirst = "John",
                IsECard = true
            };

            registration.SendWelcomeEmail(_mockEmailSender.Object);

            Assert.IsTrue(emailSent.WaitOne(TimeSpan.FromSeconds(3)), "Send was never called");
            _mockEmailSender.Verify(e => e.Send(
                "user@example.com",
                "\"Library\" library@example.com",
                "library@example.com",
                "E-Card Welcome",
                "<p>E-Card Welcome, John</p>",
                "E-Card Welcome, John"
            ), Times.Once);
        }

        [TestMethod]
        public void SendWelcomeEmail_ShouldUseStandardTemplate_WhenIsECardIsFalse()
        {
            var emailSent = new ManualResetEvent(false);
            _mockEmailSender.Setup(s => s.Send(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).Callback(() => emailSent.Set());

            _mockSettings.Setup(s => s.WelcomeEmailSubject).Returns("Welcome");
            _mockSettings.Setup(s => s.WelcomeEmailTemplateText).Returns("Welcome, {{NameFirst}}");
            _mockSettings.Setup(s => s.WelcomeEmailTemplateHtml).Returns("<p>Welcome, {{NameFirst}}</p>");
            _mockSettings.Setup(s => s.WelcomeEmailFromName).Returns("Library");
            _mockSettings.Setup(s => s.WelcomeEmailFromAddress).Returns("library@example.com");

            var registration = new Registration(_mockSettings.Object)
            {
                EmailAddress = "user@example.com",
                NameFirst = "John",
                IsECard = false
            };

            registration.SendWelcomeEmail(_mockEmailSender.Object);

            Assert.IsTrue(emailSent.WaitOne(TimeSpan.FromSeconds(3)), "Send was never called");
            _mockEmailSender.Verify(e => e.Send(
                "user@example.com",
                "\"Library\" library@example.com",
                "library@example.com",
                "Welcome",
                "<p>Welcome, John</p>",
                "Welcome, John"
            ), Times.Once);
        }

        public static string RandomLetters(int length = 8, string chars = "abcdefghijklmnopqrstuvwxyz")
        {
            var stringChars = new char[length];
            var random = new Random();

            for (int i = 0; i < stringChars.Length; i++)
            {
                stringChars[i] = chars[random.Next(chars.Length)];
            }

            return new String(stringChars);
        }

        //[TestCleanup]
        public void Dispose()
        {
        }
    }
}
