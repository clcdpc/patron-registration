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

    [DataTestMethod]
    [DataRow("initial-core-release")]
    [DataRow("asset-table-release")]
    [DataRow("pre-revision-generation-release")]
    public void KnownHistoricalReleaseStates_ConvergeToCurrent(string state)
    {
        DeployHistoricalReleaseFixture(state);

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

    [DataTestMethod]
    [DataRow("wrong-column-type", "dbo.RegistrationSettingDrafts.BaselineVersion")]
    [DataRow("missing-check-constraint", "dbo.RegistrationSettingDrafts.CK_RSD_Status")]
    [DataRow("missing-index", "dbo.RegistrationSettingDrafts.UX_RSD_ActiveScope")]
    public void IncompatibleUnknownOwnedSchema_FailsAtomicallyWithoutRepairOrDataChanges(
        string incompatibleState,
        string expectedObject)
    {
        AssertSucceeded(RunDatabaseUpdate());
        using (var connection = Open())
        {
            Execute(connection, incompatibleState switch
            {
                "wrong-column-type" => "alter table dbo.RegistrationSettingDrafts alter column BaselineVersion int not null;",
                "missing-check-constraint" => "alter table dbo.RegistrationSettingDrafts drop constraint CK_RSD_Status;",
                "missing-index" => "drop index UX_RSD_ActiveScope on dbo.RegistrationSettingDrafts;",
                _ => throw new ArgumentOutOfRangeException(nameof(incompatibleState), incompatibleState, null)
            });
            Execute(connection, """
                insert dbo.RegistrationFormSettingTypes (Setting) values ('legal_name_checkbox_label');
                insert dbo.RegistrationFormSettings (OrganizationID, Setting, FormCode, Value)
                    values (101, 'legal_name_checkbox_label', 'unknown-shape', 'preserve me');
                """);
        }

        var result = RunDatabaseUpdate();

        Assert.AreNotEqual(0, result.ExitCode, result.Output);
        StringAssert.Contains(result.Output, expectedObject);
        StringAssert.Contains(result.Output, "supported historical");
        Assert.AreEqual("preserve me", ReadSetting(101, "unknown-shape", "legal_name_checkbox_label"));
        Assert.AreEqual(0, Scalar<int>("select count(*) from dbo.RegistrationFormSettings where OrganizationID=101 and FormCode='unknown-shape' and Setting='label.UseLegalName'"));

        switch (incompatibleState)
        {
            case "wrong-column-type":
                Assert.AreEqual(1, Scalar<int>("select count(*) from sys.columns where object_id=object_id('dbo.RegistrationSettingDrafts') and name='BaselineVersion' and system_type_id=56"));
                break;
            case "missing-check-constraint":
                Assert.AreEqual(0, Scalar<int>("select count(*) from sys.check_constraints where parent_object_id=object_id('dbo.RegistrationSettingDrafts') and name='CK_RSD_Status'"));
                break;
            case "missing-index":
                Assert.AreEqual(0, Scalar<int>("select count(*) from sys.indexes where object_id=object_id('dbo.RegistrationSettingDrafts') and name='UX_RSD_ActiveScope'"));
                break;
        }
    }

    [TestMethod]
    public void SameNameActiveScopeIndexWithWrongPredicate_FailsAtomically()
    {
        AssertSucceeded(RunDatabaseUpdate());
        using (var connection = Open())
        {
            Execute(connection, "drop index UX_RSD_ActiveScope on dbo.RegistrationSettingDrafts;");
            Execute(connection, "create unique index UX_RSD_ActiveScope on dbo.RegistrationSettingDrafts (OrganizationId, FormCode) where Status <> 'Active';");
            SeedLegacyTransformationCandidate(connection, "wrong-filter");
        }

        var result = RunDatabaseUpdate();

        AssertValidationFailurePreservedLegacyData(result, "dbo.RegistrationSettingDrafts.UX_RSD_ActiveScope", "wrong-filter");
        Assert.AreEqual(1, Scalar<int>("select count(*) from sys.indexes where object_id=object_id('dbo.RegistrationSettingDrafts') and name='UX_RSD_ActiveScope' and filter_definition like '%<>%'") );
    }

    [TestMethod]
    public void SameNameStatusCheckWithWeakerTrustedDefinition_FailsAtomically()
    {
        AssertSucceeded(RunDatabaseUpdate());
        using (var connection = Open())
        {
            Execute(connection, "alter table dbo.RegistrationSettingDrafts drop constraint CK_RSD_Status;");
            Execute(connection, "alter table dbo.RegistrationSettingDrafts add constraint CK_RSD_Status check (Status <> 'Never');");
            Assert.AreEqual(0, Scalar<int>("select is_not_trusted from sys.check_constraints where parent_object_id=object_id('dbo.RegistrationSettingDrafts') and name='CK_RSD_Status'"));
            SeedLegacyTransformationCandidate(connection, "wrong-status-check");
        }

        var result = RunDatabaseUpdate();

        AssertValidationFailurePreservedLegacyData(result, "dbo.RegistrationSettingDrafts.CK_RSD_Status", "wrong-status-check");
        Assert.AreEqual(1, Scalar<int>("select count(*) from sys.check_constraints where parent_object_id=object_id('dbo.RegistrationSettingDrafts') and name='CK_RSD_Status' and definition like '%<>%'") );
    }

    [TestMethod]
    public void SameNameOwnedForeignKeyWithWrongColumnsAndParent_FailsAtomically()
    {
        AssertSucceeded(RunDatabaseUpdate());
        using (var connection = Open())
        {
            Execute(connection, "alter table dbo.RegistrationSettingDraftChanges drop constraint FK_RSDC_Draft;");
            Execute(connection, "alter table dbo.RegistrationSettingDraftChanges add constraint FK_RSDC_Draft foreign key (SettingKey) references dbo.RegistrationFormSettingTypes (Setting) on delete cascade;");
            Assert.AreEqual(0, Scalar<int>("select is_not_trusted from sys.foreign_keys where parent_object_id=object_id('dbo.RegistrationSettingDraftChanges') and name='FK_RSDC_Draft'"));
            SeedLegacyTransformationCandidate(connection, "wrong-fk");
        }

        var result = RunDatabaseUpdate();

        AssertValidationFailurePreservedLegacyData(result, "dbo.RegistrationSettingDraftChanges.FK_RSDC_Draft", "wrong-fk");
        Assert.AreEqual(1, Scalar<int>("select count(*) from sys.foreign_keys fk join sys.foreign_key_columns fkc on fkc.constraint_object_id=fk.object_id join sys.columns c on c.object_id=fk.parent_object_id and c.column_id=fkc.parent_column_id where fk.parent_object_id=object_id('dbo.RegistrationSettingDraftChanges') and fk.name='FK_RSDC_Draft' and fk.referenced_object_id=object_id('dbo.RegistrationFormSettingTypes') and c.name='SettingKey' and fk.delete_referential_action=1"));
    }

    [TestMethod]
    public void SameNameOwnedDefaultWithWrongDefinition_FailsAtomically()
    {
        AssertSucceeded(RunDatabaseUpdate());
        using (var connection = Open())
        {
            Execute(connection, "alter table dbo.RegistrationSettingDrafts drop constraint DF_RSD_Code;");
            Execute(connection, "alter table dbo.RegistrationSettingDrafts add constraint DF_RSD_Code default 'mutated' for FormCode;");
            SeedLegacyTransformationCandidate(connection, "wrong-default");
        }

        var result = RunDatabaseUpdate();

        AssertValidationFailurePreservedLegacyData(result, "dbo.RegistrationSettingDrafts.DF_RSD_Code", "wrong-default");
        Assert.AreEqual(1, Scalar<int>("select count(*) from sys.default_constraints where parent_object_id=object_id('dbo.RegistrationSettingDrafts') and name='DF_RSD_Code' and definition like '%mutated%'") );
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
    public void UnexpectedTrustedCheckConstraint_FailsAtomically()
    {
        AssertSucceeded(RunDatabaseUpdate());
        using (var connection = Open())
        {
            Execute(connection, "alter table dbo.RegistrationSettingDrafts add constraint CK_RSD_UnexpectedState check (Status <> 'Forbidden');");
            Assert.AreEqual(0, Scalar<int>("select is_not_trusted from sys.check_constraints where parent_object_id=object_id('dbo.RegistrationSettingDrafts') and name='CK_RSD_UnexpectedState'"));
            SeedLegacyTransformationCandidate(connection, "unexpected-check");
        }

        var before = LogicalSnapshot();
        var result = RunDatabaseUpdate();

        AssertValidationFailurePreservedSchemaAndData(
            result,
            "dbo.RegistrationSettingDrafts.CK_RSD_UnexpectedState",
            before);
        Assert.AreEqual(1, Scalar<int>("select count(*) from sys.check_constraints where parent_object_id=object_id('dbo.RegistrationSettingDrafts') and name='CK_RSD_UnexpectedState' and is_not_trusted=0"));
    }

    [TestMethod]
    public void UnexpectedUniqueConstraint_FailsAtomically()
    {
        AssertSucceeded(RunDatabaseUpdate());
        using (var connection = Open())
        {
            Execute(connection, "alter table dbo.RegistrationSettingDrafts add constraint UQ_RSD_UnexpectedScope unique (OrganizationId, FormCode, Status);");
            SeedLegacyTransformationCandidate(connection, "unexpected-unique");
        }

        var before = LogicalSnapshot();
        var result = RunDatabaseUpdate();

        AssertValidationFailurePreservedSchemaAndData(
            result,
            "dbo.RegistrationSettingDrafts.UQ_RSD_UnexpectedScope",
            before);
        Assert.AreEqual(1, Scalar<int>("select count(*) from sys.key_constraints where parent_object_id=object_id('dbo.RegistrationSettingDrafts') and name='UQ_RSD_UnexpectedScope' and type='UQ'"));
    }

    [DataTestMethod]
    [DataRow("unexpected-default", "dbo.RegistrationSettingDrafts.DF_RSD_Unexpected", "default")]
    [DataRow("unexpected-foreign-key", "dbo.RegistrationSettingDrafts.FK_RSD_Unexpected", "foreign-key")]
    public void UnexpectedTrustedDefaultOrForeignKey_FailsAtomically(
        string formCode,
        string expectedObject,
        string constraintKind)
    {
        AssertSucceeded(RunDatabaseUpdate());
        using (var connection = Open())
        {
            Execute(connection, constraintKind switch
            {
                "default" => "alter table dbo.RegistrationSettingDrafts add constraint DF_RSD_Unexpected default 'unexpected' for CommittedBy;",
                "foreign-key" => "alter table dbo.RegistrationSettingDrafts add constraint FK_RSD_Unexpected foreign key (DraftId) references dbo.RegistrationSettingDrafts(DraftId);",
                _ => throw new ArgumentOutOfRangeException(nameof(constraintKind), constraintKind, null)
            });
            SeedLegacyTransformationCandidate(connection, formCode);
        }

        var before = LogicalSnapshot();
        var result = RunDatabaseUpdate();

        AssertValidationFailurePreservedSchemaAndData(result, expectedObject, before);
        if (constraintKind == "default")
        {
            Assert.AreEqual(1, Scalar<int>("select count(*) from sys.default_constraints where parent_object_id=object_id('dbo.RegistrationSettingDrafts') and name='DF_RSD_Unexpected'"));
        }
        else
        {
            Assert.AreEqual(1, Scalar<int>("select count(*) from sys.foreign_keys where parent_object_id=object_id('dbo.RegistrationSettingDrafts') and name='FK_RSD_Unexpected' and is_not_trusted=0"));
        }
    }

    [TestMethod]
    public void UnexpectedOwnedIndex_FailsAtomically()
    {
        AssertSucceeded(RunDatabaseUpdate());
        using (var connection = Open())
        {
            Execute(connection, "create index IX_RSD_UnexpectedStatus on dbo.RegistrationSettingDrafts (Status);");
            SeedLegacyTransformationCandidate(connection, "unexpected-index");
        }

        var before = LogicalSnapshot();
        var result = RunDatabaseUpdate();

        AssertValidationFailurePreservedSchemaAndData(
            result,
            "dbo.RegistrationSettingDrafts.IX_RSD_UnexpectedStatus",
            before);
        Assert.AreEqual(1, Scalar<int>("select count(*) from sys.indexes where object_id=object_id('dbo.RegistrationSettingDrafts') and name='IX_RSD_UnexpectedStatus'"));
    }

    [DataTestMethod]
    [DataRow("missing-include", "")]
    [DataRow("incorrect-include-set", "EventType,TargetOrganizationId,Succeeded")]
    [DataRow("incorrect-include-order", "FormCode,TargetOrganizationId,EventType")]
    public void AuditLibraryTimeIndexWithUnexpectedIncludes_FailsAtomically(
        string mutation,
        string expectedIncludedColumns)
    {
        AssertSucceeded(RunDatabaseUpdate());
        using (var connection = Open())
        {
            Execute(connection, "drop index IX_RSAE_LibraryTime on dbo.RegistrationSettingAuditEvents;");
            Execute(connection, mutation switch
            {
                "missing-include" => "create index IX_RSAE_LibraryTime on dbo.RegistrationSettingAuditEvents (TargetLibraryId, TimestampUtc desc);",
                "incorrect-include-set" => "create index IX_RSAE_LibraryTime on dbo.RegistrationSettingAuditEvents (TargetLibraryId, TimestampUtc desc) include (EventType, TargetOrganizationId, Succeeded);",
                "incorrect-include-order" => "create index IX_RSAE_LibraryTime on dbo.RegistrationSettingAuditEvents (TargetLibraryId, TimestampUtc desc) include (FormCode, TargetOrganizationId, EventType);",
                _ => throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null)
            });
            SeedLegacyTransformationCandidate(connection, mutation);
        }

        var before = LogicalSnapshot();
        var result = RunDatabaseUpdate();

        AssertValidationFailurePreservedSchemaAndData(
            result,
            "dbo.RegistrationSettingAuditEvents.IX_RSAE_LibraryTime",
            before);
        Assert.AreEqual(expectedIncludedColumns, ReadAuditLibraryTimeIncludedColumns());
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

    private void DeployHistoricalReleaseFixture(string state)
    {
        using var connection = Open();
        var fixturePath = Path.Combine(
            RepositoryRoot(),
            "database",
            "test-fixtures",
            "historical",
            $"{state}.sql");
        if (!File.Exists(fixturePath))
        {
            throw new FileNotFoundException($"Historical schema fixture was not found for {state}.", fixturePath);
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
        Assert.AreEqual("EventType,TargetOrganizationId,FormCode", ReadAuditLibraryTimeIncludedColumns());
        Assert.AreEqual(1, Scalar<int>("select count(*) from sys.indexes where object_id=object_id('dbo.RegistrationSettingDrafts') and name='UX_RSD_ActiveScope' and is_unique=1 and has_filter=1"));
        Assert.AreEqual(1, Scalar<int>("select count(*) from sys.indexes where object_id=object_id('dbo.RegistrationFormAssets') and name='IX_RegistrationFormAssets_UploadScope'"));
        Assert.AreEqual(1, Scalar<int>("select count(*) from sys.indexes where object_id=object_id('dbo.RegistrationFormAssets') and name='IX_RegistrationFormAssets_CreatedDate'"));
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

    private void AssertValidationFailurePreservedSchemaAndData(
        UpdateResult result,
        string expectedObject,
        string beforeSnapshot)
    {
        Assert.AreNotEqual(0, result.ExitCode, result.Output);
        StringAssert.Contains(result.Output, expectedObject);
        StringAssert.Contains(result.Output, "supported");
        Assert.AreEqual(beforeSnapshot, LogicalSnapshot());
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

    private static void SeedLegacyTransformationCandidate(SqlConnection connection, string formCode)
    {
        Execute(connection, """
            if not exists (select 1 from dbo.RegistrationFormSettingTypes where Setting='legal_name_checkbox_label')
                insert dbo.RegistrationFormSettingTypes (Setting) values ('legal_name_checkbox_label');
            insert dbo.RegistrationFormSettings (OrganizationID, Setting, FormCode, Value)
                values (101, 'legal_name_checkbox_label', @formCode, 'preserve me');
            """, parameters: command => command.Parameters.AddWithValue("@formCode", formCode));
    }

    private void AssertValidationFailurePreservedLegacyData(UpdateResult result, string expectedObject, string formCode)
    {
        Assert.AreNotEqual(0, result.ExitCode, result.Output);
        StringAssert.Contains(result.Output, expectedObject);
        StringAssert.Contains(result.Output, "Restore");
        Assert.AreEqual("preserve me", ReadSetting(101, formCode, "legal_name_checkbox_label"));
        Assert.AreEqual(1, Scalar<int>("select count(*) from dbo.RegistrationFormSettings where OrganizationID=101 and FormCode=@formCode and Setting='legal_name_checkbox_label'",
            command => command.Parameters.AddWithValue("@formCode", formCode)));
        Assert.AreEqual(0, Scalar<int>("select count(*) from dbo.RegistrationFormSettings where OrganizationID=101 and FormCode=@formCode and Setting='label.UseLegalName'",
            command => command.Parameters.AddWithValue("@formCode", formCode)));
        Assert.AreEqual(0L, Scalar<long>("select Generation from dbo.RegistrationSettingsCacheGeneration where Id=1"));
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

    private string ReadAuditLibraryTimeIncludedColumns() => Scalar<string>("""
        select coalesce(stuff
        (
            (
                select ',' + c.name
                from sys.index_columns ic
                inner join sys.indexes i on i.object_id = ic.object_id and i.index_id = ic.index_id
                inner join sys.columns c on c.object_id = ic.object_id and c.column_id = ic.column_id
                where i.object_id = object_id('dbo.RegistrationSettingAuditEvents')
                    and i.name = 'IX_RSAE_LibraryTime'
                    and ic.is_included_column = 1
                order by ic.index_column_id
                for xml path(''), type
            ).value('.', 'nvarchar(max)'),
            1, 1, ''
        ), '')
        """);

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
