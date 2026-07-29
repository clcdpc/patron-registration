# Patron-registration settings administration

The MVC administration interface is rooted at `/settings`. It uses the existing Dapper and `RegistrationFormSettings` architecture; it does not use EF Core or run database migrations at startup.

## Authorization and scope

Access requires authentication, the configurable `SettingsAdministration:RequiredRole` (default `Clc.CardReg.ManageSettings`), and an integer organization claim named `organization`, `organization_id`, or `extension_Organization`. The configured global organization (default `-1`) can select system, library, and branch scopes. A library administrator can select only its library and branches. Scope and form-code authorization is repeated for every read and write; selector values are not treated as authorization.

The single configured system organization, `SettingsAdministration:SystemOrganizationId`, defaults to `1`. That same options value is passed to live `DbSettingProvider`, administration resolution, and preview overlays; there is no separate registration system-ID setting. Non-global administrators never receive sensitive catalog definitions, values, hidden fields, or search data.

## Resolution, catalog, and overrides

`SettingsResolver` evaluates, in order: named branch, default branch, named library, default library, named system, and default system. Inapplicable levels are omitted. `DbSettingProvider`, required-field lookup, administration display, and preview overlay all use this precedence.

The code-defined catalog is the write allowlist. It defines editor type, validation, sensitivity, empty-value behavior, and recognized dynamic suffixes. String-like values can be explicitly empty. Boolean and non-null numeric/date values cannot. Nullable integer/date settings store an empty string and `DbSettingProvider` consistently converts that representation to `null`. Removing an override is always a distinct operation.

## Direct save and drafts

Direct save submits only browser-edited rows using ASP.NET Core `Changes.Index` tokens, displays an exact confirmation, validates every key and value again, locks the scope version, applies all operations in one transaction, writes audit events, increments scope and cache generations, and immediately rebuilds local live settings.

One active shared draft is enforced by a filtered unique database index for each organization/form-code scope. Authorized administrators can create or reopen it, stage Upsert or RemoveOverride changes, commit, or discard it. Commit revalidates the catalog, compares the live scope version with the draft baseline, atomically applies changes, increments versions, marks the draft committed, revokes links, audits, and invalidates live cache. Discard revokes links without changing live cache.

## Preview links

An active draft can issue an unauthenticated bearer URL. Every link persists an operational branch: a branch draft uses itself, a library draft requires one of that library's branches, and a system draft requires an explicitly selected valid branch. The branch binding is revalidated on every preview request and controls form construction and live submission. The plaintext token is returned once; the database receives only its SHA-256 hash. Tokens contain 256 cryptographically random bits and use URL-safe encoding. Preview lookup rejects revoked, expired, committed, discarded, invalidated, and invalid-branch links. Responses use `no-store` and `Referrer-Policy: no-referrer`.

Preview rendering uses the real registration view and a `PreviewSettingProvider`: live inheritance is resolved first, draft Upserts replace the selected-scope row, and draft RemoveOverride operations expose the next inherited value. Safe preview performs rendering, client validation, read-only duplicate checks, and driver-license parsing, but its final POST returns a blocked result without calling patron creation, sending email, writing normal success history, or performing other registration side effects. When the database link's `AllowLiveSubmission` flag is enabled, POST revalidates the token and active draft and runs the existing real workflow. The page prominently identifies live mode. No browser Boolean controls this decision.

Treat every preview URL as a credential. Revoke it immediately if shared incorrectly. Enabling live mode permits real PAPI, Melissa, Postmark, record-set, note, and registration-history effects.

## Form codes

The empty default code is implicit and cannot be created, renamed, or deleted. Named codes allow letters, numbers, hyphens, and underscores and are immutable. Global administrators can own system metadata; library administrators can own metadata only at their library. Library metadata using an existing system code customizes its display name and description without changing the system definition.

Deletion requires an impact page showing metadata, override, draft, and preview-link counts. The transaction removes affected metadata, library/branch or global overrides, drafts, and links. Removing a library customization therefore resumes system metadata inheritance. Creating metadata never copies inherited setting rows.

## Audit and cache consistency

Audit events record actor ID/name/organization, target organization/library/form, setting changes, request correlation ID, IP address, result, and failure reason where available. Library audit searches filter by `TargetLibraryId`; global searches include all libraries. Postmark and Melissa values pass through `SensitiveValueMasker` before persistence and remain masked for global viewers.

Every live mutation increments `RegistrationSettingsCacheGeneration` and immediately rebuilds the current process cache. A hosted worker compares the database generation at `GenerationCheckSeconds` intervals and rebuilds when another process changes it. Rebuild and generation checks are serialized. Draft-only edits do not change live cache.

## Manual database deployment

1. Back up `clcdb` and verify the existing `dbo.RegistrationFormSettings` table.
2. Run [`database/001-settings-administration.sql`](database/001-settings-administration.sql) against `clcdb`.
3. Run [`database/002-preview-operational-branch.sql`](database/002-preview-operational-branch.sql); on upgraded installations it revokes legacy unbound links before requiring an operational branch.
4. Apply the least-privilege grants described in [`database/README.md`](database/README.md).
5. Configure Azure AD, role assignments, organization IDs, SQL access, and existing external services.
6. Deploy the application.

Production requires HTTPS; the application redirects to HTTPS and enables HSTS outside Development. The SQL script is manual and idempotent. The application never applies production schema changes automatically.
