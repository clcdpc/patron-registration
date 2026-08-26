# Settings administration database deployment

The numbered SQL files in [`migrations/`](migrations/) are the authoritative database change history for patron-registration. The application does not apply production migrations at startup.

## Deployment

1. Back up `clcdb` and confirm that the deployment identity can create tables, constraints, and indexes and can perform the data changes required by the migrations.
2. Supply a connection string without putting credentials in the repository. The runner reads `PATRON_REGISTRATION_SQL_CONNECTION_STRING`, or accepts `-ConnectionStringFile` for a protected secret file. It always targets `clcdb` by default.
3. Run the migration runner from the repository:

   ```powershell
   $env:PATRON_REGISTRATION_SQL_CONNECTION_STRING = 'Server=...;User ID=...;Password=...;Encrypt=True;TrustServerCertificate=False'
   & .\database\Invoke-Migrations.ps1
   ```

   The runner creates `dbo.PatronRegistrationMigrations` when needed, takes the SQL Server application lock `Clc.PatronRegistration.DatabaseMigrations`, discovers and orders the files numerically, and reports each migration. Do not print the connection string or credentials.
4. Verify the final status and history rows, then deploy the application.
5. Upload database-backed header-image replacements where required and verify the affected registration pages.

Normal output is intentionally short:

```text
001 already applied
002 already applied
...
012 already applied
013 applying...
013 applied
Database current at migration 013
```

The runner is the only normal deployment entry point. It skips an applied migration only when both its filename and SHA-256 checksum match the history row. A changed applied file fails before any new migration runs and reports the migration ID, filename, stored checksum, and current checksum. Applied migration files are immutable; corrections require a new migration.

## Existing databases: explicit baseline/adoption

Do not run 001–012 directly against a database that may already contain their changes. For an environment where those migrations were previously applied manually, take a backup and intentionally adopt the existing state:

```powershell
& .\database\Invoke-Migrations.ps1 -Baseline
```

`-Baseline` is a separate operation. It does not execute the SQL files. It requires migration files 001 through 012, an empty migration-history table, and explicit schema/data invariants covering the tables and columns introduced by 001–012, the required indexes, the cache-generation and asset-reference singleton rows, the header-image/catalog state, and removal of retired live and active-draft keys. Missing or incompatible invariants cause the baseline to fail without recording migrations. A successful baseline records the current repository checksums for 001–012 and clearly reports each baselined ID; a later normal run applies any migration after 012.

If a history table already contains rows, use normal execution to finish a partially recorded deployment. Do not use baseline to bypass checksum validation.

## Migration history and execution safety

`dbo.PatronRegistrationMigrations` is owned by patron-registration and contains:

| Column | Purpose |
| --- | --- |
| `MigrationId` | Numeric filename prefix; primary key. |
| `Name` | Exact migration filename; unique and immutable. |
| `Checksum` | Exact-file SHA-256 stored as `varbinary(32)`. |
| `AppliedAtUtc` | UTC application timestamp. |
| `AppliedBy` | Deployment actor recorded by the runner. |

The runner owns an outer transaction for every new migration. It enables `XACT_ABORT`, executes the migration, inserts its history row, and commits both together. Failures roll back the migration and history insert together. Existing 001–012 files contain `BEGIN TRANSACTION`/`COMMIT` pairs; they are intentionally unchanged and work as nested transactions inside the runner-owned transaction. A new migration must not commit or roll back the runner's outer transaction. The runner rejects a migration that changes the outer transaction shape.

The application lock prevents two deployment processes from running this workflow concurrently. The lock is session-owned, waits for the configured timeout, and is released in a `finally` block; closing the connection also releases a session lock if release itself encounters an error.

Only files matching `NNN-name.sql` in `database/migrations/` are migration files. The runner rejects malformed names, non-canonical numeric prefixes, duplicate numeric IDs, and ambiguous ordering before connecting to SQL Server. `database/settings-administration.sql` is a separate convergence script and is not discovered by the runner.

## Adding migration 013+

1. Add one new file under `database/migrations/`, using the next numeric prefix, for example `013-add-registration-option.sql`.
2. Derive ordering from the filename; do not add an order list to code or documentation.
3. Put the business change in that file. New migrations do not need `IF EXISTS` or `IF NOT EXISTS` guards merely for repeatable deployment because history prevents a second execution. Keep guards when they express a genuinely convergent business operation, such as ensuring a catalog row exists.
4. Run the runner against an isolated integration database, then run it a second time and confirm the new migration is skipped.
5. Never edit or rename a migration after it has been applied. Add a subsequent migration for every correction.

The application continues to use the shared existing `clcdb`; the runner is responsible only for the numbered patron-registration migration directory and its own history table. It does not create unrelated `clcdb` schema.

The filtered draft index, catalog/data transformations, asset reference lock, cache-generation counter, and preview-generation behavior are described in the administration documentation. The convergence script remains available for its existing compatibility/test scenarios, but it is not a replacement for migration history and is not run at application startup.
