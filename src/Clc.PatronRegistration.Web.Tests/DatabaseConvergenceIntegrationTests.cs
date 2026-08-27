using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Clc.PatronRegistration.Administration;
using Clc.PatronRegistration.Web.Settings;

#nullable enable

namespace Clc.PatronRegistration.Tests;

[TestClass]
[DoNotParallelize]
public sealed class DatabaseConvergenceIntegrationTests
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
            Assert.Inconclusive($"SQL convergence integration tests require {ConnectionVariable}.");
        }

        databaseName = $"PatronRegistrationConvergenceTests_{Guid.NewGuid():N}";
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
            DeploySharedPrerequisites(connection);
        }
        catch (SqlException exception)
        {
            TryDropDatabase(configured!);
            Assert.Fail($"SQL convergence integration database setup failed: {exception.Message}");
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
    public void OldPrerequisiteDatabase_ConvergesToCurrentState()
    {
        var result = RunDatabaseUpdate();

        AssertSucceeded(result);
        AssertCurrentState();
        Assert.AreEqual("preserved setting", ReadSetting(101, "form", "registration_text"));

        var afterFirstConvergence = LogicalSnapshot();
        AssertSucceeded(RunDatabaseUpdate());
        Assert.AreEqual(afterFirstConvergence, LogicalSnapshot());
    }

    [TestMethod]
    public void RepresentativeOldDatabase_ConvergesToCurrentState()
    {
        DeployRepresentativeOldDatabase();

        var result = RunDatabaseUpdate();

        AssertSucceeded(result);
        AssertCurrentState();

        var afterFirstConvergence = LogicalSnapshot();
        AssertSucceeded(RunDatabaseUpdate());
        Assert.AreEqual(afterFirstConvergence, LogicalSnapshot());
    }
    [TestMethod]
    public void CurrentDatabase_ConvergesWithoutChangingLogicalState()
    {
        AssertSucceeded(RunDatabaseUpdate());
        var before = LogicalSnapshot();

        var result = RunDatabaseUpdate();

        AssertSucceeded(result);
        Assert.AreEqual(before, LogicalSnapshot());
        AssertCurrentState();
    }

    [TestMethod]
    public void RepeatedDatabaseUpdates_AreIdempotent()
    {
        AssertSucceeded(RunDatabaseUpdate());
        AssertSucceeded(RunDatabaseUpdate());
        AssertSucceeded(RunDatabaseUpdate());

        AssertCurrentState();
        Assert.AreEqual(1, Scalar<int>("select count(*) from dbo.RegistrationSettingsCacheGeneration where Id=1"));
        Assert.AreEqual(1, Scalar<int>("select count(*) from dbo.RegistrationFormAssetReferenceLocks where LockId=1"));
    }

    [TestMethod]
    public void AdditionalOwnedIndex_DoesNotPreventConvergenceOrGetRemoved()
    {
        AssertSucceeded(RunDatabaseUpdate());
        using (var connection = Open())
        {
            Execute(connection, "create index IX_RSD_DbaStatus on dbo.RegistrationSettingDrafts (Status);");
            Execute(connection, "create index IX_RSAE_ScopeFilter on dbo.RegistrationSettingAuditEvents (TargetOrganizationId, FormCode, EventType, TimestampUtc desc);");
        }

        AssertSucceeded(RunDatabaseUpdate());
        Assert.AreEqual(1, Scalar<int>("select count(*) from sys.indexes where object_id=object_id('dbo.RegistrationSettingDrafts') and name='IX_RSD_DbaStatus'"));
        Assert.AreEqual(1, Scalar<int>("select count(*) from sys.indexes where object_id=object_id('dbo.RegistrationSettingAuditEvents') and name='IX_RSAE_ScopeFilter'"));
    }

    [TestMethod]
    public void FinalInvariantFailure_RollsBackRequiredDataTransformations()
    {
        AssertSucceeded(RunDatabaseUpdate());

        using (var connection = Open())
        {
            Execute(connection, """
                insert dbo.RegistrationSettingsCacheGeneration (Id, Generation, ModifiedAtUtc)
                    values (2, 9, sysutcdatetime());
                insert dbo.RegistrationFormSettingTypes (Setting) values ('legal_name_checkbox_label');
                insert dbo.RegistrationFormSettings (OrganizationID, Setting, FormCode, Value)
                    values (101, 'legal_name_checkbox_label', 'rollback', 'legacy value');
                """);
        }

        var result = RunDatabaseUpdate();

        Assert.AreNotEqual(0, result.ExitCode, result.Output);
        StringAssert.Contains(result.Output, "must contain exactly one singleton row");
        Assert.AreEqual("legacy value", ReadSetting(101, "rollback", "legal_name_checkbox_label"));
        Assert.AreEqual(0, Scalar<int>("select count(*) from dbo.RegistrationFormSettings where OrganizationID=101 and FormCode='rollback' and Setting='label.UseLegalName'"));
        Assert.AreEqual(2, Scalar<int>("select count(*) from dbo.RegistrationSettingsCacheGeneration"));
    }

    [TestMethod]
    public void ConcurrentDatabaseUpdates_AreSerializedByTheDatabaseLock()
    {
        using var lockConnection = Open();
        using var lockTransaction = lockConnection.BeginTransaction();
        using (var lockCommand = new SqlCommand("""
            declare @result int;
            exec @result = sys.sp_getapplock
                @Resource=N'Clc.PatronRegistration.DatabaseConvergence',
                @LockMode=N'Exclusive',
                @LockOwner=N'Transaction',
                @LockTimeout=0,
                @DbPrincipal=N'public';
            select @result;
            """, lockConnection, lockTransaction) { CommandTimeout = 30 })
        {
            Assert.AreEqual(0, Convert.ToInt32(lockCommand.ExecuteScalar()));
        }

        using var first = StartDatabaseUpdate();
        using var second = StartDatabaseUpdate();
        Thread.Sleep(3000);
        Assert.IsFalse(first.HasExited, "The first update completed while the deployment lock was held.");
        Assert.IsFalse(second.HasExited, "The second update completed while the deployment lock was held.");

        lockTransaction.Commit();

        var firstResult = Complete(first, TimeSpan.FromMinutes(2));
        var secondResult = Complete(second, TimeSpan.FromMinutes(2));
        AssertSucceeded(firstResult);
        AssertSucceeded(secondResult);
        AssertCurrentState();
    }

    [TestMethod]
    public void MissingSharedPrerequisite_FailsWithSpecificErrorAndNoOwnedSchema()
    {
        using (var connection = Open())
        {
            Execute(connection, "drop table dbo.RegistrationFormSettings;");
        }

        var result = RunDatabaseUpdate();

        Assert.AreNotEqual(0, result.ExitCode, result.Output);
        StringAssert.Contains(result.Output, "dbo.RegistrationFormSettings must exist");
        Assert.AreEqual(0, Scalar<int>("select count(*) from sys.tables where name='RegistrationFormCodeMetadata' and schema_id=schema_id('dbo')"));
    }

    [TestMethod]
    public void IncompatibleSharedPrerequisite_FailsWithSpecificErrorAndNoOwnedSchema()
    {
        using (var connection = Open())
        {
            Execute(connection, "alter table dbo.RegistrationFormSettings alter column Value nvarchar(100) null;");
        }

        var result = RunDatabaseUpdate();

        Assert.AreNotEqual(0, result.ExitCode, result.Output);
        StringAssert.Contains(result.Output, "has an incompatible OrganizationID, Setting, FormCode, or Value definition");
        Assert.AreEqual(0, Scalar<int>("select count(*) from sys.tables where name='RegistrationFormCodeMetadata' and schema_id=schema_id('dbo')"));
    }

    [TestMethod]
    public void FilteredSharedSettingsKey_FailsBeforeOwnedSchemaCreation()
    {
        using (var connection = Open())
        {
            Execute(connection, """
                alter table dbo.RegistrationFormSettings drop constraint PK_Convergence_Settings;
                create unique index UX_Convergence_Settings_Filtered
                    on dbo.RegistrationFormSettings (OrganizationID, Setting, FormCode)
                    where OrganizationID > 0;
                """);
        }

        var result = RunDatabaseUpdate();

        Assert.AreNotEqual(0, result.ExitCode, result.Output);
        StringAssert.Contains(result.Output, "must have a unique key on OrganizationID, Setting, and FormCode");
        StringAssert.Contains(result.Output, "unconditional");
        AssertNoOwnedSchema();
        Assert.AreEqual(1, Scalar<int>("select count(*) from sys.indexes where object_id=object_id('dbo.RegistrationFormSettings') and name='UX_Convergence_Settings_Filtered' and has_filter=1 and filter_definition is not null"));
    }

    [TestMethod]
    public void ExistingSettingsDraftsAndAssets_ArePreservedAndTransformedAsRequired()
    {
        AssertSucceeded(RunDatabaseUpdate());

        long draftId;
        long previewLinkId;
        using (var connection = Open())
        {
            Execute(connection, """
                insert dbo.RegistrationFormSettingTypes (Setting) values ('header_image_url');
                insert dbo.RegistrationFormSettingTypes (Setting) values ('legal_name_checkbox_label');
                insert dbo.RegistrationFormSettings (OrganizationID, Setting, FormCode, Value)
                    values (101, 'header_image_url', 'form', 'https://legacy.example/header.png');
                insert dbo.RegistrationFormSettings (OrganizationID, Setting, FormCode, Value)
                    values (101, 'legal_name_checkbox_label', 'form', 'Use legal name');
                insert dbo.RegistrationFormAssets
                    (FileName, ContentType, Content, ContentHash, UploadOrganizationId, UploadFormCode)
                    values ('existing.png', 'image/png', 0x010203, 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 101, 'form');
                """);

            using var draftCommand = new SqlCommand("""
                insert dbo.RegistrationSettingDrafts
                    (OrganizationId, FormCode, BaselineVersion, Status, CreatedBy, ModifiedBy)
                output inserted.DraftId
                values (101, 'form', 0, 'Active', 'existing-data-test', 'existing-data-test');
                """, connection) { CommandTimeout = 30 };
            draftId = Convert.ToInt64(draftCommand.ExecuteScalar());
            Execute(connection, """
                insert dbo.RegistrationSettingDraftChanges
                    (DraftId, SettingKey, Operation, Value, ModifiedBy)
                values (@draftId, 'legal_name_checkbox_label', 'Upsert', 'Use legal name', 'existing-data-test');
                """, parameters: command => command.Parameters.AddWithValue("@draftId", draftId));

            using var linkCommand = new SqlCommand("""
                insert dbo.RegistrationSettingPreviewLinks
                    (DraftId, TokenHash, OperationalBranchId, AllowLiveSubmission, CreatedBy, ModifiedBy, ExpiresAtUtc)
                output inserted.PreviewLinkId
                values (@draftId, hashbytes('SHA2_256', convert(varbinary(max), 'existing-preview')),
                    101, 1, 'existing-data-test', 'existing-data-test', dateadd(hour, 1, sysutcdatetime()));
                """, connection) { CommandTimeout = 30 };
            linkCommand.Parameters.AddWithValue("@draftId", draftId);
            previewLinkId = Convert.ToInt64(linkCommand.ExecuteScalar());
        }

        var result = RunDatabaseUpdate();

        AssertSucceeded(result);
        Assert.AreEqual("Use legal name", ReadSetting(101, "form", "label.UseLegalName"));
        Assert.AreEqual(0, Scalar<int>("select count(*) from dbo.RegistrationFormSettings where Setting in ('header_image_url','legal_name_checkbox_label')"));
        Assert.AreEqual(1, Scalar<int>("select count(*) from dbo.RegistrationFormAssets where FileName='existing.png' and Content=0x010203 and UploadOrganizationId=101 and UploadFormCode='form'"));
        Assert.AreEqual(0, Scalar<int>("select count(*) from dbo.RegistrationSettingDraftChanges where DraftId=@draftId and SettingKey='legal_name_checkbox_label'", command => command.Parameters.AddWithValue("@draftId", draftId)));
        Assert.AreEqual(1, Scalar<int>("select count(*) from dbo.RegistrationSettingDrafts where DraftId=@draftId and Revision=1", command => command.Parameters.AddWithValue("@draftId", draftId)));
        Assert.AreEqual(1, Scalar<int>("select count(*) from dbo.RegistrationSettingPreviewLinks where PreviewLinkId=@linkId and RevokedAtUtc is not null", command => command.Parameters.AddWithValue("@linkId", previewLinkId)));
    }

    private void DeployRepresentativeOldDatabase()
    {
        using var connection = Open();
        var fixturePath = Path.Combine(
            RepositoryRoot(),
            "database",
            "test-fixtures",
            "representative-old-database.sql");
        if (!File.Exists(fixturePath))
        {
            throw new FileNotFoundException($"Representative old-database fixture was not found.", fixturePath);
        }

        Execute(connection, File.ReadAllText(fixturePath));
    }

    private Process StartDatabaseUpdate()
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
        startInfo.ArgumentList.Add(UpdateScriptPath());
        startInfo.ArgumentList.Add("-DatabaseName");
        startInfo.ArgumentList.Add(databaseName!);
        startInfo.ArgumentList.Add("-CommandTimeoutSeconds");
        startInfo.ArgumentList.Add("120");
        startInfo.Environment["PATRON_REGISTRATION_SQL_CONNECTION_STRING"] =
            Environment.GetEnvironmentVariable(ConnectionVariable)!;
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start PowerShell for convergence integration testing.");
    }

    private UpdateResult RunDatabaseUpdate()
    {
        using var process = StartDatabaseUpdate();
        return Complete(process, TimeSpan.FromMinutes(2));
    }

    private static UpdateResult Complete(Process process, TimeSpan timeout)
    {
        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            Assert.Fail("Database convergence did not finish before the integration-test timeout.");
        }

        var output = process.StandardOutput.ReadToEnd() + Environment.NewLine + process.StandardError.ReadToEnd();
        return new UpdateResult(process.ExitCode, output);
    }

    private static void AssertSucceeded(UpdateResult result) =>
        Assert.AreEqual(0, result.ExitCode, result.Output);

    private void AssertCurrentState()
    {
        var requiredTables = new[]
        {
            "RegistrationFormCodeMetadata", "RegistrationSettingScopeVersions", "RegistrationSettingDrafts",
            "RegistrationSettingDraftChanges", "RegistrationSettingPreviewLinks", "RegistrationSettingAuditEvents",
            "RegistrationSettingsCacheGeneration", "RegistrationFormAssets", "RegistrationFormAssetReferenceLocks"
        };
        foreach (var table in requiredTables)
        {
            Assert.AreEqual(1, Scalar<int>($"select case when object_id('dbo.{table}','U') is null then 0 else 1 end"), table);
        }

        Assert.AreEqual(1, Scalar<int>("select count(*) from sys.columns where object_id=object_id('dbo.RegistrationSettingDrafts') and name='Revision' and is_nullable=0"));
        Assert.AreEqual(1, Scalar<int>("select count(*) from sys.columns where object_id=object_id('dbo.RegistrationSettingPreviewLinks') and name='OperationalBranchId' and is_nullable=0"));
        Assert.AreEqual(1, Scalar<int>("select count(*) from sys.columns where object_id=object_id('dbo.RegistrationSettingPreviewLinks') and name='LiveSettingsGeneration' and is_nullable=1"));
        Assert.AreEqual(1, Scalar<int>("select count(*) from sys.columns where object_id=object_id('dbo.RegistrationSettingAuditEvents') and name='PreviousValue' and system_type_id=231 and max_length=-1 and is_nullable=1"));
        Assert.AreEqual(1, Scalar<int>("select count(*) from sys.columns where object_id=object_id('dbo.RegistrationSettingAuditEvents') and name='NewValue' and system_type_id=231 and max_length=-1 and is_nullable=1"));
        Assert.AreEqual(1, Scalar<int>("select count(*) from sys.indexes where object_id=object_id('dbo.RegistrationSettingAuditEvents') and name='IX_RSAE_LibraryTime'"));
        Assert.AreEqual(1, Scalar<int>("select count(*) from sys.indexes where object_id=object_id('dbo.RegistrationSettingDrafts') and name='UX_RSD_ActiveScope' and is_unique=1 and has_filter=1"));
        Assert.AreEqual(1, Scalar<int>("select count(*) from sys.indexes where object_id=object_id('dbo.RegistrationFormAssets') and name='IX_RegistrationFormAssets_CreatedDate'"));
        Assert.AreEqual(0, Scalar<int>("select count(*) from sys.indexes where object_id=object_id('dbo.RegistrationSettingAuditEvents') and name='IX_RSAE_ScopeFilter'"));
        Assert.AreEqual(0, Scalar<int>("select count(*) from sys.indexes where object_id=object_id('dbo.RegistrationFormAssets') and name='IX_RegistrationFormAssets_UploadScope'"));
        Assert.AreEqual(1, Scalar<int>("select count(*) from sys.foreign_keys where parent_object_id=object_id('dbo.RegistrationSettingDraftChanges') and name='FK_RSDC_Draft' and delete_referential_action=1 and is_disabled=0 and is_not_trusted=0"));
        Assert.AreEqual(1, Scalar<int>("select count(*) from sys.foreign_keys where parent_object_id=object_id('dbo.RegistrationSettingPreviewLinks') and name='FK_RSPL_Draft' and delete_referential_action=1 and is_disabled=0 and is_not_trusted=0"));
        Assert.AreEqual(1, Scalar<int>("select count(*) from dbo.RegistrationSettingsCacheGeneration where Id=1"));
        Assert.AreEqual(1, Scalar<int>("select count(*) from dbo.RegistrationFormAssetReferenceLocks where LockId=1"));

        foreach (var setting in new SettingCatalog().All)
        {
            Assert.AreEqual(1, Scalar<int>("select count(*) from dbo.RegistrationFormSettingTypes where Setting=@setting",
                command => command.Parameters.AddWithValue("@setting", setting.Key)), setting.Key);
        }
    }

    private void AssertNoOwnedSchema()
    {
        var ownedTables = new[]
        {
            "RegistrationFormCodeMetadata", "RegistrationSettingScopeVersions", "RegistrationSettingDrafts",
            "RegistrationSettingDraftChanges", "RegistrationSettingPreviewLinks", "RegistrationSettingAuditEvents",
            "RegistrationSettingsCacheGeneration", "RegistrationFormAssets", "RegistrationFormAssetReferenceLocks"
        };

        foreach (var table in ownedTables)
        {
            Assert.AreEqual(0, Scalar<int>($"select case when object_id('dbo.{table}','U') is null then 0 else 1 end"), table);
        }
    }

    private string LogicalSnapshot()
    {
        var rows = new List<string>
        {
            Scalar<int>("select count(*) from dbo.RegistrationFormCodeMetadata").ToString(),
            Scalar<int>("select count(*) from dbo.RegistrationSettingScopeVersions").ToString(),
            Scalar<int>("select count(*) from dbo.RegistrationSettingDrafts").ToString(),
            Scalar<int>("select count(*) from dbo.RegistrationSettingDraftChanges").ToString(),
            Scalar<int>("select count(*) from dbo.RegistrationSettingPreviewLinks").ToString(),
            Scalar<int>("select count(*) from dbo.RegistrationSettingAuditEvents").ToString(),
            Scalar<int>("select count(*) from dbo.RegistrationFormAssets").ToString(),
            Scalar<long>("select Generation from dbo.RegistrationSettingsCacheGeneration where Id=1").ToString()
        };
        rows.AddRange(Query("select Setting from dbo.RegistrationFormSettingTypes order by Setting", reader => reader.GetString(0)));
        rows.AddRange(Query("select concat(OrganizationID,'|',Setting,'|',FormCode,'|',coalesce(Value,'<null>')) from dbo.RegistrationFormSettings order by OrganizationID,Setting,FormCode", reader => reader.GetString(0)));
        rows.AddRange(Query("select DraftId,SettingKey,Operation,coalesce(Value,'<null>') from dbo.RegistrationSettingDraftChanges order by DraftId,SettingKey", reader =>
            $"{reader.GetInt64(0)}|{reader.GetString(1)}|{reader.GetString(2)}|{reader.GetString(3)}"));
        rows.AddRange(Query("select DraftId,Revision,Status from dbo.RegistrationSettingDrafts order by DraftId", reader =>
            $"{reader.GetInt64(0)}|{reader.GetInt64(1)}|{reader.GetString(2)}"));
        rows.AddRange(Query("select PreviewLinkId,AllowLiveSubmission,coalesce(LiveSettingsGeneration,-1),case when RevokedAtUtc is null then 0 else 1 end from dbo.RegistrationSettingPreviewLinks order by PreviewLinkId", reader =>
            $"{reader.GetInt64(0)}|{reader.GetBoolean(1)}|{reader.GetInt64(2)}|{reader.GetInt32(3)}"));
        return string.Join("\n", rows);
    }

    private void DeploySharedPrerequisites(SqlConnection connection)
    {
        Execute(connection, """
            create table dbo.RegistrationFormSettingTypes
            (
                Setting nvarchar(200) not null constraint PK_Convergence_SettingTypes primary key
            );
            create table dbo.RegistrationFormSettings
            (
                OrganizationID int not null,
                Setting nvarchar(200) not null,
                FormCode nvarchar(64) not null constraint DF_Convergence_FormCode default '',
                Value nvarchar(max) null,
                constraint PK_Convergence_Settings primary key (OrganizationID, Setting, FormCode),
                constraint FK_Convergence_Settings_Types foreign key (Setting)
                    references dbo.RegistrationFormSettingTypes(Setting)
            );
            insert dbo.RegistrationFormSettingTypes (Setting) values ('registration_text');
            insert dbo.RegistrationFormSettings (OrganizationID, Setting, FormCode, Value)
                values (101, 'registration_text', 'form', 'preserved setting');
            """);
    }

    private string ReadSetting(int organizationId, string formCode, string setting)
    {
        using var connection = Open();
        using var command = new SqlCommand("select Value from dbo.RegistrationFormSettings where OrganizationID=@organizationId and FormCode=@formCode and Setting=@setting", connection)
        {
            CommandTimeout = 30
        };
        command.Parameters.AddWithValue("@organizationId", organizationId);
        command.Parameters.AddWithValue("@formCode", formCode);
        command.Parameters.AddWithValue("@setting", setting);
        return (string?)command.ExecuteScalar() ?? string.Empty;
    }

    private T Scalar<T>(string sql, Action<SqlCommand>? parameters = null)
    {
        using var connection = Open();
        using var command = new SqlCommand(sql, connection) { CommandTimeout = 30 };
        parameters?.Invoke(command);
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
    }

    private List<T> Query<T>(string sql, Func<SqlDataReader, T> map)
    {
        using var connection = Open();
        using var command = new SqlCommand(sql, connection) { CommandTimeout = 30 };
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read()) rows.Add(map(reader));
        return rows;
    }

    private SqlConnection Open()
    {
        var connection = new SqlConnection(databaseConnectionString!);
        connection.Open();
        return connection;
    }

    private static void Execute(SqlConnection connection, string sql, Action<SqlCommand>? parameters = null)
    {
        using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        parameters?.Invoke(command);
        command.ExecuteNonQuery();
    }

    private static string RepositoryRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    private static string UpdateScriptPath() => Path.Combine(RepositoryRoot(), "database", "Invoke-DatabaseUpdate.ps1");

    private void TryDropDatabase(string configured)
    {
        if (string.IsNullOrWhiteSpace(databaseName)) return;
        try
        {
            var builder = new SqlConnectionStringBuilder(configured) { InitialCatalog = "master", ConnectTimeout = 10 };
            using var connection = new SqlConnection(builder.ConnectionString);
            connection.Open();
            Execute(connection, $"if db_id(N'{databaseName}') is not null begin alter database [{databaseName}] set single_user with rollback immediate; drop database [{databaseName}]; end;", null);
        }
        catch
        {
            // Cleanup is best effort so the test result reports the deployment behavior.
        }
        finally
        {
            databaseName = null;
            databaseConnectionString = null;
        }
    }

    private sealed record UpdateResult(int ExitCode, string Output);
}
