using Clc.PatronRegistration.Configuration;
using Clc.Polaris.Api.Models;
using Clc.Polaris.Api;
using Clc.PatronRegistration.Data;
using Clc.Melissa;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Moq;

namespace Clc.PatronRegistration.Tests;

[TestClass]
public sealed class ExpirationDateYearsTests
{
    [DataTestMethod]
    [DataRow(null, BoundedIntegerSettingState.Unconfigured, null)]
    [DataRow("", BoundedIntegerSettingState.Unconfigured, null)]
    [DataRow("0", BoundedIntegerSettingState.Valid, 0)]
    [DataRow("1", BoundedIntegerSettingState.Valid, 1)]
    [DataRow("100", BoundedIntegerSettingState.Valid, 100)]
    [DataRow("-1", BoundedIntegerSettingState.Invalid, null)]
    [DataRow("101", BoundedIntegerSettingState.Invalid, null)]
    [DataRow("9999", BoundedIntegerSettingState.Invalid, null)]
    [DataRow("not-a-number", BoundedIntegerSettingState.Invalid, null)]
    [DataRow("   ", BoundedIntegerSettingState.Invalid, null)]
    public void Parser_PreservesPersistedState(string? raw, BoundedIntegerSettingState state, int? value)
    {
        var result = ExpirationDateYearsSettingParser.Parse(raw);
        Assert.AreEqual(state, result.State);
        Assert.AreEqual(value, result.Value);
    }

    [DataTestMethod]
    [DataRow("-1")]
    [DataRow("101")]
    [DataRow("9999")]
    [DataRow("not-a-number")]
    [DataRow("   ")]
    public void InvalidPersistedValue_FailsExpirationCalculation(string raw)
    {
        var settings = new Mock<ISettingProvider>();
        settings.As<IExpirationDateYearsSettingStateProvider>()
            .Setup(provider => provider.GetExpirationDateYearsState())
            .Returns(ExpirationDateYearsSettingParser.Parse(raw));
        var registration = new Registration(settings.Object);

        Assert.IsFalse(registration.HandleExpirationDate(new PatronRegistrationParams()));
        Assert.IsTrue(registration.ModelErrors.Any(error => error.Value == "Registration expiration configuration is invalid."));
    }

    [TestMethod]
    public void MaximumPersistedValue_ProducesRepresentableDate()
    {
        var settings = new Mock<ISettingProvider>();
        settings.As<IExpirationDateYearsSettingStateProvider>()
            .Setup(provider => provider.GetExpirationDateYearsState())
            .Returns(new ExpirationDateYearsSettingResult(BoundedIntegerSettingState.Valid, 100));
        var parameters = new PatronRegistrationParams();

        Assert.IsTrue(new Registration(settings.Object).HandleExpirationDate(parameters));
        Assert.IsTrue(parameters.ExpirationDate.HasValue);
        Assert.IsTrue(parameters.ExpirationDate.Value < DateTime.MaxValue);
    }

    [TestMethod]
    public void InvalidPersistedValue_BlocksPatronCreation()
    {
        var settings = new Mock<ISettingProvider>();
        settings.SetupGet(value => value.BypassDupeCheck).Returns(true);
        settings.SetupGet(value => value.PhoneNumberFormat).Returns("($1) $2-$3");
        settings.SetupGet(value => value.RegistrationLogonUserId).Returns(12);
        settings.As<IIdentifierSettingStateProvider>().Setup(value => value.GetIdentifierState(It.IsAny<string>()))
            .Returns(new IdentifierSettingResult(IdentifierSettingState.Positive, 12));
        settings.As<IExpirationDateYearsSettingStateProvider>().Setup(value => value.GetExpirationDateYearsState())
            .Returns(new ExpirationDateYearsSettingResult(BoundedIntegerSettingState.Invalid, null));
        var papi = new Mock<IPapiClient>();
        var registration = new Registration(settings.Object)
        {
            NameFirst = "Jane", NameLast = "Doe", Birthdate = new DateTime(2000, 1, 1),
            StreetOne = "1 Main St", City = "Columbus", State = "OH", PostalCode = "43215"
        };

        var result = registration.CreateRegistration("127.0.0.1", new ModelStateDictionary(), settings.Object,
            Mock.Of<IDbHelper>(), papi.Object, Mock.Of<IMelissaRestClient>(), Mock.Of<IEmailSender>());

        Assert.AreEqual(RegistrationStatus.Error, result.Status);
        Assert.IsTrue(result.Errors.Any(error => error.Value == "Registration expiration configuration is invalid."));
        papi.Verify(value => value.PatronRegistrationCreate(It.IsAny<PatronRegistrationParams>()), Times.Never);
    }
}
