# Settings administration database deployment

1. Back up `clcdb` and verify the existing `dbo.RegistrationFormSettings` table and its unique scope/key constraint.
2. Run `001-settings-administration.sql` against **clcdb** using a deployment identity allowed to create tables, constraints, and indexes.
3. Run `002-preview-operational-branch.sql`. It is a no-op on fresh installations and safely revokes legacy links before adding their required operational branch.
4. Run `003-expand-audit-setting-values.sql` so long non-sensitive HTML and templates can be audited without truncation.
5. Run `004-registration-form-assets.sql` to create the database-backed registration image asset table.
6. Grant the application's database identity `SELECT`, `INSERT`, `UPDATE`, and `DELETE` on the new tables and existing `RegistrationFormSettings` (least privilege may instead be supplied through approved stored procedures).
7. Deploy the application only after all scripts succeed.

The script is repeatable and uses UTC `datetime2`. The application never runs this script or any production migration automatically. The filtered unique index enforces one Active draft per organization/form-code scope. Token hashes, not bearer tokens, are persisted. `RegistrationSettingsCacheGeneration` is the cross-process invalidation counter.

Registration image assets are stored separately from `RegistrationFormSettings`; settings contain only the referenced asset ID. Assets uploaded for a draft may remain as unreferenced rows when that draft is discarded. They are not enumerated by the application: the anonymous asset route serves only IDs referenced by persisted settings, while authenticated settings and token-scoped preview routes serve draft assets.
