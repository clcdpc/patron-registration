using Clc.Melissa;
using Clc.PatronRegistration.Administration;
using Clc.PatronRegistration.Configuration;
using Clc.PatronRegistration.Data;
using Clc.PatronRegistration.Web.Controllers;
using Clc.PatronRegistration.Web.Settings;
using Clc.Polaris.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using System.Reflection;

namespace Clc.PatronRegistration.Tests;

[TestClass]
public sealed class AgeBlockControllerTests
{
    private static readonly DateTime UnderageBirthdate = DateTime.Today.AddYears(-18).AddDays(1);

    [TestMethod]
    public void LiveAgeBlockCheck_UsesLiveSettings()
    {
        var settings = Settings(true, "Live block");
        var controller = new RegistrationController(
            Mock.Of<IPapiClient>(), Mock.Of<IMelissaRestClient>(), Mock.Of<IDbHelper>(), settings.Object,
            Mock.Of<IEmailSender>());

        var result = controller.AgeBlockCheck(UnderageBirthdate);
        var decision = (AgeBlockResult)((JsonResult)result).Value!;

        Assert.IsTrue(decision.IsBlocked);
        Assert.AreEqual("Live block", decision.Message);
        Assert.IsNotNull(typeof(RegistrationController).GetMethod(nameof(RegistrationController.AgeBlockCheck))!
            .GetCustomAttribute<HttpPostAttribute>());
        Assert.IsNotNull(typeof(RegistrationController).GetMethod(nameof(RegistrationController.AgeBlockCheck))!
            .GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
    }

    [TestMethod]
    public void PreviewAgeBlockCheck_UsesDraftSettingsInsteadOfLiveSettings()
    {
        var draft = new SettingDraft(7, 3, string.Empty, 0, DraftStatus.Active,
        [
            new SettingMutation("enable_age_block", DraftOperation.Upsert, "true"),
            new SettingMutation("age_block_text", DraftOperation.Upsert, "Draft block")
        ]);
        var context = new PreviewRequestContext(
            new PreviewLinkRecord(9, 7, new byte[32], false, null, null, 3, string.Empty, "Active", 3),
            draft,
            new PreviewSettingProvider(draft, 3, new TestCache(), 1));
        var controller = CreatePreviewController(context);

        var result = controller.AgeBlockCheck("preview-token", UnderageBirthdate);
        var decision = (AgeBlockResult)((JsonResult)result).Value!;

        Assert.IsTrue(decision.IsBlocked);
        Assert.AreEqual("Draft block", decision.Message);
        var action = typeof(PreviewController).GetMethod(nameof(PreviewController.AgeBlockCheck))!;
        Assert.AreEqual("{token}/age-block-check", action.GetCustomAttribute<HttpPostAttribute>()!.Template);
        Assert.IsNotNull(action.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
    }

    [TestMethod]
    public void PreviewAgeBlockCheck_InvalidContext_ReturnsNotFound()
    {
        var controller = CreatePreviewController(null);

        var result = controller.AgeBlockCheck("invalid-token", UnderageBirthdate);

        Assert.IsInstanceOfType<NotFoundResult>(result);
    }

    private static Mock<ISettingProvider> Settings(bool enabled, string message)
    {
        var settings = new Mock<ISettingProvider>();
        settings.SetupGet(value => value.EnableAgeBlock).Returns(enabled);
        settings.SetupGet(value => value.AgeBlockText).Returns(message);
        return settings;
    }

    private static PreviewController CreatePreviewController(PreviewRequestContext? context) =>
        new(
            Mock.Of<ISettingsAdministrationRepository>(),
            new PreviewRequestContextAccessor { IsPreviewRequest = true, Current = context },
            new TestCache(),
            Mock.Of<IDbHelper>(),
            Mock.Of<IPapiClient>(),
            Mock.Of<IMelissaRestClient>(),
            Mock.Of<IEmailSender>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
}
