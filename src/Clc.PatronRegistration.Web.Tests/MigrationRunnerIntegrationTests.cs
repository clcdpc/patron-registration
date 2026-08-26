using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;

#nullable enable

namespace Clc.PatronRegistration.Tests;

[TestClass]
[DoNotParallelize]
public sealed class MigrationRunnerIntegrationTests
{
    private const string ConnectionVariable = "PATRON_REGISTRATION_TEST_SQL_CONNECTION_STRING";
    private string? databaseName;
    private string? databaseConnectionString;

    [TestInitialize]
    public void CreateIsolatedDatabase()
    {
        var configured = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            Assert.Inconclusive($"SQL migration-runner integration tests require {ConnectionVariable}.");
        }

        databaseName = $"PatronRegistrationMigrationTests_{Guid.NewGuid():N}";
        try
        {
            var adminBuilder = new SqlConnectionStringBuilder(configured!)
            {
                InitialCatalog = "master",
                ConnectTimeout = 10
            };
            using var admin = new SqlConnection(adminBuilder.ConnectionString);
            admin.Open();
            Execute(admin, $"CREATE DATABASE [{databaseName}]");

            var databaseBuilder = new SqlConnectionStringBuilder(configured)
            {
                InitialCatalog = databaseName,
                ConnectTimeout = 10
            };
            databaseConnectionString = databaseBuilder.ConnectionString;
            using var connection = Open();
            DeployPrerequisiteSchema(connection);
        }
        catch (SqlException exception)
        {
            TryDropDatabase(configured!);
            Assert.Fail($"SQL migration-runner integration database setup failed: {exception.Message}");
        }
    }

    [TestCleanup]
    public void DropIsolatedDatabase()
    {
        var configured = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            TryDropDatabase(configured);
        }
    }

    [TestMethod]
    public void FreshRunRecordsExactChecksumsAndSecondRunSkipsEverything()
    {
        var first = RunRunner(MigrationDirectory());
        Assert.AreEqual(0, first.ExitCode, first.Output);
        StringAssert.Contains(first.Output, "001 applying...");
        StringAssert.Contains(first.Output, "012 applied");
        Assert.AreEqual(12, Scalar<int>("select count(*) from dbo.PatronRegistrationMigrations"));

        foreach (var migrationPath in NumberedMigrationPaths())
        {
            var fileName = Path.GetFileName(migrationPath);
            var migrationId = int.Parse(fileName.Split('-', 2)[0]);
            var expectedChecksum = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(migrationPath)));
            var row = QuerySingle(
                "select MigrationId,Name,convert(varchar(64),[Checksum],2) from dbo.PatronRegistrationMigrations where MigrationId=@id",
                command => command.Parameters.AddWithValue("@id", migrationId),
                reader => (Id: reader.GetInt32(0), Name: reader.GetString(1), Checksum: reader.GetString(2)));

            Assert.AreEqual(migrationId, row.Id);
            Assert.AreEqual(fileName, row.Name);
            Assert.IsTrue(string.Equals(expectedChecksum, row.Checksum, StringComparison.OrdinalIgnoreCase));
        }

        var before = DatabaseSnapshot();
        var second = RunRunner(MigrationDirectory());
        Assert.AreEqual(0, second.ExitCode, second.Output);
        Assert.IsFalse(second.Output.Contains(" applying...", StringComparison.Ordinal));
        foreach (var migrationPath in NumberedMigrationPaths())
        {
            StringAssert.Contains(second.Output, $"{Path.GetFileName(migrationPath)[..3]} already applied");
        }
        Assert.AreEqual(before, DatabaseSnapshot());
    }

    [TestMethod]
    public void ChangedAppliedMigrationFailsWithStoredAndCurrentChecksums()
    {
        var first = RunRunner(MigrationDirectory());
        Assert.AreEqual(0, first.ExitCode, first.Output);

        var temporaryMigrations = CopyMigrationDirectory();
        try
        {
            var tamperedPath = Path.Combine(temporaryMigrations, "001-settings-administration.sql");
            File.AppendAllText(tamperedPath, "\r\n-- tampered after deployment\r\n", new UTF8Encoding(false));

            var result = RunRunner(temporaryMigrations);
            Assert.AreNotEqual(0, result.ExitCode, result.Output);
            StringAssert.Contains(result.Output, "Migration integrity check failed");
            StringAssert.Contains(result.Output, "ID 001");
            StringAssert.Contains(result.Output, "001-settings-administration.sql");
            StringAssert.Contains(result.Output, "Stored checksum:");
            StringAssert.Contains(result.Output, "Current checksum:");
            Assert.AreEqual(12, Scalar<int>("select count(*) from dbo.PatronRegistrationMigrations"));
        }
        finally
        {
            Directory.Delete(temporaryMigrations, recursive: true);
        }
    }

    [TestMethod]
    public void FailedMigrationRollsBackItsChangesAndHistoryInsert()
    {
        var temporaryMigrations = CreateTemporaryDirectory();
        try
        {
            WriteMigration(temporaryMigrations, "001-create-probe.sql", """
                SET XACT_ABORT ON;
                BEGIN TRANSACTION;
                CREATE TABLE dbo.MigrationRunnerFailureProbe (Id int NOT NULL);
                COMMIT;
                """);
            WriteMigration(temporaryMigrations, "002-fail-after-change.sql", """
                SET XACT_ABORT ON;
                BEGIN TRANSACTION;
                CREATE TABLE dbo.MigrationRunnerFailureProbeData (Id int NOT NULL);
                INSERT dbo.MigrationRunnerFailureProbeData (Id) VALUES (1);
                THROW 51100, 'intentional migration test failure', 1;
                COMMIT;
                """);

            var result = RunRunner(temporaryMigrations);
            Assert.AreNotEqual(0, result.ExitCode, result.Output);
            StringAssert.Contains(result.Output, "002-fail-after-change.sql");
            Assert.AreEqual(1, Scalar<int>("select count(*) from dbo.PatronRegistrationMigrations"));
            Assert.AreEqual(1, Scalar<int>("select count(*) from dbo.PatronRegistrationMigrations where MigrationId=1"));
            Assert.AreEqual(0, Scalar<int>("select case when object_id('dbo.MigrationRunnerFailureProbeData','U') is null then 0 else 1 end"));
            Assert.AreEqual(1, Scalar<int>("select case when object_id('dbo.MigrationRunnerFailureProbe','U') is null then 0 else 1 end"));
        }
        finally
        {
            Directory.Delete(temporaryMigrations, recursive: true);
        }
    }

    [TestMethod]
    public void NumericOrderingIsUsedAndMalformedOrDuplicateIdsAreRejected()
    {
        var orderedMigrations = CreateTemporaryDirectory();
        try
        {
            // Create the files in a deliberately non-numeric order. The runner
            // must discover the directory in any order but execute by ID.
            WriteMigration(orderedMigrations, "005-five.sql", """
                SET XACT_ABORT ON;
                BEGIN TRANSACTION;
                INSERT dbo.MigrationRunnerOrderProbe (MigrationId) VALUES (5);
                COMMIT;
                """);
            WriteMigration(orderedMigrations, "001-create-order-probe.sql", """
                SET XACT_ABORT ON;
                BEGIN TRANSACTION;
                CREATE TABLE dbo.MigrationRunnerOrderProbe (AppliedOrder int IDENTITY(1,1) NOT NULL, MigrationId int NOT NULL);
                INSERT dbo.MigrationRunnerOrderProbe (MigrationId) VALUES (1);
                COMMIT;
                """);
            WriteMigration(orderedMigrations, "010-ten.sql", """
                SET XACT_ABORT ON;
                BEGIN TRANSACTION;
                INSERT dbo.MigrationRunnerOrderProbe (MigrationId) VALUES (10);
                COMMIT;
                """);
            WriteMigration(orderedMigrations, "003-three.sql", """
                SET XACT_ABORT ON;
                BEGIN TRANSACTION;
                INSERT dbo.MigrationRunnerOrderProbe (MigrationId) VALUES (3);
                COMMIT;
                """);
            WriteMigration(orderedMigrations, "007-seven.sql", """
                SET XACT_ABORT ON;
                BEGIN TRANSACTION;
                INSERT dbo.MigrationRunnerOrderProbe (MigrationId) VALUES (7);
                COMMIT;
                """);
            WriteMigration(orderedMigrations, "002-two.sql", """
                SET XACT_ABORT ON;
                BEGIN TRANSACTION;
                INSERT dbo.MigrationRunnerOrderProbe (MigrationId) VALUES (2);
                COMMIT;
                """);
            WriteMigration(orderedMigrations, "009-nine.sql", """
                SET XACT_ABORT ON;
                BEGIN TRANSACTION;
                INSERT dbo.MigrationRunnerOrderProbe (MigrationId) VALUES (9);
                COMMIT;
                """);
            WriteMigration(orderedMigrations, "004-four.sql", """
                SET XACT_ABORT ON;
                BEGIN TRANSACTION;
                INSERT dbo.MigrationRunnerOrderProbe (MigrationId) VALUES (4);
                COMMIT;
                """);
            WriteMigration(orderedMigrations, "008-eight.sql", """
                SET XACT_ABORT ON;
                BEGIN TRANSACTION;
                INSERT dbo.MigrationRunnerOrderProbe (MigrationId) VALUES (8);
                COMMIT;
                """);
            WriteMigration(orderedMigrations, "006-six.sql", """
                SET XACT_ABORT ON;
                BEGIN TRANSACTION;
                INSERT dbo.MigrationRunnerOrderProbe (MigrationId) VALUES (6);
                COMMIT;
                """);

            var ordered = RunRunner(orderedMigrations);
            Assert.AreEqual(0, ordered.ExitCode, ordered.Output);
            CollectionAssert.AreEqual(Enumerable.Range(1, 10).ToArray(), Query(
                "select MigrationId from dbo.MigrationRunnerOrderProbe order by AppliedOrder",
                null,
                reader => reader.GetInt32(0)).ToArray());
        }
        finally
        {
            Directory.Delete(orderedMigrations, recursive: true);
        }

        var duplicateMigrations = CreateTemporaryDirectory();
        try
        {
            WriteMigration(duplicateMigrations, "001-one.sql", "select 1;");
            WriteMigration(duplicateMigrations, "001-two.sql", "select 1;");
            var duplicate = RunRunner(duplicateMigrations);
            Assert.AreNotEqual(0, duplicate.ExitCode, duplicate.Output);
            StringAssert.Contains(duplicate.Output, "Duplicate migration IDs");
            StringAssert.Contains(duplicate.Output, "001-one.sql");
            StringAssert.Contains(duplicate.Output, "001-two.sql");
        }
        finally
        {
            Directory.Delete(duplicateMigrations, recursive: true);
        }

        var malformedMigrations = CreateTemporaryDirectory();
        try
        {
            WriteMigration(malformedMigrations, "01-malformed.sql", "select 1;");
            var malformed = RunRunner(malformedMigrations);
            Assert.AreNotEqual(0, malformed.ExitCode, malformed.Output);
            StringAssert.Contains(malformed.Output, "Malformed migration filename");
        }
        finally
        {
            Directory.Delete(malformedMigrations, recursive: true);
        }
    }

    [TestMethod]
    public void GapInMigrationIdsIsRejectedWithMissingIdDiagnostic()
    {
        var gapMigrations = CreateTemporaryDirectory();
        try
        {
            WriteMigration(gapMigrations, "001-one.sql", "select 1;");
            WriteMigration(gapMigrations, "002-two.sql", "select 1;");
            WriteMigration(gapMigrations, "010-ten.sql", "select 1;");

            var gap = RunRunner(gapMigrations);
            Assert.AreNotEqual(0, gap.ExitCode, gap.Output);
            StringAssert.Contains(gap.Output, "Missing migration ID 003");
            StringAssert.Contains(gap.Output, "010-ten.sql");
        }
        finally
        {
            Directory.Delete(gapMigrations, recursive: true);
        }
    }

    [TestMethod]
    public void PendingMigrationBelowAppliedHigherIdIsRejectedBeforeExecution()
    {
        var chronologicalMigrations = CreateTemporaryDirectory();
        try
        {
            WriteMigration(chronologicalMigrations, "001-create-chronology-probe.sql", """
                SET XACT_ABORT ON;
                BEGIN TRANSACTION;
                CREATE TABLE dbo.MigrationRunnerChronologyProbe (MigrationId int NOT NULL);
                INSERT dbo.MigrationRunnerChronologyProbe (MigrationId) VALUES (1);
                COMMIT;
                """);
            WriteMigration(chronologicalMigrations, "002-two.sql", """
                SET XACT_ABORT ON;
                BEGIN TRANSACTION;
                INSERT dbo.MigrationRunnerChronologyProbe (MigrationId) VALUES (2);
                COMMIT;
                """);
            WriteMigration(chronologicalMigrations, "003-three.sql", """
                SET XACT_ABORT ON;
                BEGIN TRANSACTION;
                INSERT dbo.MigrationRunnerChronologyProbe (MigrationId) VALUES (3);
                COMMIT;
                """);

            var initial = RunRunner(chronologicalMigrations);
            Assert.AreEqual(0, initial.ExitCode, initial.Output);
            using (var connection = Open())
            {
                Execute(connection, "delete dbo.PatronRegistrationMigrations where MigrationId=2;");
            }

            var retry = RunRunner(chronologicalMigrations);
            Assert.AreNotEqual(0, retry.ExitCode, retry.Output);
            StringAssert.Contains(retry.Output, "pending below already-applied migration 003");
            Assert.AreEqual(3, Scalar<int>("select count(*) from dbo.MigrationRunnerChronologyProbe"));
            Assert.AreEqual(2, Scalar<int>("select count(*) from dbo.PatronRegistrationMigrations"));
        }
        finally
        {
            Directory.Delete(chronologicalMigrations, recursive: true);
        }
    }

    [TestMethod]
    public void ApplicationLockAllowsOnlyOneConcurrentRunnerToApply()
    {
        var temporaryMigrations = CreateTemporaryDirectory();
        try
        {
            WriteMigration(temporaryMigrations, "001-slow-create.sql", """
                SET XACT_ABORT ON;
                BEGIN TRANSACTION;
                WAITFOR DELAY '00:00:03';
                CREATE TABLE dbo.MigrationRunnerConcurrencyProbe (Id int NOT NULL);
                COMMIT;
                """);

            using var first = StartRunner(temporaryMigrations);
            Thread.Sleep(500);
            using var second = StartRunner(temporaryMigrations);
            var firstResult = CompleteRunner(first, TimeSpan.FromSeconds(30));
            var secondResult = CompleteRunner(second, TimeSpan.FromSeconds(30));

            Assert.AreEqual(0, firstResult.ExitCode, firstResult.Output);
            Assert.AreEqual(0, secondResult.ExitCode, secondResult.Output);
            var combined = firstResult.Output + Environment.NewLine + secondResult.Output;
            Assert.AreEqual(1, CountOccurrences(combined, "001 applying..."));
            Assert.AreEqual(1, CountOccurrences(combined, "001 already applied"));
            Assert.AreEqual(1, Scalar<int>("select count(*) from dbo.PatronRegistrationMigrations"));
        }
        finally
        {
            Directory.Delete(temporaryMigrations, recursive: true);
        }
    }

    [TestMethod]
    public void PreMigratedDatabaseWithStaleLivePreviewCanBeBaselinedWithoutRebindingIt()
    {
        foreach (var migrationPath in NumberedMigrationPaths())
        {
            using var connection = Open();
            Execute(connection, File.ReadAllText(migrationPath), 60);
        }

        const long originalGeneration = 0;
        long previewLinkId;
        var draftId = QuerySingle(
            """
            insert dbo.RegistrationSettingDrafts
                (OrganizationId, FormCode, BaselineVersion, Status, CreatedBy, ModifiedBy)
            output inserted.DraftId
            values (101, 'form', 0, 'Active', 'baseline-test', 'baseline-test');
            """,
            null,
            reader => reader.GetInt64(0));
        previewLinkId = QuerySingle(
            """
            insert dbo.RegistrationSettingPreviewLinks
                (DraftId, TokenHash, OperationalBranchId, AllowLiveSubmission, LiveSettingsGeneration, CreatedBy, ModifiedBy)
            output inserted.PreviewLinkId
            values (@draftId, hashbytes('SHA2_256', 'stale-baseline'), 101, 1, @generation, 'baseline-test', 'baseline-test');
            """,
            command =>
            {
                command.Parameters.AddWithValue("@draftId", draftId);
                command.Parameters.AddWithValue("@generation", originalGeneration);
            },
            reader => reader.GetInt64(0));
        using (var connection = Open())
        {
            // Normal settings publication advances this singleton counter in
            // the same way; this simulates that committed state transition.
            Execute(connection, """
                update dbo.RegistrationSettingsCacheGeneration
                set Generation = Generation + 1, ModifiedAtUtc = SYSUTCDATETIME()
                where Id = 1;
                """);
        }

        var baseline = RunRunner(MigrationDirectory(), baseline: true);
        Assert.AreEqual(0, baseline.ExitCode, baseline.Output);
        StringAssert.Contains(baseline.Output, "Baselining migrations 001 through 012");
        StringAssert.Contains(baseline.Output, "001 baselined");
        StringAssert.Contains(baseline.Output, "012 baselined");
        Assert.AreEqual(12, Scalar<int>("select count(*) from dbo.PatronRegistrationMigrations"));
        var persistedGeneration = QuerySingle(
            "select LiveSettingsGeneration, AllowLiveSubmission from dbo.RegistrationSettingPreviewLinks where PreviewLinkId=@id",
            command => command.Parameters.AddWithValue("@id", previewLinkId),
            reader => (Generation: reader.IsDBNull(0) ? (long?)null : reader.GetInt64(0), AllowLive: reader.GetBoolean(1)));
        Assert.AreEqual((long?)originalGeneration, persistedGeneration.Generation);
        Assert.IsTrue(persistedGeneration.AllowLive);
        Assert.AreEqual(1L, Scalar<long>("select Generation from dbo.RegistrationSettingsCacheGeneration where Id=1"));
        Assert.IsTrue(persistedGeneration.Generation < Scalar<long>("select Generation from dbo.RegistrationSettingsCacheGeneration where Id=1"));

        var normal = RunRunner(MigrationDirectory());
        Assert.AreEqual(0, normal.ExitCode, normal.Output);
        Assert.IsFalse(normal.Output.Contains(" applying...", StringComparison.Ordinal));
    }

    [TestMethod]
    public void LivePreviewWithNullGenerationCannotBeBaselined()
    {
        foreach (var migrationPath in NumberedMigrationPaths())
        {
            using var connection = Open();
            Execute(connection, File.ReadAllText(migrationPath), 60);
        }

        using (var connection = Open())
        {
            Execute(connection, """
                insert dbo.RegistrationSettingDrafts
                    (OrganizationId, FormCode, BaselineVersion, Status, CreatedBy, ModifiedBy)
                values (101, 'form', 0, 'Active', 'baseline-test', 'baseline-test');
                declare @draftId bigint = scope_identity();
                insert dbo.RegistrationSettingPreviewLinks
                    (DraftId, TokenHash, OperationalBranchId, AllowLiveSubmission, CreatedBy, ModifiedBy)
                values (@draftId, hashbytes('SHA2_256', 'null-generation-baseline'), 1, 1, 'baseline-test', 'baseline-test');
                """);
        }

        var baseline = RunRunner(MigrationDirectory(), baseline: true);
        Assert.AreNotEqual(0, baseline.ExitCode, baseline.Output);
        StringAssert.Contains(baseline.Output, "Baseline refused");
        Assert.AreEqual(0, Scalar<int>("select count(*) from dbo.PatronRegistrationMigrations"));
        Assert.AreEqual(1, Scalar<int>("select count(*) from dbo.RegistrationSettingPreviewLinks where AllowLiveSubmission=1 and LiveSettingsGeneration is null"));
    }

    [TestMethod]
    public void LivePreviewWithFutureGenerationCannotBeBaselined()
    {
        foreach (var migrationPath in NumberedMigrationPaths())
        {
            using var connection = Open();
            Execute(connection, File.ReadAllText(migrationPath), 60);
        }

        var draftId = QuerySingle(
            """
            insert dbo.RegistrationSettingDrafts
                (OrganizationId, FormCode, BaselineVersion, Status, CreatedBy, ModifiedBy)
            output inserted.DraftId
            values (101, 'future-generation', 0, 'Active', 'baseline-test', 'baseline-test');
            """,
            null,
            reader => reader.GetInt64(0));
        var previewLinkId = QuerySingle(
            """
            insert dbo.RegistrationSettingPreviewLinks
                (DraftId, TokenHash, OperationalBranchId, AllowLiveSubmission, LiveSettingsGeneration, CreatedBy, ModifiedBy)
            output inserted.PreviewLinkId
            values (@draftId, hashbytes('SHA2_256', 'future-generation-baseline'), 1, 1, 1, 'baseline-test', 'baseline-test');
            """,
            command => command.Parameters.AddWithValue("@draftId", draftId),
            reader => reader.GetInt64(0));

        var baseline = RunRunner(MigrationDirectory(), baseline: true);
        Assert.AreNotEqual(0, baseline.ExitCode, baseline.Output);
        StringAssert.Contains(baseline.Output, "settings-cache generation no greater than the current generation");
        Assert.AreEqual(0, Scalar<int>("select count(*) from dbo.PatronRegistrationMigrations"));
        Assert.AreEqual(0L, Scalar<long>("select Generation from dbo.RegistrationSettingsCacheGeneration where Id=1"));
        Assert.AreEqual(1L, Scalar<long>(
            "select LiveSettingsGeneration from dbo.RegistrationSettingPreviewLinks where PreviewLinkId=@id",
            command => command.Parameters.AddWithValue("@id", previewLinkId)));
    }

    private RunnerResult RunRunner(string migrationsPath, bool baseline = false)
    {
        using var process = StartRunner(migrationsPath, baseline);
        return CompleteRunner(process, TimeSpan.FromMinutes(2));
    }

    private Process StartRunner(string migrationsPath, bool baseline = false)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(RunnerPath());
        startInfo.ArgumentList.Add("-MigrationsPath");
        startInfo.ArgumentList.Add(migrationsPath);
        startInfo.ArgumentList.Add("-DatabaseName");
        startInfo.ArgumentList.Add(databaseName!);
        startInfo.ArgumentList.Add("-LockTimeoutSeconds");
        startInfo.ArgumentList.Add("60");
        startInfo.ArgumentList.Add("-CommandTimeoutSeconds");
        startInfo.ArgumentList.Add("120");
        if (baseline)
        {
            startInfo.ArgumentList.Add("-Baseline");
        }

        startInfo.Environment["PATRON_REGISTRATION_SQL_CONNECTION_STRING"] =
            Environment.GetEnvironmentVariable(ConnectionVariable)!;
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start PowerShell for migration-runner integration testing.");
    }

    private static RunnerResult CompleteRunner(Process process, TimeSpan timeout)
    {
        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            Assert.Fail("Migration runner did not finish before the integration-test timeout.");
        }

        var output = process.StandardOutput.ReadToEnd() + Environment.NewLine + process.StandardError.ReadToEnd();
        return new RunnerResult(process.ExitCode, output);
    }

    private void DeployPrerequisiteSchema(SqlConnection connection)
    {
        Execute(connection, """
            create table dbo.RegistrationFormSettingTypes
            (
                Setting nvarchar(200) not null constraint PK_MigrationRunner_SettingTypes primary key
            );
            create table dbo.RegistrationFormSettings
            (
                OrganizationID int not null,
                Setting nvarchar(200) not null,
                FormCode nvarchar(64) not null constraint DF_MigrationRunner_FormCode default '',
                Value nvarchar(max) null,
                constraint PK_MigrationRunner_Settings primary key (OrganizationID, Setting, FormCode),
                constraint FK_MigrationRunner_Settings_Types foreign key (Setting) references dbo.RegistrationFormSettingTypes(Setting)
            );
            """);

        foreach (var key in new[]
        {
            "header_image_url",
            "legal_name_checkbox_label",
            "ecard_checkbox_label",
            "mailing_list_checkbox_label",
            "require_preferred_pickup_location",
            "css_file"
        })
        {
            Execute(connection,
                "insert dbo.RegistrationFormSettingTypes (Setting) values (@key);",
                parameters: command => command.Parameters.AddWithValue("@key", key));
        }

        foreach (var setting in new[]
        {
            (Key: "header_image_url", Value: "https://legacy.example/header.png"),
            (Key: "legal_name_checkbox_label", Value: "Use legal name"),
            (Key: "ecard_checkbox_label", Value: "E-card"),
            (Key: "mailing_list_checkbox_label", Value: "Join mailing list"),
            (Key: "require_preferred_pickup_location", Value: "true")
        })
        {
            Execute(connection,
                "insert dbo.RegistrationFormSettings (OrganizationID, Setting, FormCode, Value) values (101, @key, 'form', @value);",
                parameters: command =>
                {
                    command.Parameters.AddWithValue("@key", setting.Key);
                    command.Parameters.AddWithValue("@value", setting.Value);
                });
        }
    }

    private string DatabaseSnapshot() => string.Join("|", new[]
    {
        Scalar<int>("select count(*) from dbo.PatronRegistrationMigrations"),
        Scalar<int>("select count(*) from dbo.RegistrationFormSettingTypes"),
        Scalar<int>("select count(*) from dbo.RegistrationFormSettings"),
        Scalar<int>("select count(*) from sys.tables where name in ('RegistrationFormAssets','RegistrationFormAssetReferenceLocks','RegistrationSettingDrafts')")
    });

    private SqlConnection Open()
    {
        var connection = new SqlConnection(databaseConnectionString!);
        connection.Open();
        return connection;
    }

    private T Scalar<T>(string sql)
    {
        using var connection = Open();
        using var command = new SqlCommand(sql, connection) { CommandTimeout = 30 };
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
    }

    private T Scalar<T>(string sql, Action<SqlCommand> parameters)
    {
        using var connection = Open();
        using var command = new SqlCommand(sql, connection) { CommandTimeout = 30 };
        parameters(command);
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
    }

    private T QuerySingle<T>(string sql, Action<SqlCommand>? parameters, Func<SqlDataReader, T> map)
        => Query(sql, parameters, map).Single();

    private List<T> Query<T>(string sql, Action<SqlCommand>? parameters, Func<SqlDataReader, T> map)
    {
        using var connection = Open();
        using var command = new SqlCommand(sql, connection) { CommandTimeout = 30 };
        parameters?.Invoke(command);
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read())
        {
            rows.Add(map(reader));
        }
        return rows;
    }

    private static void Execute(SqlConnection connection, string sql, int timeout = 30, Action<SqlCommand>? parameters = null)
    {
        using var command = new SqlCommand(sql, connection) { CommandTimeout = timeout };
        parameters?.Invoke(command);
        command.ExecuteNonQuery();
    }

    private static string RepositoryRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    private static string MigrationDirectory() => Path.Combine(RepositoryRoot(), "database", "migrations");
    private static string RunnerPath() => Path.Combine(RepositoryRoot(), "database", "Invoke-Migrations.ps1");
    private static IEnumerable<string> NumberedMigrationPaths() => Directory
        .EnumerateFiles(MigrationDirectory(), "*.sql")
        .OrderBy(path => int.Parse(Path.GetFileName(path).Split('-', 2)[0]));

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"patron-registration-migrations-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string CopyMigrationDirectory()
    {
        var destination = CreateTemporaryDirectory();
        foreach (var source in NumberedMigrationPaths())
        {
            File.Copy(source, Path.Combine(destination, Path.GetFileName(source)));
        }
        return destination;
    }

    private static void WriteMigration(string directory, string fileName, string sql)
        => File.WriteAllText(Path.Combine(directory, fileName), sql, new UTF8Encoding(false));

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(search, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += search.Length;
        }
        return count;
    }

    private void TryDropDatabase(string configured)
    {
        if (string.IsNullOrWhiteSpace(databaseName)) return;
        try
        {
            var builder = new SqlConnectionStringBuilder(configured) { InitialCatalog = "master", ConnectTimeout = 10 };
            using var connection = new SqlConnection(builder.ConnectionString);
            connection.Open();
            Execute(connection, $"if db_id(N'{databaseName}') is not null begin alter database [{databaseName}] set single_user with rollback immediate; drop database [{databaseName}]; end;", 30);
        }
        catch
        {
            // Cleanup is best effort so the test result reports the migration behavior.
        }
        finally
        {
            databaseName = null;
            databaseConnectionString = null;
        }
    }

    private sealed record RunnerResult(int ExitCode, string Output);
}
