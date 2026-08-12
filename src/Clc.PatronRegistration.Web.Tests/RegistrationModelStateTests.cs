using Clc.Melissa;
using Clc.PatronRegistration.Configuration;
using Clc.PatronRegistration.Data;
using Clc.Polaris.Api;
using Clc.Polaris.Api.Models;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Moq;
using Clc.Melissa.Models;

namespace Clc.PatronRegistration.Tests;

[TestClass]
public class RegistrationModelStateTests
{
    [DataTestMethod]
    [DataRow("PhoneVoice1", "Phone is dynamically required.")]
    [DataRow("EmailAddress", "The email address is invalid.")]
    [DataRow("Password", "The PIN format is invalid.")]
    [DataRow("Password2", "Passwords must match.")]
    public void InvalidMvcModelState_BlocksAllRegistrationSideEffects(string field, string message)
    {
        var settings = new Mock<ISettingProvider>();
        var db = new Mock<IDbHelper>();
        var papi = new Mock<IPapiClient>();
        var melissa = new Mock<IMelissaRestClient>();
        var email = new Mock<IEmailSender>();
        var modelState = new ModelStateDictionary();
        modelState.AddModelError(field, message);
        var registration = new Registration(settings.Object);

        var result = registration.CreateRegistration("127.0.0.1", modelState, settings.Object, db.Object, papi.Object, melissa.Object, email.Object);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(field, result.Errors.Single().Key);
        Assert.AreEqual(message, result.Errors.Single().Value);
        Assert.AreEqual(0, papi.Invocations.Count);
        Assert.AreEqual(0, melissa.Invocations.Count);
        Assert.AreEqual(0, email.Invocations.Count);
        Assert.AreEqual(0, db.Invocations.Count);
    }

    [TestMethod]
    public void DuplicateModelStateErrors_AreReturnedOnlyOnce()
    {
        var modelState = new ModelStateDictionary();
        modelState.AddModelError("NameFirst", "Name is required.");
        modelState.AddModelError("NameFirst", "Name is required.");

        var errors = RegistrationAttempt.ErrorsFromModelState(modelState);

        Assert.AreEqual(1, errors.Count);
        Assert.AreEqual("NameFirst", errors[0].Key);
    }

    [TestMethod]
    public void ValidMvcModelState_ContinuesIntoExistingWorkflow()
    {
        var settings = new Mock<ISettingProvider>();
        settings.SetupGet(value => value.PhoneNumberFormat).Returns("($1) $2-$3");
        settings.SetupGet(value => value.FormCode).Returns(string.Empty);
        var db = new Mock<IDbHelper>();
        db.Setup(value => value.CheckPatronIsDuplicate(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>())).Returns(false);
        var melissa = new Mock<IMelissaRestClient>();
        melissa.Setup(value => value.PersonatorRequest(It.IsAny<PersonatorRequestRecord>())).Throws(new InvalidOperationException("workflow reached"));
        var registration = new Registration(settings.Object)
        {
            NameFirst = "Jane",
            NameLast = "Doe",
            Birthdate = new DateTime(2000, 1, 1),
            State = "OH",
            StreetOne = "1 Main St",
            City = "Columbus",
            PostalCode = "43215"
        };

        var exception = Assert.ThrowsException<InvalidOperationException>(() => registration.CreateRegistration(
            "127.0.0.1", new ModelStateDictionary(), settings.Object, db.Object, Mock.Of<IPapiClient>(), melissa.Object, Mock.Of<IEmailSender>()));

        Assert.AreEqual("workflow reached", exception.Message);
        melissa.Verify(value => value.PersonatorRequest(It.IsAny<PersonatorRequestRecord>()), Times.Once);
    }

    [DataTestMethod]
    [DataRow(IdentifierSettingState.Missing)]
    [DataRow(IdentifierSettingState.Zero)]
    [DataRow(IdentifierSettingState.Negative)]
    [DataRow(IdentifierSettingState.Malformed)]
    public void InvalidRequiredLogonIdentifier_BlocksPatronCreation(IdentifierSettingState state)
    {
        var settings = new Mock<ISettingProvider>();
        settings.SetupGet(value => value.PhoneNumberFormat).Returns("($1) $2-$3");
        settings.SetupGet(value => value.FormCode).Returns(string.Empty);
        settings.SetupGet(value => value.PatronCodeId).Returns(1);
        settings.SetupGet(value => value.RegistrationLogonUserId).Returns(0);
        settings.As<IIdentifierSettingStateProvider>()
            .Setup(value => value.GetIdentifierState("patron_code_id"))
            .Returns(new IdentifierSettingResult(IdentifierSettingState.Positive, 1));
        settings.As<IIdentifierSettingStateProvider>()
            .Setup(value => value.GetIdentifierState("registration_logon_user_id"))
            .Returns(new IdentifierSettingResult(state, state == IdentifierSettingState.Negative ? -1 : 0));
        var db = new Mock<IDbHelper>();
        db.Setup(value => value.CheckPatronIsDuplicate(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>())).Returns(false);
        var papi = new Mock<IPapiClient>();
        var registration = new Registration(settings.Object)
        {
            NameFirst = "Jane",
            NameLast = "Doe",
            Birthdate = new DateTime(2000, 1, 1),
            State = "OH",
            StreetOne = "1 Main St",
            City = "Columbus",
            PostalCode = "43215"
        };

        var result = registration.CreateRegistration("127.0.0.1", new ModelStateDictionary(), settings.Object,
            db.Object, papi.Object, Mock.Of<IMelissaRestClient>(), Mock.Of<IEmailSender>());

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Message, "configuration");
        papi.Verify(value => value.PatronRegistrationCreate(It.IsAny<PatronRegistrationParams>()), Times.Never);
    }
}
