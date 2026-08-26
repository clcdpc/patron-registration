using Clc.Melissa;
using Clc.Melissa.Models;
using Clc.PatronRegistration.Configuration;
using Clc.PatronRegistration.Data;
using Clc.Polaris.Api;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Moq;

namespace Clc.PatronRegistration.Tests;

[TestClass]
public sealed class RegistrationAgeBlockTests
{
    [TestMethod]
    public void DirectCreateRegistration_RejectsUnderageBeforeAnySideEffect()
    {
        var settings = Settings(enabled: true, message: "Underage registrations are not allowed.");
        var db = new Mock<IDbHelper>();
        var papi = new Mock<IPapiClient>();
        var melissa = new Mock<IMelissaRestClient>();
        var email = new Mock<IEmailSender>();
        var registration = CreateRegistrationModel(UnderageBirthdate());

        var result = registration.CreateRegistration("127.0.0.1", new ModelStateDictionary(), settings.Object,
            db.Object, papi.Object, melissa.Object, email.Object);

        Assert.AreEqual(RegistrationStatus.Error, result.Status);
        Assert.AreEqual("Underage registrations are not allowed.", result.Message);
        AssertNoCalls(db, papi, melissa, email);
    }

    [TestMethod]
    public void ExactlyEighteen_IsNotRejectedByServerAgePolicy()
    {
        var settings = Settings(enabled: true, message: "Underage registrations are not allowed.");
        settings.SetupGet(value => value.BypassDupeCheck).Returns(true);
        var melissa = new Mock<IMelissaRestClient>();
        melissa.Setup(value => value.PersonatorRequest(It.IsAny<PersonatorRequestRecord>()))
            .Throws(new InvalidOperationException("registration reached the existing workflow"));
        var registration = CreateRegistrationModel(DateTime.Today.AddYears(-AgeBlockPolicy.MinimumAge).AddHours(12));

        var exception = Assert.ThrowsException<InvalidOperationException>(() => registration.CreateRegistration(
            "127.0.0.1", new ModelStateDictionary(), settings.Object, new Mock<IDbHelper>().Object,
            Mock.Of<IPapiClient>(), melissa.Object, Mock.Of<IEmailSender>()));

        Assert.AreEqual("registration reached the existing workflow", exception.Message);
        melissa.Verify(value => value.PersonatorRequest(It.IsAny<PersonatorRequestRecord>()), Times.Once);
    }

    [TestMethod]
    public void DisabledAgePolicy_AllowsUnderageIntoExistingWorkflow()
    {
        var settings = Settings(enabled: false, message: "Underage registrations are not allowed.");
        settings.SetupGet(value => value.BypassDupeCheck).Returns(true);
        var melissa = new Mock<IMelissaRestClient>();
        melissa.Setup(value => value.PersonatorRequest(It.IsAny<PersonatorRequestRecord>()))
            .Throws(new InvalidOperationException("registration reached the existing workflow"));
        var registration = CreateRegistrationModel(UnderageBirthdate());

        var exception = Assert.ThrowsException<InvalidOperationException>(() => registration.CreateRegistration(
            "127.0.0.1", new ModelStateDictionary(), settings.Object, new Mock<IDbHelper>().Object,
            Mock.Of<IPapiClient>(), melissa.Object, Mock.Of<IEmailSender>()));

        Assert.AreEqual("registration reached the existing workflow", exception.Message);
        melissa.Verify(value => value.PersonatorRequest(It.IsAny<PersonatorRequestRecord>()), Times.Once);
    }

    [TestMethod]
    public void FutureBirthdate_FailsValidationInsteadOfAgeBlockingBeforeSideEffects()
    {
        var settings = Settings(enabled: true, message: "Underage registrations are not allowed.");
        var db = new Mock<IDbHelper>();
        var papi = new Mock<IPapiClient>();
        var melissa = new Mock<IMelissaRestClient>();
        var email = new Mock<IEmailSender>();
        var registration = CreateRegistrationModel(DateTime.Today.AddDays(1));

        var result = registration.CreateRegistration("127.0.0.1", new ModelStateDictionary(), settings.Object,
            db.Object, papi.Object, melissa.Object, email.Object);

        Assert.AreEqual(RegistrationStatus.Error, result.Status);
        Assert.AreEqual("Please correct the validation errors and try again.", result.Message);
        Assert.AreEqual("Please enter a valid birth date.", result.Errors.Single(value => value.Key == nameof(Registration.Birthdate)).Value);
        Assert.IsFalse(result.Message.Contains("Underage", StringComparison.Ordinal));
        AssertNoCalls(db, papi, melissa, email);
    }

    [TestMethod]
    public void InvalidModelState_IsRejectedBeforeAgePolicyWorkflowSideEffects()
    {
        var settings = Settings(enabled: true, message: "Underage registrations are not allowed.");
        var db = new Mock<IDbHelper>();
        var papi = new Mock<IPapiClient>();
        var melissa = new Mock<IMelissaRestClient>();
        var email = new Mock<IEmailSender>();
        var modelState = new ModelStateDictionary();
        modelState.AddModelError("NameFirst", "Name is required.");

        var result = CreateRegistrationModel(UnderageBirthdate()).CreateRegistration("127.0.0.1", modelState, settings.Object,
            db.Object, papi.Object, melissa.Object, email.Object);

        Assert.AreEqual(RegistrationStatus.Error, result.Status);
        Assert.AreEqual("Name is required.", result.Errors.Single().Value);
        AssertNoCalls(db, papi, melissa, email);
    }

    private static Mock<ISettingProvider> Settings(bool enabled, string message)
    {
        var settings = new Mock<ISettingProvider>();
        settings.SetupGet(value => value.EnableAgeBlock).Returns(enabled);
        settings.SetupGet(value => value.AgeBlockText).Returns(message);
        settings.SetupGet(value => value.BypassDupeCheck).Returns(false);
        settings.SetupGet(value => value.ForceEcardRemotely).Returns(false);
        settings.SetupGet(value => value.DriversLicenseButtonEnabledIpAddresses).Returns(Array.Empty<string>());
        return settings;
    }

    private static Registration CreateRegistrationModel(DateTime birthdate) => new(Mock.Of<ISettingProvider>())
    {
        Birthdate = birthdate
    };

    private static DateTime UnderageBirthdate() => DateTime.Today.AddYears(-AgeBlockPolicy.MinimumAge).AddDays(1);

    private static void AssertNoCalls(params Mock[] mocks)
    {
        foreach (var mock in mocks)
        {
            Assert.AreEqual(0, mock.Invocations.Count);
        }
    }
}
