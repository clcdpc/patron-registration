using System.Data;
using Clc.PatronRegistration.Administration;
using Clc.PatronRegistration.Web.Settings;
using Microsoft.Data.SqlClient;

#nullable enable

namespace Clc.PatronRegistration.Tests;

[TestClass]
[DoNotParallelize]
public sealed class SettingsAdministrationRepositoryIntegrationTests
{
    private const string ConnectionVariable = "PATRON_REGISTRATION_TEST_SQL_CONNECTION_STRING";
    private static string? databaseName;
    private static string? databaseConnectionString;
    private static string? unavailableReason;
    private SettingsAdministrationRepository repository = null!;

    private static readonly SettingDefinition First = new("test.first", "First", "Test value", SettingValueType.ShortString);
    private static readonly SettingDefinition Second = new("test.second", "Second", "Test value", SettingValueType.ShortString);
    private static readonly SettingDefinition Secret = new("test.secret", "Secret", "Test secret", SettingValueType.ShortString, IsSensitive: true);
    private static readonly IReadOnlyDictionary<string, SettingDefinition> Catalog =
        new[] { First, Second, Secret }.ToDictionary(item => item.Key, StringComparer.OrdinalIgnoreCase);

    [ClassInitialize]
    public static void CreateDatabase(TestContext _)
    {
        var configured = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            unavailableReason = $"SQL-backed repository tests require {ConnectionVariable}. The connection must permit CREATE DATABASE.";
            return;
        }

        try
        {
            var adminBuilder = new SqlConnectionStringBuilder(configured) { InitialCatalog = "master", ConnectTimeout = 10 };
            databaseName = $"PatronRegistrationTests_{Guid.NewGuid():N}";
            using (var connection = new SqlConnection(adminBuilder.ConnectionString))
            {
                connection.Open();
                Execute(connection, $"CREATE DATABASE [{databaseName}]");
            }

            var databaseBuilder = new SqlConnectionStringBuilder(configured)
            {
                InitialCatalog = databaseName,
                ConnectTimeout = 10
            };
            databaseConnectionString = databaseBuilder.ConnectionString;
            using var database = new SqlConnection(databaseConnectionString);
            database.Open();
            foreach (var file in new[] { "001-settings-administration.sql", "002-preview-operational-branch.sql", "003-expand-audit-setting-values.sql" })
            {
                Execute(database, File.ReadAllText(Path.Combine(RepositoryRoot(), "database", file)), 30);
            }
        }
        catch (Exception exception)
        {
            unavailableReason = $"SQL-backed repository fixture could not start using {ConnectionVariable}: {exception.Message}";
            DropDatabase(configured);
        }
    }

    [ClassCleanup]
    public static void DeleteDatabase()
    {
        var configured = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (!string.IsNullOrWhiteSpace(configured)) DropDatabase(configured);
    }

    [TestInitialize]
    public void ResetDatabase()
    {
        if (unavailableReason is not null) Assert.Inconclusive(unavailableReason);
        repository = new SettingsAdministrationRepository(databaseConnectionString!);
        using var connection = Open();
        Execute(connection, @"delete dbo.RegistrationSettingAuditEvents;
delete dbo.RegistrationSettingPreviewLinks;
delete dbo.RegistrationSettingDraftChanges;
delete dbo.RegistrationSettingDrafts;
delete dbo.RegistrationSettingScopeVersions;");
    }

    [TestMethod]
    public void SaveToSharedDraft_NoActiveDraft_CreatesDraftAndPersistsAllChangesAtomically()
    {
        SeedVersion(17);
        var result = repository.SaveToSharedDraft(101, "form", null,
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
        Assert.ThrowsException<InvalidOperationException>(() => repository.SaveToSharedDraft(101, "form", null,
            [Upsert(First, "written-before-failure"), missing], Catalog, Audit()));

        AssertNoDraftChangesOrSuccessAudits();
        Assert.AreEqual(9L, ReadScopeVersion());
        var retry = repository.SaveToSharedDraft(101, "form", null, [Upsert(First, "retry")], Catalog, Audit());
        Assert.IsTrue(retry.DraftCreated);
        Assert.AreEqual(1, CountActiveDrafts());
    }

    [TestMethod]
    public void SaveToSharedDraft_NullExpectedDraftId_ReusesExistingActiveDraft()
    {
        SeedVersion(3);
        var first = repository.SaveToSharedDraft(101, "form", null, [Upsert(First, "existing")], Catalog, Audit());
        var beforeEdited = ReadAudits().Count(row => row.EventType == "DraftEdited");

        var result = repository.SaveToSharedDraft(101, "form", null, [Upsert(Second, "new")], Catalog, Audit());

        Assert.AreEqual(first.DraftId, result.DraftId);
        Assert.IsFalse(result.DraftCreated);
        Assert.AreEqual(1, CountActiveDrafts());
        CollectionAssert.AreEquivalent(new[] { "test.first|Upsert|existing", "test.second|Upsert|new" }, ReadChanges(result.DraftId).ToArray());
        AssertAuditCount("DraftCreated", 1);
        Assert.AreEqual(beforeEdited + 1, ReadAudits().Count(row => row.EventType == "DraftEdited"));
    }

    [TestMethod]
    public void SaveToSharedDraft_ExistingDraft_UpdatesSubmittedKeyAndPreservesOtherChanges()
    {
        SeedVersion(1);
        var first = repository.SaveToSharedDraft(101, "form", null,
            [Upsert(First, "old"), Upsert(Second, "untouched")], Catalog, Audit());

        var result = repository.SaveToSharedDraft(101, "form", first.DraftId,
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
        var existing = repository.SaveToSharedDraft(101, "form", null, [Upsert(First, "existing")], Catalog, Audit());
        var auditCount = ReadAudits().Count;

        Assert.ThrowsException<DBConcurrencyException>(() => repository.SaveToSharedDraft(101, "form", existing.DraftId + 100,
            [Upsert(Second, "must-not-write")], Catalog, Audit()));

        Assert.AreEqual(1, CountActiveDrafts());
        CollectionAssert.AreEqual(new[] { "test.first|Upsert|existing" }, ReadChanges(existing.DraftId).ToArray());
        Assert.AreEqual(auditCount, ReadAudits().Count);
    }

    [TestMethod]
    public void SaveToSharedDraft_ExpectedDraftIdButNoActiveDraft_ThrowsAndDoesNotCreateReplacement()
    {
        SeedVersion(1);
        Assert.ThrowsException<DBConcurrencyException>(() => repository.SaveToSharedDraft(101, "form", 42,
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
            try { return (new SettingsAdministrationRepository(databaseConnectionString!).SaveToSharedDraft(101, "form", null, [mutation], Catalog, Audit()), null); }
            catch (Exception exception) { return ((SaveToDraftResult?)null, exception); }
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
        var result = repository.SaveToSharedDraft(101, "form", null, [Upsert(Secret, secret)], Catalog, Audit());

        StringAssert.Contains(ReadChanges(result.DraftId).Single(), secret);
        var audits = ReadAudits();
        Assert.IsFalse(audits.Any(row => (row.MetadataJson ?? "").Contains(secret, StringComparison.Ordinal) ||
            (row.FailureReason ?? "").Contains(secret, StringComparison.Ordinal) ||
            (row.PreviousValue ?? "").Contains(secret, StringComparison.Ordinal) ||
            (row.NewValue ?? "").Contains(secret, StringComparison.Ordinal)));
        // DraftCreated and DraftEdited are draft-level events and deliberately do not classify an individual mutation.
        Assert.IsTrue(audits.All(row => !row.IsSensitive));
    }

    private void SeedVersion(long version) => Execute(Open(),
        "insert dbo.RegistrationSettingScopeVersions(OrganizationId,FormCode,Version) values(101,'form',@version)",
        parameters: command => command.Parameters.AddWithValue("@version", version));
    private long ReadScopeVersion() => Scalar<long>("select Version from dbo.RegistrationSettingScopeVersions where OrganizationId=101 and FormCode='form'");
    private int CountActiveDrafts() => Scalar<int>("select count(*) from dbo.RegistrationSettingDrafts where OrganizationId=101 and FormCode='form' and Status='Active'");
    private DraftState ReadDraft(long draftId) => QuerySingle("select BaselineVersion,ModifiedAtUtc,ModifiedBy from dbo.RegistrationSettingDrafts where DraftId=@id",
        command => command.Parameters.AddWithValue("@id", draftId), reader => new DraftState(reader.GetInt64(0), reader.GetDateTime(1), reader.GetString(2)));
    private List<string> ReadChanges(long draftId) => Query("select SettingKey,Operation,Value from dbo.RegistrationSettingDraftChanges where DraftId=@id order by SettingKey",
        command => command.Parameters.AddWithValue("@id", draftId), reader => $"{reader.GetString(0)}|{reader.GetString(1)}|{(reader.IsDBNull(2) ? "" : reader.GetString(2))}");
    private List<AuditState> ReadAudits() => Query("select EventType,MetadataJson,FailureReason,PreviousValue,NewValue,IsSensitive from dbo.RegistrationSettingAuditEvents order by AuditEventId", null,
        reader => new AuditState(reader.GetString(0), Text(reader, 1), Text(reader, 2), Text(reader, 3), Text(reader, 4), reader.GetBoolean(5)));
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
        using (connection)
        {
            using var command = new SqlCommand(sql, connection) { CommandTimeout = timeout };
            parameters?.Invoke(command);
            command.ExecuteNonQuery();
        }
    }
    private static string RepositoryRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    private static void DropDatabase(string configured)
    {
        if (databaseName is null) return;
        try { var builder = new SqlConnectionStringBuilder(configured) { InitialCatalog = "master", ConnectTimeout = 10 }; using var connection = new SqlConnection(builder.ConnectionString); connection.Open(); Execute(connection, $"if db_id('{databaseName}') is not null begin alter database [{databaseName}] set single_user with rollback immediate; drop database [{databaseName}]; end", 30); }
        catch { /* Class cleanup must not hide test results. */ }
    }
    private sealed record DraftState(long BaselineVersion, DateTime ModifiedAtUtc, string ModifiedBy);
    private sealed record AuditState(string EventType, string? MetadataJson, string? FailureReason, string? PreviousValue, string? NewValue, bool IsSensitive);
}
