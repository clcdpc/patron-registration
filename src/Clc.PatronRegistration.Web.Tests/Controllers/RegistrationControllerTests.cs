//using Clc.Melissa;
//using Clc.PatronRegistration.Configuration;
//using Clc.PatronRegistration.Data;
//using Clc.PatronRegistration.Web.Controllers;
//using Clc.PatronRegistration;
//using Clc.Polaris.Api;
//using Microsoft.VisualStudio.TestTools.UnitTesting;
//using Moq;
//using Clc.Polaris.Api.Models;
//using Clc.Rest;
//using Clc.Rest.Models;

//namespace Clc.PatronRegistration.Web.Tests.Controllers
//{
//    [TestClass]
//    public class RegistrationControllerTests
//    {
//        private RegistrationController _controller;
//        private Mock<IPapiClient> _mockPolaris;
//        private Mock<IMelissaRestClient> _mockMelissa;
//        private Mock<IDbHelper> _mockDb;
//        private Mock<IEmailSender> _mockEmailSender;
//        private Mock<ISettingProvider> _mockSettings;

//        [TestInitialize]
//        public void SetUp()
//        {
//            _mockPolaris = new Mock<IPapiClient>();
//            _mockMelissa = new Mock<IMelissaRestClient>();
//            _mockDb = new Mock<IDbHelper>();
//            _mockEmailSender = new Mock<IEmailSender>();
//            _mockSettings = new Mock<ISettingProvider>();

//            // Setup mocked settings values
//            _mockSettings.SetupGet(s => s.DisableBranch).Returns(false);
//            _mockSettings.SetupGet(s => s.BlockOutOfStateRegistrations).Returns(true);
//            _mockSettings.SetupGet(s => s.OutOfStateBlockMessage).Returns("Registrations from outside the state are not allowed.");
//            _mockSettings.SetupGet(s => s.WelcomeEmailSubject).Returns("Welcome!");
//            _mockSettings.SetupGet(s => s.WelcomeEmailTemplateHtml).Returns("<h1>Welcome</h1>");
//            _mockSettings.SetupGet(s => s.WelcomeEmailTemplateText).Returns("Welcome to the service!");

//            // Setup mocked email sender behavior
//            _mockEmailSender
//                .Setup(e => e.Send(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
//                .Returns(true);

//            _controller = new RegistrationController(
//                _mockPolaris.Object,
//                _mockMelissa.Object,
//                _mockDb.Object,
//                _mockSettings.Object,
//                _mockEmailSender.Object
//            );
//        }

//        [TestMethod]
//        public void Submit_WhenPasswordProvided_ReturnsEmptyRegistrationAttempt()
//        {
//            var registration = new Registration { a_password = "somepassword" };

//            var result = _controller.Submit(registration);

//            Assert.IsNotNull(result);
//            Assert.AreEqual(RegistrationStatus.Error, result.Status);
//        }

//        [TestMethod]
//        public void Submit_WhenNoBirthdate_ReturnsErrorWithMessage()
//        {
//            var registration = new Registration { Birthdate = null };

//            var result = _controller.Submit(registration);

//            Assert.AreEqual(RegistrationStatus.Error, result.Status);
//            Assert.IsTrue(result.Errors.Any(e => e.Value == "Please enter a valid birth date."));
//        }

//        [TestMethod]
//        public void Submit_WhenDuplicateRegistration_ReturnsErrorWithDuplicateMessage()
//        {
//            var registration = new Registration();
//            _mockDb.Setup(d => d.IsDuplicate(It.IsAny<Registration>())).Returns(true);

//            var result = _controller.Submit(registration);

//            Assert.AreEqual(RegistrationStatus.Duplicate, result.Status);
//        }

//        [TestMethod]
//        public void Submit_WhenStateIsOhio_NormalizesStateToOH()
//        {
//            var registration = new Registration { State = "ohio" };

//            var result = _controller.Submit(registration);

//            Assert.AreEqual("OH", registration.State);
//        }

//        [TestMethod]
//        public void Submit_WhenOutOfStateAndBlocked_ReturnsErrorWithOutOfStateMessage()
//        {
//            var registration = new Registration { State = "NY" };

//            var result = _controller.Submit(registration);

//            Assert.AreEqual(RegistrationStatus.Error, result.Status);
//            Assert.IsTrue(result.Errors.Any(e => e.Value == "Registrations from outside the state are not allowed."));
//        }

//        [TestMethod]
//        public void Submit_WhenAddressVerified_UpdatesLogonUserId()
//        {
//            var registration = new Registration { AddressVerificationStatus = AddressVerificationStatus.Valid };

//            var result = _controller.Submit(registration);

//            Assert.AreEqual(1, registration.LogonUserID);
//        }

//        [TestMethod]
//        public void Submit_WhenRegistrationIncludesEmail_SendsWelcomeEmail()
//        {
//            var registration = new Registration
//            {
//                EmailAddress = "test@example.com",
//                IsECard = false
//            };

//            var result = _controller.Submit(registration);

//            _mockEmailSender.Verify(e =>
//                e.Send(
//                    It.Is<string>(to => to == "test@example.com"),
//                    It.Is<string>(from => from.Contains("noreply")),
//                    It.Is<string>(replyTo => !string.IsNullOrWhiteSpace(replyTo)),
//                    It.Is<string>(subject => subject == "Welcome!"),
//                    It.Is<string>(htmlBody => htmlBody.Contains("<h1>Welcome</h1>")),
//                    It.Is<string>(textBody => textBody.Contains("Welcome to the service!"))),
//                Times.Once);

//            Assert.AreEqual(RegistrationStatus.Success, result.Status);
//        }

//        [TestMethod]
//        public void Submit_WhenEmailSenderFails_ReturnsErrorStatus()
//        {
//            _mockEmailSender
//                .Setup(e => e.Send(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
//                .Returns(false);

//            var registration = new Registration
//            {
//                EmailAddress = "test@example.com",
//                IsECard = false
//            };

//            var result = _controller.Submit(registration);

//            Assert.AreEqual(RegistrationStatus.Error, result.Status);
//        }

//        [TestMethod]
//        public void Submit_WhenNoEmailProvided_DoesNotSendWelcomeEmail()
//        {
//            var registration = new Registration
//            {
//                EmailAddress = null,
//                IsECard = false
//            };

//            var result = _controller.Submit(registration);

//            _mockEmailSender.Verify(e => e.Send(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);

//            Assert.AreEqual(RegistrationStatus.Success, result.Status);
//        }

//        [TestMethod]
//        public void Submit_WhenOutOfStateWithFee_IncludesFeeMessage()
//        {
//            _mockSettings.SetupGet(s => s.ChargeForOutOfStateRegistrations).Returns(true);
//            _mockSettings.SetupGet(s => s.RegistrationChargeText).Returns("Out-of-state registrations incur a $10 fee.");

//            var registration = new Registration { State = "CA" };

//            var result = _controller.Submit(registration);

//            Assert.AreEqual(RegistrationStatus.Success, result.Status);
//            Assert.IsTrue(result.Message.Contains("Out-of-state registrations incur a $10 fee."));
//        }

//        [TestMethod]
//        public void Submit_WhenDisableBranchIsTrue_ReturnsEmptyRegistrationAttempt()
//        {
//            _mockSettings.SetupGet(s => s.DisableBranch).Returns(true);
//            var registration = new Registration();

//            var result = _controller.Submit(registration);

//            Assert.AreEqual(RegistrationStatus.Error, result.Status);
//        }

//        [TestMethod]
//        public void Submit_WhenEmailAddressIsWhitespace_SetsEmailToNull()
//        {
//            var registration = new Registration { EmailAddress = "   " };

//            _controller.Submit(registration);

//            Assert.IsNull(registration.EmailAddress);
//        }

//        [TestMethod]
//        public void Submit_WhenAltEmailAddressIsWhitespace_SetsAltEmailToNull()
//        {
//            var registration = new Registration { AltEmailAddress = "   " };

//            _controller.Submit(registration);

//            Assert.IsNull(registration.AltEmailAddress);
//        }

//        [TestMethod]
//        public void Submit_WhenNoModelErrors_SendsWelcomeEmail()
//        {
//            var registration = new Registration { EmailAddress = "test@example.com" };

//            _controller.Submit(registration);

//            _mockEmailSender.Verify(e =>
//                e.Send(
//                    It.Is<string>(to => to == "test@example.com"),
//                    It.IsAny<string>(),
//                    It.IsAny<string>(),
//                    It.IsAny<string>(),
//                    It.IsAny<string>(),
//                    It.IsAny<string>()),
//                Times.Once);
//        }

//        [TestMethod]
//        public void Submit_WhenOutOfStateAndBlocked_ReturnsErrorStatus()
//        {
//            _mockSettings.SetupGet(s => s.BlockOutOfStateRegistrations).Returns(true);
//            var registration = new Registration { State = "NY" };

//            var result = _controller.Submit(registration);

//            Assert.AreEqual(RegistrationStatus.Error, result.Status);
//            Assert.IsTrue(result.Errors.Any(e => e.Message == "Registrations from outside the state are not allowed."));
//        }

//        [TestMethod]
//        public void Submit_WhenAddressVerificationFails_UsesDefaultLogonUserId()
//        {
//            var registration = new Registration
//            {
//                AddressVerificationStatus = AddressVerificationStatus.Invalid
//            };
//            _mockSettings.SetupGet(s => s.RegistrationLogonUserId).Returns(999);

//            _controller.Submit(registration);

//            Assert.AreEqual(999, registration.LogonUserID);
//        }

//        [TestMethod]
//        public void Submit_WhenPapiErrorOccurs_ReturnsErrorWithErrorMessage()
//        {
//            var registration = new Registration();
//            _mockPolaris
//                .Setup(p => p.PatronRegistrationCreate(It.IsAny<PatronRegistrationParams>()))
//                .Returns(new PapiResponse { Data = new PapiResponseData { PAPIErrorCode = -1, ErrorMessage = "Some error occurred." } });

//            var result = _controller.Submit(registration);

//            Assert.AreEqual(RegistrationStatus.Error, result.Status);
//            Assert.AreEqual("-1", result.Message);
//        }

//        [TestMethod]
//        public void Submit_WhenStateIsNotOHAndFeeIsEnabled_IncludesFeeMessage()
//        {
//            _mockSettings.SetupGet(s => s.ChargeForOutOfStateRegistrations).Returns(true);
//            _mockSettings.SetupGet(s => s.RegistrationChargeText).Returns("Out-of-state registration fee applies.");

//            var registration = new Registration { State = "NY" };

//            var result = _controller.Submit(registration);

//            Assert.IsTrue(result.Message.Contains("Out-of-state registration fee applies."));
//        }

//        [TestMethod]
//        public void Submit_WhenExpirationDateInSettings_UsesExpirationDate()
//        {
//            _mockSettings.SetupGet(s => s.ExpirationDate).Returns(DateTime.Parse("2025-01-01"));
//            var registration = new Registration();

//            _controller.Submit(registration);

//            Assert.AreEqual(DateTime.Parse("2025-01-01"), registration.ExpirationDate);
//        }

//        [TestMethod]
//        public void Submit_WhenExpirationDateYearsInSettings_AddsYearsToExpirationDate()
//        {
//            _mockSettings.SetupGet(s => s.ExpirationDateYears).Returns(5);
//            var registration = new Registration();

//            _controller.Submit(registration);

//            Assert.AreEqual(DateTime.Now.AddYears(5).Date, registration.ExpirationDate?.Date);
//        }

//        [TestMethod]
//        public void Submit_WhenSchoolDeliveryRequested_AddsNoteForSchoolDelivery()
//        {
//            var registration = new Registration
//            {
//                DeliverCardToSchool = true,
//                IsStudent = true,
//                User1 = "Some school"
//            };

//            _controller.Submit(registration);

//            _mockPolaris.Verify(p => p.UpdatePatronNotesData(It.IsAny<string>(), "School Delivery Requested", null, UpdateNoteMode.Prepend, null), Times.Once);
//        }

//        [TestMethod]
//        public void Submit_WhenDuplicatePapiErrorBypassed_ReattemptsRegistration()
//        {
//            var registration = new Registration();
//            _mockSettings.SetupGet(s => s.PerformPapiDupeBypass).Returns(true);
//            _mockPolaris
//                .SetupSequence(p => p.PatronRegistrationCreate(It.IsAny<PatronRegistrationParams>()))
//                .Returns(new RestResponse<PatronRegistrationCreateResult> { Data = new PatronRegistrationCreateResult { PAPIErrorCode = -3528 } }) // Initial failure
//                .Returns(new RestResponse<PatronRegistrationCreateResult> { Data = new PatronRegistrationCreateResult { PAPIErrorCode = 0, Barcode = "12345" } }); // Retry success

//            var result = _controller.Submit(registration);

//            Assert.AreEqual(RegistrationStatus.Success, result.Status);
//            Assert.AreEqual("12345", registration.Barcode);
//        }

//    }
//}