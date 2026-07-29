# Settings administration database deployment

1. Back up `clcdb` and verify the existing `dbo.RegistrationFormSettings` table and its unique scope/key constraint.
2. Run `001-settings-administration.sql` against **clcdb** using a deployment identity allowed to create tables, constraints, and indexes.
3. Grant the application's database identity `SELECT`, `INSERT`, `UPDATE`, and `DELETE` on the new tables and existing `RegistrationFormSettings` (least privilege may instead be supplied through approved stored procedures).
4. Deploy the application only after the script succeeds.

The script is repeatable and uses UTC `datetime2`. The application never runs this script or any production migration automatically. The filtered unique index enforces one Active draft per organization/form-code scope. Token hashes, not bearer tokens, are persisted. `RegistrationSettingsCacheGeneration` is the cross-process invalidation counter.
