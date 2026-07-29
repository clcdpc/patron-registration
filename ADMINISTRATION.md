# Patron-registration settings administration

The MVC interface is rooted at `/settings`. Access requires authentication, the configurable `SettingsAdministration:RequiredRole` (default `Clc.CardReg.ManageSettings`), and an integer organization claim (`organization`, `organization_id`, or `extension_Organization`). Organization `-1` is the configurable global administrator; organization `1` is the configurable system scope. A library administrator is restricted server-side to its library and branches and never receives sensitive catalog entries. `UseAuthentication` runs before `UseAuthorization`.

## Resolution and overrides

One resolver evaluates named branch, default branch, named library, default library, named system, then default system rows. Inapplicable levels are omitted. A stored empty string remains an explicit override. **Remove override** is a separate delete operation and resumes inheritance; clearing an editor saves an empty override.

The code-defined catalog is the write allowlist and supplies editor types, validation, sensitivity, search metadata, and the centralized recognized suffixes for `alert.*`, `label.*`, and `require.*`. HTML/template source is previewed only in a sandboxed iframe.

## Forms, drafts, and saving

The empty form code is the implicit default and has no metadata row. Named system and library metadata use immutable codes; library metadata with the same code customizes a system definition. Creation copies no settings. The database schema supports one shared Active draft per scope, Upsert (including empty) and RemoveOverride operations, optimistic baseline versions, transactional commit cleanup, and preview-link revocation. Direct save submits an exact browser confirmation, validates catalog keys again, locks the scope version, applies a transaction, audits changes, increments both scope version and cache generation, commits, and immediately rebuilds the local cache.

Shared preview URLs are bearer credentials. Tokens contain 256 random bits, are URL-safe, and only SHA-256 hashes are stored. Validation uses constant-time comparison. Responses use `no-store` and `Referrer-Policy: no-referrer`. Keep live-submission disabled unless real PAPI/Melissa/Postmark side effects are intended. Safe preview must block final POST side effects; live preview must re-read the server-side flag rather than accepting a hidden field.

Secrets are visible only to global administrators, use password controls, are masked in browser confirmation, and must be passed through `SensitiveValueMasker` before audit persistence. Masking never records a whole short, medium, or long value.

## Deployment and operations

Apply [`database/001-settings-administration.sql`](database/001-settings-administration.sql) manually before deploying; see [`database/README.md`](database/README.md). The app does not run migrations. Configure role/global/system IDs and the generation polling interval in configuration. Production deployments require HTTPS (the app redirects and uses HSTS outside Development), Azure AD role assignment, database least-privilege grants, and all existing external-service configuration. Multi-node deployments compare the database cache generation at the configured interval; a successful local mutation also rebuilds memory immediately. Draft-only edits do not invalidate live cache.

Audit access is global or library-isolated and searchable. Events carry actor/scope/correlation/network and success metadata supported by the schema; sensitive values remain masked even for global viewers.
