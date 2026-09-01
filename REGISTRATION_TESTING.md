# Registration testing and release validation

The repository has one broad deterministic test suite and one small live
contract smoke test.

Deterministic CI runs on pushes and pull requests. It starts isolated SQL
Server for the database convergence tests, runs the JavaScript checks, and
runs the complete .NET suite through `test.sh`. The suite uses real MVC
binding, selected-branch revalidation, registration transformations, and the
full registration pipeline. PAPI, Melissa, email, and other external effects
are replaced with test doubles; the captured PAPI request is asserted without
creating a patron. The standard, school/User1, e-card, student, teacher,
branch, validation, binding, and transformation matrix belongs here.

`test.sh` always excludes `LiveDevelopment` from ordinary runs. A raw filter
that names that category is rejected. Live mode is a separate explicit entry
point and requires `PATRON_REGISTRATION_LIVE_TESTS=true`.

## DEVELOPMENT smoke validation

The version-tag workflow resolves the exact tag commit, checks that the tag
still points to that commit, requires the commit to be in current `master`,
runs deterministic CI, and then waits for the protected
`live-development-tests` Environment. Immediately before mutation it repeats
only the tag-resolution and `master`-ancestry checks needed for a realistic
TOCTOU change.

The live job runs one serial `standard` registration. It is deliberately a
representative non-school contract: the deterministic suite owns the school,
student, teacher, and e-card permutations, and those permutations do not need
separate live records to prove application behavior. The smoke test adds value
by sending the final transformed request to the approved DEVELOPMENT Polaris
host and checking the real endpoint contract.

Before the create boundary, the test:

1. requires explicit opt-in and valid live configuration;
2. requires HTTPS and an exact host in the committed DEVELOPMENT allowlist;
3. performs the read-only `ApiKeyValidate` call;
4. binds the synthetic form through the real MVC model binder; and
5. resolves the selected branch and runs the controller's non-mutating
   preparation path.

Only after all preflight steps pass does it write one PII-safe pre-create
breadcrumb and invoke `PatronRegistrationCreate`. The call is made at most
once. A confirmed response containing both a positive patron ID and barcode is
`ConfirmedCreated`; a definite negative PAPI error without either value is
`Rejected`; a missing, partial, malformed, or exceptional result after the
call is `Ambiguous`. Ambiguous means “possibly created”: the workflow never
retries it. A pre-create failure is `NotAttempted`.

The result artifact contains only the release tag, commit, synthetic
investigation token, UTC timestamp, and safe outcome. It never
contains patron IDs, barcodes, credentials, raw PAPI responses, or personal
information. Use the tag, commit, token, timestamp, and job log breadcrumb to
locate and clean up a synthetic DEVELOPMENT record through the approved
internal process before any recovery action. Do not rerun a possibly-created
attempt.

An ordinary GitHub Actions rerun has `github.run_attempt > 1`; the mutating job
is skipped before its runner, Environment, or secrets are selected, and the
final result job fails the workflow. Live runs use a dedicated concurrency
group, `DoNotParallelize`, a dedicated runner label, and a 30-minute timeout.

## Repository and GitHub policy

The workflow intentionally relies on repository governance for durable release
policy. Before enabling live validation, administrators must configure and
verify:

1. normal `master` branch protection or an equivalent ruleset;
2. required deterministic CI for changes to `master`;
3. a `v*` tag ruleset that restricts creation to trusted release actors,
   prohibits or restricts tag modification, and prohibits deletion;
4. the protected `live-development-tests` Environment, with required reviewers
   where appropriate;
5. DEVELOPMENT-only secrets and variables in that Environment; and
6. a dedicated, restricted self-hosted runner group available only to the
   approved live workflow.

If these controls cannot be established, leave the live job disabled or
unusable. The workflow may verify what tag and commit it is testing, but it
does not use GitHub Actions history or repository files as a uniqueness
database.

## Production boundary

Registration history uses the `IDbHelper` passed through the registration flow;
registration processing does not depend on `DbHelper.Global`. PAPI response
logging records safe error-code information rather than credentials, raw
responses, IDs, or patron data. The controller's `PrepareSubmission` and
`ExecutePreparedSubmission` seam keeps non-mutating validation separate from
the mutating pipeline for both deterministic and live coverage.
