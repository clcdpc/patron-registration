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
    bool FinalizationSucceeded = true,
    LivePublicResult? CreatedRecord = null)
{
    public static LiveCreateOutcome Created(
        int patronId,
        string barcode,
        LivePublicResult? createdRecord = null) =>
        new(LiveCreateState.Created, patronId, barcode, CreatedRecord: createdRecord);

    public static LiveCreateOutcome CreatedWithFailedFinalization(
        int patronId,
        string barcode,
        LivePublicResult? createdRecord = null) =>
        new(LiveCreateState.Created, patronId, barcode, FinalizationSucceeded: false, CreatedRecord: createdRecord);

    public static LiveCreateOutcome NotAttempted() => new(LiveCreateState.NotAttempted);

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

            if (outcome is null)
            {
                store.Transition(attempt, LiveCreateState.Unknown, LiveScenarioState.Failed, "transport-ambiguous");
                return new LiveGateRunResult(false, store.History);
            }

            if (outcome.State == LiveCreateState.Created)
            {
                // The forwarding callback may already have persisted this marker before
                // production finalization returned. Fakes without that callback use the
                // fallback transition so the runner remains deterministic.
                var created = outcome.CreatedRecord ??
                    store.Transition(attempt, LiveCreateState.Created, LiveScenarioState.Running);
                bool valid;
                try
                {
                    valid = outcome.FinalizationSucceeded && scenario.ValidateCreated(outcome);
                }
                catch
                {
                    valid = false;
                }

                if (!valid)
                {
                    store.Transition(
                        created,
                        LiveCreateState.Created,
                        LiveScenarioState.Failed,
                        outcome.FinalizationSucceeded ? "post-create" : "finalization-failed");
                    return new LiveGateRunResult(false, store.History);
                }

                store.Transition(created, LiveCreateState.Created, LiveScenarioState.Passed);
                continue;
            }

            var safeCreateState = outcome.State switch
            {
                LiveCreateState.NotAttempted => LiveCreateState.NotAttempted,
                LiveCreateState.Rejected => LiveCreateState.Rejected,
                _ => LiveCreateState.Unknown
            };
            var failureClass = safeCreateState switch
            {
                LiveCreateState.NotAttempted => "not-attempted",
                LiveCreateState.Rejected => "rejected",
                _ => "transport-ambiguous"
            };
            store.Transition(attempt, safeCreateState, LiveScenarioState.Failed, failureClass);
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

    public static LiveDevelopmentConfiguration FromEnvironment(
        IReadOnlyCollection<string>? selectedScenarios = null,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        string? Get(string name) => environment is null
            ? Environment.GetEnvironmentVariable(name)
            : environment.TryGetValue(name, out var value) ? value : null;

        string Required(string name) => string.IsNullOrWhiteSpace(Get(name))
            ? throw new InvalidOperationException($"Missing required live configuration {name}; no patron mutation was attempted.")
            : Get(name)!;

        int Positive(string name) => int.TryParse(Required(name), out var parsed) && parsed > 0
            ? parsed
            : throw new InvalidOperationException($"Live configuration {name} must be positive; no patron mutation was attempted.");

        // Keep the rerun guard first so a repeat is rejected before any other
        // live configuration is interpreted. This remains defense in depth
        // for local/manual invocation; the workflow guard runs earlier still.
        var runAttempt = Get("GITHUB_RUN_ATTEMPT");
        if (int.TryParse(runAttempt, out var attempt) && attempt > 1)
        {
            throw new InvalidOperationException(
                "A workflow rerun is fail-closed because an earlier attempt may have created patrons; inspect the public manifest before recovery.");
        }

        var enabled = Get("PATRON_REGISTRATION_LIVE_TESTS");
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

        var selected = (selectedScenarios ?? LiveDevelopmentScenarioSelector.AllScenarioNames)
            .Select(name => name.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selected.Count == 0)
        {
            throw new InvalidOperationException(
                "At least one live registration scenario must be selected; no patron mutation was attempted.");
        }

        var unknown = selected
            .Where(name => !LiveDevelopmentScenarioSelector.AllScenarioNames.Contains(name, StringComparer.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (unknown.Length > 0)
        {
            throw new InvalidOperationException(
                $"Unknown live registration scenario: {string.Join(", ", unknown)}; no patron mutation was attempted.");
        }

        var requiresStandardPatronCode = selected.Contains("standard") || selected.Contains("school");
        var requiresEcardPatronCode = selected.Contains("ecard");

        return new LiveDevelopmentConfiguration(
            host,
            Required("PATRON_REGISTRATION_PAPI_ACCESS_ID"),
            Required("PATRON_REGISTRATION_PAPI_ACCESS_KEY"),
            Positive("PATRON_REGISTRATION_PAPI_ORGANIZATION_ID"),
            Positive("PATRON_REGISTRATION_PAPI_LIBRARY_ID"),
            Positive("PATRON_REGISTRATION_PAPI_BRANCH_ID"),
            requiresStandardPatronCode ? Positive("PATRON_REGISTRATION_PAPI_PATRON_CODE_ID") : 0,
            Positive("PATRON_REGISTRATION_PAPI_LOGON_USER_ID"),
            requiresEcardPatronCode ? Positive("PATRON_REGISTRATION_PAPI_ECARD_PATRON_CODE_ID") : 0,
            // The selected live matrix deliberately submits neither a student nor a
            // teacher. Their patron-code settings are therefore not operational
            // requirements for this gate.
            0,
            0,
            string.IsNullOrWhiteSpace(Get("PATRON_REGISTRATION_PAPI_SCHOOL"))
                ? "School"
                : Get("PATRON_REGISTRATION_PAPI_SCHOOL")!,
            Get("PATRON_REGISTRATION_LIVE_MANIFEST") ?? "live-development-results.json");
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
    public void SelectedScenarios_AreAcceptedByDevelopmentPolaris()
    {
        var selectedNames = LiveDevelopmentScenarioSelector.Select(
            Environment.GetEnvironmentVariable("PATRON_REGISTRATION_LIVE_SCENARIOS"));
        var configuration = LiveDevelopmentConfiguration.FromEnvironment(selectedNames);
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

        using var store = new LiveAttemptStore(configuration.ManifestPath);
        using var harness = LiveSubmissionHarness.Create(configuration, realPapi, store);
        var definitions = selectedNames.Select(name => harness.BuildScenario(name, identity)).ToArray();
        var result = LiveDevelopmentGateRunner.Run(definitions, identity, store);
        Assert.IsTrue(result.Succeeded, result.SafeSummary());
    }

    [TestMethod]
    public void LiveSettings_AreScenarioSpecific()
    {
        var configuration = SyntheticConfiguration();
        using var store = new LiveAttemptStore();
        var papi = new Mock<IPapiClient>();
        using var harness = LiveSubmissionHarness.Create(configuration, papi.Object, store);

        Assert.IsTrue(string.IsNullOrWhiteSpace(harness.SettingsFor("standard").SchoolInfoFormat));
        Assert.AreEqual(4, harness.SettingsFor("standard").PatronCodeId);
        Assert.AreEqual(0, harness.SettingsFor("standard").EcardPatronCodeId);
        Assert.AreEqual("uapl", harness.SettingsFor("school").SchoolInfoFormat);
        Assert.AreEqual(4, harness.SettingsFor("school").PatronCodeId);
        Assert.AreEqual("uapl", harness.SettingsFor("ecard").SchoolInfoFormat);
        Assert.AreEqual(6, harness.SettingsFor("ecard").EcardPatronCodeId);
        Assert.AreNotEqual(
            harness.SettingsFor("standard").SchoolInfoFormat,
            harness.SettingsFor("school").SchoolInfoFormat);
    }

    [TestMethod]
    public void SchoolScenario_EmptyUser1PassesCompletePreflightWithoutCreate()
    {
        var configuration = SyntheticConfiguration();
        var papi = new Mock<IPapiClient>();
        using var store = new LiveAttemptStore();
        using var harness = LiveSubmissionHarness.Create(configuration, papi.Object, store);
        var identity = new LiveIdentity("v1.2.3", new string('a', 40), "run", 1, "invocation");
        var scenario = harness.BuildScenario("school", identity);

        var preflightPassed = scenario.Preflight();
        papi.Verify(value => value.PatronRegistrationCreate(It.IsAny<PatronRegistrationParams>()), Times.Never);
        Assert.IsTrue(preflightPassed);
        Assert.IsFalse(harness.ModelStateFor("school").TryGetValue(
            nameof(Registration.User1), out var user1State) && user1State.Errors.Count > 0);
    }

    [TestMethod]
    public void StandardScenario_PreflightUsesNonSchoolContract()
    {
        var configuration = SyntheticConfiguration();
        var papi = new Mock<IPapiClient>();
        papi.Setup(value => value.PatronRegistrationCreate(It.IsAny<PatronRegistrationParams>()))
            .Returns((PatronRegistrationParams parameters) => new RestResponse<PatronRegistrationCreateResult>
            {
                Data = new PatronRegistrationCreateResult
                {
                    PAPIErrorCode = 0,
                    PatronID = 123,
                    Barcode = "STANDARD-123"
                }
            });
        using var store = new LiveAttemptStore();
        using var harness = LiveSubmissionHarness.Create(configuration, papi.Object, store);
        var identity = new LiveIdentity("v1.2.3", new string('a', 40), "run", 1, "invocation");
        var scenario = harness.BuildScenario("standard", identity);

        var result = LiveDevelopmentGateRunner.Run([scenario], identity, store);

        Assert.IsTrue(result.Succeeded, result.SafeSummary());
        Assert.IsNotNull(harness.LastParameters);
        Assert.AreEqual(4, harness.LastParameters!.PatronCode);
        Assert.IsTrue(string.IsNullOrEmpty(harness.LastParameters.User1));
        papi.Verify(value => value.PatronRegistrationCreate(It.IsAny<PatronRegistrationParams>()), Times.Once);
    }

    [TestMethod]
    public void PreparedScenario_IsReusedForExecution()
    {
        var configuration = SyntheticConfiguration();
        var formBuildCount = 0;
        var papi = new Mock<IPapiClient>();
        papi.Setup(value => value.PatronRegistrationCreate(It.IsAny<PatronRegistrationParams>()))
            .Returns((PatronRegistrationParams parameters) => new RestResponse<PatronRegistrationCreateResult>
            {
                Data = new PatronRegistrationCreateResult
                {
                    PAPIErrorCode = 0,
                    PatronID = 123,
                    Barcode = "STANDARD-123"
                }
            });
        using var store = new LiveAttemptStore();
        using var harness = LiveSubmissionHarness.Create(
            configuration,
            papi.Object,
            store,
            (name, token) =>
            {
                formBuildCount++;
                return SyntheticFormValues(configuration.BranchId, name, token);
            });
        var identity = new LiveIdentity("v1.2.3", new string('a', 40), "run", 1, "invocation");
        var scenario = harness.BuildScenario("standard", identity);

        var result = LiveDevelopmentGateRunner.Run([scenario], identity, store);

        Assert.IsTrue(result.Succeeded, result.SafeSummary());
        Assert.AreEqual(1, formBuildCount);
    }

    [TestMethod]
    public void EcardScenario_UsesUaplAndClearsUser1DuringExecution()
    {
        var configuration = SyntheticConfiguration();
        var papi = new Mock<IPapiClient>();
        papi.Setup(value => value.PatronRegistrationCreate(It.IsAny<PatronRegistrationParams>()))
            .Returns((PatronRegistrationParams parameters) => new RestResponse<PatronRegistrationCreateResult>
            {
                Data = new PatronRegistrationCreateResult
                {
                    PAPIErrorCode = 0,
                    PatronID = 123,
                    Barcode = parameters.Barcode
                }
            });
        using var store = new LiveAttemptStore();
        using var harness = LiveSubmissionHarness.Create(configuration, papi.Object, store);
        var identity = new LiveIdentity("v1.2.3", new string('a', 40), "run", 1, "invocation");
        var scenario = harness.BuildScenario("ecard", identity);

        var result = LiveDevelopmentGateRunner.Run([scenario], identity, store);

        Assert.IsTrue(result.Succeeded, result.SafeSummary());
        Assert.IsNotNull(harness.LastParameters);
        Assert.AreEqual(string.Empty, harness.LastParameters!.User1);
        Assert.AreEqual(6, harness.LastParameters.PatronCode);
        papi.Verify(value => value.PatronRegistrationCreate(It.IsAny<PatronRegistrationParams>()), Times.Once);
    }

    [TestMethod]
    public void LaterBindingFailure_PreventsEveryLiveCreate()
    {
        var configuration = SyntheticConfiguration();
        var papi = new Mock<IPapiClient>();
        using var store = new LiveAttemptStore();
        using var harness = LiveSubmissionHarness.Create(
            configuration,
            papi.Object,
            store,
            (name, token) =>
            {
                var values = SyntheticFormValues(configuration.BranchId, name, token);
                if (name == "school")
                {
                    values[nameof(Registration.DeliveryOptionId)] = "not-a-number";
                }
                return values;
            });
        var identity = new LiveIdentity("v1.2.3", new string('a', 40), "run", 1, "invocation");
        LiveScenarioDefinition[] scenarios = [
            harness.BuildScenario("standard", identity),
            harness.BuildScenario("school", identity),
            harness.BuildScenario("ecard", identity)];

        var result = LiveDevelopmentGateRunner.Run(scenarios, identity, store);

        Assert.IsFalse(result.Succeeded,
            string.Join(" | ", harness.ModelStateFor("school").SelectMany(entry =>
                entry.Value.Errors.Select(error => $"{entry.Key}={error.ErrorMessage}"))));
        Assert.AreEqual(0, result.Results.Count);
        papi.Verify(value => value.PatronRegistrationCreate(It.IsAny<PatronRegistrationParams>()), Times.Never);
    }

    [TestMethod]
    public void LaterSelectedBranchValidationFailure_PreventsEveryLiveCreate()
    {
        var configuration = SyntheticConfiguration();
        var papi = new Mock<IPapiClient>();
        using var store = new LiveAttemptStore();
        using var harness = LiveSubmissionHarness.Create(
            configuration,
            papi.Object,
            store,
            configureSettings: (name, settings) =>
            {
                if (name == "school")
                {
                    settings.SetupGet(value => value.DisplayResponsiblePersonField).Returns(true);
                    settings.Setup(value => value.GetFieldRequired(nameof(Registration.User5))).Returns(true);
                }
            });
        Assert.IsTrue(harness.SettingsFor("school").GetFieldRequired(nameof(Registration.User5)));
        var identity = new LiveIdentity("v1.2.3", new string('a', 40), "run", 1, "invocation");
        LiveScenarioDefinition[] scenarios = [
            harness.BuildScenario("standard", identity),
            harness.BuildScenario("school", identity),
            harness.BuildScenario("ecard", identity)];

        var result = LiveDevelopmentGateRunner.Run(scenarios, identity, store);

        Assert.IsFalse(result.Succeeded,
            string.Join(" | ", harness.ModelStateFor("school").SelectMany(entry =>
                entry.Value.Errors.Select(error => $"{entry.Key}={error.ErrorMessage}"))));
        Assert.AreEqual(0, result.Results.Count);
        papi.Verify(value => value.PatronRegistrationCreate(It.IsAny<PatronRegistrationParams>()), Times.Never);
    }

    [TestMethod]
    public void LiveHarness_ConfirmedCreateIsPersistedBeforeDownstreamFailure()
    {
        var manifestPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"patron-registration-live-{Guid.NewGuid():N}.json");
        try
        {
            var configuration = new LiveDevelopmentConfiguration(
                "https://polaris-development.clcohio.org",
                "synthetic-access-id",
                "synthetic-access-key",
                OrganizationId: 1,
                LibraryId: 2,
                BranchId: 3,
                PatronCodeId: 4,
                LogonUserId: 5,
                EcardPatronCodeId: 6,
                StudentPatronCodeId: 7,
                TeacherPatronCodeId: 8,
                School: "Synthetic School",
                ManifestPath: manifestPath);
            var realPapi = new Mock<IPapiClient>();
            const int patronId = 913579;
            const string barcode = "SYNTHETIC-BARCODE-913579";
            realPapi.Setup(value => value.PatronRegistrationCreate(It.IsAny<PatronRegistrationParams>()))
                .Returns(new RestResponse<PatronRegistrationCreateResult>
                {
                    Data = new PatronRegistrationCreateResult
                    {
                        PAPIErrorCode = 0,
                        PatronID = patronId,
                        Barcode = barcode
                    }
                });

            using var store = new LiveAttemptStore(manifestPath);
            using var harness = LiveSubmissionHarness.Create(configuration, realPapi.Object, store);
            var observedCreatedBeforeFailure = false;
            harness.db.Setup(value => value.AddRegistrationHistoryEntry(It.IsAny<RegistrationHistoryEntry>()))
                .Callback<RegistrationHistoryEntry>(_ =>
                {
                    var latest = store.History.Last();
                    var manifest = File.ReadAllText(manifestPath);
                    observedCreatedBeforeFailure =
                        latest.CreateState == LiveCreateState.Created &&
                        latest.ScenarioState == LiveScenarioState.Running &&
                        manifest.Contains("\"CreateState\": \"Created\"", StringComparison.Ordinal) &&
                        manifest.Contains("\"ScenarioState\": \"Running\"", StringComparison.Ordinal);
                    throw new InvalidOperationException("synthetic downstream failure");
                })
                .Returns(false);

            var identity = new LiveIdentity("v1.2.3", new string('a', 40), "run", 1, "invocation");
            var scenario = harness.BuildScenario("standard", identity);
            var result = LiveDevelopmentGateRunner.Run([scenario], identity, store);
            var final = result.Results.Last();
            var manifestAfterFailure = File.ReadAllText(manifestPath);

            Assert.IsTrue(observedCreatedBeforeFailure);
            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(LiveCreateState.Created, final.CreateState);
            Assert.AreEqual(LiveScenarioState.Failed, final.ScenarioState);
            Assert.AreEqual("finalization-failed", final.FailureClass);
            Assert.IsTrue(store.History.Any(value =>
                value.CreateState == LiveCreateState.Created &&
                value.ScenarioState == LiveScenarioState.Running));
            StringAssert.Contains(manifestAfterFailure, "\"CreateState\": \"Created\"");
            StringAssert.Contains(manifestAfterFailure, "\"ScenarioState\": \"Failed\"");
            Assert.IsFalse(manifestAfterFailure.Contains(patronId.ToString(), StringComparison.Ordinal));
            Assert.IsFalse(manifestAfterFailure.Contains(barcode, StringComparison.Ordinal));
            realPapi.Verify(value => value.PatronRegistrationCreate(It.IsAny<PatronRegistrationParams>()), Times.Once);
        }
        finally
        {
            if (File.Exists(manifestPath))
            {
                File.Delete(manifestPath);
            }

            var temporaryPath = manifestPath + ".tmp";
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static LiveDevelopmentConfiguration SyntheticConfiguration() =>
        new(
            "https://polaris-development.clcohio.org",
            "synthetic-access-id",
            "synthetic-access-key",
            OrganizationId: 1,
            LibraryId: 2,
            BranchId: 3,
            PatronCodeId: 4,
            LogonUserId: 5,
            EcardPatronCodeId: 6,
            StudentPatronCodeId: 0,
            TeacherPatronCodeId: 0,
            School: "Synthetic School",
            ManifestPath: "");

    private static Dictionary<string, string> SyntheticFormValues(int branchId, string scenario, string token) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            [nameof(Registration.PatronBranchID)] = branchId.ToString(),
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
            [nameof(Registration.User1)] = scenario == "ecard" ? "Synthetic School" : string.Empty
        };

    private sealed class LiveSubmissionHarness : IDisposable
    {
        private readonly ServiceProvider provider;
        private readonly DefaultHttpContext httpContext;
        internal readonly Mock<IDbHelper> db;
        private readonly Mock<IMelissaClientFactory> melissaFactory;
        private readonly Mock<IEmailSenderFactory> emailFactory;
        private readonly Mock<IPapiClient> forwardingPapi;
        private readonly LiveDevelopmentConfiguration configuration;
        private readonly LiveAttemptStore attemptStore;
        private readonly Func<string, string, Dictionary<string, string>>? formValuesFactory;
        private readonly Action<string, Mock<ISettingProvider>>? configureSettings;
        private readonly Dictionary<string, ScenarioContext> scenarioContexts = new(StringComparer.Ordinal);
        private PatronRegistrationParams? lastParameters;
        private IRestResponse<PatronRegistrationCreateResult>? lastResponse;
        private LivePublicResult? currentAttempt;
        private LivePublicResult? confirmedCreateRecord;
        private bool createInvoked;

        private sealed record ScenarioContext(
            ISettingProvider Settings,
            RegistrationController Controller);

        private sealed record PreparedScenario(
            ScenarioContext Context,
            Registration Registration,
            string Token);

        private sealed class ScenarioRequestServices(
            IServiceProvider inner,
            ISettingProvider settings) : IServiceProvider
        {
            public object? GetService(Type serviceType) =>
                serviceType == typeof(ISettingProvider)
                    ? settings
                    : inner.GetService(serviceType);
        }

        private LiveSubmissionHarness(
            ServiceProvider provider,
            DefaultHttpContext httpContext,
            Mock<IDbHelper> db,
            Mock<IMelissaClientFactory> melissaFactory,
            Mock<IEmailSenderFactory> emailFactory,
            Mock<IPapiClient> forwardingPapi,
            LiveDevelopmentConfiguration configuration,
            LiveAttemptStore attemptStore,
            Func<string, string, Dictionary<string, string>>? formValuesFactory,
            Action<string, Mock<ISettingProvider>>? configureSettings)
        {
            this.provider = provider;
            this.httpContext = httpContext;
            this.db = db;
            this.melissaFactory = melissaFactory;
            this.emailFactory = emailFactory;
            this.forwardingPapi = forwardingPapi;
            this.configuration = configuration;
            this.attemptStore = attemptStore;
            this.formValuesFactory = formValuesFactory;
            this.configureSettings = configureSettings;
        }

        public static LiveSubmissionHarness Create(
            LiveDevelopmentConfiguration configuration,
            IPapiClient realPapi,
            LiveAttemptStore attemptStore,
            Func<string, string, Dictionary<string, string>>? formValuesFactory = null,
            Action<string, Mock<ISettingProvider>>? configureSettings = null)
        {
            var settings = LiveSettings(configuration, "standard").Object;
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

            var harness = new LiveSubmissionHarness(
                provider, httpContext, db, melissaFactory, emailFactory, forwardingPapi,
                configuration, attemptStore, formValuesFactory, configureSettings);
            forwardingPapi.Setup(value => value.PatronRegistrationCreate(It.IsAny<PatronRegistrationParams>()))
                .Returns((PatronRegistrationParams parameters) =>
                {
                    harness.createInvoked = true;
                    harness.lastParameters = null;
                    harness.lastResponse = null;
                    var response = realPapi.PatronRegistrationCreate(parameters);
                    // Capture the response immediately; later controller finalization
                    // must never cause another create call.
                    harness.lastParameters = parameters;
                    harness.lastResponse = response;
                    if (harness.HasConfirmedCreateResponse && harness.currentAttempt is not null)
                    {
                        // Persist only the public-safe marker before returning to the
                        // production controller, which may still fail during finalization.
                        harness.confirmedCreateRecord = harness.attemptStore.Transition(
                            harness.currentAttempt,
                            LiveCreateState.Created,
                            LiveScenarioState.Running);
                    }

                    return response;
                });
            return harness;
        }

        internal ISettingProvider SettingsFor(string name) => GetScenarioContext(name).Settings;

        internal PatronRegistrationParams? LastParameters => lastParameters;

        internal ModelStateDictionary ModelStateFor(string name) => GetScenarioContext(name).Controller.ModelState;

        public LiveScenarioDefinition BuildScenario(string name, LiveIdentity identity)
        {
            var token = LiveIdentityFactory.SyntheticToken(identity, name);
            var context = GetScenarioContext(name);
            PreparedScenario? prepared = null;
            return new LiveScenarioDefinition(
                name,
                () => (prepared = PrepareScenario(context, name, token)) is not null,
                marker => Submit(name, marker, prepared),
                outcome => outcome.PatronId > 0 && !string.IsNullOrWhiteSpace(outcome.Barcode) &&
                    outcome.FinalizationSucceeded &&
                    lastParameters?.NameLast.StartsWith(token, StringComparison.Ordinal) == true);
        }

        private PreparedScenario? PrepareScenario(ScenarioContext context, string name, string token)
        {
            if (!Regex.IsMatch(token, "^[A-F0-9]{12}$", RegexOptions.CultureInvariant) || token.Length + name.Length > 30)
            {
                return null;
            }

            if (!ScenarioSettingsAreValid(name, context.Settings))
            {
                return null;
            }

            var registration = Bind(context, BuildFormValues(name, token));
            if (registration is null || !ScenarioInputIsValid(name, registration) ||
                context.Controller.PrepareSubmission(registration) is not null)
            {
                return null;
            }

            return new PreparedScenario(context, registration, token);
        }

        private LiveCreateOutcome Submit(string scenario, LivePublicResult marker, PreparedScenario? prepared)
        {
            // A successful previous scenario must never be reused if this scenario
            // exits before invoking PAPI.
            lastParameters = null;
            lastResponse = null;
            currentAttempt = null;
            confirmedCreateRecord = null;
            createInvoked = false;
            if (prepared is null || !string.Equals(prepared.Token, marker.SyntheticToken, StringComparison.Ordinal))
            {
                return LiveCreateOutcome.NotAttempted();
            }

            currentAttempt = marker;
            RegistrationAttempt attempt;
            try
            {
                attempt = prepared.Context.Controller.ExecutePreparedSubmission(prepared.Registration);
            }
            catch
            {
                return OutcomeAfterFinalizationFailure();
            }

            if (attempt.Status != RegistrationStatus.Success)
            {
                return OutcomeAfterFinalizationFailure();
            }

            return ClassifyCreateResponse();
        }

        private ScenarioContext GetScenarioContext(string name)
        {
            if (scenarioContexts.TryGetValue(name, out var existing))
            {
                return existing;
            }

            var settings = LiveSettings(configuration, name);
            configureSettings?.Invoke(name, settings);
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

            // Each scenario gets its own effective settings and resolver while
            // sharing the harness's non-mutating MVC infrastructure.
            var scenario = new ScenarioContext(settings.Object, controller);
            scenarioContexts.Add(name, scenario);
            return scenario;
        }

        private Registration? Bind(
            ScenarioContext context,
            IReadOnlyDictionary<string, string> values)
        {
            var body = string.Join("&", values.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
            var bytes = Encoding.UTF8.GetBytes(body);
            var originalRequestServices = httpContext.RequestServices;
            // Registration's model-bound constructor obtains ISettingProvider
            // from RequestServices. Scope that lookup to this scenario so MVC
            // binding, selected-branch revalidation, and execution all see the
            // same settings instance.
            httpContext.RequestServices = new ScenarioRequestServices(
                originalRequestServices, context.Settings);
            try
            {
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
                context.Controller.ControllerContext = new ControllerContext(actionContext);
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
                    context.Controller.ModelState.IsValid
                        ? registration
                        : null;
            }
            finally
            {
                httpContext.RequestServices = originalRequestServices;
            }
        }

        private static bool ScenarioSettingsAreValid(string name, ISettingProvider settings) => name switch
        {
            "standard" => string.IsNullOrWhiteSpace(settings.SchoolInfoFormat) &&
                settings.PatronCodeId > 0 && settings.RegistrationLogonUserId > 0,
            "school" => string.Equals(settings.SchoolInfoFormat, "uapl", StringComparison.OrdinalIgnoreCase) &&
                settings.PatronCodeId > 0 && settings.RegistrationLogonUserId > 0,
            "ecard" => string.Equals(settings.SchoolInfoFormat, "uapl", StringComparison.OrdinalIgnoreCase) &&
                settings.EcardPatronCodeId > 0 && !string.IsNullOrWhiteSpace(settings.EcardBarcodePrefix) &&
                settings.RegistrationLogonUserId > 0,
            _ => false
        };

        private static bool ScenarioInputIsValid(string name, Registration registration) => name switch
        {
            "standard" => !registration.IsStudent && !registration.IsTeacher && !registration.IsECard &&
                string.IsNullOrWhiteSpace(registration.User1),
            "school" => !registration.IsStudent && !registration.IsTeacher && !registration.IsECard &&
                string.IsNullOrWhiteSpace(registration.User1),
            "ecard" => registration.IsECard && !string.IsNullOrWhiteSpace(registration.User1),
            _ => false
        };

        private bool HasConfirmedCreateResponse =>
            lastResponse?.Data is not null &&
            lastResponse.Data.PAPIErrorCode >= 0 &&
            lastResponse.Data.PatronID > 0 &&
            !string.IsNullOrWhiteSpace(lastResponse.Data.Barcode);

        private LiveCreateOutcome OutcomeAfterFinalizationFailure() =>
            HasConfirmedCreateResponse && confirmedCreateRecord is not null
                ? LiveCreateOutcome.CreatedWithFailedFinalization(
                    lastResponse!.Data!.PatronID,
                    lastResponse.Data.Barcode,
                    confirmedCreateRecord)
                : ClassifyCreateResponse();

        private LiveCreateOutcome ClassifyCreateResponse()
        {
            if (!createInvoked)
            {
                return LiveCreateOutcome.NotAttempted();
            }

            var data = lastResponse?.Data;
            if (data is null)
            {
                return LiveCreateOutcome.Unknown();
            }

            var hasPatronId = data.PatronID > 0;
            var hasBarcode = !string.IsNullOrWhiteSpace(data.Barcode);
            if (hasPatronId || hasBarcode)
            {
                return HasConfirmedCreateResponse && confirmedCreateRecord is not null
                    ? LiveCreateOutcome.Created(data.PatronID, data.Barcode, confirmedCreateRecord)
                    : LiveCreateOutcome.Unknown();
            }

            return data.PAPIErrorCode < 0
                ? LiveCreateOutcome.Rejected()
                : LiveCreateOutcome.Unknown();
        }

        private Dictionary<string, string> BuildFormValues(string scenario, string token) =>
            formValuesFactory?.Invoke(scenario, token) ??
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
                [nameof(Registration.User1)] = scenario == "ecard" ? "Synthetic School" : string.Empty
            };

        private static Mock<ISettingProvider> LiveSettings(
            LiveDevelopmentConfiguration configuration,
            string scenario)
        {
            var settings = new Mock<ISettingProvider>();
            settings.SetupGet(value => value.OrganizationId).Returns(configuration.OrganizationId);
            settings.SetupGet(value => value.LibraryId).Returns(configuration.LibraryId);
            settings.SetupGet(value => value.PatronCodeId)
                .Returns(scenario == "ecard" ? 0 : configuration.PatronCodeId);
            settings.SetupGet(value => value.RegistrationLogonUserId).Returns(configuration.LogonUserId);
            settings.SetupGet(value => value.EcardPatronCodeId)
                .Returns(scenario == "ecard" ? configuration.EcardPatronCodeId : 0);
            // The live matrix deliberately submits neither role. Keep those
            // unrelated identifiers out of every effective scenario settings
            // object, even when a synthetic configuration supplies them.
            settings.SetupGet(value => value.StudentPatronCodeId).Returns(0);
            settings.SetupGet(value => value.TeacherPatronCodeId).Returns(0);
            settings.SetupGet(value => value.SchoolInfoFormat)
                .Returns(scenario is "school" or "ecard" ? "uapl" : string.Empty);
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
    public void ConfigurationRequirements_FollowSelectedScenarios()
    {
        var standardEnvironment = BaseEnvironment();
        standardEnvironment.Remove("PATRON_REGISTRATION_PAPI_ECARD_PATRON_CODE_ID");
        var standard = LiveDevelopmentConfiguration.FromEnvironment(
            ["standard"], standardEnvironment);

        Assert.AreEqual(0, standard.EcardPatronCodeId);
        Assert.AreEqual(0, standard.StudentPatronCodeId);
        Assert.AreEqual(0, standard.TeacherPatronCodeId);
        Assert.AreEqual("School", standard.School);

        var school = LiveDevelopmentConfiguration.FromEnvironment(
            ["school"], standardEnvironment);
        Assert.AreEqual(0, school.StudentPatronCodeId);
        Assert.AreEqual(0, school.TeacherPatronCodeId);

        Assert.ThrowsException<InvalidOperationException>(() =>
            LiveDevelopmentConfiguration.FromEnvironment(["ecard"], standardEnvironment));

        standardEnvironment["PATRON_REGISTRATION_PAPI_ECARD_PATRON_CODE_ID"] = "22";
        standardEnvironment.Remove("PATRON_REGISTRATION_PAPI_PATRON_CODE_ID");
        var ecard = LiveDevelopmentConfiguration.FromEnvironment(
            ["ecard"], standardEnvironment);
        Assert.AreEqual(0, ecard.PatronCodeId);
        Assert.AreEqual(22, ecard.EcardPatronCodeId);

        Assert.ThrowsException<InvalidOperationException>(() =>
            LiveDevelopmentConfiguration.FromEnvironment([], standardEnvironment));
        Assert.ThrowsException<InvalidOperationException>(() =>
            LiveDevelopmentConfiguration.FromEnvironment(["diagnostic"], standardEnvironment));
    }

    [TestMethod]
    public void SelectedSpecializedIdentifierMissing_FailsBeforeHarnessCanCreate()
    {
        var environment = BaseEnvironment();
        environment.Remove("PATRON_REGISTRATION_PAPI_PATRON_CODE_ID");

        Assert.ThrowsException<InvalidOperationException>(() =>
            LiveDevelopmentConfiguration.FromEnvironment(["standard"], environment));
    }

    [TestMethod]
    public void Configuration_RerunGuardRemainsFailClosed()
    {
        var environment = BaseEnvironment();
        environment["GITHUB_RUN_ATTEMPT"] = "2";

        Assert.ThrowsException<InvalidOperationException>(() =>
            LiveDevelopmentConfiguration.FromEnvironment(["standard"], environment));
    }

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
    public void InconclusiveCreateResponse_IsRecordedUnknownAndNeverRetried()
    {
        var count = 0;
        var scenarios = new[]
        {
            Definition("standard", () => true, () => { count++; return LiveCreateOutcome.Unknown(); }),
            Definition("school", () => true, () => { count++; return LiveCreateOutcome.Created(2, "hidden"); })
        };

        var result = Run(scenarios);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(1, count);
        Assert.AreEqual(LiveCreateState.Unknown, result.Results.Last().CreateState);
        Assert.AreEqual(LiveScenarioState.Failed, result.Results.Last().ScenarioState);
    }

    [TestMethod]
    public void PreCreateFailure_IsRecordedNotAttemptedAndNeverRetried()
    {
        var count = 0;
        var scenarios = new[]
        {
            Definition("standard", () => true, () => { count++; return LiveCreateOutcome.NotAttempted(); }),
            Definition("school", () => true, () => { count++; return LiveCreateOutcome.Created(2, "hidden"); })
        };

        var result = Run(scenarios);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(1, count);
        Assert.AreEqual(LiveCreateState.NotAttempted, result.Results.Last().CreateState);
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

    private static Dictionary<string, string?> BaseEnvironment() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["PATRON_REGISTRATION_LIVE_TESTS"] = "true",
        ["PATRON_REGISTRATION_PAPI_HOST"] = "https://polaris-development.clcohio.org",
        ["PATRON_REGISTRATION_PAPI_ACCESS_ID"] = "access-id",
        ["PATRON_REGISTRATION_PAPI_ACCESS_KEY"] = "access-key",
        ["PATRON_REGISTRATION_PAPI_ORGANIZATION_ID"] = "1",
        ["PATRON_REGISTRATION_PAPI_LIBRARY_ID"] = "2",
        ["PATRON_REGISTRATION_PAPI_BRANCH_ID"] = "3",
        ["PATRON_REGISTRATION_PAPI_PATRON_CODE_ID"] = "4",
        ["PATRON_REGISTRATION_PAPI_LOGON_USER_ID"] = "5"
    };

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
