using Clc.PatronRegistration.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Clc.PatronRegistration.Tests;

[TestClass]
public sealed class RegistrationBirthdateValidationTests
{
    [TestMethod]
    public void FutureBirthdate_IsRejectedByRegistrationMetadataValidation()
    {
        var settings = Mock.Of<ISettingProvider>();
        var registration = new Registration(settings) { Birthdate = DateTime.Today.AddDays(1) };

        var modelState = ValidateModel(registration, settings);

        Assert.AreEqual(1, modelState[nameof(Registration.Birthdate)]!.Errors.Count);
        Assert.AreEqual("Please enter a valid birth date.",
            modelState[nameof(Registration.Birthdate)]!.Errors.Single().ErrorMessage);
    }

    [TestMethod]
    public void TodayBirthdate_IsNotRejectedAsFuture()
    {
        var settings = Mock.Of<ISettingProvider>();
        var registration = new Registration(settings) { Birthdate = DateTime.Today.AddHours(23) };

        var modelState = ValidateModel(registration, settings);

        Assert.IsFalse(modelState.ContainsKey(nameof(Registration.Birthdate)) &&
                       modelState[nameof(Registration.Birthdate)]!.Errors.Count > 0);
    }

    private static ModelStateDictionary ValidateModel(Registration registration, ISettingProvider settings)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(settings);
        services.AddControllersWithViews();
        using var provider = services.BuildServiceProvider();
        var modelState = new ModelStateDictionary();
        var httpContext = new DefaultHttpContext { RequestServices = provider };
        new HttpContextAccessor().HttpContext = httpContext;
        var actionContext = new ActionContext(httpContext,
            new Microsoft.AspNetCore.Routing.RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor(), modelState);

        provider.GetRequiredService<IObjectModelValidator>().Validate(actionContext, null, string.Empty, registration);
        new HttpContextAccessor().HttpContext = null;

        return modelState;
    }
}
