# Database convergence deployment

[`settings-administration.sql`](settings-administration.sql) is the authoritative deployment for the patron-registration settings-administration schema and data. It computes the current required database state from the state it finds; deployment numbers, checksums, and deployment history are intentionally not stored.

The script accepts a database where the shared prerequisites exist, whether settings administration is absent, old, or already current. It creates missing current objects, applies the few additive/widening upgrades the application needs, preserves and transforms application data, and validates focused final invariants.

It does not identify historical releases or reject harmless additional DBA indexes or constraints. States that cannot safely accept the required transformations still fail atomically with SQL Server's normal actionable error.

## Deployment

1. Back up `clcdb` and review the data changes before deployment. The update intentionally removes retired settings such as `header_image_url`; a backup is the recovery path for any unexpected data condition.
2. Use a protected deployment identity with the DDL and data permissions needed to create and upgrade the patron-registration-owned objects. Do not put credentials in the repository.
3. Provide the SQL Server connection string through `PATRON_REGISTRATION_SQL_CONNECTION_STRING`, `-ConnectionString`, or a protected `-ConnectionStringFile`. The database name defaults to `clcdb`.

   ```powershell
   & .\database\Invoke-DatabaseUpdate.ps1
   ```

   The wrapper reads `settings-administration.sql`, targets the selected database, and executes it with a long command timeout. It does not inspect or order database changes in PowerShell and does not print the connection string.
4. Deploy the application after the update succeeds.

The application runtime identity remains separate from the deployment identity. Grant runtime access only to the tables and operations the application needs; runtime access must not depend on DDL or deployment data-change permissions.

## Safety and repeatability

The SQL script validates the shared `clcdb` prerequisites, then starts one transaction with `SET XACT_ABORT ON` and acquires an exclusive SQL Server application lock with `sp_getapplock` scoped to that transaction. It converges the current state and checks focused application invariants. A second deployment waits for the lock and then evaluates the resulting state. Any failure rolls back the complete update, including schema and data changes.

The script is safe to run repeatedly. A successful second run does not duplicate catalog rows, singleton rows, settings, drafts, assets, or required indexes. Legacy values are copied to their current keys before retired rows are removed. When both a legacy value and its current replacement exist, the current replacement is retained; active draft mutations follow the same replacement-wins rule.

The shared prerequisite tables `dbo.RegistrationFormSettingTypes` and `dbo.RegistrationFormSettings` must already exist with the columns and trusted relationship required by the application. The script validates those shared objects but does not take ownership of their schema. Missing or incompatible prerequisites fail before deployment can commit.

The deployment identity should be used only for this update. Configure the application with its normal runtime identity after the database is current.
