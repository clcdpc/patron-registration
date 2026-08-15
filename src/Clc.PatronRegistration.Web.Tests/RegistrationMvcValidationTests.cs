using Clc.PatronRegistration.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Clc.PatronRegistration.Tests;

[TestClass]
public sealed class RegistrationMvcValidationTests
{
    [DataTestMethod]
    [DataRow(false, null)]
    [DataRow(false, "")]
    [DataRow(false, "Responsible adult")]
    [DataRow(true, null)]
    [DataRow(true, "")]
    [DataRow(true, "Responsible adult")]
    public void User5_UsesOnlyConfiguredRequiredness(bool required, string? value)
    {
        var modelState = Validate(required, value, out _);

        if (required && string.IsNullOrWhiteSpace(value))
        {
            Assert.AreEqual(1, modelState[nameof(Registration.User5)]!.Errors.Count);
            CollectionAssert.AreEqual(new[] { "Responsible person is required." },
                modelState[nameof(Registration.User5)]!.Errors.Select(error => error.ErrorMessage).ToArray());
        }
        else
            Assert.IsFalse(modelState.ContainsKey(nameof(Registration.User5)) &&
                           modelState[nameof(Registration.User5)]!.Errors.Count > 0);
    }

    [TestMethod]
    public void ExplicitlyRequiredNameFirst_RemainsRequired()
    {
        var modelState = Validate(false, null, out var registration);
        registration.NameFirst = string.Empty;
        modelState = ValidateModel(registration, Mock.Of<ISettingProvider>());

        Assert.IsTrue(modelState[nameof(Registration.NameFirst)]!.Errors.Count > 0);
    }

    [TestMethod]
    public void FutureBirthdate_IsRejectedByServerValidation()
    {
        var settings = Mock.Of<ISettingProvider>();
        var registration = ValidRegistration(settings);
        registration.Birthdate = DateTime.Today.AddDays(1);

        var modelState = ValidateModel(registration, settings);

        Assert.AreEqual(1, modelState[nameof(Registration.Birthdate)]!.Errors.Count);
        Assert.AreEqual("Please enter a valid birth date.",
            modelState[nameof(Registration.Birthdate)]!.Errors.Single().ErrorMessage);
    }

    private static ModelStateDictionary Validate(bool required, string? user5, out Registration registration)
    {
        var settings = new Mock<ISettingProvider>();
        settings.Setup(value => value.GetFieldRequired(nameof(Registration.User5))).Returns(required);
        settings.Setup(value => value.GetFieldLabel(nameof(Registration.User5))).Returns("Responsible person");
        settings.SetupGet(value => value.DisplayResponsiblePersonField).Returns(true);
        registration = ValidRegistration(settings.Object);
        registration.User5 = user5;
        return ValidateModel(registration, settings.Object);
    }

    [DataTestMethod]
    [DataRow(false, true, null)]
    [DataRow(false, true, 12)]
    [DataRow(true, false, null)]
    [DataRow(true, true, null)]
    [DataRow(true, true, 12)]
    public void PreferredPickup_RequirednessDependsOnDisplayApplicability(bool display, bool required, int? value)
    {
        var settings = new Mock<ISettingProvider>();
        settings.Setup(value => value.GetFieldRequired(nameof(Registration.RequestPickupBranchID))).Returns(required);
        settings.Setup(value => value.GetFieldLabel(nameof(Registration.RequestPickupBranchID))).Returns("Preferred pickup location");
        settings.SetupGet(value => value.DisplayPreferredPickupLocation).Returns(display);
        var registration = ValidRegistration(settings.Object);
        registration.RequestPickupBranchID = value;

        var modelState = ValidateModel(registration, settings.Object);
        var errors = modelState.ContainsKey(nameof(Registration.RequestPickupBranchID))
            ? modelState[nameof(Registration.RequestPickupBranchID)]!.Errors.ToArray()
            : Array.Empty<ModelError>();

        if (display && required && value is null)
        {
            CollectionAssert.AreEqual(new[] { "Preferred pickup location is required." },
                errors.Select(error => error.ErrorMessage).ToArray());
        }
        else
        {
            Assert.AreEqual(0, errors.Length);
        }
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

    private static Registration ValidRegistration(ISettingProvider settings) => new(settings)
    {
        NameFirst = "Jane", NameLast = "Doe", Birthdate = new DateTime(2000, 1, 1),
        Password = "1234", Password2 = "1234", StreetOne = "1 Main St", City = "Columbus",
        State = "OH", PostalCode = "43215"
    };
}
