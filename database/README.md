# Settings administration database deployment

1. Back up `clcdb` and verify the existing `dbo.RegistrationFormSettings` table and its unique scope/key constraint.
2. Run `001-settings-administration.sql` against **clcdb** using a deployment identity allowed to create tables, constraints, and indexes.
3. Run `002-preview-operational-branch.sql`. It is a no-op on fresh installations and safely revokes legacy links before adding their required operational branch.
4. Grant the application's database identity `SELECT`, `INSERT`, `UPDATE`, and `DELETE` on the new tables and existing `RegistrationFormSettings` (least privilege may instead be supplied through approved stored procedures).
5. Deploy the application only after both scripts succeed.

The script is repeatable and uses UTC `datetime2`. The application never runs this script or any production migration automatically. The filtered unique index enforces one Active draft per organization/form-code scope. Token hashes, not bearer tokens, are persisted. `RegistrationSettingsCacheGeneration` is the cross-process invalidation counter.
