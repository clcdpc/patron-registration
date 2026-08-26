using Clc.Melissa;
using Clc.PatronRegistration.Configuration;
using Clc.PatronRegistration.Data;
using Clc.PatronRegistration.Web.Controllers;
using Clc.Polaris.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using System.Reflection;

namespace Clc.PatronRegistration.Tests;

[TestClass]
public sealed class AgeBlockControllerTests
{
    private static readonly DateTime UnderageBirthdate = DateTime.Today.AddYears(-AgeBlockPolicy.MinimumAge).AddDays(1);

    [TestMethod]
    public void AgeBlockCheck_IsPostOnly()
    {
        var action = typeof(RegistrationController).GetMethod(nameof(RegistrationController.AgeBlockCheck))!;

        Assert.IsNotNull(action.GetCustomAttribute<HttpPostAttribute>());
        Assert.IsNull(action.GetCustomAttribute<HttpGetAttribute>());
    }

    [TestMethod]
    public void EnabledUnderageBirthdate_ReturnsBlockedResultAndConfiguredMessage()
    {
        var controller = CreateController(enabled: true, message: "Live block");

        var result = controller.AgeBlockCheck(UnderageBirthdate);
        var decision = (AgeBlockResult)((JsonResult)result).Value!;

        Assert.IsTrue(decision.IsBlocked);
        Assert.AreEqual("Live block", decision.Message);
    }

    [TestMethod]
    public void DisabledSetting_AllowsUnderageBirthdate()
    {
        var controller = CreateController(enabled: false, message: "Live block");

        var result = controller.AgeBlockCheck(UnderageBirthdate);
        var decision = (AgeBlockResult)((JsonResult)result).Value!;

        Assert.IsFalse(decision.IsBlocked);
    }

    [TestMethod]
    public void Submit_RejectsUnderageBeforeAnyExternalOrPersistentSideEffect()
    {
        var settings = CreateSettings(enabled: true, message: "Underage registrations are not allowed.");
        var db = new Mock<IDbHelper>();
        var papi = new Mock<IPapiClient>();
        var melissa = new Mock<IMelissaRestClient>();
        var email = new Mock<IEmailSender>();
        var controller = new RegistrationController(
            papi.Object, melissa.Object, db.Object, settings.Object, email.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var result = controller.Submit(new Registration(settings.Object) { Birthdate = UnderageBirthdate });

        Assert.AreEqual(RegistrationStatus.Error, result.Status);
        Assert.AreEqual("Underage registrations are not allowed.", result.Message);
        Assert.AreEqual(0, db.Invocations.Count);
        Assert.AreEqual(0, papi.Invocations.Count);
        Assert.AreEqual(0, melissa.Invocations.Count);
        Assert.AreEqual(0, email.Invocations.Count);
    }

    private static RegistrationController CreateController(bool enabled, string message)
    {
        var settings = CreateSettings(enabled, message);

        return new RegistrationController(
            Mock.Of<IPapiClient>(),
            Mock.Of<IMelissaRestClient>(),
            Mock.Of<IDbHelper>(),
            settings.Object,
            Mock.Of<IEmailSender>());
    }

    private static Mock<ISettingProvider> CreateSettings(bool enabled, string message)
    {
        var settings = new Mock<ISettingProvider>();
        settings.SetupGet(value => value.EnableAgeBlock).Returns(enabled);
        settings.SetupGet(value => value.AgeBlockText).Returns(message);
        return settings;
    }
}
