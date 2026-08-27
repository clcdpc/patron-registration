using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Reflection;
using System.Text.RegularExpressions;
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

public enum LiveCreateState
{
    NotAttempted,
    Attempting,
    Created,
    Rejected,
    Unknown
}

public enum LiveScenarioState
{
    Pending,
    Running,
    Passed,
    Failed
}

public sealed record LivePublicResult(
    string Scenario,
    string SyntheticToken,
    string Tag,
    string CommitSha,
    string RunId,
    int RunAttempt,
    DateTimeOffset UtcTimestamp,
    LiveCreateState CreateState,
    LiveScenarioState ScenarioState,
    string FailureClass = "");

public sealed record LiveCreateOutcome(
    LiveCreateState State,
    int PatronId = 0,
    string Barcode = "",
    bool FinalizationSucceeded = true)
{
    public static LiveCreateOutcome Created(int patronId, string barcode) =>
        new(LiveCreateState.Created, patronId, barcode);

    public static LiveCreateOutcome CreatedWithFailedFinalization(int patronId, string barcode) =>
        new(LiveCreateState.Created, patronId, barcode, FinalizationSucceeded: false);

    public static LiveCreateOutcome Rejected() => new(LiveCreateState.Rejected);

    public static LiveCreateOutcome Unknown() => new(LiveCreateState.Unknown);
}

public sealed record LiveScenarioDefinition(
    string Name,
    Func<bool> Preflight,
    Func<LivePublicResult, LiveCreateOutcome> Create,
    Func<LiveCreateOutcome, bool> ValidateCreated);

public sealed class LiveAttemptStore : IDisposable
{
    private readonly object gate = new();
    private readonly string? manifestPath;
    private readonly List<LivePublicResult> history = [];

    public LiveAttemptStore(string? manifestPath = null)
    {
        this.manifestPath = string.IsNullOrWhiteSpace(manifestPath) ? null : manifestPath;
    }

    public IReadOnlyList<LivePublicResult> History
    {
        get
        {
            lock (gate)
            {
                return history.ToArray();
            }
        }
    }

    public LivePublicResult Begin(string scenario, string token, LiveIdentity identity)
    {
        var result = new LivePublicResult(
            scenario,
            token,
            identity.Tag,
            identity.CommitSha,
            identity.RunId,
            identity.RunAttempt,
            DateTimeOffset.UtcNow,
            LiveCreateState.Attempting,
            LiveScenarioState.Running);
        Append(result, emitBreadcrumb: true);
        return result;
    }

    public LivePublicResult Transition(
        LivePublicResult prior,
        LiveCreateState createState,
        LiveScenarioState scenarioState,
        string failureClass = "")
    {
        var result = prior with
        {
            UtcTimestamp = DateTimeOffset.UtcNow,
            CreateState = createState,
            ScenarioState = scenarioState,
            FailureClass = failureClass
        };
        Append(result, emitBreadcrumb: false);
        return result;
    }

    private void Append(LivePublicResult result, bool emitBreadcrumb)
    {
        lock (gate)
        {
            history.Add(result);
            PersistLocked();
        }

        if (emitBreadcrumb)
        {
            // This is deliberately limited to the investigation key and safe state.
            Console.WriteLine($"live-registration attempt scenario={result.Scenario} token={result.SyntheticToken} " +
                $"commit={result.CommitSha} tag={result.Tag} state={result.CreateState}");
        }
    }

    private void PersistLocked()
    {
        if (manifestPath is null)
        {
            return;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(manifestPath));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = manifestPath + ".tmp";
        var options = new JsonSerializerOptions { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        var json = JsonSerializer.Serialize(history, options);
        File.WriteAllText(temporaryPath, json, Encoding.UTF8);
        File.Move(temporaryPath, manifestPath, overwrite: true);
    }

    public void Dispose()
    {
        // The manifest is written on every transition. There is no deferred state to flush.
    }
}

public sealed record LiveIdentity(
    string Tag,
    string CommitSha,
    string RunId,
    int RunAttempt,
    string InvocationId,
    string RecoveryNonce = "");

public static class LiveIdentityFactory
{
    public static LiveIdentity FromEnvironment(IReadOnlyDictionary<string, string?>? environment = null)
    {
        string Get(string name) => environment is null
            ? Environment.GetEnvironmentVariable(name) ?? ""
            : environment.TryGetValue(name, out var value) ? value ?? "" : "";

        var tag = Get("GITHUB_REF_NAME");
        var commit = Get("PATRON_REGISTRATION_LIVE_COMMIT_SHA");
        if (string.IsNullOrWhiteSpace(commit))
        {
            commit = Get("GITHUB_SHA");
        }
        var runId = Get("GITHUB_RUN_ID");
        var runAttemptText = Get("GITHUB_RUN_ATTEMPT");
        var recoveryNonce = Get("PATRON_REGISTRATION_RECOVERY_NONCE");
        _ = int.TryParse(runAttemptText, out var runAttempt);
        if (runAttempt <= 0)
        {
            runAttempt = 1;
        }

        // Local runs have no release identity. The invocation id differentiates them;
        // GitHub release runs derive their logical identity from tag, commit, and scenario.
        var invocationId = Get("PATRON_REGISTRATION_INVOCATION_ID");
        if (string.IsNullOrWhiteSpace(invocationId))
        {
            invocationId = Guid.NewGuid().ToString("N");
        }

        return new LiveIdentity(
            string.IsNullOrWhiteSpace(tag) ? "local" : tag,
            string.IsNullOrWhiteSpace(commit) ? "local" : commit,
            string.IsNullOrWhiteSpace(runId) ? "local" : runId,
            runAttempt,
            invocationId,
            recoveryNonce);
    }

    public static string SyntheticToken(LiveIdentity identity, string scenario)
    {
        var invocation = identity.Tag == "local" && identity.CommitSha == "local"
            ? identity.InvocationId
            : "release";
        var logical = string.Join("|", identity.Tag, identity.CommitSha, scenario, identity.RecoveryNonce, invocation);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(logical));
        return Convert.ToHexString(bytes)[..12];
    }
}

public static class LiveDevelopmentScenarioSelector
{
    public static readonly IReadOnlyList<string> AllScenarioNames = ["standard", "school", "ecard"];

    public static IReadOnlyList<string> Select(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return AllScenarioNames;
        }

        var requested = raw.Split(',', StringSplitOptions.None)
            .Select(value => value.Trim().ToLowerInvariant())
            .ToArray();
        if (requested.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException("PATRON_REGISTRATION_LIVE_SCENARIOS contains an empty scenario name.");
        }

        var unknown = requested.Where(name => !AllScenarioNames.Contains(name, StringComparer.Ordinal)).Distinct().ToArray();
        if (unknown.Length > 0)
        {
            throw new InvalidOperationException($"Unknown live registration scenario: {string.Join(", ", unknown)}.");
        }

        var requestedSet = requested.ToHashSet(StringComparer.Ordinal);
        return AllScenarioNames.Where(requestedSet.Contains).ToArray();
    }
}

public sealed record LiveGateRunResult(bool Succeeded, IReadOnlyList<LivePublicResult> Results)
{
    public string SafeSummary() => string.Join(
        Environment.NewLine,
        Results.Select(result =>
            $"{result.Scenario}: create={result.CreateState}, scenario={result.ScenarioState}, token={result.SyntheticToken}"));
}

public static class LiveDevelopmentGateRunner
{
    public static LiveGateRunResult Run(
        IReadOnlyList<LiveScenarioDefinition> scenarios,
        LiveIdentity identity,
        LiveAttemptStore store)
    {
        ArgumentNullException.ThrowIfNull(scenarios);
        ArgumentNullException.ThrowIfNull(store);

        if (scenarios.Count == 0)
        {
            return new LiveGateRunResult(false, store.History);
        }

        // Every scenario is checked before the first attempt marker/create.
        foreach (var scenario in scenarios)
        {
            bool ready;
            try
            {
                ready = scenario.Preflight();
            }
            catch
            {
                ready = false;
            }

            if (!ready)
            {
                return new LiveGateRunResult(false, store.History);
            }
        }

        foreach (var scenario in scenarios)
        {
            var token = LiveIdentityFactory.SyntheticToken(identity, scenario.Name);
            var attempt = store.Begin(scenario.Name, token, identity);
            LiveCreateOutcome outcome;
            try
            {
                // Exactly one invocation is intentional. A transport exception can mean
                // that Polaris already created the patron, so this call is never retried.
                outcome = scenario.Create(attempt);
            }
            catch
            {
                store.Transition(attempt, LiveCreateState.Unknown, LiveScenarioState.Failed, "transport-ambiguous");
                return new LiveGateRunResult(false, store.History);
            }

            if (outcome.State == LiveCreateState.Created)
            {
                var created = store.Transition(attempt, LiveCreateState.Created, LiveScenarioState.Running);
                bool valid;
                try
                {
                    valid = scenario.ValidateCreated(outcome);
                }
                catch
                {
                    valid = false;
                }

                if (!valid)
                {
                    store.Transition(created, LiveCreateState.Created, LiveScenarioState.Failed, "post-create");
                    return new LiveGateRunResult(false, store.History);
                }

                store.Transition(created, LiveCreateState.Created, LiveScenarioState.Passed);
                continue;
            }

            var failureClass = outcome.State == LiveCreateState.Rejected ? "rejected" : "transport-ambiguous";
            store.Transition(attempt, outcome.State, LiveScenarioState.Failed, failureClass);
            return new LiveGateRunResult(false, store.History);
        }

        return new LiveGateRunResult(true, store.History);
    }
}

public sealed record LiveDevelopmentConfiguration(
    string Host,
    string AccessId,
    string AccessKey,
    int OrganizationId,
    int LibraryId,
    int BranchId,
    int PatronCodeId,
    int LogonUserId,
    int EcardPatronCodeId,
    int StudentPatronCodeId,
    int TeacherPatronCodeId,
    string School,
    string ManifestPath)
{
    // This is a committed, non-secret proof. It must be updated by code review when
    // the approved DEVELOPMENT Polaris host changes; secrets cannot change the target.
    public static readonly IReadOnlySet<string> ApprovedDevelopmentHosts =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "polaris-development.clcohio.org",
            "polaris-dev.clcohio.org"
        };

    public static LiveDevelopmentConfiguration FromEnvironment()
    {
        var enabled = Environment.GetEnvironmentVariable("PATRON_REGISTRATION_LIVE_TESTS");
        if (!string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase))
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

        var runAttempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT");
        if (int.TryParse(runAttempt, out var attempt) && attempt > 1)
        {
            throw new InvalidOperationException(
                "A workflow rerun is fail-closed because an earlier attempt may have created patrons; inspect the public manifest before recovery.");
        }

        return new LiveDevelopmentConfiguration(
            host,
            Required("PATRON_REGISTRATION_PAPI_ACCESS_ID"),
            Required("PATRON_REGISTRATION_PAPI_ACCESS_KEY"),
            Positive("PATRON_REGISTRATION_PAPI_ORGANIZATION_ID"),
            Positive("PATRON_REGISTRATION_PAPI_LIBRARY_ID"),
            Positive("PATRON_REGISTRATION_PAPI_BRANCH_ID"),
            Positive("PATRON_REGISTRATION_PAPI_PATRON_CODE_ID"),
            Positive("PATRON_REGISTRATION_PAPI_LOGON_USER_ID"),
            Positive("PATRON_REGISTRATION_PAPI_ECARD_PATRON_CODE_ID"),
            Positive("PATRON_REGISTRATION_PAPI_STUDENT_PATRON_CODE_ID"),
            Positive("PATRON_REGISTRATION_PAPI_TEACHER_PATRON_CODE_ID"),
            Required("PATRON_REGISTRATION_PAPI_SCHOOL"),
            Environment.GetEnvironmentVariable("PATRON_REGISTRATION_LIVE_MANIFEST") ?? "live-development-results.json");
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

    private static string Required(string name) =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))
            ? throw new InvalidOperationException($"Missing required live configuration {name}; no patron mutation was attempted.")
            : Environment.GetEnvironmentVariable(name)!;

    private static int Positive(string name)
    {
        var value = Required(name);
        return int.TryParse(value, out var parsed) && parsed > 0
            ? parsed
            : throw new InvalidOperationException($"Live configuration {name} must be positive; no patron mutation was attempted.");
    }
}

[TestClass]
[DoNotParallelize]
public sealed class LiveDevelopmentRegistrationGateTests
{
    [TestMethod]
    [TestCategory("LiveDevelopment")]
    public void SelectedScenarios_AreAcceptedByDevelopmentPolaris()
    {
        var configuration = LiveDevelopmentConfiguration.FromEnvironment();
        var selectedNames = LiveDevelopmentScenarioSelector.Select(
            Environment.GetEnvironmentVariable("PATRON_REGISTRATION_LIVE_SCENARIOS"));
        var identity = LiveIdentityFactory.FromEnvironment();
        var realPapi = new PapiClient(new PapiSettings
        {
            AccessId = configuration.AccessId,
            AccessKey = configuration.AccessKey,
            Hostname = configuration.Host
        });

        // Authentication and endpoint proof are read-only and happen before any
        // scenario marker or PatronRegistrationCreate call.
        IRestResponse<PapiResponseCommon>? validation;
        try
        {
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
        }

        using var harness = LiveSubmissionHarness.Create(configuration, realPapi);
        var definitions = selectedNames.Select(name => harness.BuildScenario(name, identity)).ToArray();
        using var store = new LiveAttemptStore(configuration.ManifestPath);
        var result = LiveDevelopmentGateRunner.Run(definitions, identity, store);
        Assert.IsTrue(result.Succeeded, result.SafeSummary());
    }

    private sealed class LiveSubmissionHarness : IDisposable
    {
        private readonly ServiceProvider provider;
        private readonly DefaultHttpContext httpContext;
        private readonly ISettingProvider settings;
        private readonly Mock<IDbHelper> db;
        private readonly Mock<IMelissaRestClient> melissa;
        private readonly Mock<IEmailSender> email;
        private readonly Mock<IPapiClient> forwardingPapi;
        private readonly LiveDevelopmentConfiguration configuration;
        private PatronRegistrationParams? lastParameters;
        private IRestResponse<PatronRegistrationCreateResult>? lastResponse;

        private LiveSubmissionHarness(
            ServiceProvider provider,
            DefaultHttpContext httpContext,
            ISettingProvider settings,
            Mock<IDbHelper> db,
            Mock<IMelissaRestClient> melissa,
            Mock<IEmailSender> email,
            Mock<IPapiClient> forwardingPapi,
            LiveDevelopmentConfiguration configuration)
        {
            this.provider = provider;
            this.httpContext = httpContext;
            this.settings = settings;
            this.db = db;
            this.melissa = melissa;
            this.email = email;
            this.forwardingPapi = forwardingPapi;
            this.configuration = configuration;
        }

        public static LiveSubmissionHarness Create(
            LiveDevelopmentConfiguration configuration,
            IPapiClient realPapi)
        {
            var settings = LiveSettings(configuration).Object;
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
            var email = new Mock<IEmailSender>();
            var melissaFactory = new Mock<IMelissaClientFactory>();
            melissaFactory.Setup(value => value.Create(It.IsAny<string>())).Returns(melissa.Object);
            var emailFactory = new Mock<IEmailSenderFactory>();
            emailFactory.Setup(value => value.Create(It.IsAny<string>())).Returns(email.Object);

            // Test-only forwarding through the existing interface keeps the request,
            // endpoint, credentials, and real response unchanged.
            var forwardingPapi = new Mock<IPapiClient>();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddControllersWithViews().AddApplicationPart(typeof(RegistrationController).Assembly);
            services.AddHttpContextAccessor();
            services.AddSingleton(settings);
            var provider = services.BuildServiceProvider();
            var httpContext = new DefaultHttpContext { RequestServices = provider };
            provider.GetRequiredService<IHttpContextAccessor>().HttpContext = httpContext;

            var scopeResolver = new Mock<IRegistrationScopeResolver>();
            scopeResolver.Setup(value => value.ResolveForSubmission(
                    It.IsAny<HttpContext>(), settings, It.IsAny<int>()))
                .Returns(new RegistrationScopeResolution(true, settings));

            var controller = new RegistrationController(
                forwardingPapi.Object,
                db.Object,
                settings,
                emailFactory.Object,
                melissaFactory.Object,
                provider.GetRequiredService<IObjectModelValidator>(),
                scopeResolver.Object)
            {
                ControllerContext = new ControllerContext { HttpContext = httpContext }
            };

            var harness = new LiveSubmissionHarness(provider, httpContext, settings, db, melissa, email, forwardingPapi, configuration)
            {
                Controller = controller
            };
            forwardingPapi.Setup(value => value.PatronRegistrationCreate(It.IsAny<PatronRegistrationParams>()))
                .Returns((PatronRegistrationParams parameters) =>
                {
                    var response = realPapi.PatronRegistrationCreate(parameters);
                    // Capture the response immediately; later controller finalization
                    // must never cause another create call.
                    harness.lastParameters = parameters;
                    return harness.lastResponse = response;
                });
            return harness;
        }

        private RegistrationController Controller { get; init; } = null!;

        public LiveScenarioDefinition BuildScenario(string name, LiveIdentity identity)
        {
            var token = LiveIdentityFactory.SyntheticToken(identity, name);
            return new LiveScenarioDefinition(
                name,
                () => ScenarioPreflight(name, token),
                marker => Submit(name, marker.SyntheticToken),
                outcome => outcome.PatronId > 0 && !string.IsNullOrWhiteSpace(outcome.Barcode) &&
                    outcome.FinalizationSucceeded &&
                    lastParameters?.NameLast.StartsWith(token, StringComparison.Ordinal) == true);
        }

        private bool ScenarioPreflight(string name, string token)
        {
            if (!Regex.IsMatch(token, "^[A-F0-9]{12}$", RegexOptions.CultureInvariant) || token.Length + name.Length > 30)
            {
                return false;
            }

            var values = BuildFormValues(name, token);
            var required = new[]
            {
                nameof(Registration.PatronBranchID), nameof(Registration.NameFirst),
                nameof(Registration.NameLast), nameof(Registration.Birthdate),
                nameof(Registration.DeliveryOptionId), nameof(Registration.StreetOne),
                nameof(Registration.City), nameof(Registration.State),
                nameof(Registration.PostalCode), nameof(Registration.EmailAddress),
                nameof(Registration.Password), nameof(Registration.Password2)
            };
            if (required.Any(key => !values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value)))
            {
                return false;
            }

            return name switch
            {
                "standard" => settings.PatronCodeId > 0 && settings.RegistrationLogonUserId > 0 &&
                    values[nameof(Registration.IsStudent)] == bool.FalseString &&
                    values[nameof(Registration.IsTeacher)] == bool.FalseString,
                "school" => settings.StudentPatronCodeId > 0 && !string.IsNullOrWhiteSpace(settings.SchoolInfoFormat) &&
                    values[nameof(Registration.IsStudent)] == bool.FalseString &&
                    values[nameof(Registration.IsTeacher)] == bool.FalseString &&
                    string.IsNullOrWhiteSpace(values[nameof(Registration.User1)]),
                "ecard" => settings.EcardPatronCodeId > 0 && !string.IsNullOrWhiteSpace(settings.EcardBarcodePrefix) &&
                    values[nameof(Registration.IsECard)] == bool.TrueString,
                _ => false
            };
        }

        private LiveCreateOutcome Submit(string scenario, string token)
        {
            // A successful previous scenario must never be reused if this scenario
            // exits before invoking PAPI.
            lastParameters = null;
            lastResponse = null;
            var values = BuildFormValues(scenario, token);
            var body = string.Join("&", values.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
            var bytes = Encoding.UTF8.GetBytes(body);
            httpContext.Request.Method = HttpMethods.Post;
            httpContext.Request.ContentType = "application/x-www-form-urlencoded";
            httpContext.Request.ContentLength = bytes.Length;
            httpContext.Request.Body = new MemoryStream(bytes);

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
            var metadata = ((DefaultModelMetadataProvider)metadataProvider).GetMetadataForParameter(parameter.ParameterInfo);
            var binder = provider.GetRequiredService<IModelBinderFactory>().CreateBinder(new ModelBinderFactoryContext
            {
                BindingInfo = parameter.BindingInfo,
                Metadata = metadata,
                CacheToken = parameter
            });
            var valueProvider = CompositeValueProvider.CreateAsync(
                actionContext,
                provider.GetRequiredService<IOptions<MvcOptions>>().Value.ValueProviderFactories).GetAwaiter().GetResult();
            var bindingResult = provider.GetRequiredService<ParameterBinder>().BindModelAsync(
                actionContext,
                binder,
                valueProvider,
                parameter,
                metadata,
                null).GetAwaiter().GetResult();
            if (!bindingResult.IsModelSet || !Controller.ModelState.IsValid)
            {
                return LiveCreateOutcome.Rejected();
            }

            var registration = (Registration)bindingResult.Model!;
            RegistrationAttempt attempt;
            try
            {
                attempt = Controller.Submit(registration);
            }
            catch
            {
                return CreatedOutcome(downstreamSucceeded: false);
            }

            if (attempt.Status != RegistrationStatus.Success)
            {
                if (lastResponse?.Data?.PatronID > 0)
                {
                    return CreatedOutcome(downstreamSucceeded: false);
                }

                return LiveCreateOutcome.Rejected();
            }

            if (lastResponse is null || lastParameters is null)
            {
                return LiveCreateOutcome.Unknown();
            }

            var response = lastResponse;
            if (response?.Data?.PatronID > 0 && !string.IsNullOrWhiteSpace(response.Data.Barcode))
            {
                return LiveCreateOutcome.Created(response.Data.PatronID, response.Data.Barcode);
            }

            return response?.Data is not null && response.Data.PAPIErrorCode < 0
                ? LiveCreateOutcome.Rejected()
                : LiveCreateOutcome.Unknown();

            LiveCreateOutcome CreatedOutcome(bool downstreamSucceeded)
            {
                return lastResponse?.Data?.PatronID > 0 && !string.IsNullOrWhiteSpace(lastResponse.Data.Barcode)
                    ? (downstreamSucceeded
                        ? LiveCreateOutcome.Created(lastResponse.Data.PatronID, lastResponse.Data.Barcode)
                        : LiveCreateOutcome.CreatedWithFailedFinalization(lastResponse.Data.PatronID, lastResponse.Data.Barcode))
                    : LiveCreateOutcome.Unknown();
            }
        }

        private Dictionary<string, string> BuildFormValues(string scenario, string token) =>
            new(StringComparer.OrdinalIgnoreCase)
            {
                [nameof(Registration.PatronBranchID)] = configuration.BranchId.ToString(),
                [nameof(Registration.NameFirst)] = "CI",
                [nameof(Registration.NameLast)] = $"{token}{scenario.ToUpperInvariant()}",
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
                [nameof(Registration.IsECard)] = (scenario == "ecard").ToString(),
                [nameof(Registration.User1)] = string.Empty
            };

        private static Mock<ISettingProvider> LiveSettings(LiveDevelopmentConfiguration configuration)
        {
            var settings = new Mock<ISettingProvider>();
            settings.SetupGet(value => value.OrganizationId).Returns(configuration.OrganizationId);
            settings.SetupGet(value => value.LibraryId).Returns(configuration.LibraryId);
            settings.SetupGet(value => value.PatronCodeId).Returns(configuration.PatronCodeId);
            settings.SetupGet(value => value.RegistrationLogonUserId).Returns(configuration.LogonUserId);
            settings.SetupGet(value => value.EcardPatronCodeId).Returns(configuration.EcardPatronCodeId);
            settings.SetupGet(value => value.StudentPatronCodeId).Returns(configuration.StudentPatronCodeId);
            settings.SetupGet(value => value.TeacherPatronCodeId).Returns(configuration.TeacherPatronCodeId);
            settings.SetupGet(value => value.SchoolInfoFormat).Returns("uapl");
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
            settings.Setup(value => value.GetFieldLabel(It.IsAny<string>())).Returns(configuration.School);
            return settings;
        }

        public void Dispose()
        {
            httpContext.RequestServices = null!;
            provider.Dispose();
        }
    }
}

[TestClass]
public sealed class LiveDevelopmentGateSafetyTests
{
    [TestMethod]
    public void FailedPreflight_PreventsEveryCreate()
    {
        var count = 0;
        var scenarios = new[]
        {
            Definition("standard", () => true, () => { count++; return LiveCreateOutcome.Created(1, "secret"); }),
            Definition("school", () => false, () => { count++; return LiveCreateOutcome.Created(2, "secret"); })
        };

        var result = Run(scenarios);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(0, count);
        Assert.AreEqual(0, result.Results.Count);
    }

    [TestMethod]
    public void Selector_DefaultSubsetAndDuplicatesAreDeterministic()
    {
        CollectionAssert.AreEqual(new[] { "standard", "school", "ecard" },
            LiveDevelopmentScenarioSelector.Select(null).ToArray());
        CollectionAssert.AreEqual(new[] { "standard", "ecard" },
            LiveDevelopmentScenarioSelector.Select("ecard,standard,ecard").ToArray());
        Assert.ThrowsException<InvalidOperationException>(() =>
            LiveDevelopmentScenarioSelector.Select("standard,unknown"));
        Assert.ThrowsException<InvalidOperationException>(() =>
            LiveDevelopmentScenarioSelector.Select("standard,,ecard"));
    }

    [TestMethod]
    public void AmbiguousCreate_IsRecordedUnknownAndNeverRetried()
    {
        var count = 0;
        var scenarios = new[]
        {
            Definition("standard", () => true, () => { count++; throw new TimeoutException(); }),
            Definition("school", () => true, () => { count++; return LiveCreateOutcome.Created(2, "hidden"); })
        };

        var result = Run(scenarios);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(1, count);
        Assert.AreEqual(LiveCreateState.Unknown, result.Results.Last().CreateState);
        Assert.AreEqual(LiveScenarioState.Failed, result.Results.Last().ScenarioState);
    }

    [TestMethod]
    public void CreatedThenDownstreamFailure_RetainsCreatedStateAndStopsLaterScenarios()
    {
        var count = 0;
        var scenarios = new[]
        {
            Definition("standard", () => true, () => { count++; return LiveCreateOutcome.Created(1, "private"); }, _ => false),
            Definition("school", () => true, () => { count++; return LiveCreateOutcome.Created(2, "private"); })
        };

        var result = Run(scenarios);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(1, count);
        Assert.AreEqual(LiveCreateState.Created, result.Results.Last().CreateState);
        Assert.AreEqual(LiveScenarioState.Failed, result.Results.Last().ScenarioState);
        Assert.AreEqual("post-create", result.Results.Last().FailureClass);
    }

    [TestMethod]
    public void PublicManifest_ContainsOnlySafeResultFields()
    {
        using var temporary = new TemporaryManifest();
        var store = new LiveAttemptStore(temporary.Path);
        var result = Run([Definition("standard", () => true, () => LiveCreateOutcome.Created(12345, "BARCODE-SECRET"))], store);
        var json = File.ReadAllText(temporary.Path);

        StringAssert.Contains(json, "standard");
        StringAssert.Contains(json, "SyntheticToken");
        Assert.IsFalse(json.Contains("12345", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("BARCODE-SECRET", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("Authorization", StringComparison.Ordinal));
        Assert.IsTrue(result.Succeeded);
        store.Dispose();
    }

    [TestMethod]
    public void LogicalIdentity_DoesNotChangeWithWorkflowAttempt()
    {
        var first = LiveIdentityFactory.FromEnvironment(new Dictionary<string, string?>
        {
            ["GITHUB_REF_NAME"] = "v1.2.3",
            ["GITHUB_SHA"] = "abcdef",
            ["GITHUB_RUN_ID"] = "100",
            ["GITHUB_RUN_ATTEMPT"] = "1"
        });
        var second = first with { RunAttempt = 2, RunId = "101" };

        Assert.AreEqual(LiveIdentityFactory.SyntheticToken(first, "standard"),
            LiveIdentityFactory.SyntheticToken(second, "standard"));
        Assert.AreNotEqual(LiveIdentityFactory.SyntheticToken(first, "standard"),
            LiveIdentityFactory.SyntheticToken(first, "school"));
    }

    [TestMethod]
    public void DevelopmentTargetProofRequiresHttpsAndExactCommittedHost()
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
    public void FailedScenario_StopsLaterScenarioMutation()
    {
        var invoked = new List<string>();
        var scenarios = new[]
        {
            Definition("standard", () => true, () => { invoked.Add("standard"); return LiveCreateOutcome.Created(1, "ok"); }),
            Definition("school", () => true, () => { invoked.Add("school"); return LiveCreateOutcome.Rejected(); }),
            Definition("ecard", () => true, () => { invoked.Add("ecard"); return LiveCreateOutcome.Created(3, "never"); })
        };

        var result = Run(scenarios);

        CollectionAssert.AreEqual(new[] { "standard", "school" }, invoked);
        Assert.IsFalse(result.Succeeded);
    }

    [TestMethod]
    public void AttemptMarker_IsPersistedBeforeCreateCallback()
    {
        using var temporary = new TemporaryManifest();
        var observed = false;
        var store = new LiveAttemptStore(temporary.Path);
        var scenarios = new[]
        {
            Definition("standard", () => true, () =>
            {
                var json = File.ReadAllText(temporary.Path);
                observed = json.Contains("Attempting", StringComparison.Ordinal);
                return LiveCreateOutcome.Created(1, "private");
            })
        };

        _ = Run(scenarios, store);

        Assert.IsTrue(observed);
        store.Dispose();
    }

    private static LiveScenarioDefinition Definition(
        string name,
        Func<bool> preflight,
        Func<LiveCreateOutcome> create,
        Func<LiveCreateOutcome, bool>? validate = null) =>
        new(name, preflight, _ => create(), validate ?? (_ => true));

    private static LiveGateRunResult Run(
        IReadOnlyList<LiveScenarioDefinition> scenarios,
        LiveAttemptStore? store = null)
    {
        store ??= new LiveAttemptStore();
        return LiveDevelopmentGateRunner.Run(
            scenarios,
            new LiveIdentity("v1.2.3", "abcdef", "run", 1, "invocation"),
            store);
    }

    private sealed class TemporaryManifest : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
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
