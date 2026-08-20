using System.Reflection;
using System.Text;
using Clc.Melissa;
using Clc.PatronRegistration.Configuration;
using Clc.PatronRegistration.Data;
using Clc.PatronRegistration.Web.Controllers;
using Clc.PatronRegistration.Web.Settings;
using Clc.Polaris.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;

namespace Clc.PatronRegistration.Tests;

[TestClass]
public sealed class RegistrationControllerSubmitIntegrationTests
{
    [TestMethod]
    public async Task SchoolInfoFormat_EmptyHiddenUser1_WhenNeitherStudentNorTeacher_IsValidForUser1()
    {
        using var harness = SubmitHarness.Create(schoolInfoFormat: "uapl");
        var registration = await harness.BindAsync(FormValues(user1: string.Empty));

        var result = harness.Controller.Submit(registration);

        Assert.IsFalse(HasErrors(harness.Controller.ModelState, nameof(Registration.User1)));
        Assert.IsFalse(result.Errors.Any(error => error.Key == nameof(Registration.User1)));
    }

    [DataTestMethod]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public async Task SchoolInfoFormat_EmptyHiddenUser1_WhenStudentOrTeacher_IsInvalid(
        bool isStudent, bool isTeacher)
    {
        using var harness = SubmitHarness.Create(schoolInfoFormat: "uapl");
        var previousGlobal = DbHelper.Global;
        DbHelper.Global = harness.Db.Object;

        try
        {
            var registration = await harness.BindAsync(FormValues(
                user1: string.Empty, isStudent: isStudent, isTeacher: isTeacher));

            var result = harness.Controller.Submit(registration);

            Assert.IsTrue(result.Errors.Any(error => error.Value == "Please select a school"),
                $"Errors: {string.Join(" | ", result.Errors.Select(error => $"{error.Key}={error.Value}"))}");
            Assert.IsFalse(HasErrors(harness.Controller.ModelState, nameof(Registration.User1)));
            harness.Melissa.VerifyNoOtherCalls();
            harness.Papi.VerifyNoOtherCalls();
            harness.Email.VerifyNoOtherCalls();
            harness.Db.Verify(db => db.AddRegistrationHistoryEntry(It.IsAny<RegistrationHistoryEntry>()), Times.Once);
        }
        finally
        {
            DbHelper.Global = previousGlobal;
        }
    }

    [DataTestMethod]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public async Task SchoolInfoFormat_HiddenUser1Value_BindsForStudentAndTeacher(
        bool isStudent, bool isTeacher)
    {
        using var harness = SubmitHarness.Create(schoolInfoFormat: "uapl");
        var registration = await harness.BindAsync(FormValues(
            user1: "Selected school", isStudent: isStudent, isTeacher: isTeacher));

        var result = harness.Controller.Submit(registration);

        Assert.AreEqual("Selected school", registration.User1);
        Assert.IsFalse(HasErrors(harness.Controller.ModelState, nameof(Registration.User1)));
        Assert.IsFalse(result.Errors.Any(error => error.Value == "Please select a school"));
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("not-a-number")]
    public async Task InvalidDeliveryOptionBinding_RemainsInvalidAndBlocksRegistrationSideEffects(
        string deliveryOptionId)
    {
        using var harness = SubmitHarness.Create();
        var previousGlobal = DbHelper.Global;
        DbHelper.Global = harness.Db.Object;

        try
        {
            var registration = await harness.BindAsync(FormValues(deliveryOptionId: deliveryOptionId));
            var bindingErrorsBeforeSubmit = harness.Controller.ModelState[nameof(Registration.DeliveryOptionId)]!
                .Errors.Select(error => error.ErrorMessage).ToArray();

            var result = harness.Controller.Submit(registration);

            Assert.IsFalse(harness.Controller.ModelState.IsValid);
            Assert.IsTrue(HasErrors(harness.Controller.ModelState, nameof(Registration.DeliveryOptionId)));
            CollectionAssert.AreEqual(bindingErrorsBeforeSubmit,
                harness.Controller.ModelState[nameof(Registration.DeliveryOptionId)]!.Errors
                    .Select(error => error.ErrorMessage).ToArray());
            Assert.IsTrue(result.Errors.Any(error => error.Key == nameof(Registration.DeliveryOptionId)));
            harness.MelissaFactory.Verify(factory => factory.Create(It.IsAny<string>()), Times.Never);
            harness.EmailFactory.Verify(factory => factory.Create(It.IsAny<string>()), Times.Never);
            harness.Melissa.VerifyNoOtherCalls();
            harness.Papi.VerifyNoOtherCalls();
            harness.Email.VerifyNoOtherCalls();
            harness.Db.Verify(db => db.AddRegistrationHistoryEntry(It.IsAny<RegistrationHistoryEntry>()), Times.Never);
            harness.Db.VerifyNoOtherCalls();
        }
        finally
        {
            DbHelper.Global = previousGlobal;
        }
    }

    [TestMethod]
    public async Task SelectedBranchValidation_UsesSelectedRequiredFieldAndLabel()
    {
        using var harness = SubmitHarness.Create(
            routeRequiredUser5: false,
            routeUser5Label: "Route responsible person",
            selectedRequiredUser5: true,
            selectedUser5Label: "Selected responsible person",
            displayResponsiblePersonField: true);
        var registration = await harness.BindAsync(FormValues(user5: string.Empty));

        var result = harness.Controller.Submit(registration);

        Assert.AreEqual("Selected responsible person is required.",
            result.Errors.Single(error => error.Key == nameof(Registration.User5)).Value);
        Assert.IsFalse(result.Errors.Any(error => error.Value == "Route responsible person is required."));
        harness.MelissaFactory.Verify(factory => factory.Create(It.IsAny<string>()), Times.Never);
        harness.EmailFactory.Verify(factory => factory.Create(It.IsAny<string>()), Times.Never);
    }

    private static Dictionary<string, string> FormValues(
        string user1 = "",
        bool isStudent = false,
        bool isTeacher = false,
        string deliveryOptionId = "1",
        string user5 = "") => new(StringComparer.OrdinalIgnoreCase)
        {
            [nameof(Registration.PatronBranchID)] = "3",
            [nameof(Registration.NameFirst)] = "Jane",
            [nameof(Registration.NameLast)] = "Doe",
            [nameof(Registration.Birthdate)] = "2000-01-01",
            [nameof(Registration.DeliveryOptionId)] = deliveryOptionId,
            [nameof(Registration.StreetOne)] = "1 Main St",
            [nameof(Registration.City)] = "Columbus",
            [nameof(Registration.State)] = "OH",
            [nameof(Registration.PostalCode)] = "43215",
            [nameof(Registration.EmailAddress)] = "jane@example.com",
            [nameof(Registration.Password)] = "1234",
            [nameof(Registration.Password2)] = "1234",
            [nameof(Registration.User1)] = user1,
            [nameof(Registration.User5)] = user5,
            [nameof(Registration.IsStudent)] = isStudent.ToString(),
            [nameof(Registration.IsTeacher)] = isTeacher.ToString()
        };

    private static bool HasErrors(ModelStateDictionary modelState, string key) =>
        modelState.TryGetValue(key, out var entry) && entry.Errors.Count > 0;

    private sealed class SubmitHarness : IDisposable
    {
        private readonly ServiceProvider provider;
        private readonly DefaultHttpContext httpContext;
        public readonly RegistrationController Controller;
        public readonly Mock<IDbHelper> Db;
        public readonly Mock<IPapiClient> Papi;
        public readonly Mock<IMelissaRestClient> Melissa;
        public readonly Mock<IEmailSender> Email;
        public readonly Mock<IMelissaClientFactory> MelissaFactory;
        public readonly Mock<IEmailSenderFactory> EmailFactory;

        private SubmitHarness(
            ServiceProvider provider,
            DefaultHttpContext httpContext,
            RegistrationController controller,
            Mock<IDbHelper> db,
            Mock<IPapiClient> papi,
            Mock<IMelissaRestClient> melissa,
            Mock<IEmailSender> email,
            Mock<IMelissaClientFactory> melissaFactory,
            Mock<IEmailSenderFactory> emailFactory)
        {
            this.provider = provider;
            this.httpContext = httpContext;
            Controller = controller;
            Db = db;
            Papi = papi;
            Melissa = melissa;
            Email = email;
            MelissaFactory = melissaFactory;
            EmailFactory = emailFactory;
        }

        public static SubmitHarness Create(
            string schoolInfoFormat = "",
            bool routeRequiredUser5 = false,
            string routeUser5Label = "Route responsible person",
            bool selectedRequiredUser5 = false,
            string selectedUser5Label = "Selected responsible person",
            bool displayResponsiblePersonField = false)
        {
            var routeSettings = Settings(
                schoolInfoFormat, routeRequiredUser5, routeUser5Label, displayResponsiblePersonField);
            var selectedSettings = Settings(
                schoolInfoFormat, selectedRequiredUser5, selectedUser5Label, displayResponsiblePersonField);
            var db = new Mock<IDbHelper>();
            db.Setup(value => value.CheckPatronIsDuplicate(
                    It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>()))
                .Returns(false);
            var papi = new Mock<IPapiClient>();
            var melissa = new Mock<IMelissaRestClient>();
            var email = new Mock<IEmailSender>();
            var melissaFactory = new Mock<IMelissaClientFactory>();
            var emailFactory = new Mock<IEmailSenderFactory>();
            melissaFactory.Setup(factory => factory.Create(It.IsAny<string>())).Returns(melissa.Object);
            emailFactory.Setup(factory => factory.Create(It.IsAny<string>())).Returns(email.Object);

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddControllersWithViews()
                .AddApplicationPart(typeof(RegistrationController).Assembly);
            services.AddHttpContextAccessor();
            services.AddSingleton<ISettingProvider>(routeSettings.Object);
            var provider = services.BuildServiceProvider();
            var httpContext = new DefaultHttpContext { RequestServices = provider };
            provider.GetRequiredService<IHttpContextAccessor>().HttpContext = httpContext;

            var scopeResolver = new Mock<IRegistrationScopeResolver>();
            scopeResolver.Setup(value => value.ResolveForSubmission(
                    It.IsAny<HttpContext>(), routeSettings.Object, It.IsAny<int>()))
                .Returns(new RegistrationScopeResolution(true, selectedSettings.Object));

            var controller = new RegistrationController(
                papi.Object,
                db.Object,
                routeSettings.Object,
                emailFactory.Object,
                melissaFactory.Object,
                provider.GetRequiredService<IObjectModelValidator>(),
                scopeResolver.Object)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = httpContext
                }
            };

            return new SubmitHarness(provider, httpContext, controller, db, papi, melissa, email,
                melissaFactory, emailFactory);
        }

        public async Task<Registration> BindAsync(IReadOnlyDictionary<string, string> values)
        {
            var body = string.Join("&", values.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
            var bytes = Encoding.UTF8.GetBytes(body);
            httpContext.Request.Method = HttpMethods.Post;
            httpContext.Request.ContentType = "application/x-www-form-urlencoded";
            httpContext.Request.ContentLength = bytes.Length;
            httpContext.Request.Body = new MemoryStream(bytes);

            var actionDescriptor = provider.GetRequiredService<IActionDescriptorCollectionProvider>()
                .ActionDescriptors.Items
                .OfType<ControllerActionDescriptor>()
                .Single(descriptor => descriptor.ControllerTypeInfo == typeof(RegistrationController).GetTypeInfo() &&
                    descriptor.ActionName == nameof(RegistrationController.Submit));
            var parameter = actionDescriptor.Parameters.OfType<ControllerParameterDescriptor>().Single();
            var parameterInfo = parameter.ParameterInfo;
            var modelState = new ModelStateDictionary();
            var actionContext = new ActionContext(
                httpContext,
                new Microsoft.AspNetCore.Routing.RouteData(),
                actionDescriptor,
                modelState);
            Controller.ControllerContext = new ControllerContext(actionContext);

            var metadataProvider = provider.GetRequiredService<IModelMetadataProvider>();
            var metadata = ((DefaultModelMetadataProvider)metadataProvider)
                .GetMetadataForParameter(parameterInfo);
            var binder = provider.GetRequiredService<IModelBinderFactory>().CreateBinder(
                new ModelBinderFactoryContext
                {
                    BindingInfo = parameter.BindingInfo,
                    Metadata = metadata,
                    CacheToken = parameter
                });
            var valueProvider = await CompositeValueProvider.CreateAsync(
                actionContext,
                provider.GetRequiredService<IOptions<MvcOptions>>().Value.ValueProviderFactories);
            var bindingResult = await provider.GetRequiredService<ParameterBinder>().BindModelAsync(
                actionContext,
                binder,
                valueProvider,
                parameter,
                metadata,
                null);

            Assert.IsTrue(bindingResult.IsModelSet);
            var registration = (Registration)bindingResult.Model!;
            Assert.AreEqual("Jane", registration.NameFirst);
            Assert.AreEqual("jane@example.com", registration.EmailAddress);
            if (values[nameof(Registration.DeliveryOptionId)] == "1")
            {
                Assert.IsTrue(Controller.ModelState.IsValid,
                    $"Binding errors: {string.Join(" | ", Controller.ModelState.SelectMany(entry => entry.Value.Errors.Select(error => $"{entry.Key}={error.ErrorMessage}")))}");
            }
            return registration;
        }

        public void Dispose()
        {
            httpContext.RequestServices = null!;
            provider.Dispose();
        }

        private static Mock<ISettingProvider> Settings(
            string schoolInfoFormat,
            bool requiredUser5,
            string user5Label,
            bool displayResponsiblePersonField)
        {
            var settings = new Mock<ISettingProvider>();
            settings.Setup(value => value.GetFieldRequired(nameof(Registration.User5))).Returns(requiredUser5);
            settings.Setup(value => value.GetFieldLabel(nameof(Registration.User5))).Returns(user5Label);
            settings.Setup(value => value.GetFieldRequired(nameof(Registration.RequestPickupBranchID))).Returns(false);
            settings.SetupGet(value => value.DisplayResponsiblePersonField)
                .Returns(displayResponsiblePersonField);
            settings.SetupGet(value => value.DisplayPreferredPickupLocation).Returns(false);
            settings.SetupGet(value => value.DisableBranch).Returns(false);
            settings.SetupGet(value => value.LibraryId).Returns(2);
            settings.SetupGet(value => value.OrganizationId).Returns(3);
            settings.SetupGet(value => value.PhoneNumberFormat).Returns("($1) $2-$3");
            settings.SetupGet(value => value.FormCode).Returns(string.Empty);
            settings.SetupGet(value => value.SchoolInfoFormat).Returns(schoolInfoFormat);
            settings.SetupGet(value => value.MelissaDataApiKey).Returns(string.Empty);
            settings.SetupGet(value => value.PostmarkApiKey).Returns(string.Empty);
            return settings;
        }
    }
}
