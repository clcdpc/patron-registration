using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Clc.Melissa;
using Clc.Melissa.Models;
using Clc.PatronRegistration.Configuration;
using Clc.PatronRegistration.Data;
using Clc.PatronRegistration.Web.Controllers;
using Clc.PatronRegistration.Web.Settings;
using Clc.Polaris.Api;
using Clc.Polaris.Api.Configuration;
using Clc.Polaris.Api.Models;
using Clc.Rest;
using Clc.Rest.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;

namespace Clc.PatronRegistration.Tests;

public enum LiveCreateOutcome
{
    NotAttempted,
    ConfirmedCreated,
    Rejected,
    Ambiguous
}

public sealed record LivePublicResult(
    string ReleaseTag,
    string CommitSha,
    string SyntheticToken,
    DateTimeOffset UtcTimestamp,
    LiveCreateOutcome Outcome);

public sealed record LiveDevelopmentConfiguration(
    string Host,
    string AccessId,
    string AccessKey,
    int LibraryId,
    int BranchId,
    int PatronCodeId,
    int LogonUserId,
    string ReleaseTag,
    string CommitSha,
    string ManifestPath)
{
    // This is committed, non-secret proof that the live job is targeting DEVELOPMENT.
    public static readonly IReadOnlySet<string> ApprovedDevelopmentHosts =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "polaris-development.clcohio.org",
            "polaris-dev.clcohio.org"
        };

    public static LiveDevelopmentConfiguration FromEnvironment(
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        string? Get(string name) => environment is null
            ? Environment.GetEnvironmentVariable(name)
            : environment.TryGetValue(name, out var value) ? value : null;

        string Required(string name) => string.IsNullOrWhiteSpace(Get(name))
            ? throw new InvalidOperationException($"Missing required live configuration {name}; no patron mutation was attempted.")
            : Get(name)!;

        int Positive(string name) => int.TryParse(Required(name), out var value) && value > 0
            ? value
            : throw new InvalidOperationException($"Live configuration {name} must be positive; no patron mutation was attempted.");

        if (!string.Equals(Get("PATRON_REGISTRATION_LIVE_TESTS"), "true", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "LiveDevelopment requires PATRON_REGISTRATION_LIVE_TESTS=true; no patron mutation was attempted.");
        }

        var host = Required("PATRON_REGISTRATION_PAPI_HOST");
        if (!IsApprovedDevelopmentHost(host))
        {
            throw new InvalidOperationException(
                "The configured Polaris endpoint is not the committed DEVELOPMENT allowlist; no patron mutation was attempted.");
        }

        var releaseTag = Get("GITHUB_REF_NAME");
        var commitSha = Get("PATRON_REGISTRATION_LIVE_COMMIT_SHA");
        if (string.IsNullOrWhiteSpace(commitSha))
        {
            commitSha = Get("GITHUB_SHA");
        }

        return new LiveDevelopmentConfiguration(
            host,
            Required("PATRON_REGISTRATION_PAPI_ACCESS_ID"),
            Required("PATRON_REGISTRATION_PAPI_ACCESS_KEY"),
            Positive("PATRON_REGISTRATION_PAPI_LIBRARY_ID"),
            Positive("PATRON_REGISTRATION_PAPI_BRANCH_ID"),
            Positive("PATRON_REGISTRATION_PAPI_PATRON_CODE_ID"),
            Positive("PATRON_REGISTRATION_PAPI_LOGON_USER_ID"),
            string.IsNullOrWhiteSpace(releaseTag) ? "local" : releaseTag!,
            string.IsNullOrWhiteSpace(commitSha) ? "local" : commitSha!,
            Get("PATRON_REGISTRATION_LIVE_MANIFEST") ?? "live-development-result.json");
    }

    public static bool IsApprovedDevelopmentHost(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            !string.Equals(uri.AbsolutePath, "/", StringComparison.Ordinal) ||
            (uri.Port is not (-1 or 443)))
        {
            return false;
        }

        return ApprovedDevelopmentHosts.Contains(uri.Host);
    }
}

[TestClass]
[DoNotParallelize]
public sealed class LiveDevelopmentRegistrationGateTests
{
    [TestMethod]
    [TestCategory("LiveDevelopment")]
    public void DevelopmentPolarisAcceptsRepresentativeRegistration()
    {
        var configuration = LiveDevelopmentConfiguration.FromEnvironment();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(6));
        var realPapi = new PapiClient(new PapiSettings
        {
            AccessId = configuration.AccessId,
            AccessKey = configuration.AccessKey,
            Hostname = configuration.Host
        });

        IRestResponse<PapiResponseCommon>? validation;
        try
        {
            // Endpoint authentication is read-only and must succeed before any
            // registration is prepared or the create boundary is reached.
            validation = realPapi.ApiKeyValidate();
        }
        catch
        {
            Assert.Fail("DEVELOPMENT Polaris ApiKeyValidate could not reach the approved target; no patron mutation was attempted.");
            return;
        }

        if (validation?.Data is null || validation.Data.PAPIErrorCode != 0)
        {
            Assert.Fail("DEVELOPMENT Polaris ApiKeyValidate did not establish a usable target; no patron mutation was attempted.");
            return;
        }

        using var harness = LiveSubmissionHarness.Create(configuration, realPapi);
        var registration = harness.Bind(BuildFormValues(configuration.BranchId, token));
        if (registration is null)
        {
            WriteSafeResult(configuration, token, LiveCreateOutcome.NotAttempted);
            Assert.Fail("The representative registration could not be bound; no patron mutation was attempted.");
            return;
        }

        RegistrationAttempt? preparationFailure;
        try
        {
            preparationFailure = harness.Controller.PrepareSubmission(registration);
        }
        catch
        {
            WriteSafeResult(configuration, token, LiveCreateOutcome.NotAttempted);
            Assert.Fail("Live registration preflight failed; no patron mutation was attempted.");
            return;
        }

        if (preparationFailure is not null)
        {
            WriteSafeResult(configuration, token, LiveCreateOutcome.NotAttempted);
            Assert.Fail("Live registration preflight rejected the representative registration; no patron mutation was attempted.");
            return;
        }

        WritePreCreateBreadcrumb(configuration, token);

        RegistrationAttempt attempt;
        try
        {
            // This is the only call path that can invoke PatronRegistrationCreate.
            // Do not retry it: a transport failure may have created the patron.
            attempt = harness.Controller.ExecutePreparedSubmission(registration);
        }
        catch
        {
            var outcome = ClassifyCreateOutcome(harness.CreateInvoked, harness.CreateCount, harness.LastResponse);
            WriteSafeResult(configuration, token, outcome);
            Assert.Fail($"Live registration ended with {outcome}; inspect the safe result before recovery.");
            return;
        }

        var createOutcome = ClassifyCreateOutcome(harness.CreateInvoked, harness.CreateCount, harness.LastResponse);
        WriteSafeResult(configuration, token, createOutcome);

        Assert.AreEqual(LiveCreateOutcome.ConfirmedCreated, createOutcome,
            "A non-confirmed create result must be investigated rather than retried.");
        Assert.AreEqual(RegistrationStatus.Success, attempt.Status,
            "A confirmed create followed by finalization failure still requires investigation.");
        Assert.AreEqual(1, harness.CreateCount, "The live create boundary must be invoked at most once.");
    }

    [TestMethod]
    public void LiveModeRequiresExplicitOptIn()
    {
        var environment = BaseEnvironment();
        environment["PATRON_REGISTRATION_LIVE_TESTS"] = "false";

        Assert.ThrowsException<InvalidOperationException>(() =>
            LiveDevelopmentConfiguration.FromEnvironment(environment));
    }

    [TestMethod]
    public void DevelopmentTargetRequiresHttpsAndExactCommittedHost()
    {
        Assert.IsTrue(LiveDevelopmentConfiguration.IsApprovedDevelopmentHost(
            "https://polaris-development.clcohio.org/"));
        Assert.IsTrue(LiveDevelopmentConfiguration.IsApprovedDevelopmentHost(
            "https://polaris-dev.clcohio.org"));
        Assert.IsFalse(LiveDevelopmentConfiguration.IsApprovedDevelopmentHost(
            "http://polaris-development.clcohio.org"));
        Assert.IsFalse(LiveDevelopmentConfiguration.IsApprovedDevelopmentHost(
            "https://polaris-production.clcohio.org"));
        Assert.IsFalse(LiveDevelopmentConfiguration.IsApprovedDevelopmentHost(
            "https://polaris-development.clcohio.org.evil.example"));
    }

    [TestMethod]
    public void PreparationCompletesBeforeCreateBoundary()
    {
        var configuration = SyntheticConfiguration();
        var targetPapi = new Mock<IPapiClient>();
        using var harness = LiveSubmissionHarness.Create(configuration, targetPapi.Object);
        var registration = harness.Bind(BuildFormValues(configuration.BranchId, "ABCDEF123456"));

        Assert.IsNotNull(registration);
        Assert.IsNull(harness.Controller.PrepareSubmission(registration!));
        Assert.AreEqual(0, harness.CreateCount);
        Assert.IsFalse(harness.Controller.ModelState.Values.Any(value => value.Errors.Count > 0));
    }

    [TestMethod]
    public void CreateTransportFailureIsAmbiguousAndNeverRetried()
    {
        var configuration = SyntheticConfiguration();
        var targetPapi = new Mock<IPapiClient>();
        targetPapi.Setup(value => value.PatronRegistrationCreate(It.IsAny<PatronRegistrationParams>()))
            .Throws<TimeoutException>();
        using var harness = LiveSubmissionHarness.Create(configuration, targetPapi.Object);
        var registration = Prepare(harness, configuration, "ABCDEF123456");

        Assert.ThrowsException<TimeoutException>(() =>
            harness.Controller.ExecutePreparedSubmission(registration));

        Assert.AreEqual(1, harness.CreateCount);
        Assert.AreEqual(LiveCreateOutcome.Ambiguous,
            ClassifyCreateOutcome(harness.CreateInvoked, harness.CreateCount, harness.LastResponse));
        targetPapi.Verify(value => value.PatronRegistrationCreate(It.IsAny<PatronRegistrationParams>()), Times.Once);
    }

    [TestMethod]
    public void InconclusiveCreateResponseIsAmbiguous()
    {
        var configuration = SyntheticConfiguration();
        var targetPapi = new Mock<IPapiClient>();
        targetPapi.Setup(value => value.PatronRegistrationCreate(It.IsAny<PatronRegistrationParams>()))
            .Returns(new RestResponse<PatronRegistrationCreateResult>
            {
                Data = new PatronRegistrationCreateResult { PAPIErrorCode = 0 }
            });
        using var harness = LiveSubmissionHarness.Create(configuration, targetPapi.Object);
        var registration = Prepare(harness, configuration, "ABCDEF123456");

        _ = harness.Controller.ExecutePreparedSubmission(registration);

        Assert.AreEqual(1, harness.CreateCount);
        Assert.AreEqual(LiveCreateOutcome.Ambiguous,
            ClassifyCreateOutcome(harness.CreateInvoked, harness.CreateCount, harness.LastResponse));
    }

    [TestMethod]
    public void NegativePapiErrorWithIdentifiersIsAmbiguous()
    {
        var response = new RestResponse<PatronRegistrationCreateResult>
        {
            Data = new PatronRegistrationCreateResult
            {
                PAPIErrorCode = -1,
                PatronID = 12345,
                Barcode = "BARCODE-SECRET"
            }
        };

        Assert.AreEqual(LiveCreateOutcome.Ambiguous,
            ClassifyCreateOutcome(createInvoked: true, createCount: 1, response));
    }

    [TestMethod]
    public void SafeResultContainsOnlyInvestigationFields()
    {
        using var result = new TemporaryResult();
        var configuration = SyntheticConfiguration() with { ManifestPath = result.Path };

        WriteSafeResult(configuration, "ABCDEF123456", LiveCreateOutcome.ConfirmedCreated);
        var json = File.ReadAllText(result.Path);

        StringAssert.Contains(json, "v1.2.3");
        StringAssert.Contains(json, "ABCDEF123456");
        StringAssert.Contains(json, "ConfirmedCreated");
        Assert.IsFalse(json.Contains("913579", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("SYNTHETIC-BARCODE-913579", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("access-key", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("Authorization", StringComparison.Ordinal));
    }

    internal static LiveCreateOutcome ClassifyCreateOutcome(
        bool createInvoked,
        int createCount,
        IRestResponse<PatronRegistrationCreateResult>? response)
    {
        if (!createInvoked)
        {
            return LiveCreateOutcome.NotAttempted;
        }

        if (createCount != 1)
        {
            return LiveCreateOutcome.Ambiguous;
        }

        var data = response?.Data;
        if (data is null)
        {
            return LiveCreateOutcome.Ambiguous;
        }

        if (data.PAPIErrorCode >= 0 && data.PatronID > 0 && !string.IsNullOrWhiteSpace(data.Barcode))
        {
            return LiveCreateOutcome.ConfirmedCreated;
        }

        return data.PAPIErrorCode < 0 && data.PatronID <= 0 && string.IsNullOrWhiteSpace(data.Barcode)
            ? LiveCreateOutcome.Rejected
            : LiveCreateOutcome.Ambiguous;
    }

    internal static void WriteSafeResult(
        LiveDevelopmentConfiguration configuration,
        string token,
        LiveCreateOutcome outcome)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(configuration.ManifestPath));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var result = new LivePublicResult(
            configuration.ReleaseTag,
            configuration.CommitSha,
            token,
            DateTimeOffset.UtcNow,
            outcome);
        var options = new JsonSerializerOptions { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        File.WriteAllText(configuration.ManifestPath, JsonSerializer.Serialize(result, options), Encoding.UTF8);
    }

    private static void WritePreCreateBreadcrumb(
        LiveDevelopmentConfiguration configuration,
        string token) =>
        Console.WriteLine(
            $"live-registration pre-create token={token} " +
            $"commit={configuration.CommitSha} tag={configuration.ReleaseTag} timestamp={DateTimeOffset.UtcNow:O}");

    private static Registration Prepare(
        LiveSubmissionHarness harness,
        LiveDevelopmentConfiguration configuration,
        string token)
    {
        var registration = harness.Bind(BuildFormValues(configuration.BranchId, token));
        Assert.IsNotNull(registration);
        Assert.IsNull(harness.Controller.PrepareSubmission(registration!));
        return registration!;
    }

    private static Dictionary<string, string> BuildFormValues(int branchId, string token) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            [nameof(Registration.PatronBranchID)] = branchId.ToString(),
            [nameof(Registration.NameFirst)] = "CI",
            [nameof(Registration.NameLast)] = $"{token}STANDARD",
            [nameof(Registration.Birthdate)] = "1990-01-01",
            [nameof(Registration.DeliveryOptionId)] = "1",
            [nameof(Registration.StreetOne)] = "1 Main St",
            [nameof(Registration.City)] = "Columbus",
            [nameof(Registration.State)] = "OH",
            [nameof(Registration.PostalCode)] = "43215",
            [nameof(Registration.EmailAddress)] = $"{token.ToLowerInvariant()}@example.com",
            [nameof(Registration.Password)] = "Synthetic123",
            [nameof(Registration.Password2)] = "Synthetic123",
            [nameof(Registration.IsStudent)] = bool.FalseString,
            [nameof(Registration.IsTeacher)] = bool.FalseString,
            [nameof(Registration.IsECard)] = bool.FalseString,
            [nameof(Registration.User1)] = string.Empty
        };

    private static Dictionary<string, string?> BaseEnvironment() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["PATRON_REGISTRATION_LIVE_TESTS"] = "true",
            ["PATRON_REGISTRATION_PAPI_HOST"] = "https://polaris-development.clcohio.org",
            ["PATRON_REGISTRATION_PAPI_ACCESS_ID"] = "access-id",
            ["PATRON_REGISTRATION_PAPI_ACCESS_KEY"] = "access-key",
            ["PATRON_REGISTRATION_PAPI_LIBRARY_ID"] = "2",
            ["PATRON_REGISTRATION_PAPI_BRANCH_ID"] = "3",
            ["PATRON_REGISTRATION_PAPI_PATRON_CODE_ID"] = "4",
            ["PATRON_REGISTRATION_PAPI_LOGON_USER_ID"] = "5",
            ["GITHUB_REF_NAME"] = "v1.2.3",
            ["PATRON_REGISTRATION_LIVE_COMMIT_SHA"] = new string('a', 40)
        };

    private static LiveDevelopmentConfiguration SyntheticConfiguration() =>
        new(
            "https://polaris-development.clcohio.org",
            "synthetic-access-id",
            "synthetic-access-key",
            LibraryId: 2,
            BranchId: 3,
            PatronCodeId: 4,
            LogonUserId: 5,
            ReleaseTag: "v1.2.3",
            CommitSha: new string('a', 40),
            ManifestPath: "");

    internal sealed class LiveSubmissionHarness : IDisposable
    {
        private readonly ServiceProvider provider;
        private readonly DefaultHttpContext httpContext;

        private LiveSubmissionHarness(
            ServiceProvider provider,
            DefaultHttpContext httpContext,
            RegistrationController controller)
        {
            this.provider = provider;
            this.httpContext = httpContext;
            Controller = controller;
        }

        internal RegistrationController Controller { get; }
        internal int CreateCount { get; private set; }
        internal bool CreateInvoked => CreateCount > 0;
        internal IRestResponse<PatronRegistrationCreateResult>? LastResponse { get; private set; }

        internal static LiveSubmissionHarness Create(
            LiveDevelopmentConfiguration configuration,
            IPapiClient targetPapi)
        {
            var settings = LiveSettings(configuration);
            var db = new Mock<IDbHelper>();
            db.Setup(value => value.CheckPatronIsDuplicate(
                    It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>()))
                .Returns(false);

            var melissa = new Mock<IMelissaRestClient>();
            melissa.Setup(value => value.PersonatorRequest(It.IsAny<PersonatorRequest>()))
                .Returns(new RestResponse<PersonatorResponse>
                {
                    Data = new PersonatorResponse { Records = [] }
                });
            var melissaFactory = new Mock<IMelissaClientFactory>();
            melissaFactory.Setup(value => value.Create(It.IsAny<string>())).Returns(melissa.Object);

            var email = new Mock<IEmailSender>();
            var emailFactory = new Mock<IEmailSenderFactory>();
            emailFactory.Setup(value => value.Create(It.IsAny<string>())).Returns(email.Object);

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddControllersWithViews().AddApplicationPart(typeof(RegistrationController).Assembly);
            services.AddHttpContextAccessor();
            services.AddSingleton<ISettingProvider>(settings.Object);
            var provider = services.BuildServiceProvider();
            var httpContext = new DefaultHttpContext { RequestServices = provider };
            provider.GetRequiredService<IHttpContextAccessor>().HttpContext = httpContext;

            var forwardingPapi = new Mock<IPapiClient>();
            var scopeResolver = new Mock<IRegistrationScopeResolver>();
            scopeResolver.Setup(value => value.ResolveForSubmission(
                    It.IsAny<HttpContext>(), settings.Object, It.IsAny<int>()))
                .Returns(new RegistrationScopeResolution(true, settings.Object));

            var controller = new RegistrationController(
                forwardingPapi.Object,
                db.Object,
                settings.Object,
                emailFactory.Object,
                melissaFactory.Object,
                provider.GetRequiredService<IObjectModelValidator>(),
                scopeResolver.Object)
            {
                ControllerContext = new ControllerContext { HttpContext = httpContext }
            };

            var harness = new LiveSubmissionHarness(provider, httpContext, controller);
            forwardingPapi.Setup(value => value.PatronRegistrationCreate(It.IsAny<PatronRegistrationParams>()))
                .Returns((PatronRegistrationParams parameters) =>
                {
                    harness.CreateCount++;
                    if (harness.CreateCount > 1)
                    {
                        // Stop a second call before it reaches Polaris. The first call
                        // may already have created a patron, so this is still ambiguous.
                        throw new InvalidOperationException("The live create boundary was invoked more than once.");
                    }

                    harness.LastResponse = targetPapi.PatronRegistrationCreate(parameters);
                    return harness.LastResponse;
                });
            return harness;
        }

        internal Registration? Bind(IReadOnlyDictionary<string, string> values)
        {
            var body = string.Join("&", values.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
            var bytes = Encoding.UTF8.GetBytes(body);
            httpContext.Request.Method = HttpMethods.Post;
            httpContext.Request.ContentType = "application/x-www-form-urlencoded";
            httpContext.Request.ContentLength = bytes.Length;
            httpContext.Request.Body = new MemoryStream(bytes);
            httpContext.Features.Set<IFormFeature>(new FormFeature(httpContext.Request));

            var actionDescriptor = provider.GetRequiredService<IActionDescriptorCollectionProvider>()
                .ActionDescriptors.Items.OfType<ControllerActionDescriptor>()
                .Single(descriptor => descriptor.ControllerTypeInfo == typeof(RegistrationController).GetTypeInfo() &&
                    descriptor.ActionName == nameof(RegistrationController.Submit));
            var parameter = actionDescriptor.Parameters.OfType<ControllerParameterDescriptor>().Single();
            var actionContext = new ActionContext(
                httpContext,
                new Microsoft.AspNetCore.Routing.RouteData(),
                actionDescriptor,
                new ModelStateDictionary());
            Controller.ControllerContext = new ControllerContext(actionContext);

            var metadataProvider = provider.GetRequiredService<IModelMetadataProvider>();
            var metadata = ((DefaultModelMetadataProvider)metadataProvider)
                .GetMetadataForParameter(parameter.ParameterInfo);
            var binder = provider.GetRequiredService<IModelBinderFactory>().CreateBinder(new ModelBinderFactoryContext
            {
                BindingInfo = parameter.BindingInfo,
                Metadata = metadata,
                CacheToken = parameter
            });
            var valueProvider = CompositeValueProvider.CreateAsync(
                actionContext,
                provider.GetRequiredService<IOptions<MvcOptions>>().Value.ValueProviderFactories)
                .GetAwaiter().GetResult();
            var bindingResult = provider.GetRequiredService<ParameterBinder>().BindModelAsync(
                actionContext,
                binder,
                valueProvider,
                parameter,
                metadata,
                null).GetAwaiter().GetResult();

            return bindingResult.IsModelSet &&
                bindingResult.Model is Registration registration &&
                Controller.ModelState.IsValid
                    ? registration
                    : null;
        }

        public void Dispose()
        {
            httpContext.RequestServices = null!;
            provider.Dispose();
        }

        private static Mock<ISettingProvider> LiveSettings(
            LiveDevelopmentConfiguration configuration)
        {
            var settings = new Mock<ISettingProvider>();
            settings.SetupGet(value => value.LibraryId).Returns(configuration.LibraryId);
            settings.SetupGet(value => value.PatronCodeId).Returns(configuration.PatronCodeId);
            settings.SetupGet(value => value.RegistrationLogonUserId).Returns(configuration.LogonUserId);
            settings.SetupGet(value => value.EcardPatronCodeId).Returns(0);
            settings.SetupGet(value => value.StudentPatronCodeId).Returns(0);
            settings.SetupGet(value => value.TeacherPatronCodeId).Returns(0);
            settings.SetupGet(value => value.SchoolInfoFormat).Returns(string.Empty);
            settings.SetupGet(value => value.EcardBarcodePrefix).Returns("CI-");
            settings.SetupGet(value => value.PhoneNumberFormat).Returns("($1) $2-$3");
            settings.SetupGet(value => value.FormCode).Returns(string.Empty);
            settings.SetupGet(value => value.RegistrationText).Returns("Registration complete");
            settings.SetupGet(value => value.DriversLicenseButtonEnabledIpAddresses).Returns(Array.Empty<string>());
            settings.SetupGet(value => value.DisplayECardCheckbox).Returns(true);
            settings.SetupGet(value => value.DisplayPreferredPickupLocation).Returns(false);
            settings.SetupGet(value => value.DisplayResponsiblePersonField).Returns(false);
            settings.SetupGet(value => value.DisableBranch).Returns(false);
            settings.SetupGet(value => value.BypassDupeCheck).Returns(false);
            settings.SetupGet(value => value.PerformPapiDupeBypass).Returns(false);
            settings.SetupGet(value => value.NormalizeToUppercase).Returns(true);
            settings.SetupGet(value => value.UpdatePatronRecordWithMelissaAddress).Returns(false);
            settings.SetupGet(value => value.ExpirationDateYears).Returns(1);
            settings.SetupGet(value => value.MelissaDataApiKey).Returns(string.Empty);
            settings.SetupGet(value => value.PostmarkApiKey).Returns(string.Empty);
            settings.SetupGet(value => value.MailingListRecordSetId).Returns(0);
            settings.SetupGet(value => value.ValidAddressRecordSetId).Returns(0);
            settings.SetupGet(value => value.ValidAddressPlusNameRecordSetId).Returns(0);
            settings.SetupGet(value => value.InvalidAddressRecordSetId).Returns(0);
            settings.SetupGet(value => value.AddToRecordSetId).Returns((int?)null);
            settings.Setup(value => value.GetFieldRequired(It.IsAny<string>())).Returns(false);
            return settings;
        }
    }

    private sealed class TemporaryResult : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"patron-registration-live-{Guid.NewGuid():N}.json");

        public void Dispose()
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
    }
}
