# Patron-registration settings administration

The MVC administration interface is rooted at `/settings`. It uses the existing Dapper and `RegistrationFormSettings` architecture; it does not use EF Core or run database migrations at startup.

## Authorization and scope

Access requires authentication, the configurable `SettingsAdministration:RequiredRole` (default `Clc.CardReg.ManageSettings`), and an integer organization claim named `organization`, `organization_id`, or `extension_Organization`. The configured global organization (default `-1`) can select system, library, and branch scopes. A library administrator can select only its library and branches. Scope and form-code authorization is repeated for every read and write; selector values are not treated as authorization.

The single configured system organization, `SettingsAdministration:SystemOrganizationId`, defaults to `1`. That same options value is passed to live `DbSettingProvider`, administration resolution, and preview overlays; there is no separate registration system-ID setting. Non-global administrators never receive sensitive catalog definitions, values, hidden fields, or search data.

## Resolution, catalog, and overrides

`SettingsResolver` evaluates, in order: named branch, default branch, named library, default library, named system, and default system. Inapplicable levels are omitted. `DbSettingProvider`, required-field lookup, administration display, and preview overlay all use this precedence.

The code-defined catalog is the write allowlist. It defines editor type, validation, sensitivity, empty-value behavior, and recognized dynamic suffixes. String-like values can be explicitly empty. Boolean and non-null numeric/date values cannot. Nullable integer/date settings store an empty string and `DbSettingProvider` consistently converts that representation to `null`. Removing an override is always a distinct operation.

Fields decorated with ASP.NET `[Required]` are globally required for every organization and form and are never exposed through `require.*`. Dynamic `require.*` administration is limited to the properties that actually use `[DbConfiguredRequired]`: `PhoneVoice1`, `ReceiveEreceipts`, `EmailAddress`, and `User5`. Configurable `label.*` entries remain available for supported display-name fields. `alert.*` values remain stored and administrable for future use, but applying them to validation messages is deferred and is not part of the current runtime behavior.

`add_to_record_set_id` is a nullable positive integer: missing and explicitly empty values disable the post-registration record-set action, positive values select the record set, and zero or negative values fail catalog validation. Legacy malformed nullable values are converted to `null` rather than throwing after patron creation.

Configured patron-code and registration-logon identifiers must be positive. Record-set settings whose existing runtime contract uses zero as “disabled” accept zero but reject negative values; positive values select the Polaris record set. This prevents an administratively supplied negative identifier from reaching a post-creation PAPI call.

Runtime side-effect guards are independent of catalog validation for legacy rows: every record-set operation requires both a positive patron ID and a positive record-set ID. Missing, malformed, zero, or negative optional identifiers are skipped, and negative legacy configuration produces only a safe key-level configuration log without credentials or secret values.

## Direct save and drafts

Direct save submits only browser-edited rows using ASP.NET Core `Changes.Index` tokens, displays an exact confirmation, validates every key and value again, locks the scope version, applies all operations in one transaction, writes audit events, increments scope and cache generations, and immediately rebuilds local live settings.

One active shared draft is enforced by a filtered unique database index for each organization/form-code scope. Authorized administrators can create or reopen it, stage Upsert or RemoveOverride changes, commit, or discard it. Commit revalidates the catalog, compares the live scope version with the draft baseline, atomically applies changes, increments versions, marks the draft committed, revokes links, audits, and invalidates live cache. Discard revokes links without changing live cache.

If a global administrator stages a sensitive change, library administrators see only a generic restricted-changes notice. They may continue editing non-sensitive rows, but cannot commit, discard, preview, revoke, toggle, or remove restricted mutations. Every action rechecks the actual draft mutations server-side, and rejected audit events contain no sensitive key or value.

These lifecycle checks are repeated inside each serializable Dapper transaction while the active draft is locked, closing the race between the controller check and persistence. When a draft first becomes restricted, that same draft-edit transaction revokes every existing preview link and writes only the generic `PreviewLinksRevokedForRestrictedDraft` audit event. Later sensitive edits do not repeatedly revoke replacement links created by a global administrator, and removed links are never automatically reactivated.

## Preview links

An active draft can issue an unauthenticated bearer URL. Every link persists an operational branch: a branch draft uses itself, a library draft requires one of that library's branches, and a system draft requires an explicitly selected valid branch. Eligible branches come from the same `GetSelfRegistrationOrganizations` source used by normal registration. The branch binding and current eligibility are revalidated on every preview request and control form construction, duplicate checks, and live submission. The rendered home branch is locked to this one branch. The plaintext token is returned once; the database receives only its SHA-256 hash. Tokens contain 256 cryptographically random bits and use URL-safe encoding. Preview lookup rejects revoked, expired, committed, discarded, invalidated, ineligible, and invalid-branch links. Responses use `no-store` and `Referrer-Policy: no-referrer`.

Token resolution reads one immutable link-and-draft snapshot. The initial candidate draft-ID lookup occurs before the serializable transaction and is not trusted. The transaction then locks and validates the draft, authoritatively re-reads and locks the link, and reads every mutation in that order before releasing the locks. Preview mutation, revocation, toggle, commit, discard, and deletion paths use the same draft-before-link order. This prevents both deadlocks and a request combining a pre-revocation link read with the restricted draft that caused its revocation.

Preview rendering uses the real registration view and a `PreviewSettingProvider`. Draft mutations are overlaid only at the draft scope, while effective values are resolved at the operational branch through the normal branch, library, and system hierarchy. Consequently, lower-scope overrides correctly mask a library or system draft change. Safe preview performs rendering, client validation, read-only duplicate checks, and driver-license parsing, but its final POST returns a blocked result without calling patron creation, sending email, writing normal success history, or performing other registration side effects. When the database link's `AllowLiveSubmission` flag is enabled, POST revalidates the token and active draft and runs the existing real workflow. The page prominently identifies live mode. No browser Boolean controls this decision.

MVC `ModelState` is authoritative for normal and live-preview submissions before Melissa, PAPI, Postmark, history, notes, record sets, or patron mutations can run. Safe preview returns the same keyed MVC errors to the browser but never continues into the final workflow; a valid safe submission reports that MVC validation passed while final submission remained blocked.

After endpoint routing and before MVC controller activation, `PreviewRequestContextMiddleware` resolves the bearer token, active draft, operational branch, eligibility, revocation, and expiration into one scoped preview context. The scoped `ISettingProvider` returns that context's `PreviewSettingProvider`; an invalid preview never falls back to live system defaults. This means registration construction, MVC model binding, `label.*` metadata, supported `require.*` validation, Razor tag helpers, duplicate checks, and final submission all see the same draft overlay. Scoped Melissa and Postmark client factories select credentials from this same provider, so staged credential changes apply only to the validated preview request.

Treat every preview URL as a credential. Revoke it immediately if shared incorrectly. Enabling live mode permits real PAPI, Melissa, Postmark, record-set, note, and registration-history effects.

The application replaces the preview request path and raw target with `/preview/[redacted]` immediately after routing, before preview resolution and MVC execution, so downstream application diagnostics do not retain the bearer token. The shared URL still contains the bearer token as a path segment and may be observed by IIS, a reverse proxy, an APM agent that records requests before ASP.NET middleware, or browser history. Production operators **must** disable path capture or configure explicit `/preview/*` path redaction in every upstream access log, proxy, WAF, tracing, and monitoring layer. This upstream configuration is required for the no-plaintext-token logging guarantee; the application cannot retroactively redact logs written before its middleware executes.

The one-time token-display response sets `Cache-Control: no-store, no-cache, max-age=0`, `Pragma: no-cache`, and `Referrer-Policy: no-referrer`. The plaintext URL is returned only as that response model and is never placed in TempData, session, audit metadata, logs, or exception text.

## Form codes

The empty default code is implicit and cannot be created, renamed, or deleted. Named codes allow letters, numbers, hyphens, and underscores and are immutable. Global administrators can own system metadata; library administrators can own metadata only at their library. Library metadata using an existing system code customizes its display name and description without changing the system definition.

Distinct nonblank codes already present in `RegistrationFormSettings` are included even when metadata has never been deployed, including `kiosk`. One centralized availability service supplies both selectors and server authorization, so displayed legacy codes can be inspected and edited before adoption because they already affect production. They appear as legacy/unregistered and can be explicitly adopted into authorized system or library metadata. Branch-owned rows are mapped to their library with the existing organization cache, and codes from another library remain unavailable. Adoption creates metadata only and never copies inherited settings.

Deletion requires an impact page showing the owner, definition/customization type, legacy status, affected organizations, and metadata/override/draft/link counts. The serializable transaction locks drafts, then preview links, then revalidates ownership from settings and metadata before deleting anything. A system deletion is rejected unless a system metadata or system-owned legacy definition exists, so it cannot erase independently owned library codes with the same string. Library deletion is restricted to that library and its branches; removing a library customization resumes system inheritance. Creating metadata never copies inherited setting rows.

## Audit and cache consistency

Audit events record actor ID/name/organization, target organization/library/form, setting changes, request correlation ID, IP address, result, and failure reason where available. Library audit searches filter by `TargetLibraryId` and exclude `IsSensitive` rows in the database query before applying the search term, so sensitive keys and values cannot match or reach the view. Global searches include sensitive rows and their keys, but values remain masked. Non-sensitive audit values use `nvarchar(max)` so validated HTML and templates are not truncated. Postmark and Melissa values pass through `SensitiveValueMasker` before persistence. Registration-history settings snapshots omit every catalog-sensitive provider property.

Every live mutation increments `RegistrationSettingsCacheGeneration` and immediately rebuilds the current process cache. A hosted worker compares the database generation at `GenerationCheckSeconds` intervals and rebuilds when another process changes it. Rebuild and generation checks are serialized. Draft-only edits do not change live cache.

## Manual database deployment

1. Back up `clcdb` and verify the existing `dbo.RegistrationFormSettings` table.
2. Run [`database/001-settings-administration.sql`](database/001-settings-administration.sql) against `clcdb`.
3. Run [`database/002-preview-operational-branch.sql`](database/002-preview-operational-branch.sql); on upgraded installations it revokes legacy unbound links before requiring an operational branch.
4. Run [`database/003-expand-audit-setting-values.sql`](database/003-expand-audit-setting-values.sql).
5. Apply the least-privilege grants described in [`database/README.md`](database/README.md).
6. Configure Azure AD, role assignments, organization IDs, SQL access, and existing external services.
7. Deploy the application.

Production requires HTTPS; the application redirects to HTTPS and enables HSTS outside Development. All SQL scripts are manual and idempotent when applied in order. The application never applies production schema changes automatically.
