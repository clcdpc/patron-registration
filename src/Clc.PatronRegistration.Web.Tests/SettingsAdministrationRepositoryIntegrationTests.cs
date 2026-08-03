using System.Data;
using Clc.PatronRegistration.Administration;
using Clc.PatronRegistration.Web.Settings;
using Microsoft.Data.SqlClient;

#nullable enable

namespace Clc.PatronRegistration.Tests;

internal enum DatabaseCleanupMode { None, BestEffort, Strict }

internal static class DatabaseCleanupModeSelector
{
    internal static DatabaseCleanupMode Select(string? configured, string? unavailableReason) =>
        string.IsNullOrWhiteSpace(configured)
            ? DatabaseCleanupMode.None
            : unavailableReason is not null
                ? DatabaseCleanupMode.BestEffort
                : DatabaseCleanupMode.Strict;
}

[TestClass]
[DoNotParallelize]
public sealed class SettingsAdministrationRepositoryIntegrationTests
{
    private const string ConnectionVariable = "PATRON_REGISTRATION_TEST_SQL_CONNECTION_STRING";
    private static string? databaseName;
    private static string? databaseConnectionString;
    private static string? unavailableReason;
    private static bool databaseCreated;
    private static bool schemaReady;
    private static TestContext? classContext;
    private SettingsAdministrationRepository repository = null!;

    private static readonly SettingDefinition First = new("test.first", "First", "Test value", SettingValueType.ShortString);
    private static readonly SettingDefinition Second = new("test.second", "Second", "Test value", SettingValueType.ShortString);
    private static readonly SettingDefinition Secret = new("test.secret", "Secret", "Test secret", SettingValueType.ShortString, IsSensitive: true);
    private static readonly IReadOnlyDictionary<string, SettingDefinition> Catalog =
        new[] { First, Second, Secret }.ToDictionary(item => item.Key, StringComparer.OrdinalIgnoreCase);

    [ClassInitialize]
    public static void CreateDatabase(TestContext context)
    {
        classContext = context;
        var configured = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            unavailableReason = $"SQL-backed repository tests require {ConnectionVariable}. The connection must permit CREATE DATABASE.";
            return;
        }

        databaseName = $"PatronRegistrationTests_{Guid.NewGuid():N}";
        try
        {
            var adminBuilder = new SqlConnectionStringBuilder(configured) { InitialCatalog = "master", ConnectTimeout = 10 };
            using var connection = new SqlConnection(adminBuilder.ConnectionString);
            connection.Open();
            Execute(connection, $"CREATE DATABASE [{databaseName}]");
            databaseCreated = true;
        }
        catch (SqlException exception)
        {
            TryDropDatabase(configured);
            unavailableReason = $"SQL-backed repository tests could not create a temporary database using {ConnectionVariable}: " +
                $"{exception.Message} The configured login must be able to create and drop a temporary database.";
            return;
        }

        try
        {
            var databaseBuilder = new SqlConnectionStringBuilder(configured) { InitialCatalog = databaseName, ConnectTimeout = 10 };
            var candidateConnectionString = databaseBuilder.ConnectionString;
            using var database = new SqlConnection(candidateConnectionString);
            database.Open();
            foreach (var file in new[] { "001-settings-administration.sql", "002-preview-operational-branch.sql", "003-expand-audit-setting-values.sql" })
            {
                Execute(database, File.ReadAllText(Path.Combine(RepositoryRoot(), "database", file)), 30);
            }
            databaseConnectionString = candidateConnectionString;
            schemaReady = true;
        }
        catch
        {
            TryDropDatabase(configured);
            throw;
        }
    }

    [ClassCleanup]
    public static void DeleteDatabase()
    {
        var configured = Environment.GetEnvironmentVariable(ConnectionVariable);
        switch (DatabaseCleanupModeSelector.Select(configured, unavailableReason))
        {
            case DatabaseCleanupMode.None:
                return;
            case DatabaseCleanupMode.BestEffort:
                TryDropDatabase(configured!);
                return;
            case DatabaseCleanupMode.Strict:
                DropDatabaseCore(configured!);
                return;
            default:
                throw new InvalidOperationException("Unknown SQL integration database cleanup mode.");
        }
    }

    [TestInitialize]
    public void ResetDatabase()
    {
        if (unavailableReason is not null) Assert.Inconclusive(unavailableReason);
        Assert.IsTrue(databaseCreated && schemaReady && databaseConnectionString is not null,
            "The SQL integration fixture attempted setup but did not finish deploying the schema.");
        repository = new SettingsAdministrationRepository(databaseConnectionString!);
        using var connection = Open();
        Execute(connection, @"delete dbo.RegistrationSettingAuditEvents;
delete dbo.RegistrationSettingPreviewLinks;
delete dbo.RegistrationSettingDraftChanges;
delete dbo.RegistrationSettingDrafts;
delete dbo.RegistrationSettingScopeVersions;");
    }

    [TestMethod]
    public void Fixture_DeploysRequiredSettingsAdministrationSchema()
    {
        var requiredObjects = new[]
        {
            "dbo.RegistrationSettingScopeVersions",
            "dbo.RegistrationSettingDrafts",
            "dbo.RegistrationSettingDraftChanges",
            "dbo.RegistrationSettingAuditEvents"
        };
        foreach (var requiredObject in requiredObjects)
        {
            Assert.AreEqual(1, Scalar<int>($"select case when object_id('{requiredObject}', 'U') is null then 0 else 1 end"),
                $"Required schema object {requiredObject} was not deployed.");
        }
        Assert.AreEqual(1, Scalar<int>("select count(*) from sys.indexes where object_id=object_id('dbo.RegistrationSettingDrafts') and name='UX_RSD_ActiveScope' and is_unique=1 and has_filter=1"));
    }

    [TestMethod]
    public void SaveToSharedDraft_NoActiveDraft_CreatesDraftAndPersistsAllChangesAtomically()
    {
        SeedVersion(17);
        var result = repository.SaveToSharedDraft(101, "form", 17, null,
            [Upsert(First, "one"), Upsert(Second, "two")], Catalog, Audit());

        Assert.IsTrue(result.DraftCreated);
        Assert.AreEqual(1, CountActiveDrafts());
        var draft = ReadDraft(result.DraftId);
        Assert.AreEqual(17L, draft.BaselineVersion);
        Assert.AreEqual("integration-admin", draft.ModifiedBy);
        Assert.IsNotNull(draft.ModifiedAtUtc);
        CollectionAssert.AreEquivalent(new[] { "test.first|Upsert|one", "test.second|Upsert|two" }, ReadChanges(result.DraftId).ToArray());
        AssertAuditCount("DraftCreated", 1);
        AssertAuditCount("DraftEdited", 1);
        Assert.AreEqual("{\"changeCount\":2}", ReadAudits().Single(row => row.EventType == "DraftEdited").MetadataJson);
    }

    [TestMethod]
    public void SaveToSharedDraft_PostInsertMutationFailure_RollsBackDraftChangesAndAudits()
    {
        SeedVersion(9);
        var missing = new SettingMutation("missing.key", DraftOperation.Upsert, "bad");
        Assert.ThrowsException<InvalidOperationException>(() => repository.SaveToSharedDraft(101, "form", 9, null,
            [Upsert(First, "written-before-failure"), missing], Catalog, Audit()));

        AssertNoDraftChangesOrSuccessAudits();
        Assert.AreEqual(9L, ReadScopeVersion());
        var retry = repository.SaveToSharedDraft(101, "form", 9, null, [Upsert(First, "retry")], Catalog, Audit());
        Assert.IsTrue(retry.DraftCreated);
        Assert.AreEqual(1, CountActiveDrafts());
    }

    [TestMethod]
    public void SaveToSharedDraft_NullExpectedDraftId_ReusesExistingActiveDraft()
    {
        SeedVersion(3);
        var first = repository.SaveToSharedDraft(101, "form", 3, null, [Upsert(First, "existing")], Catalog, Audit());
        var beforeEdited = ReadAudits().Count(row => row.EventType == "DraftEdited");

        var result = repository.SaveToSharedDraft(101, "form", 3, null, [Upsert(Second, "new")], Catalog, Audit());

        Assert.AreEqual(first.DraftId, result.DraftId);
        Assert.IsFalse(result.DraftCreated);
        Assert.AreEqual(1, CountActiveDrafts());
        CollectionAssert.AreEquivalent(new[] { "test.first|Upsert|existing", "test.second|Upsert|new" }, ReadChanges(result.DraftId).ToArray());
        AssertAuditCount("DraftCreated", 1);
        Assert.AreEqual(beforeEdited + 1, ReadAudits().Count(row => row.EventType == "DraftEdited"));
    }

    [TestMethod]
    public void SaveToSharedDraft_StaleVersion_RollsBackEverything()
    {
        SeedVersion(6);
        Assert.ThrowsException<DBConcurrencyException>(() => repository.SaveToSharedDraft(101, "form", 5, null,
            [Upsert(First, "stale")], Catalog, Audit()));
        AssertNoDraftChangesOrSuccessAudits();
        Assert.AreEqual(6L, ReadScopeVersion());
    }

    [TestMethod]
    public void SaveToSharedDraft_ActiveDraftWithDifferentBaseline_IsUnchanged()
    {
        SeedVersion(6);
        var draftId = SeedActiveDraft(6, First, "existing");
        var auditCount = ReadAudits().Count;

        Assert.ThrowsException<DBConcurrencyException>(() => repository.SaveToSharedDraft(101, "form", 5, null,
            [Upsert(Second, "stale")], Catalog, Audit()));

        Assert.AreEqual(1, CountActiveDrafts());
        CollectionAssert.AreEqual(new[] { "test.first|Upsert|existing" }, ReadChanges(draftId).ToArray());
        Assert.AreEqual(auditCount, ReadAudits().Count);
    }

    [TestMethod]
    public void SaveToSharedDraft_ExistingDraft_UpdatesSubmittedKeyAndPreservesOtherChanges()
    {
        SeedVersion(1);
        var first = repository.SaveToSharedDraft(101, "form", 1, null,
            [Upsert(First, "old"), Upsert(Second, "untouched")], Catalog, Audit());

        var result = repository.SaveToSharedDraft(101, "form", 1, first.DraftId,
            [new SettingMutation(First.Key, DraftOperation.RemoveOverride, null)], Catalog, Audit());

        Assert.AreEqual(first.DraftId, result.DraftId);
        Assert.IsFalse(result.DraftCreated);
        CollectionAssert.AreEquivalent(new[] { "test.first|RemoveOverride|", "test.second|Upsert|untouched" }, ReadChanges(result.DraftId).ToArray());
        Assert.AreEqual(1, ReadChanges(result.DraftId).Count(row => row.StartsWith("test.first|", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void SaveToSharedDraft_WrongExpectedDraftId_ThrowsAndWritesNothing()
    {
        SeedVersion(1);
        var existing = repository.SaveToSharedDraft(101, "form", 1, null, [Upsert(First, "existing")], Catalog, Audit());
        var auditCount = ReadAudits().Count;

        Assert.ThrowsException<DBConcurrencyException>(() => repository.SaveToSharedDraft(101, "form", 1, existing.DraftId + 100,
            [Upsert(Second, "must-not-write")], Catalog, Audit()));

        Assert.AreEqual(1, CountActiveDrafts());
        CollectionAssert.AreEqual(new[] { "test.first|Upsert|existing" }, ReadChanges(existing.DraftId).ToArray());
        Assert.AreEqual(auditCount, ReadAudits().Count);
    }

    [TestMethod]
    public void SaveToSharedDraft_ExpectedDraftIdButNoActiveDraft_ThrowsAndDoesNotCreateReplacement()
    {
        SeedVersion(1);
        Assert.ThrowsException<DBConcurrencyException>(() => repository.SaveToSharedDraft(101, "form", 1, 42,
            [Upsert(First, "must-not-write")], Catalog, Audit()));
        AssertNoDraftChangesOrSuccessAudits();
    }

    [TestMethod]
    public async Task SaveToSharedDraft_ConcurrentFirstSaves_ProduceOneActiveDraft()
    {
        SeedVersion(1);
        using var barrier = new Barrier(2);
        Task<(SaveToDraftResult? Result, Exception? Error)> Start(SettingMutation mutation) => Task.Run(() =>
        {
            barrier.SignalAndWait(TimeSpan.FromSeconds(10));
            try
            {
                return ((SaveToDraftResult?)new SettingsAdministrationRepository(databaseConnectionString!)
                    .SaveToSharedDraft(101, "form", 1, null, [mutation], Catalog, Audit()), (Exception?)null);
            }
            catch (Exception exception) { return ((SaveToDraftResult?)null, (Exception?)exception); }
        });

        var tasks = new[] { Start(Upsert(First, "one")), Start(Upsert(Second, "two")) };
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(40));
        var outcomes = tasks.Select(task => task.Result).ToList();
        var errors = outcomes.Where(item => item.Error is not null).Select(item => item.Error!).ToList();
        Assert.IsTrue(errors.All(error => error is DBConcurrencyException || error is SqlException { Number: 1205 or 2601 or 2627 }),
            $"Unexpected concurrent-save error: {errors.FirstOrDefault()}");
        var successes = outcomes.Where(item => item.Result is not null).Select(item => item.Result!).ToList();
        Assert.IsTrue(successes.Count is 1 or 2);
        Assert.AreEqual(1, CountActiveDrafts());
        Assert.AreEqual(1, successes.Count(item => item.DraftCreated));
        if (successes.Count == 2)
        {
            Assert.AreEqual(successes[0].DraftId, successes[1].DraftId);
            CollectionAssert.AreEquivalent(new[] { "test.first|Upsert|one", "test.second|Upsert|two" }, ReadChanges(successes[0].DraftId).ToArray());
        }
        else Assert.AreEqual(1, ReadChanges(successes[0].DraftId).Count);
        AssertAuditCount("DraftCreated", 1);
    }

    [TestMethod]
    public void SaveToSharedDraft_SensitiveValueIsStoredButNotLeakedToAudit()
    {
        SeedVersion(1);
        const string secret = "super-secret-value";
        var result = repository.SaveToSharedDraft(101, "form", 1, null, [Upsert(Secret, secret)], Catalog, Audit());

        StringAssert.Contains(ReadChanges(result.DraftId).Single(), secret);
        var audits = ReadAudits();
        Assert.IsFalse(audits.Any(row => (row.MetadataJson ?? "").Contains(secret, StringComparison.Ordinal) ||
            (row.FailureReason ?? "").Contains(secret, StringComparison.Ordinal) ||
            (row.PreviousValue ?? "").Contains(secret, StringComparison.Ordinal) ||
            (row.NewValue ?? "").Contains(secret, StringComparison.Ordinal)));
        // DraftCreated and DraftEdited are draft-level events and deliberately do not classify an individual mutation.
        Assert.IsTrue(audits.All(row => !row.IsSensitive));
    }

    [DataTestMethod]
    [DataRow(1)]
    [DataRow(24)]
    [DataRow(168)]
    public void CreatePreviewLink_BoundedLifetimePersistsExactExpiration(int lifetimeHours)
    {
        SeedVersion(1);
        var draftId = SeedActiveDraft(1, First, "draft");
        var nowUtc = new DateTime(2030, 4, 5, 6, 7, 8, DateTimeKind.Utc);

        var linkId = repository.CreatePreviewLink(draftId, new byte[32], false, 101, nowUtc, lifetimeHours, Catalog, true, Audit());

        Assert.AreEqual(nowUtc.AddHours(lifetimeHours), ReadPreviewExpiration(linkId));
        AssertAuditCount("PreviewLinkCreated", 1);
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(169)]
    public void CreatePreviewLink_InvalidLifetimeWritesNothing(int lifetimeHours)
    {
        SeedVersion(1);
        var draftId = SeedActiveDraft(1, First, "draft");
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => repository.CreatePreviewLink(draftId,
            new byte[32], false, 101, new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc), lifetimeHours, Catalog, true, Audit()));
        Assert.AreEqual(0, Scalar<int>("select count(*) from dbo.RegistrationSettingPreviewLinks"));
        AssertAuditCount("PreviewLinkCreated", 0);
    }

    [DataTestMethod]
    [DataRow(-1, true)]
    [DataRow(0, false)]
    [DataRow(1, false)]
    public void ResolvePreviewContext_RequiresFutureNonNullExpiration(int secondsFromExpiration, bool resolves)
    {
        SeedVersion(1);
        var draftId = SeedActiveDraft(1, First, "draft");
        var nowUtc = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var hash = Enumerable.Repeat((byte)7, 32).ToArray();
        repository.CreatePreviewLink(draftId, hash, false, 101, nowUtc, 1, Catalog, true, Audit());

        Assert.AreEqual(resolves, repository.ResolvePreviewContext(hash, nowUtc.AddHours(1).AddSeconds(secondsFromExpiration)) is not null);
    }

    [TestMethod]
    public void ResolveAndReplacePreviewContext_RejectLegacyNullExpiration()
    {
        SeedVersion(1);
        var draftId = SeedActiveDraft(1, First, "draft");
        var hash = Enumerable.Repeat((byte)8, 32).ToArray();
        using (var connection = Open())
            Execute(connection, @"insert dbo.RegistrationSettingPreviewLinks(DraftId,TokenHash,AllowLiveSubmission,OperationalBranchId,CreatedBy,ModifiedBy,ExpiresAtUtc)
values(@draftId,@hash,0,101,'legacy','legacy',null)", parameters: command =>
            {
                command.Parameters.AddWithValue("@draftId", draftId);
                command.Parameters.AddWithValue("@hash", hash);
            });
        var linkId = Scalar<long>("select PreviewLinkId from dbo.RegistrationSettingPreviewLinks");

        Assert.IsNull(repository.ResolvePreviewContext(hash, new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        Assert.ThrowsException<DBConcurrencyException>(() => repository.ReplacePreviewLinkMode(linkId, new byte[32], true, Catalog, true, Audit()));
        Assert.AreEqual(1, Scalar<int>("select count(*) from dbo.RegistrationSettingPreviewLinks"));
    }

    [DataTestMethod]
    [DataRow(false, true)]
    [DataRow(true, false)]
    public void ReplacePreviewLinkMode_PreservesExactExpiration(bool originalLive, bool replacementLive)
    {
        SeedVersion(1);
        var draftId = SeedActiveDraft(1, First, "draft");
        var nowUtc = DateTime.UtcNow;
        var originalId = repository.CreatePreviewLink(draftId, Enumerable.Repeat((byte)3, 32).ToArray(),
            originalLive, 101, nowUtc, 24, Catalog, true, Audit());
        var expectedExpiration = repository.GetPreviewLink(originalId)!.ExpiresAtUtc;

        var replacementId = repository.ReplacePreviewLinkMode(originalId, Enumerable.Repeat((byte)4, 32).ToArray(),
            replacementLive, Catalog, true, Audit());

        Assert.IsNotNull(replacementId);
        Assert.AreEqual(expectedExpiration, repository.GetPreviewLink(replacementId.Value)!.ExpiresAtUtc);
        Assert.IsNotNull(repository.GetPreviewLink(originalId)!.RevokedAtUtc);
    }

    [TestMethod]
    public void ReplacePreviewLinkMode_ExpiredLinkWritesNothing()
    {
        SeedVersion(1);
        var draftId = SeedActiveDraft(1, First, "draft");
        var originalId = repository.CreatePreviewLink(draftId, Enumerable.Repeat((byte)5, 32).ToArray(),
            false, 101, DateTime.UtcNow.AddHours(-2), 1, Catalog, true, Audit());
        var original = repository.GetPreviewLink(originalId)!;
        var auditCount = Scalar<int>("select count(*) from dbo.RegistrationSettingAuditEvents where EventType='PreviewLinkModeReplaced' and Succeeded=1");

        Assert.ThrowsException<DBConcurrencyException>(() => repository.ReplacePreviewLinkMode(originalId,
            Enumerable.Repeat((byte)6, 32).ToArray(), true, Catalog, true, Audit()));

        var unchanged = repository.GetPreviewLink(originalId)!;
        Assert.IsNull(unchanged.RevokedAtUtc);
        Assert.AreEqual(original.ExpiresAtUtc, unchanged.ExpiresAtUtc);
        Assert.AreEqual(1, Scalar<int>("select count(*) from dbo.RegistrationSettingPreviewLinks"));
        Assert.AreEqual(auditCount, Scalar<int>("select count(*) from dbo.RegistrationSettingAuditEvents where EventType='PreviewLinkModeReplaced' and Succeeded=1"));
    }

    private void SeedVersion(long version)
    {
        using var connection = Open();
        Execute(connection,
            "insert dbo.RegistrationSettingScopeVersions(OrganizationId,FormCode,Version) values(101,'form',@version)",
            parameters: command => command.Parameters.AddWithValue("@version", version));
    }
    private long SeedActiveDraft(long baselineVersion, SettingDefinition definition, string value)
    {
        using var connection = Open();
        using var command = Command(connection, @"insert dbo.RegistrationSettingDrafts(OrganizationId,FormCode,BaselineVersion,Status,CreatedBy,ModifiedBy)
output inserted.DraftId values(101,'form',@baseline,'Active','other','other')");
        command.Parameters.AddWithValue("@baseline", baselineVersion);
        var draftId = (long)command.ExecuteScalar()!;
        Execute(connection, @"insert dbo.RegistrationSettingDraftChanges(DraftId,SettingKey,Operation,Value,ModifiedBy)
values(@draftId,@key,'Upsert',@value,'other')", parameters: mutation =>
        {
            mutation.Parameters.AddWithValue("@draftId", draftId);
            mutation.Parameters.AddWithValue("@key", definition.Key);
            mutation.Parameters.AddWithValue("@value", value);
        });
        return draftId;
    }
    private long ReadScopeVersion() => Scalar<long>("select Version from dbo.RegistrationSettingScopeVersions where OrganizationId=101 and FormCode='form'");
    private int CountActiveDrafts() => Scalar<int>("select count(*) from dbo.RegistrationSettingDrafts where OrganizationId=101 and FormCode='form' and Status='Active'");
    private DraftState ReadDraft(long draftId) => QuerySingle("select BaselineVersion,ModifiedAtUtc,ModifiedBy from dbo.RegistrationSettingDrafts where DraftId=@id",
        command => command.Parameters.AddWithValue("@id", draftId), reader => new DraftState(reader.GetInt64(0), reader.GetDateTime(1), reader.GetString(2)));
    private List<string> ReadChanges(long draftId) => Query("select SettingKey,Operation,Value from dbo.RegistrationSettingDraftChanges where DraftId=@id order by SettingKey",
        command => command.Parameters.AddWithValue("@id", draftId), reader => $"{reader.GetString(0)}|{reader.GetString(1)}|{(reader.IsDBNull(2) ? "" : reader.GetString(2))}");
    private List<AuditState> ReadAudits() => Query("select EventType,MetadataJson,FailureReason,PreviousValue,NewValue,IsSensitive from dbo.RegistrationSettingAuditEvents order by AuditEventId", null,
        reader => new AuditState(reader.GetString(0), Text(reader, 1), Text(reader, 2), Text(reader, 3), Text(reader, 4), reader.GetBoolean(5)));
    private DateTime ReadPreviewExpiration(long previewLinkId) => QuerySingle("select ExpiresAtUtc from dbo.RegistrationSettingPreviewLinks where PreviewLinkId=@id",
        command => command.Parameters.AddWithValue("@id", previewLinkId), reader => reader.GetDateTime(0));
    private void AssertAuditCount(string eventType, int expected) => Assert.AreEqual(expected, ReadAudits().Count(row => row.EventType == eventType));
    private void AssertNoDraftChangesOrSuccessAudits()
    {
        Assert.AreEqual(0, CountActiveDrafts());
        Assert.AreEqual(0, Scalar<int>("select count(*) from dbo.RegistrationSettingDraftChanges"));
        Assert.AreEqual(0, Scalar<int>("select count(*) from dbo.RegistrationSettingAuditEvents where Succeeded=1 and EventType in ('DraftCreated','DraftEdited')"));
    }

    private static SettingMutation Upsert(SettingDefinition definition, string value) => new(definition.Key, DraftOperation.Upsert, value);
    private static AuditContext Audit() => new("integration", "integration-admin", 101, 101, 101, "form", "integration-test", "127.0.0.1");
    private SqlConnection Open() { var connection = new SqlConnection(databaseConnectionString!); connection.Open(); return connection; }
    private T Scalar<T>(string sql) { using var connection = Open(); using var command = Command(connection, sql); return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T)); }
    private T QuerySingle<T>(string sql, Action<SqlCommand>? parameters, Func<SqlDataReader, T> map) => Query(sql, parameters, map).Single();
    private List<T> Query<T>(string sql, Action<SqlCommand>? parameters, Func<SqlDataReader, T> map)
    {
        using var connection = Open(); using var command = Command(connection, sql); parameters?.Invoke(command);
        using var reader = command.ExecuteReader(); var rows = new List<T>(); while (reader.Read()) rows.Add(map(reader)); return rows;
    }
    private static string? Text(SqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static SqlCommand Command(SqlConnection connection, string sql) => new(sql, connection) { CommandTimeout = 15 };
    private static void Execute(SqlConnection connection, string sql, int timeout = 15, Action<SqlCommand>? parameters = null)
    {
        using var command = new SqlCommand(sql, connection) { CommandTimeout = timeout };
        parameters?.Invoke(command);
        command.ExecuteNonQuery();
    }
    private static string RepositoryRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    private static void DropDatabaseCore(string configured)
    {
        if (databaseName is null) return;
        var nameToDrop = databaseName;
        try
        {
            var builder = new SqlConnectionStringBuilder(configured) { InitialCatalog = "master", ConnectTimeout = 10 };
            using var connection = new SqlConnection(builder.ConnectionString);
            connection.Open();
            Execute(connection, $"""
                if db_id('{nameToDrop}') is not null
                begin
                    alter database [{nameToDrop}] set single_user with rollback immediate;
                    drop database [{nameToDrop}];
                end
                """, 30);
            databaseCreated = false;
            schemaReady = false;
            databaseConnectionString = null;
            databaseName = null;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"Could not drop temporary SQL integration database {nameToDrop}.", exception);
        }
    }

    private static void TryDropDatabase(string configured)
    {
        try
        {
            DropDatabaseCore(configured);
        }
        catch (Exception exception)
        {
            classContext?.WriteLine($"Best-effort cleanup failed for temporary SQL integration database {databaseName}: {exception.Message}");
        }
    }
    private sealed record DraftState(long BaselineVersion, DateTime ModifiedAtUtc, string ModifiedBy);
    private sealed record AuditState(string EventType, string? MetadataJson, string? FailureReason, string? PreviousValue, string? NewValue, bool IsSensitive);
}

[TestClass]
public sealed class DatabaseCleanupModeSelectorTests
{
    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void Select_NoConfiguredConnectionSkipsCleanup(string? configured) =>
        Assert.AreEqual(DatabaseCleanupMode.None, DatabaseCleanupModeSelector.Select(configured, "unavailable"));

    [TestMethod]
    public void Select_UnavailableInfrastructureUsesBestEffortCleanup() =>
        Assert.AreEqual(DatabaseCleanupMode.BestEffort,
            DatabaseCleanupModeSelector.Select("Server=test", "SQL Server unavailable"));

    [TestMethod]
    public void Select_SuccessfulFixtureUsesStrictCleanup() =>
        Assert.AreEqual(DatabaseCleanupMode.Strict,
            DatabaseCleanupModeSelector.Select("Server=test", null));
}
