# Registration testing and release validation

This repository has two deliberately different registration checks:

* Deterministic tests run in pull requests, ordinary branch pushes, and local
  development. They exercise the checked-out MVC binding, selected-branch
  revalidation, `Registration.CreateRegistration`, transformations, and final
  `PatronRegistrationParams` generation with mocked PAPI, Melissa, email, and
  database collaborators. They are repeatable and never create a patron in
  Polaris.
* The `LiveDevelopment` check is a small contract test run only by the release
  workflow (or an explicitly opted-in developer diagnostic). It uses the same
  checked-out application code and sends the final request to the approved
  DEVELOPMENT Polaris host. Melissa, email, history, and duplicate persistence
  remain substituted; DEVELOPMENT SQL is not needed for this contract.

Both are needed: deterministic tests catch application regressions without
shared-state risk, while the live check catches an incompatible final payload
or operational identifier that mocks cannot detect.

## Local entry points

`./test.sh` is the normal deterministic entry point. It runs the JavaScript
checks, then .NET tests with `TestCategory!=LiveDevelopment`. A caller may pass
ordinary `dotnet test` arguments; a supplied `--filter` is composed with the
mandatory live exclusion. Filters mentioning `LiveDevelopment` are rejected,
so credentials or a clever raw filter cannot turn an ordinary run into a live
mutation. The SQL integration tests use the existing
`PATRON_REGISTRATION_TEST_SQL_CONNECTION_STRING` convention; local runs do not
start DEVELOPMENT SQL. CI supplies an isolated SQL Server and fails if the
SQL-enabled run reports skipped tests.

The live entry point is:

```sh
PATRON_REGISTRATION_LIVE_TESTS=true ./test.sh --live-development
```

Live mode runs only the single categorized orchestration test. Raw `--filter`
arguments are rejected; use `PATRON_REGISTRATION_LIVE_SCENARIOS` for a deliberate
subset. The fixed allowlist is `standard,school,ecard`. An absent selector runs
all three in that order. Names are trimmed and case-folded, duplicates are
deduplicated, and empty or unknown names fail before any create.

The live test requires the common live configuration variables, credentials,
and `PATRON_REGISTRATION_LIVE_TESTS=true`. Operational requirements are aware
of the selected scenario set: `standard` requires the normal patron code,
`school` uses that same normal code and does not require student/teacher IDs
for its non-student/non-teacher contract, and `ecard` requires only the e-card
patron code for its specialized path. An ordinary `standard`-only diagnostic
therefore does not fail because school or e-card settings are absent. The
default matrix still validates the union of the settings used by all three
selected scenarios. The endpoint must be HTTPS and match the committed exact
host allowlist in `LiveDevelopmentConfiguration`; a hostname containing “dev”
is not sufficient.
The test performs the read-only `ApiKeyValidate` call before constructing an
attempt or calling `PatronRegistrationCreate`. It never falls back to a
production endpoint. Read-only setup has no automatic retry today; if a retry is
added, it must remain finite and read-only.

## Deterministic registration matrix

`RegistrationControllerSubmitIntegrationTests` binds a real form body through
ASP.NET MVC and invokes the real controller. The resolver is a deterministic
test double, but selected-branch revalidation is production MVC behavior. The
mock PAPI response is successful and captures the exact final payload; the
tests verify one create call and stable final fields. Coverage includes:

* a normalized/standard registration with school functionality disabled;
* school-enabled UAPL with empty `User1` for a non-student/non-teacher (the
  historical nullable-property regression);
* UAPL e-card `User1` clearing from the specialized e-card form;
* school student and teacher patron-code selection and school preservation; and
* a general e-card code, expiration, and generated barcode.

The historical mutation (`string? User1` to `string User1`) was reproduced once
against the school-enabled empty-`User1` test. MVC required validation rejected
the form; the nullable source was restored and the test passed again. No
mutation framework or alternate production path is committed.

Registration history now uses the `IDbHelper` passed through the production
flow. `DbHelper.Global` is not initialized by the web application and is not
read by registration processing. A static compatibility member remains only
for older tests/API consumers; new successful-path tests use the injected mock.

## Live orchestration and mutation safety

There is one `[TestCategory("LiveDevelopment")]`, `[DoNotParallelize]`
orchestration test. It constructs every selected scenario with its own
settings shape: `standard` is a true non-school baseline (`SchoolInfoFormat`
empty and empty `User1`), `school` is the UAPL empty-`User1` regression
contract, and `ecard` is the specialized UAPL e-card shape that supplies a
school value which the real transformation clears. Configuration is checked
against the selected scenario set before the harness starts.

For every selected scenario, preflight builds the actual form body, runs the
real MVC action-descriptor/parameter-metadata model binder and form value
provider, then invokes the controller's real selected-branch scope resolution
and MVC object revalidation. The successfully bound/prepared registration is
retained for execution, so the mutating phase does not rebuild a different
model. A binding, selected-branch validation, or other preflight failure means
zero creates across the whole selected set. Only after every preflight passes
do scenarios run serially; the first rejected, ambiguous, or downstream-failed
scenario stops the remaining matrix.

The live boundary is intentionally narrow:

* real: checked-out MVC/controller/registration code, transformations, final
  payload construction, and the real `PatronRegistrationCreate` request;
* substituted: Melissa, email/Postmark, duplicate/history persistence, and
  unrelated post-create collaborators; and
* settings: explicit common and scenario-required positive IDs are supplied by
  the environment, while the test applies each scenario's format and safe
  no-side-effect options. The forwarding `IPapiClient` exists only in test
  code, delegates the request unchanged to `PapiClient`, and captures the real
  response immediately.

The create call is made exactly once. There is no `Task.Run`/abandoned-request
timeout and no retry after a timeout, connection interruption, cancellation,
malformed response, or other ambiguous result. The current PAPI interface does
not expose a clean request-cancellation seam, so the release job's finite
timeout is the final guard; an interruption during create must be treated as
potentially ambiguous and investigated before any recovery.

Create state and scenario state are separate. The store records
`attempting/running` before each create, `created` immediately after a confirmed
positive response, and only then `passed` after downstream assertions. A
pre-create failure is `not_attempted/failed`; a safely proven API rejection is
`rejected/failed`; an invoked response that is malformed, partial, or otherwise
inconclusive is `unknown/failed` and is never retried. A later failure leaves
`created/failed`. The store appends transitions and atomically replaces the JSON
manifest after each transition, so earlier attempts survive later failures.

Every live identity is synthetic. The token is a short SHA-256 digest derived
from tag, resolved commit, scenario, and (only for local runs) an invocation ID;
an optional recovery nonce is included. The token is placed in a duplicate-
relevant name field and is constrained to supported characters. For a release,
`GITHUB_RUN_ID` and `GITHUB_RUN_ATTEMPT` are audit metadata only, so an ordinary
workflow rerun does not silently generate a new logical patron identity. Local
runs receive a random invocation ID.

Before every create the public-safe breadcrumb and manifest contain only the
scenario, token, tag, commit, run metadata, timestamp, and safe state/failure
classification. PatronID, barcode, credentials, authorization material,
connection strings, and real PII stay in memory and are never logged, placed in
the public manifest, or included in exceptions. The token is the investigation
and cleanup key for DEVELOPMENT staff; this task does not automatically delete
synthetic patrons.

## Workflows and release identity

`.github/workflows/test.yml` calls the reusable deterministic workflow for both
pushes and pull requests. The reusable workflow checks out its explicit
`commit-sha`, runs JavaScript and the normal .NET suite through `test.sh`, and
retains isolated SQL convergence coverage. It has `contents: read`, does not
receive live secrets, and never targets the live runner.

`.github/workflows/release-validation.yml` triggers on version-shaped `v*` tags
and is terminal validation (there is no deployment workflow here). It:

1. validates `vMAJOR.MINOR.PATCH` with an optional prerelease suffix;
2. peels the tag-push event SHA with `git rev-parse "${EVENT_SHA}^{commit}"`;
3. fetches the current remote tag into a separate ref and requires its peeled
   commit to equal the event commit (supporting lightweight and annotated tags
   and rejecting deleted/moved tags);
4. freshly fetches `origin/master` and requires the release commit to be an
   ancestor, but deliberately does not require it to equal the current master
   tip; and
5. passes that exact commit to deterministic CI and then, after it succeeds,
   the serialized live DEVELOPMENT job, which re-fetches and rechecks both the
   named tag and current `origin/master` ancestry immediately before live
   mutation.

Before the live job, a GitHub-hosted `live-rerun-guard` checks
`github.run_attempt` with no live Environment, secrets, or internal runner.
`live-development` also has a job-level first-attempt condition evaluated before
its runner, Environment, and secrets are selected. It depends on that guard as
well as deterministic validation, so an ordinary rerun is rejected before the
self-hosted live trust boundary is entered. A hosted
`release-validation-result` job always runs after all release stages and fails
unless this is the first attempt and every required stage succeeded. The live
job passes the resolved commit separately as
`PATRON_REGISTRATION_LIVE_COMMIT_SHA`; this keeps live breadcrumbs and synthetic
identity tied to the peeled release commit even when the pushed tag is annotated.

The live job uses the dedicated `live-development-tests` Environment, separate
environment-scoped secrets/variables, the `patron-registration-live-development`
self-hosted label, `cancel-in-progress: false`, `[DoNotParallelize]`, and a
30-minute outer timeout. Only the live job is globally serialized; deterministic
validation for different tags may run concurrently. The manifest upload is
best-effort with `if: always()` and cannot hide the test result. The workflow
checkout disables persisted credentials. Only the `Run serial DEVELOPMENT gate`
execution step receives PAPI credentials and live operational configuration;
checkout, .NET setup, tag revalidation, and artifact publication receive no live
credentials.

An ordinary GitHub rerun has `GITHUB_RUN_ATTEMPT > 1` and fails closed in the
GitHub-hosted prerequisite before live setup/mutation. The in-test
`GITHUB_RUN_ATTEMPT > 1` guard remains as defense in depth for local/manual
execution and future workflow changes. Operators must inspect the prior
public-safe manifest and breadcrumbs, locate any patron by synthetic token, and
use the normal approved DEVELOPMENT cleanup process. There is no automatic
recovery mode and a rerun is not an acknowledgement to repeat a non-idempotent
create.

## Required external controls

Repository YAML cannot configure organization policy. Before enabling the live
job, administrators must verify the `live-development-tests` Environment has
the intended required reviewers and release-tag deployment restrictions, and
that its secrets are separate from ordinary CI. A tag ruleset should restrict
creation, update, and deletion of `v*` release tags to trusted release actors;
the event-SHA/current-tag check remains required even with that ruleset.
`master` should have the normal review and required-CI branch protection. These
are assumptions to verify externally, not settings claimed by this repository.

Because this is a public repository, the live runner must be a dedicated,
ephemeral or tightly restricted runner group available only to this repository
and approved release workflow, never to arbitrary PR/branch workflows. The
label in the workflow is not proof that organization-level restrictions exist;
if they cannot be established, leave the live job blocked. Prefer first-party
actions and pin actions on the internal runner to verified immutable full SHAs.
The live job pins its first-party checkout, .NET setup, and artifact upload
actions to verified immutable full SHAs; future actions added to that trust
boundary must meet the same standard.

## Investigating a failed live attempt

Download the public-safe manifest and search the job log for the last
`live-registration attempt` breadcrumb. Use scenario, synthetic token, release
tag, commit, and UTC timestamp to locate/clean up the synthetic DEVELOPMENT
patron through the approved internal process. Treat `unknown` as “possibly
created,” never as “rejected,” and do not rerun the create until an operator has
reviewed the prior attempt and selected an explicitly approved recovery process.
