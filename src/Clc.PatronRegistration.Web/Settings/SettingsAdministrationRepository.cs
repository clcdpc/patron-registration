using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Clc.PatronRegistration.Administration;
using Clc.PatronRegistration.Configuration;
using Clc.PatronRegistration.Helpers;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Clc.PatronRegistration.Web.Settings;

public sealed record AuditContext
{
    public AuditContext(string? actorId, string? actorName, int? actorOrganizationId, int targetOrganizationId,
        int targetLibraryId, string? formCode, string? correlationId, string? ipAddress)
    {
        ActorId = actorId;
        ActorName = actorName;
        ActorOrganizationId = actorOrganizationId;
        TargetOrganizationId = targetOrganizationId;
        TargetLibraryId = targetLibraryId;
        FormCode = FormCodeNormalizer.Normalize(formCode);
        CorrelationId = correlationId;
        IpAddress = ipAddress;
    }

    public string? ActorId { get; }
    public string? ActorName { get; }
    public int? ActorOrganizationId { get; }
    public int TargetOrganizationId { get; }
    public int TargetLibraryId { get; }
    public string FormCode { get; }
    public string? CorrelationId { get; }
    public string? IpAddress { get; }
}

public sealed record FormCodeMetadata(
    int OrganizationId,
    string FormCode,
    string DisplayName,
    string? Description,
    DateTime CreatedAtUtc,
    string CreatedBy,
    DateTime ModifiedAtUtc,
    string ModifiedBy);

public sealed record PreviewLinkRecord(
    long PreviewLinkId,
    long DraftId,
    byte[] TokenHash,
    bool AllowLiveSubmission,
    DateTime? RevokedAtUtc,
    DateTime? ExpiresAtUtc,
    int OrganizationId,
    string FormCode,
    string DraftStatus,
    int OperationalBranchId,
    long? LiveSettingsGeneration = null);

public sealed record PreviewContextSnapshot(
    PreviewLinkRecord Link,
    SettingDraft Draft,
    long? CacheGeneration = null);

public sealed record PreviewLinkActions(bool Replace, bool Revoke, bool Restore, bool Remove);

public static class PreviewLinkActionPolicy
{
    public static PreviewLinkActions For(PreviewLinkRecord link, DateTime nowUtc)
    {
        var revoked = link.RevokedAtUtc.HasValue;
        var expired = !link.ExpiresAtUtc.HasValue || link.ExpiresAtUtc.Value <= nowUtc;
        return new PreviewLinkActions(!revoked && !expired, !revoked && !expired, !revoked && expired, revoked || expired);
    }
}

public enum PreviewLockStep
{
    CandidateLookupOutsideTransaction,
    Draft,
    PreviewLink,
    DraftChanges,
    LiveSettingsGeneration
}

public static class PreviewLockOrder
{
    public static IReadOnlyList<PreviewLockStep> Required { get; } =
    [
        PreviewLockStep.CandidateLookupOutsideTransaction,
        PreviewLockStep.Draft,
        PreviewLockStep.PreviewLink,
        PreviewLockStep.DraftChanges,
        PreviewLockStep.LiveSettingsGeneration
    ];
}

public sealed record FormCodeImpact(int MetadataRows, int OverrideRows, int Drafts, int PreviewLinks);
public sealed record LegacyFormCodeRow(int OrganizationId, string FormCode);
public enum FormCodeDeletionKind { SystemDefinition, LibraryDefinition, LibraryCustomization }
public sealed record FormCodeDeletionTarget(int OwnerOrganizationId, string FormCode, FormCodeDeletionKind Kind, bool IsLegacy);
public sealed record FormCodeDeletionSnapshot(FormCodeDeletionTarget Target, IReadOnlyList<int> AffectedOrganizationIds, FormCodeImpact Impact, string Fingerprint);
public static class FormCodeDeletionFingerprint
{
    public static string Compute(FormCodeDeletionTarget target, IEnumerable<int> organizationIds,
        IEnumerable<string> metadata, IEnumerable<string> versions, IEnumerable<string> settings,
        IEnumerable<string> drafts, IEnumerable<string> previewLinks)
    {
        var canonical = string.Join("\n", new[]
        {
            $"owner|{target.OwnerOrganizationId}|{target.FormCode}|{target.Kind}|{target.IsLegacy}",
            $"organizations|{string.Join(',', organizationIds.OrderBy(id => id))}"
        }.Concat(metadata.OrderBy(value => value, StringComparer.Ordinal))
            .Concat(versions.OrderBy(value => value, StringComparer.Ordinal))
            .Concat(settings.OrderBy(value => value, StringComparer.Ordinal))
            .Concat(drafts.OrderBy(value => value, StringComparer.Ordinal))
            .Concat(previewLinks.OrderBy(value => value, StringComparer.Ordinal)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
public static class FormCodeDeletionOwnership
{
    public static FormCodeDeletionTarget? Classify(int ownerOrganizationId, string formCode, int systemOrganizationId,
        bool ownerMetadata, bool systemMetadata, bool ownedSettings)
    {
        if (!ownerMetadata && !ownedSettings)
        {
            return null;
        }

        var kind = ownerOrganizationId == systemOrganizationId
            ? FormCodeDeletionKind.SystemDefinition
            : systemMetadata ? FormCodeDeletionKind.LibraryCustomization : FormCodeDeletionKind.LibraryDefinition;
        return new FormCodeDeletionTarget(ownerOrganizationId, formCode, kind, !ownerMetadata);
    }
}
public enum FormCodeDeletionLockStep { Drafts, PreviewLinks, Metadata, ScopeVersions, Settings, LiveSettingsGeneration }
public static class FormCodeDeletionLockOrder
{
    public static IReadOnlyList<FormCodeDeletionLockStep> Required { get; } =
    [
        FormCodeDeletionLockStep.Drafts,
        FormCodeDeletionLockStep.PreviewLinks,
        FormCodeDeletionLockStep.Metadata,
        FormCodeDeletionLockStep.ScopeVersions,
        FormCodeDeletionLockStep.Settings,
        FormCodeDeletionLockStep.LiveSettingsGeneration
    ];
}

public sealed record SettingsAuditRow(
    long AuditEventId,
    DateTime TimestampUtc,
    string EventType,
    int TargetOrganizationId,
    int? TargetLibraryId,
    string FormCode,
    string? SettingKey,
    string? PreviousValue,
    string? NewValue,
    bool IsSensitive,
    bool Succeeded,
    string? ActorName,
    string? FailureReason,
    string? CorrelationId,
    string? IpAddress);

public static class SettingsAuditVisibility
{
    public static IEnumerable<SettingsAuditRow> ForAdministrator(
        IEnumerable<SettingsAuditRow> rows,
        bool includeSensitive) =>
        includeSensitive ? rows : rows.Where(row => !row.IsSensitive);
}

public sealed record SaveToDraftResult(long DraftId, bool DraftCreated)
{
    public long DraftRevision { get; init; }
}

public interface ILivePreviewSubmissionAdmission : IDisposable
{
}

internal sealed class SqlLivePreviewSubmissionAdmission : ILivePreviewSubmissionAdmission
{
    private SqlConnection? connection;
    private SqlTransaction? transaction;
    private int disposed;

    internal SqlLivePreviewSubmissionAdmission(SqlConnection connection, SqlTransaction transaction)
    {
        this.connection = connection;
        this.transaction = transaction;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        var transaction = this.transaction;
        this.transaction = null;
        try
        {
            // The admission transaction is read-only. Rolling it back releases
            // the generation lock without making lock-release failures part of
            // the already-completed registration result.
            try { transaction?.Rollback(); }
            catch { /* Closing the connection below still releases the lock. */ }
        }
        finally
        {
            transaction?.Dispose();
            var connection = this.connection;
            this.connection = null;
            connection?.Dispose();
        }
    }
}

public interface ISettingsAdministrationRepository
{
    long GetVersion(int organizationId, string formCode);
    long GetCacheGeneration();
    SettingDraft? GetDraft(long draftId);
    SettingDraft? GetActiveDraft(int organizationId, string formCode);
    SaveToDraftResult SaveToSharedDraft(int organizationId, string formCode, long expectedVersion, long? expectedDraftId,
        IReadOnlyList<SettingMutation> changes, IReadOnlyDictionary<string, SettingDefinition> catalog, AuditContext audit);
    SaveToDraftResult SaveToSharedDraft(int organizationId, string formCode, long expectedVersion, long? expectedDraftId,
        IReadOnlyList<SettingMutation> changes, IReadOnlyDictionary<string, SettingDefinition> catalog, AuditContext audit,
        long? expectedDraftRevision);
    void RemoveDraftChange(long draftId, string settingKey, IReadOnlyDictionary<string, SettingDefinition> catalog, bool canManageSensitive, AuditContext audit,
        long? expectedDraftRevision);
    void RemoveDraftChange(long draftId, string settingKey, IReadOnlyDictionary<string, SettingDefinition> catalog, bool canManageSensitive, AuditContext audit);
    void CommitDraft(long draftId, IReadOnlyDictionary<string, SettingDefinition> catalog, bool canManageSensitive, AuditContext audit,
        long? expectedDraftRevision);
    void DiscardDraft(long draftId, IReadOnlyDictionary<string, SettingDefinition> catalog, bool canManageSensitive, AuditContext audit,
        long? expectedDraftRevision);
    void DirectSave(int organizationId, string formCode, long expectedVersion, IReadOnlyList<SettingMutation> changes, IReadOnlyDictionary<string, SettingDefinition> catalog, AuditContext audit);
    long CreatePreviewLink(long draftId, byte[] tokenHash, bool allowLiveSubmission, int operationalBranchId,
        int lifetimeHours, IReadOnlyDictionary<string, SettingDefinition> catalog, bool canManageSensitive, AuditContext audit,
        long? expectedDraftRevision);
    PreviewContextSnapshot? ResolvePreviewContext(byte[] tokenHash);
    ILivePreviewSubmissionAdmission? TryAdmitLivePreviewSubmission(long previewLinkId, long expectedGeneration);
    bool IsLivePreviewCurrent(long previewLinkId, long expectedGeneration);
    PreviewLinkRecord? GetPreviewLink(long previewLinkId);
    IReadOnlyList<PreviewLinkRecord> GetPreviewLinks(long draftId);
    void RevokePreviewLink(long previewLinkId, IReadOnlyDictionary<string, SettingDefinition> catalog, bool canManageSensitive, AuditContext audit);
    void RestorePreviewLink(long previewLinkId, int lifetimeHours, IReadOnlyDictionary<string, SettingDefinition> catalog, bool canManageSensitive, AuditContext audit);
    void DeletePreviewLink(long previewLinkId, IReadOnlyDictionary<string, SettingDefinition> catalog, bool canManageSensitive, AuditContext audit);
    long? ReplacePreviewLinkMode(long previewLinkId, byte[] replacementTokenHash, bool allowLiveSubmission, IReadOnlyDictionary<string, SettingDefinition> catalog, bool canManageSensitive, AuditContext audit);
    IReadOnlyList<FormCodeMetadata> GetFormCodes(int libraryId, int systemOrganizationId);
    IReadOnlyList<FormCodeMetadata> GetFormCodesForLibraries(IReadOnlyCollection<int> libraryIds, int systemOrganizationId);
    IReadOnlyList<LegacyFormCodeRow> GetLegacyFormCodes();
    void SaveFormCode(FormCodeMetadata metadata, bool isCreate, AuditContext audit, DateTime? expectedModifiedAtUtc = null);
    FormCodeImpact GetFormCodeImpact(int ownerOrganizationId, string formCode, IReadOnlyCollection<int> affectedOrganizations);
    FormCodeDeletionSnapshot? GetFormCodeDeletionSnapshot(int ownerOrganizationId, string formCode, int systemOrganizationId, IReadOnlyCollection<int> knownOrganizations);
    void DeleteFormCode(FormCodeDeletionTarget expectedTarget, string expectedFingerprint, int systemOrganizationId, IReadOnlyCollection<int> knownOrganizations, AuditContext audit);
    IEnumerable<SettingsAuditRow> SearchAudit(int? libraryId, bool includeSensitive, string? term);
    void WriteAudit(string eventType, bool succeeded, AuditContext audit, string? failureReason = null, long? draftId = null, long? previewLinkId = null, string? metadataJson = null);
}

public sealed class SettingsAdministrationRepository : ISettingsAdministrationRepository, ISettingsCacheGenerationProvider
{
    private readonly string connectionString;
    private readonly TimeProvider timeProvider;

    // Test-only seam for deterministic SQL-backed interleaving tests. It is null
    // in production and does not change the locking protocol.
    internal static Action? BeforeLiveSettingsGenerationIncrementForTesting { get; set; }

    public SettingsAdministrationRepository(IDbHelperSettings settings)
        : this($"Server={settings.db_hostname};Database={settings.db_name};Trusted_Connection=True;Encrypt=False;", TimeProvider.System)
    {
    }

    internal SettingsAdministrationRepository(string connectionString, TimeProvider? timeProvider = null)
    {
        this.connectionString = connectionString;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    private SqlConnection Open()
    {
        var connection = new SqlConnection(connectionString);
        connection.Open();
        return connection;
    }

    public long GetVersion(int organizationId, string formCode)
    {
        formCode = FormCodeNormalizer.Normalize(formCode);
        using var connection = Open();
        return connection.QuerySingleOrDefault<long>(
            "select Version from dbo.RegistrationSettingScopeVersions where OrganizationId=@organizationId and FormCode=@formCode",
            new { organizationId, formCode });
    }

    public long GetCacheGeneration()
    {
        using var connection = Open();
        return connection.QuerySingle<long>("select Generation from dbo.RegistrationSettingsCacheGeneration where Id=1");
    }

    public SettingDraft? GetActiveDraft(int organizationId, string formCode)
    {
        formCode = FormCodeNormalizer.Normalize(formCode);
        using var connection = Open();
        var draftId = connection.QuerySingleOrDefault<long?>(
            "select DraftId from dbo.RegistrationSettingDrafts where OrganizationId=@organizationId and FormCode=@formCode and Status='Active'",
            new { organizationId, formCode });
        return draftId.HasValue ? ReadDraft(connection, draftId.Value) : null;
    }

    public SettingDraft? GetDraft(long draftId)
    {
        using var connection = Open();
        return ReadDraft(connection, draftId);
    }

    private static SettingDraft? ReadDraft(SqlConnection connection, long draftId, IDbTransaction? transaction = null)
    {
        var row = connection.QuerySingleOrDefault<DraftRow>(
            "select DraftId,OrganizationId,FormCode,BaselineVersion,Revision,Status from dbo.RegistrationSettingDrafts where DraftId=@draftId",
            new { draftId }, transaction);
        if (row is null)
        {
            return null;
        }

        var changes = connection.Query<DraftChangeRow>(
                "select SettingKey,Operation,Value from dbo.RegistrationSettingDraftChanges where DraftId=@draftId order by SettingKey",
                new { draftId }, transaction)
            .Select(change => new SettingMutation(change.SettingKey, Enum.Parse<DraftOperation>(change.Operation), change.Value))
            .ToList();
        return new SettingDraft(row.DraftId, row.OrganizationId, row.FormCode, row.BaselineVersion, Enum.Parse<DraftStatus>(row.Status), changes)
        {
            Revision = row.Revision
        };
    }

    public SaveToDraftResult SaveToSharedDraft(int organizationId, string formCode, long expectedVersion, long? expectedDraftId,
        IReadOnlyList<SettingMutation> changes, IReadOnlyDictionary<string, SettingDefinition> catalog, AuditContext audit) =>
        SaveToSharedDraft(organizationId, formCode, expectedVersion, expectedDraftId, changes, catalog, audit, null);

    public SaveToDraftResult SaveToSharedDraft(int organizationId, string formCode, long expectedVersion, long? expectedDraftId,
        IReadOnlyList<SettingMutation> changes, IReadOnlyDictionary<string, SettingDefinition> catalog, AuditContext audit,
        long? expectedDraftRevision)
    {
        changes = SafeHtmlPolicy.SanitizeMutations(changes, catalog);
        if (changes.Count == 0) throw new InvalidOperationException("Submit at least one setting change.");
        DraftOperationValidation.RequireSupported(changes);
        formCode = FormCodeNormalizer.Normalize(formCode);
        using var connection = Open();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        RegistrationFormAssetReferenceCoordinator.Acquire(connection, transaction, nameof(SaveToSharedDraft));
        EnsureImageAssetsExist(connection, transaction, changes, catalog);
        var activeDraft = connection.QuerySingleOrDefault<ActiveDraftRow>(
            "select DraftId,BaselineVersion,Revision from dbo.RegistrationSettingDrafts with(updlock,holdlock) where OrganizationId=@organizationId and FormCode=@formCode and Status='Active'",
            new { organizationId, formCode }, transaction);
        var existing = activeDraft?.DraftId;
        if (expectedDraftId.HasValue && existing != expectedDraftId)
        {
            throw new DBConcurrencyException("The expected shared draft is no longer active.");
        }
        if (existing.HasValue && expectedDraftId.HasValue && expectedDraftRevision.HasValue &&
            activeDraft!.Revision != expectedDraftRevision.Value)
        {
            throw new DBConcurrencyException("The shared draft changed after this page was loaded. Reload and review before saving.");
        }
        if (existing.HasValue && expectedDraftId.HasValue && !expectedDraftRevision.HasValue)
        {
            throw new DBConcurrencyException("The shared draft revision is required. Reload the settings page and retry.");
        }
        if (existing.HasValue && !expectedDraftId.HasValue)
        {
            throw new DBConcurrencyException("The shared draft identity and revision are required to edit an existing shared draft.");
        }
        var created = !existing.HasValue;
        var draftId = existing.GetValueOrDefault();
        if (created)
        {
            // Lock order: asset-reference gate, draft range, scope version, then draft changes.
            EnsureVersionRow(connection, transaction, organizationId, formCode);
            var version = ReadVersion(connection, transaction, organizationId, formCode);
            if (version != expectedVersion)
            {
                throw new DBConcurrencyException("Live settings changed after this page was loaded.");
            }
            draftId = connection.QuerySingle<long>(@"
insert dbo.RegistrationSettingDrafts(OrganizationId,FormCode,BaselineVersion,Revision,Status,CreatedBy,ModifiedBy)
output inserted.DraftId values(@organizationId,@formCode,@expectedVersion,0,'Active',@actor,@actor)",
                new { organizationId, formCode, expectedVersion, actor = audit.ActorName ?? "unknown" }, transaction);
            InsertAudit(connection, transaction, "DraftCreated", true, audit, draftId: draftId);
        }
        else if (activeDraft!.BaselineVersion != expectedVersion)
        {
            throw new DBConcurrencyException("Live settings changed after this page was loaded.");
        }
        // Lock order: the active draft is already locked, so invalidate all existing
        // preview links before touching draft-change rows. This makes every draft
        // mutation a new preview revision, including ordinary and remove operations.
        var revokedPreviewLinks = RevokeDraftPreviewLinks(connection, transaction, draftId, audit.ActorName);
        if (revokedPreviewLinks > 0)
        {
            InsertAudit(connection, transaction, "PreviewLinksRevokedForDraftChange", true, audit, draftId: draftId,
                metadataJson: $"{{\"count\":{revokedPreviewLinks}}}");
        }
        foreach (var change in changes)
        {
            if (!catalog.TryGetValue(change.Key, out var definition))
            {
                throw new InvalidOperationException("A submitted draft setting is not recognized.");
            }
            var validationError = change.Operation == DraftOperation.Upsert ? definition.Validate(change.Value) : null;
            if (validationError is not null)
            {
                throw new InvalidOperationException($"{definition.DisplayName}: {validationError}");
            }
            connection.Execute(@"
update dbo.RegistrationSettingDraftChanges
set Operation=@operation,Value=@value,ModifiedAtUtc=SYSUTCDATETIME(),ModifiedBy=@actor
where DraftId=@draftId and SettingKey=@key;
if @@ROWCOUNT=0
 insert dbo.RegistrationSettingDraftChanges(DraftId,SettingKey,Operation,Value,ModifiedBy)
 values(@draftId,@key,@operation,@value,@actor);",
                new
                {
                    draftId,
                    key = change.Key,
                    operation = change.Operation.ToString(),
                    value = change.Operation == DraftOperation.RemoveOverride ? null : change.Value,
                    actor = audit.ActorName ?? "unknown"
                }, transaction);
        }
        var previousRevision = created ? 0 : expectedDraftRevision!.Value;
        var newRevision = connection.QuerySingleOrDefault<long>(@"
update dbo.RegistrationSettingDrafts
set Revision=Revision+1,ModifiedAtUtc=SYSUTCDATETIME(),ModifiedBy=@actor
output inserted.Revision
where DraftId=@draftId and Status='Active' and Revision=@previousRevision",
            new { draftId, previousRevision, actor = audit.ActorName ?? "unknown" }, transaction);
        if (newRevision == 0)
        {
            throw new DBConcurrencyException("The shared draft changed while this edit was being saved. Reload and review before saving.");
        }
        InsertAudit(connection, transaction, "DraftEdited", true, audit, draftId: draftId, metadataJson: $"{{\"changeCount\":{changes.Count}}}");
        transaction.Commit();
        return new SaveToDraftResult(draftId, created) { DraftRevision = newRevision };
    }

    public void RemoveDraftChange(long draftId, string settingKey, IReadOnlyDictionary<string, SettingDefinition> catalog, bool canManageSensitive, AuditContext audit) =>
        RemoveDraftChange(draftId, settingKey, catalog, canManageSensitive, audit, null);

    public void RemoveDraftChange(long draftId, string settingKey, IReadOnlyDictionary<string, SettingDefinition> catalog, bool canManageSensitive, AuditContext audit,
        long? expectedDraftRevision)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        RegistrationFormAssetReferenceCoordinator.Acquire(connection, transaction, nameof(RemoveDraftChange));
        var currentRevision = EnsureActiveDraft(connection, transaction, draftId);
        if (!expectedDraftRevision.HasValue || currentRevision != expectedDraftRevision.Value)
        {
            throw new DBConcurrencyException("The shared draft changed after this page was loaded. Reload and review before removing the change.");
        }
        var definition = DraftChangeAuditClassification.RequireDefinition(settingKey, catalog);
        if (!canManageSensitive && definition.IsSensitive)
        {
            throw RestrictedDraftException();
        }
        var revokedPreviewLinks = RevokeDraftPreviewLinks(connection, transaction, draftId, audit.ActorName);
        if (revokedPreviewLinks > 0)
        {
            InsertAudit(connection, transaction, "PreviewLinksRevokedForDraftChange", true, audit, draftId: draftId,
                metadataJson: $"{{\"count\":{revokedPreviewLinks}}}");
        }
        var removed = connection.Execute(
            "delete dbo.RegistrationSettingDraftChanges where DraftId=@draftId and SettingKey=@settingKey",
            new { draftId, settingKey }, transaction);
        if (removed != 1)
        {
            throw new DBConcurrencyException("The staged draft mutation no longer exists.");
        }
        var newRevision = connection.QuerySingleOrDefault<long>(@"
update dbo.RegistrationSettingDrafts
set Revision=Revision+1,ModifiedAtUtc=SYSUTCDATETIME(),ModifiedBy=@actor
output inserted.Revision
where DraftId=@draftId and Status='Active' and Revision=@expectedDraftRevision",
            new { draftId, expectedDraftRevision, actor = audit.ActorName ?? "unknown" }, transaction);
        if (newRevision == 0)
        {
            throw new DBConcurrencyException("The shared draft changed while this edit was being removed. Reload and review before retrying.");
        }
        InsertAudit(connection, transaction, "DraftChangeRemoved", true, audit, draftId: draftId,
            settingKey: settingKey, isSensitive: definition.IsSensitive);
        transaction.Commit();
    }

    public void DirectSave(int organizationId, string formCode, long expectedVersion, IReadOnlyList<SettingMutation> changes, IReadOnlyDictionary<string, SettingDefinition> catalog, AuditContext audit)
    {
        changes = SafeHtmlPolicy.SanitizeMutations(changes, catalog);
        DraftOperationValidation.RequireSupported(changes);
        formCode = FormCodeNormalizer.Normalize(formCode);
        using var connection = Open();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        RegistrationFormAssetReferenceCoordinator.Acquire(connection, transaction, nameof(DirectSave));
        EnsureImageAssetsExist(connection, transaction, changes, catalog);
        EnsureVersionRow(connection, transaction, organizationId, formCode);
        var current = ReadVersion(connection, transaction, organizationId, formCode);
        if (current != expectedVersion)
        {
            InsertAudit(connection, transaction, "ConcurrencyConflict", false, audit, "Expected scope version was stale.");
            transaction.Commit();
            throw new DBConcurrencyException("Settings changed since this page was loaded. Reload and review the current values.");
        }

        ApplyChanges(connection, transaction, organizationId, formCode, changes, catalog, audit);
        IncrementVersions(connection, transaction, organizationId, formCode);
        InsertAudit(connection, transaction, "DirectSave", true, audit, metadataJson: $"{{\"changeCount\":{changes.Count}}}");
        transaction.Commit();
    }

    public void CommitDraft(long draftId, IReadOnlyDictionary<string, SettingDefinition> catalog, bool canManageSensitive, AuditContext audit,
        long? expectedDraftRevision)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        RegistrationFormAssetReferenceCoordinator.Acquire(connection, transaction, nameof(CommitDraft));
        var currentRevision = EnsureActiveDraft(connection, transaction, draftId);
        if (!expectedDraftRevision.HasValue || currentRevision != expectedDraftRevision.Value)
        {
            throw new DBConcurrencyException("The shared draft changed after this page was loaded. Reload and review before publishing.");
        }
        // Keep preview-link locks before draft changes, scope settings, and the
        // final generation lock so commit and live-preview admission share the
        // same draft -> link -> generation order.
        LockDraftPreviewLinks(connection, transaction, draftId);
        var draft = ReadDraft(connection, draftId, transaction) ??
            throw new DBConcurrencyException("The shared draft no longer exists. Reload the settings page.");
        DraftOperationValidation.RequireSupported(draft.Changes);
        EnsureCanManageRestrictedDraft(connection, transaction, draftId, catalog, canManageSensitive);
        EnsureImageAssetsExist(connection, transaction, draft.Changes, catalog);

        EnsureVersionRow(connection, transaction, draft.OrganizationId, draft.FormCode);
        if (ReadVersion(connection, transaction, draft.OrganizationId, draft.FormCode) != draft.BaselineVersion)
        {
            InsertAudit(connection, transaction, "DraftCommitConflict", false, audit, "Draft baseline version was stale.", draftId);
            transaction.Commit();
            throw new DBConcurrencyException("The live settings changed after this draft was created. Reload and review before creating a new draft.");
        }

        ApplyChanges(connection, transaction, draft.OrganizationId, draft.FormCode, draft.Changes, catalog, audit, draftId);
        IncrementVersions(connection, transaction, draft.OrganizationId, draft.FormCode);
        var transitioned = connection.Execute(@"
update dbo.RegistrationSettingDrafts
set Status='Committed',Revision=Revision+1,CommittedAtUtc=SYSUTCDATETIME(),CommittedBy=@actor,ModifiedAtUtc=SYSUTCDATETIME(),ModifiedBy=@actor
where DraftId=@draftId and Status='Active' and Revision=@expectedDraftRevision;",
            new { draftId, expectedDraftRevision, actor = audit.ActorName ?? "unknown" }, transaction);
        if (transitioned != 1)
        {
            throw new DBConcurrencyException("The shared draft changed while it was being published. Reload and review before retrying.");
        }
        connection.Execute(@"
update dbo.RegistrationSettingPreviewLinks set RevokedAtUtc=coalesce(RevokedAtUtc,SYSUTCDATETIME()),RevokedBy=coalesce(RevokedBy,@actor),ModifiedAtUtc=SYSUTCDATETIME(),ModifiedBy=@actor where DraftId=@draftId;",
            new { draftId, actor = audit.ActorName ?? "unknown" }, transaction);
        InsertAudit(connection, transaction, "DraftCommitted", true, audit, draftId: draftId);
        transaction.Commit();
    }

    public void DiscardDraft(long draftId, IReadOnlyDictionary<string, SettingDefinition> catalog, bool canManageSensitive, AuditContext audit,
        long? expectedDraftRevision)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        RegistrationFormAssetReferenceCoordinator.Acquire(connection, transaction, nameof(DiscardDraft));
        var currentRevision = EnsureActiveDraft(connection, transaction, draftId);
        if (!expectedDraftRevision.HasValue || currentRevision != expectedDraftRevision.Value)
        {
            throw new DBConcurrencyException("The shared draft changed after this page was loaded. Reload and review before discarding.");
        }
        EnsureCanManageRestrictedDraft(connection, transaction, draftId, catalog, canManageSensitive);
        var transitioned = connection.Execute(@"
update dbo.RegistrationSettingDrafts
set Status='Discarded',Revision=Revision+1,DiscardedAtUtc=SYSUTCDATETIME(),DiscardedBy=@actor,ModifiedAtUtc=SYSUTCDATETIME(),ModifiedBy=@actor
where DraftId=@draftId and Status='Active' and Revision=@expectedDraftRevision;",
            new { draftId, expectedDraftRevision, actor = audit.ActorName ?? "unknown" }, transaction);
        if (transitioned != 1)
        {
            throw new DBConcurrencyException("The shared draft changed while it was being discarded. Reload and review before retrying.");
        }
        connection.Execute(@"
update dbo.RegistrationSettingPreviewLinks set RevokedAtUtc=coalesce(RevokedAtUtc,SYSUTCDATETIME()),RevokedBy=coalesce(RevokedBy,@actor),ModifiedAtUtc=SYSUTCDATETIME(),ModifiedBy=@actor where DraftId=@draftId;",
            new { draftId, actor = audit.ActorName ?? "unknown" }, transaction);
        InsertAudit(connection, transaction, "DraftDiscarded", true, audit, draftId: draftId);
        transaction.Commit();
    }

    public long CreatePreviewLink(long draftId, byte[] tokenHash, bool allowLiveSubmission, int operationalBranchId,
        int lifetimeHours, IReadOnlyDictionary<string, SettingDefinition> catalog, bool canManageSensitive, AuditContext audit,
        long? expectedDraftRevision)
    {
        if (lifetimeHours is < 1 or > SettingsAdministrationOptions.MaximumPreviewLinkLifetimeHours)
            throw new ArgumentOutOfRangeException(nameof(lifetimeHours));
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var expiresAtUtc = nowUtc.AddHours(lifetimeHours);
        using var connection = Open();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var currentRevision = EnsureActiveDraft(connection, transaction, draftId);
        EnsureCanManageRestrictedDraft(connection, transaction, draftId, catalog, canManageSensitive);
        if (!expectedDraftRevision.HasValue)
        {
            throw new DBConcurrencyException("The shared draft revision is required. Reload the settings page and retry.");
        }
        if (currentRevision != expectedDraftRevision.Value)
        {
            throw new DBConcurrencyException("The shared draft changed after this page was loaded. Reload and review before creating a preview link.");
        }
        var liveSettingsGeneration = allowLiveSubmission
            ? connection.QuerySingle<long>(
                "select Generation from dbo.RegistrationSettingsCacheGeneration with(holdlock)",
                transaction: transaction)
            : (long?)null;
        var previewLinkId = connection.QuerySingle<long>(@"
insert dbo.RegistrationSettingPreviewLinks(DraftId,TokenHash,AllowLiveSubmission,OperationalBranchId,LiveSettingsGeneration,CreatedBy,ModifiedBy,ExpiresAtUtc)
output inserted.PreviewLinkId values(@draftId,@tokenHash,@allowLiveSubmission,@operationalBranchId,@liveSettingsGeneration,@actor,@actor,@expiresAtUtc)",
            new { draftId, tokenHash, allowLiveSubmission, operationalBranchId, liveSettingsGeneration, expiresAtUtc, actor = audit.ActorName ?? "unknown" }, transaction);
        InsertAudit(connection, transaction, "PreviewLinkCreated", true, audit, draftId: draftId, previewLinkId: previewLinkId);
        transaction.Commit();
        return previewLinkId;
    }

    public PreviewContextSnapshot? ResolvePreviewContext(byte[] tokenHash)
    {
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        using var connection = Open();
        // This lookup is deliberately outside the serializable transaction. It is only a
        // candidate; the locked re-read below is authoritative.
        var candidateDraftId = connection.QuerySingleOrDefault<long?>(
            "select DraftId from dbo.RegistrationSettingPreviewLinks where TokenHash=@tokenHash",
            new { tokenHash });
        if (!candidateDraftId.HasValue)
        {
            return null;
        }

        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);

        var status = connection.QuerySingleOrDefault<string>(
            "select Status from dbo.RegistrationSettingDrafts with(updlock,holdlock) where DraftId=@draftId",
            new { draftId = candidateDraftId.Value }, transaction);
        if (status != DraftStatus.Active.ToString())
        {
            transaction.Commit();
            return null;
        }

        var link = connection.QuerySingleOrDefault<PreviewLinkRecord>(@"
select p.PreviewLinkId,p.DraftId,p.TokenHash,p.AllowLiveSubmission,p.RevokedAtUtc,p.ExpiresAtUtc,
 d.OrganizationId,d.FormCode,d.Status DraftStatus,p.OperationalBranchId,p.LiveSettingsGeneration
from dbo.RegistrationSettingPreviewLinks p with(updlock,holdlock)
join dbo.RegistrationSettingDrafts d on d.DraftId=p.DraftId
where p.TokenHash=@tokenHash and p.DraftId=@draftId and p.RevokedAtUtc is null
 and p.ExpiresAtUtc>@nowUtc and d.Status='Active'",
            new { tokenHash, draftId = candidateDraftId.Value, nowUtc }, transaction);
        if (link is null)
        {
            transaction.Commit();
            return null;
        }

        var draft = ReadDraft(connection, candidateDraftId.Value, transaction);
        if (draft is null || draft.OrganizationId != link.OrganizationId ||
            !draft.FormCode.Equals(link.FormCode, StringComparison.OrdinalIgnoreCase))
        {
            transaction.Commit();
            return null;
        }

        // Read the generation for every preview, including safe previews. The
        // resolver must bind its live baseline to the same authoritative
        // generation before it captures the process-local cache snapshot.
        var currentGeneration = connection.QuerySingle<long>(
            "select Generation from dbo.RegistrationSettingsCacheGeneration with(holdlock)",
            transaction: transaction);
        if (link.AllowLiveSubmission && link.LiveSettingsGeneration != currentGeneration)
        {
            transaction.Commit();
            return null;
        }

        transaction.Commit();
        return new PreviewContextSnapshot(link, draft, currentGeneration);
    }

    /// <summary>
    /// Atomically admits a live-preview submission with the current live-settings
    /// generation. The returned lease deliberately keeps the serializable
    /// transaction open until the caller has entered and completed the real
    /// registration workflow. Live settings publication updates the same
    /// generation row as its final coordination lock, so publication and
    /// admission are serialized rather than separated by a check/use race.
    ///
    /// Lock order is candidate lookup, draft, preview link, then live-settings
    /// generation. The generation row is intentionally the final lock acquired by
    /// both this path and live publication paths.
    /// </summary>
    public ILivePreviewSubmissionAdmission? TryAdmitLivePreviewSubmission(long previewLinkId, long expectedGeneration)
    {
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var connection = Open();
        try
        {
            // This lookup is deliberately outside the serializable transaction. It
            // is only a candidate; the locked re-read below is authoritative.
            var candidateDraftId = connection.QuerySingleOrDefault<long?>(
                "select DraftId from dbo.RegistrationSettingPreviewLinks where PreviewLinkId=@previewLinkId",
                new { previewLinkId });
            if (!candidateDraftId.HasValue)
            {
                connection.Dispose();
                return null;
            }

            var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
            try
            {
                var status = connection.QuerySingleOrDefault<string>(
                    "select Status from dbo.RegistrationSettingDrafts with(updlock,holdlock) where DraftId=@draftId",
                    new { draftId = candidateDraftId.Value }, transaction);
                if (status != DraftStatus.Active.ToString())
                {
                    transaction.Rollback();
                    transaction.Dispose();
                    connection.Dispose();
                    return null;
                }

                var link = connection.QuerySingleOrDefault<PreviewLinkRecord>(@"
select p.PreviewLinkId,p.DraftId,p.TokenHash,p.AllowLiveSubmission,p.RevokedAtUtc,p.ExpiresAtUtc,
 d.OrganizationId,d.FormCode,d.Status DraftStatus,p.OperationalBranchId,p.LiveSettingsGeneration
from dbo.RegistrationSettingPreviewLinks p with(updlock,holdlock)
join dbo.RegistrationSettingDrafts d on d.DraftId=p.DraftId
where p.PreviewLinkId=@previewLinkId and p.DraftId=@draftId
  and p.AllowLiveSubmission=1
  and p.RevokedAtUtc is null
  and p.ExpiresAtUtc>@nowUtc
  and d.Status='Active'",
                    new { previewLinkId, draftId = candidateDraftId.Value, nowUtc }, transaction);
                if (link is null)
                {
                    transaction.Rollback();
                    transaction.Dispose();
                    connection.Dispose();
                    return null;
                }

                // A transaction-held shared lock is sufficient: publications
                // need an exclusive lock to increment this row, while unrelated
                // admitted previews at the same generation may coexist.
                var currentGeneration = connection.QuerySingle<long>(
                    "select Generation from dbo.RegistrationSettingsCacheGeneration with(holdlock)",
                    transaction: transaction);
                if (link.LiveSettingsGeneration != expectedGeneration || currentGeneration != expectedGeneration)
                {
                    transaction.Rollback();
                    transaction.Dispose();
                    connection.Dispose();
                    return null;
                }

                return new SqlLivePreviewSubmissionAdmission(connection, transaction);
            }
            catch
            {
                try { transaction.Rollback(); }
                catch { /* The connection disposal below still releases the lock. */ }
                transaction.Dispose();
                connection.Dispose();
                throw;
            }
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public bool IsLivePreviewCurrent(long previewLinkId, long expectedGeneration)
    {
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        using var connection = Open();
        return connection.QuerySingle<int>(@"
select case when exists(
    select 1
    from dbo.RegistrationSettingPreviewLinks p
    join dbo.RegistrationSettingDrafts d on d.DraftId=p.DraftId
    cross join dbo.RegistrationSettingsCacheGeneration g
    where p.PreviewLinkId=@previewLinkId
      and p.AllowLiveSubmission=1
      and p.LiveSettingsGeneration=@expectedGeneration
      and p.LiveSettingsGeneration=g.Generation
      and p.RevokedAtUtc is null
      and p.ExpiresAtUtc>@nowUtc
      and d.Status='Active'
) then 1 else 0 end", new { previewLinkId, expectedGeneration, nowUtc }) == 1;
    }

    public PreviewLinkRecord? GetPreviewLink(long previewLinkId)
    {
        using var connection = Open();
        return connection.QuerySingleOrDefault<PreviewLinkRecord>(@"
select p.PreviewLinkId,p.DraftId,p.TokenHash,p.AllowLiveSubmission,p.RevokedAtUtc,p.ExpiresAtUtc,d.OrganizationId,d.FormCode,d.Status DraftStatus,p.OperationalBranchId,p.LiveSettingsGeneration
from dbo.RegistrationSettingPreviewLinks p join dbo.RegistrationSettingDrafts d on d.DraftId=p.DraftId
where p.PreviewLinkId=@previewLinkId", new { previewLinkId });
    }

    public IReadOnlyList<PreviewLinkRecord> GetPreviewLinks(long draftId)
    {
        using var connection = Open();
        return connection.Query<PreviewLinkRecord>(@"
select p.PreviewLinkId,p.DraftId,p.TokenHash,p.AllowLiveSubmission,p.RevokedAtUtc,p.ExpiresAtUtc,d.OrganizationId,d.FormCode,d.Status DraftStatus,p.OperationalBranchId,p.LiveSettingsGeneration
from dbo.RegistrationSettingPreviewLinks p join dbo.RegistrationSettingDrafts d on d.DraftId=p.DraftId
where p.DraftId=@draftId order by p.PreviewLinkId desc", new { draftId }).ToList();
    }

    public void RevokePreviewLink(long previewLinkId, IReadOnlyDictionary<string, SettingDefinition> catalog, bool canManageSensitive, AuditContext audit)
    {
        using var connection = Open();
        var candidateDraftId = FindPreviewLinkDraftCandidate(connection, previewLinkId);
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var draftId = LockPreviewLinkDraft(connection, transaction, previewLinkId, candidateDraftId, nowUtc);
        EnsureCanManageRestrictedDraft(connection, transaction, draftId, catalog, canManageSensitive);
        var updated = connection.Execute(@"
update dbo.RegistrationSettingPreviewLinks
set RevokedAtUtc=coalesce(RevokedAtUtc,SYSUTCDATETIME()),RevokedBy=coalesce(RevokedBy,@actor),ModifiedAtUtc=SYSUTCDATETIME(),ModifiedBy=@actor
where PreviewLinkId=@previewLinkId and RevokedAtUtc is null
 and exists(select 1 from dbo.RegistrationSettingDrafts d where d.DraftId=dbo.RegistrationSettingPreviewLinks.DraftId and d.Status='Active')", new { previewLinkId, actor = audit.ActorName ?? "unknown" }, transaction);
        if (updated != 1)
        {
            InsertAudit(connection, transaction, "PreviewLinkRevocationFailed", false, audit, "The preview link was already revoked or invalidated.", previewLinkId: previewLinkId);
            transaction.Commit();
            throw new DBConcurrencyException("The preview link was already revoked or invalidated.");
        }
        InsertAudit(connection, transaction, "PreviewLinkRevoked", true, audit, previewLinkId: previewLinkId);
        transaction.Commit();
    }

    public void RestorePreviewLink(long previewLinkId, int lifetimeHours,
        IReadOnlyDictionary<string, SettingDefinition> catalog, bool canManageSensitive, AuditContext audit)
    {
        if (!SettingsAdministrationOptions.IsValidPreviewLinkLifetime(lifetimeHours))
            throw new ArgumentOutOfRangeException(nameof(lifetimeHours));

        using var connection = Open();
        var candidateDraftId = FindPreviewLinkDraftCandidate(connection, previewLinkId);
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var link = LockPreviewLink(connection, transaction, previewLinkId, candidateDraftId);
        EnsureCanManageRestrictedDraft(connection, transaction, candidateDraftId, catalog, canManageSensitive);
        if (link.RevokedAtUtc.HasValue || link.ExpiresAtUtc.HasValue && link.ExpiresAtUtc.Value > nowUtc)
            throw new DBConcurrencyException("The preview link is not eligible for restoration.");

        var expiresAtUtc = nowUtc.AddHours(lifetimeHours);
        var liveSettingsGeneration = link.AllowLiveSubmission
            ? connection.QuerySingle<long>(
                "select Generation from dbo.RegistrationSettingsCacheGeneration with(holdlock)",
                transaction: transaction)
            : (long?)null;
        var updated = connection.Execute(@"
update dbo.RegistrationSettingPreviewLinks
set ExpiresAtUtc=@expiresAtUtc,LiveSettingsGeneration=@liveSettingsGeneration,ModifiedAtUtc=@nowUtc,ModifiedBy=@actor
where PreviewLinkId=@previewLinkId and DraftId=@draftId and RevokedAtUtc is null
 and (ExpiresAtUtc is null or ExpiresAtUtc<=@nowUtc)",
            new { previewLinkId, draftId = candidateDraftId, expiresAtUtc, liveSettingsGeneration, nowUtc, actor = audit.ActorName ?? "unknown" }, transaction);
        if (updated != 1)
            throw new DBConcurrencyException("The preview link changed while it was being restored.");

        InsertAudit(connection, transaction, "PreviewLinkRestored", true, audit, draftId: candidateDraftId,
            previewLinkId: previewLinkId,
            metadataJson: $"{{\"previousExpiresAtUtc\":{(link.ExpiresAtUtc.HasValue ? $"\"{link.ExpiresAtUtc.Value:O}\"" : "null")},\"newExpiresAtUtc\":\"{expiresAtUtc:O}\"}}");
        transaction.Commit();
    }

    public void DeletePreviewLink(long previewLinkId, IReadOnlyDictionary<string, SettingDefinition> catalog,
        bool canManageSensitive, AuditContext audit)
    {
        using var connection = Open();
        var candidateDraftId = FindPreviewLinkDraftCandidate(connection, previewLinkId);
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var link = LockPreviewLink(connection, transaction, previewLinkId, candidateDraftId);
        EnsureCanManageRestrictedDraft(connection, transaction, candidateDraftId, catalog, canManageSensitive);
        var expired = !link.ExpiresAtUtc.HasValue || link.ExpiresAtUtc.Value <= nowUtc;
        if (!link.RevokedAtUtc.HasValue && !expired)
            throw new DBConcurrencyException("Active preview links must be revoked before removal.");

        InsertAudit(connection, transaction, "PreviewLinkDeleted", true, audit, draftId: candidateDraftId,
            previewLinkId: previewLinkId,
            metadataJson: $"{{\"expired\":{expired.ToString().ToLowerInvariant()},\"revoked\":{link.RevokedAtUtc.HasValue.ToString().ToLowerInvariant()}}}");
        var deleted = connection.Execute(@"
delete from dbo.RegistrationSettingPreviewLinks
where PreviewLinkId=@previewLinkId and DraftId=@draftId
 and (RevokedAtUtc is not null or ExpiresAtUtc is null or ExpiresAtUtc<=@nowUtc)",
            new { previewLinkId, draftId = candidateDraftId, nowUtc }, transaction);
        if (deleted != 1)
            throw new DBConcurrencyException("The preview link changed while it was being removed.");
        transaction.Commit();
    }

    public long? ReplacePreviewLinkMode(long previewLinkId, byte[] replacementTokenHash, bool allowLiveSubmission,
        IReadOnlyDictionary<string, SettingDefinition> catalog, bool canManageSensitive, AuditContext audit)
    {
        using var connection = Open();
        var candidateDraftId = FindPreviewLinkDraftCandidate(connection, previewLinkId);
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var draftId = LockPreviewLinkDraft(connection, transaction, previewLinkId, candidateDraftId, nowUtc);
        EnsureCanManageRestrictedDraft(connection, transaction, draftId, catalog, canManageSensitive);
        var current = connection.QuerySingle<PreviewLinkModeRow>(@"
select AllowLiveSubmission,OperationalBranchId,ExpiresAtUtc
from dbo.RegistrationSettingPreviewLinks with(updlock,holdlock)
where PreviewLinkId=@previewLinkId and DraftId=@draftId and RevokedAtUtc is null",
            new { previewLinkId, draftId }, transaction);
        if (current.AllowLiveSubmission == allowLiveSubmission)
        {
            transaction.Commit();
            return null;
        }

        var actor = audit.ActorName ?? "unknown";
        var revoked = connection.Execute(@"
update dbo.RegistrationSettingPreviewLinks
set RevokedAtUtc=SYSUTCDATETIME(),RevokedBy=@actor,ModifiedAtUtc=SYSUTCDATETIME(),ModifiedBy=@actor
where PreviewLinkId=@previewLinkId and DraftId=@draftId and RevokedAtUtc is null",
            new { previewLinkId, draftId, actor }, transaction);
        if (revoked != 1)
        {
            throw new DBConcurrencyException("The preview link was revoked or invalidated.");
        }
        var replacementId = connection.QuerySingle<long>(@"
insert dbo.RegistrationSettingPreviewLinks(
 DraftId,TokenHash,AllowLiveSubmission,OperationalBranchId,LiveSettingsGeneration,CreatedBy,ModifiedBy,ExpiresAtUtc)
output inserted.PreviewLinkId
values(@draftId,@replacementTokenHash,@allowLiveSubmission,@operationalBranchId,@liveSettingsGeneration,@actor,@actor,@expiresAtUtc)",
            new
            {
                draftId,
                replacementTokenHash,
                allowLiveSubmission,
                current.OperationalBranchId,
                liveSettingsGeneration = allowLiveSubmission
                    ? connection.QuerySingle<long>(
                        "select Generation from dbo.RegistrationSettingsCacheGeneration with(holdlock)",
                        transaction: transaction)
                    : (long?)null,
                actor,
                current.ExpiresAtUtc
            }, transaction);
        InsertAudit(connection, transaction, "PreviewLinkModeReplaced", true, audit, draftId: draftId,
            previewLinkId: replacementId,
            metadataJson: $"{{\"replacedPreviewLinkId\":{previewLinkId},\"liveSubmission\":{allowLiveSubmission.ToString().ToLowerInvariant()}}}");
        transaction.Commit();
        return replacementId;
    }

    public IReadOnlyList<FormCodeMetadata> GetFormCodes(int libraryId, int systemOrganizationId)
    {
        using var connection = Open();
        return connection.Query<FormCodeMetadata>(@"
select OrganizationId,FormCode,DisplayName,Description,CreatedAtUtc,CreatedBy,ModifiedAtUtc,ModifiedBy
from dbo.RegistrationFormCodeMetadata where OrganizationId in (@libraryId,@systemOrganizationId)
order by FormCode,case when OrganizationId=@libraryId then 0 else 1 end",
            new { libraryId, systemOrganizationId }).ToList();
    }

    public IReadOnlyList<FormCodeMetadata> GetFormCodesForLibraries(IReadOnlyCollection<int> libraryIds, int systemOrganizationId)
    {
        var targetLibraryIds = libraryIds.Distinct().ToList();
        if (targetLibraryIds.Count == 0)
        {
            return [];
        }

        using var connection = Open();
        return connection.Query<FormCodeMetadata>(@"
select OrganizationId,FormCode,DisplayName,Description,CreatedAtUtc,CreatedBy,ModifiedAtUtc,ModifiedBy
from dbo.RegistrationFormCodeMetadata
where OrganizationId=@systemOrganizationId or OrganizationId in @targetLibraryIds
order by FormCode,case when OrganizationId=@systemOrganizationId then 1 else 0 end,OrganizationId",
            new { targetLibraryIds, systemOrganizationId }).ToList();
    }

    public IReadOnlyList<LegacyFormCodeRow> GetLegacyFormCodes()
    {
        using var connection = Open();
        return connection.Query<LegacyFormCodeRow>(@"
select distinct OrganizationID OrganizationId,FormCode
from dbo.RegistrationFormSettings
where FormCode is not null and len(FormCode)>0
order by FormCode,OrganizationID").ToList();
    }

    public void SaveFormCode(FormCodeMetadata metadata, bool isCreate, AuditContext audit, DateTime? expectedModifiedAtUtc = null)
    {
        if (string.IsNullOrWhiteSpace(metadata.FormCode))
        {
            throw new ArgumentException("The default form code cannot have a metadata row.");
        }
        using var connection = Open();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        if (isCreate)
        {
            var exists = connection.QuerySingle<int>(
                "select count(*) from dbo.RegistrationFormCodeMetadata with(updlock,holdlock) where OrganizationId=@OrganizationId and FormCode=@FormCode",
                metadata, transaction) > 0;
            if (exists)
            {
                throw new InvalidOperationException("That form code already exists at the selected owning scope.");
            }
            connection.Execute(@"
insert dbo.RegistrationFormCodeMetadata(OrganizationId,FormCode,DisplayName,Description,CreatedBy,ModifiedBy)
values(@OrganizationId,@FormCode,@DisplayName,@Description,@CreatedBy,@ModifiedBy)", metadata, transaction);
        }
        else
        {
            if (!expectedModifiedAtUtc.HasValue)
            {
                InsertAudit(connection, transaction, "ConcurrencyConflict", false, audit,
                    failureReason: "Expected form-code metadata timestamp was not supplied.");
                transaction.Commit();
                throw new DBConcurrencyException("Form-code metadata changed while you were editing. Reload and review the current values.");
            }
            var updated = connection.Execute(@"
update dbo.RegistrationFormCodeMetadata set DisplayName=@DisplayName,Description=@Description,ModifiedAtUtc=SYSUTCDATETIME(),ModifiedBy=@ModifiedBy
where OrganizationId=@OrganizationId and FormCode=@FormCode and ModifiedAtUtc=@expectedModifiedAtUtc",
                new { metadata.OrganizationId, metadata.FormCode, metadata.DisplayName, metadata.Description, metadata.ModifiedBy, expectedModifiedAtUtc }, transaction);
            if (updated == 0)
            {
                InsertAudit(connection, transaction, "ConcurrencyConflict", false, audit,
                    failureReason: "Expected form-code metadata timestamp was stale or the row no longer exists.");
                transaction.Commit();
                throw new DBConcurrencyException("Form-code metadata changed while you were editing. Reload and review the current values.");
            }
        }
        EnsureVersionRow(connection, transaction, metadata.OrganizationId, metadata.FormCode);
        IncrementVersions(connection, transaction, metadata.OrganizationId, metadata.FormCode);
        InsertAudit(connection, transaction, isCreate ? "FormCodeCreated" : "FormCodeMetadataUpdated", true, audit);
        transaction.Commit();
    }

    public FormCodeImpact GetFormCodeImpact(int ownerOrganizationId, string formCode, IReadOnlyCollection<int> affectedOrganizations)
    {
        using var connection = Open();
        return connection.QuerySingle<FormCodeImpact>(@"
select
 (select count(*) from dbo.RegistrationFormCodeMetadata where OrganizationId in @affectedOrganizations and FormCode=@formCode) MetadataRows,
 (select count(*) from dbo.RegistrationFormSettings where OrganizationID in @affectedOrganizations and FormCode=@formCode) OverrideRows,
 (select count(*) from dbo.RegistrationSettingDrafts where OrganizationId in @affectedOrganizations and FormCode=@formCode) Drafts,
 (select count(*) from dbo.RegistrationSettingPreviewLinks p join dbo.RegistrationSettingDrafts d on d.DraftId=p.DraftId where d.OrganizationId in @affectedOrganizations and d.FormCode=@formCode) PreviewLinks",
            new { ownerOrganizationId, formCode, affectedOrganizations });
    }

    public FormCodeDeletionSnapshot? GetFormCodeDeletionSnapshot(int ownerOrganizationId, string formCode, int systemOrganizationId, IReadOnlyCollection<int> knownOrganizations)
    {
        using var connection = Open();
        var affected = DiscoverAffectedOrganizations(connection, ownerOrganizationId, formCode, systemOrganizationId, knownOrganizations);
        // Confirmation is intentionally read-only. Missing version rows are represented as
        // version zero; the POST takes authoritative locks and may create those rows.
        return BuildDeletionSnapshot(connection, null, ownerOrganizationId, formCode, systemOrganizationId, affected, false);
    }

    public void DeleteFormCode(FormCodeDeletionTarget expectedTarget, string expectedFingerprint, int systemOrganizationId, IReadOnlyCollection<int> knownOrganizations, AuditContext audit)
    {
        if (string.IsNullOrWhiteSpace(expectedTarget.FormCode))
        {
            throw new ArgumentException("The default form code cannot be deleted.");
        }
        using var connection = Open();
        var affectedOrganizations = DiscoverAffectedOrganizations(connection, expectedTarget.OwnerOrganizationId,
            expectedTarget.FormCode, systemOrganizationId, knownOrganizations);
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        RegistrationFormAssetReferenceCoordinator.Acquire(connection, transaction, nameof(DeleteFormCode));
        var lockedSnapshot = BuildDeletionSnapshot(connection, transaction, expectedTarget.OwnerOrganizationId,
            expectedTarget.FormCode, systemOrganizationId, affectedOrganizations, true);
        if (string.IsNullOrWhiteSpace(expectedFingerprint) || lockedSnapshot is null || lockedSnapshot.Target.Kind != expectedTarget.Kind ||
            lockedSnapshot.Target.IsLegacy != expectedTarget.IsLegacy ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(lockedSnapshot.Fingerprint), Encoding.ASCII.GetBytes(expectedFingerprint)))
        {
            throw new DBConcurrencyException("The form-code deletion contents changed. Review the deletion impact again.");
        }
        connection.Execute(@"
delete p from dbo.RegistrationSettingPreviewLinks p join dbo.RegistrationSettingDrafts d on d.DraftId=p.DraftId where d.OrganizationId in @affectedOrganizations and d.FormCode=@formCode;
delete from dbo.RegistrationSettingDrafts where OrganizationId in @affectedOrganizations and FormCode=@formCode;
delete from dbo.RegistrationFormSettings where OrganizationID in @affectedOrganizations and FormCode=@formCode;
delete from dbo.RegistrationFormCodeMetadata where OrganizationId in @affectedOrganizations and FormCode=@formCode;",
            new { ownerOrganizationId = expectedTarget.OwnerOrganizationId, formCode = expectedTarget.FormCode, affectedOrganizations }, transaction);
        foreach (var organizationId in AffectedVersionScopes(affectedOrganizations))
        {
            EnsureVersionRow(connection, transaction, organizationId, expectedTarget.FormCode);
            connection.Execute(
                "update dbo.RegistrationSettingScopeVersions set Version=Version+1,ModifiedAtUtc=SYSUTCDATETIME() where OrganizationId=@organizationId and FormCode=@formCode",
                new { organizationId, formCode = expectedTarget.FormCode }, transaction);
        }
        IncrementCacheGeneration(connection, transaction);
        InsertAudit(connection, transaction, "FormCodeDeleted", true, audit);
        transaction.Commit();
    }

    private static IReadOnlyList<int> DiscoverAffectedOrganizations(SqlConnection connection, int ownerOrganizationId,
        string formCode, int systemOrganizationId, IReadOnlyCollection<int> knownOrganizations)
    {
        var discovered = new HashSet<int>(knownOrganizations) { ownerOrganizationId };
        if (ownerOrganizationId == systemOrganizationId)
        {
            foreach (var id in connection.Query<int>(@"
select OrganizationId from dbo.RegistrationFormCodeMetadata where FormCode=@formCode
union select OrganizationID from dbo.RegistrationFormSettings where FormCode=@formCode
union select OrganizationId from dbo.RegistrationSettingDrafts where FormCode=@formCode", new { formCode }))
            {
                discovered.Add(id);
            }
        }
        else
        {
            foreach (var id in connection.Query<int>(@"
select distinct TargetOrganizationId from dbo.RegistrationSettingAuditEvents
where TargetLibraryId=@ownerOrganizationId and FormCode=@formCode
 and (exists(select 1 from dbo.RegistrationFormCodeMetadata m where m.OrganizationId=TargetOrganizationId and m.FormCode=@formCode)
   or exists(select 1 from dbo.RegistrationFormSettings s where s.OrganizationID=TargetOrganizationId and s.FormCode=@formCode)
   or exists(select 1 from dbo.RegistrationSettingDrafts d where d.OrganizationId=TargetOrganizationId and d.FormCode=@formCode))",
                new { ownerOrganizationId, formCode }))
            {
                discovered.Add(id);
            }
        }
        return discovered.OrderBy(id => id).ToList();
    }

    private static FormCodeDeletionSnapshot? BuildDeletionSnapshot(SqlConnection connection, IDbTransaction? transaction,
        int ownerOrganizationId, string formCode, int systemOrganizationId, IReadOnlyList<int> affectedOrganizations, bool lockRows)
    {
        var hint = lockRows ? " with(updlock,holdlock)" : string.Empty;
        var organizationFilter = ownerOrganizationId == systemOrganizationId
            ? "FormCode=@formCode"
            : "OrganizationId in @affectedOrganizations and FormCode=@formCode";
        var drafts = connection.Query<string>($@"
select concat('d|',DraftId,'|',OrganizationId,'|',Status,'|',BaselineVersion,'|',convert(varchar(33),ModifiedAtUtc,126))
from dbo.RegistrationSettingDrafts{hint} where {organizationFilter} order by DraftId",
            new { affectedOrganizations, formCode }, transaction).ToList();
        var links = connection.Query<string>($@"
select concat('p|',p.PreviewLinkId,'|',p.DraftId,'|',convert(int,p.AllowLiveSubmission),'|',
 coalesce(convert(varchar(33),p.RevokedAtUtc,126),''),'|',convert(varchar(33),p.ModifiedAtUtc,126),'|',p.OperationalBranchId)
from dbo.RegistrationSettingPreviewLinks p{hint} join dbo.RegistrationSettingDrafts d on d.DraftId=p.DraftId
where {(ownerOrganizationId == systemOrganizationId ? "d.FormCode=@formCode" : "d.OrganizationId in @affectedOrganizations and d.FormCode=@formCode")} order by p.PreviewLinkId",
            new { affectedOrganizations, formCode }, transaction).ToList();
        var metadataFilter = ownerOrganizationId == systemOrganizationId
            ? "FormCode=@formCode"
            : "(OrganizationId in @affectedOrganizations or OrganizationId=@systemOrganizationId) and FormCode=@formCode";
        var metadata = connection.Query<string>($@"
select concat('m|',OrganizationId,'|',convert(varchar(33),ModifiedAtUtc,126),'|',
 convert(varchar(64),hashbytes('SHA2_256',concat(DisplayName,'|',coalesce(Description,''))),2))
from dbo.RegistrationFormCodeMetadata{hint} where {metadataFilter} order by OrganizationId",
            new { affectedOrganizations, formCode, systemOrganizationId }, transaction).ToList();
        if (lockRows)
        {
            foreach (var organizationId in affectedOrganizations)
            {
                EnsureVersionRow(connection, transaction!, organizationId, formCode);
            }
        }
        var versions = connection.Query<string>($@"
select concat('v|',OrganizationId,'|',Version)
from dbo.RegistrationSettingScopeVersions{hint} where OrganizationId in @affectedOrganizations and FormCode=@formCode order by OrganizationId",
            new { affectedOrganizations, formCode }, transaction).ToList();
        foreach (var organizationId in affectedOrganizations.Where(id =>
                     !versions.Any(row => row.StartsWith($"v|{id}|", StringComparison.Ordinal))))
        {
            versions.Add($"v|{organizationId}|0");
        }
        versions.Sort(StringComparer.Ordinal);
        var settingsFilter = ownerOrganizationId == systemOrganizationId
            ? "FormCode=@formCode"
            : "OrganizationID in @affectedOrganizations and FormCode=@formCode";
        var settings = connection.Query<string>($@"
select concat('s|',OrganizationID,'|',Setting,'|',convert(varchar(64),hashbytes('SHA2_256',coalesce(Value,'')),2))
from dbo.RegistrationFormSettings{hint} where {settingsFilter} order by OrganizationID,Setting",
            new { affectedOrganizations, formCode }, transaction).ToList();
        var ownerMetadata = metadata.Any(row => row.StartsWith($"m|{ownerOrganizationId}|", StringComparison.Ordinal));
        var systemMetadata = ownerOrganizationId == systemOrganizationId ||
            metadata.Any(row => row.StartsWith($"m|{systemOrganizationId}|", StringComparison.Ordinal));
        var ownedOrganizationIds = ownerOrganizationId == systemOrganizationId
            ? new HashSet<int> { systemOrganizationId }
            : affectedOrganizations.ToHashSet();
        var hasOwnedSettings = settings.Any(row =>
        {
            var separator = row.IndexOf('|', 2);
            return separator > 2 && int.TryParse(row.AsSpan(2, separator - 2), out var id) && ownedOrganizationIds.Contains(id);
        });
        var target = FormCodeDeletionOwnership.Classify(ownerOrganizationId, formCode, systemOrganizationId,
            ownerMetadata, systemMetadata, hasOwnedSettings);
        if (target is null)
        {
            return null;
        }
        var fingerprint = FormCodeDeletionFingerprint.Compute(target, affectedOrganizations,
            metadata, versions, settings, drafts, links);
        var affectedMetadataCount = ownerOrganizationId == systemOrganizationId
            ? metadata.Count
            : metadata.Count(row => RowOrganizationId(row) is int id && affectedOrganizations.Contains(id));
        return new FormCodeDeletionSnapshot(target, affectedOrganizations,
            new FormCodeImpact(affectedMetadataCount, settings.Count, drafts.Count, links.Count), fingerprint);
    }

    private static int? RowOrganizationId(string row)
    {
        var separator = row.IndexOf('|', 2);
        return separator > 2 && int.TryParse(row.AsSpan(2, separator - 2), out var id) ? id : null;
    }

    public static IReadOnlyList<int> AffectedVersionScopes(IEnumerable<int> affectedOrganizations) =>
        affectedOrganizations.Distinct().ToList();

    public IEnumerable<SettingsAuditRow> SearchAudit(int? libraryId, bool includeSensitive, string? term)
    {
        using var connection = Open();
        var pattern = $"%{term ?? string.Empty}%";
        return connection.Query<SettingsAuditRow>(@"
select top(500) AuditEventId,TimestampUtc,EventType,TargetOrganizationId,TargetLibraryId,FormCode,SettingKey,
 PreviousValue,NewValue,IsSensitive,Succeeded,ActorName,FailureReason,CorrelationId,IpAddress
from dbo.RegistrationSettingAuditEvents
where (@libraryId is null or TargetLibraryId=@libraryId)
 and (@includeSensitive=1 or IsSensitive=0)
 and (EventType like @pattern or SettingKey like @pattern or ActorName like @pattern or FormCode like @pattern)
order by TimestampUtc desc", new { libraryId, includeSensitive, pattern }).ToList();
    }

    public void WriteAudit(string eventType, bool succeeded, AuditContext audit, string? failureReason = null, long? draftId = null, long? previewLinkId = null, string? metadataJson = null)
    {
        using var connection = Open();
        InsertAudit(connection, null, eventType, succeeded, audit, failureReason, draftId, previewLinkId, metadataJson);
    }

    private static void ApplyChanges(SqlConnection connection, IDbTransaction transaction, int organizationId, string formCode, IReadOnlyList<SettingMutation> changes, IReadOnlyDictionary<string, SettingDefinition> catalog, AuditContext audit, long? draftId = null)
    {
        // Validate the complete batch before the first query, write, audit, or version change.
        changes = SafeHtmlPolicy.SanitizeMutations(changes, catalog);
        DraftOperationValidation.RequireSupported(changes);
        foreach (var change in changes)
        {
            if (!catalog.TryGetValue(change.Key, out var definition))
            {
                throw new InvalidOperationException($"Unrecognized setting key: {change.Key}");
            }
            var validationError = change.Operation == DraftOperation.Upsert ? definition.Validate(change.Value) : null;
            if (validationError is not null)
            {
                throw new InvalidOperationException($"{definition.DisplayName}: {validationError}");
            }

            var old = connection.QuerySingleOrDefault<string>(
                "select Value from dbo.RegistrationFormSettings where OrganizationID=@organizationId and FormCode=@formCode and Setting=@key",
                new { organizationId, formCode, key = change.Key }, transaction);
            if (change.Operation == DraftOperation.RemoveOverride)
            {
                connection.Execute(
                    "delete dbo.RegistrationFormSettings where OrganizationID=@organizationId and FormCode=@formCode and Setting=@key",
                    new { organizationId, formCode, key = change.Key }, transaction);
            }
            else
            {
                connection.Execute(@"
update dbo.RegistrationFormSettings set Value=@value where OrganizationID=@organizationId and FormCode=@formCode and Setting=@key;
if @@ROWCOUNT=0 insert dbo.RegistrationFormSettings(OrganizationID,Setting,FormCode,Value) values(@organizationId,@key,@formCode,@value);",
                    new { organizationId, formCode, key = change.Key, value = change.Value }, transaction);
            }

            InsertAudit(connection, transaction,
                change.Operation == DraftOperation.RemoveOverride ? "OverrideRemoved" : old is null ? "OverrideCreated" : "OverrideUpdated",
                true, audit, draftId: draftId, settingKey: change.Key,
                previousValue: AuditValueFormatter.Format(old, definition.IsSensitive),
                newValue: AuditValueFormatter.Format(change.Value, definition.IsSensitive),
                isSensitive: definition.IsSensitive);
        }
    }

    private static void EnsureImageAssetsExist(SqlConnection connection, IDbTransaction transaction,
        IReadOnlyList<SettingMutation> changes, IReadOnlyDictionary<string, SettingDefinition> catalog)
    {
        // The caller has acquired the asset-reference gate before any other
        // repository locks. Cleanup therefore cannot delete a row between this
        // existence check and the setting/draft write in the same transaction.
        foreach (var change in changes)
        {
            if (change.Operation != DraftOperation.Upsert ||
                !catalog.TryGetValue(change.Key, out var definition) ||
                definition.ValueType != SettingValueType.Image)
            {
                continue;
            }

            var validationError = definition.Validate(change.Value);
            if (validationError is not null)
            {
                throw new InvalidOperationException($"{definition.DisplayName}: {validationError}");
            }

            if (!int.TryParse(change.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var assetId) || assetId <= 0)
            {
                throw new InvalidOperationException($"{definition.DisplayName}: Choose a valid uploaded image.");
            }

            var existingAssetId = connection.QuerySingleOrDefault<int?>(
                "select AssetId from dbo.RegistrationFormAssets with (updlock,holdlock) where AssetId=@assetId",
                new { assetId }, transaction);
            if (existingAssetId != assetId)
            {
                throw new InvalidOperationException(
                    "The referenced registration-form image no longer exists. Upload the image again and retry the save.");
            }
        }
    }

    private static long EnsureActiveDraft(SqlConnection connection, IDbTransaction transaction, long draftId)
    {
        var row = connection.QuerySingleOrDefault<ActiveDraftStateRow>(
            "select Status,Revision from dbo.RegistrationSettingDrafts with(updlock,holdlock) where DraftId=@draftId",
            new { draftId }, transaction);
        if (row is null || row.Status != "Active")
        {
            throw new DBConcurrencyException("The shared draft is no longer active. Reload the settings page.");
        }

        return row.Revision;
    }

    private static bool DraftContainsSensitiveChanges(
        SqlConnection connection,
        IDbTransaction transaction,
        long draftId,
        IReadOnlyDictionary<string, SettingDefinition> catalog)
    {
        var keys = connection.Query<string>(
            "select SettingKey from dbo.RegistrationSettingDraftChanges with(updlock,holdlock) where DraftId=@draftId",
            new { draftId }, transaction);
        return SensitiveDraftPolicy.ContainsSensitiveChange(keys, catalog);
    }

    private static void EnsureCanManageRestrictedDraft(
        SqlConnection connection,
        IDbTransaction transaction,
        long draftId,
        IReadOnlyDictionary<string, SettingDefinition> catalog,
        bool canManageSensitive)
    {
        if (!canManageSensitive && DraftContainsSensitiveChanges(connection, transaction, draftId, catalog))
        {
            throw RestrictedDraftException();
        }
    }

    private static UnauthorizedAccessException RestrictedDraftException() =>
        new("This draft contains restricted changes that require a global administrator.");

    private static long FindPreviewLinkDraftCandidate(SqlConnection connection, long previewLinkId)
    {
        var draftId = connection.QuerySingleOrDefault<long?>(
            "select DraftId from dbo.RegistrationSettingPreviewLinks where PreviewLinkId=@previewLinkId",
            new { previewLinkId });
        if (!draftId.HasValue)
        {
            throw new DBConcurrencyException("The preview link was already revoked or invalidated.");
        }

        return draftId.Value;
    }

    private static long LockPreviewLinkDraft(
        SqlConnection connection,
        IDbTransaction transaction,
        long previewLinkId,
        long candidateDraftId,
        DateTime nowUtc)
    {
        // The candidate was read before this transaction and is untrusted. The draft lock is
        // always acquired before the authoritative preview-link lock.
        EnsureActiveDraft(connection, transaction, candidateDraftId);
        var activeLink = connection.QuerySingleOrDefault<long?>(@"
select p.PreviewLinkId
from dbo.RegistrationSettingPreviewLinks p with(updlock,holdlock)
join dbo.RegistrationSettingDrafts d on d.DraftId=p.DraftId
where p.PreviewLinkId=@previewLinkId and p.DraftId=@candidateDraftId and p.RevokedAtUtc is null
 and p.ExpiresAtUtc>@nowUtc and d.Status='Active'",
            new { previewLinkId, candidateDraftId, nowUtc }, transaction);
        if (!activeLink.HasValue)
        {
            throw new DBConcurrencyException("The preview link was already revoked or invalidated.");
        }

        return candidateDraftId;
    }

    private static InactivePreviewLinkRow LockPreviewLink(SqlConnection connection, IDbTransaction transaction,
        long previewLinkId, long candidateDraftId)
    {
        EnsureActiveDraft(connection, transaction, candidateDraftId);
        var link = connection.QuerySingleOrDefault<InactivePreviewLinkRow>(@"
select p.AllowLiveSubmission,p.RevokedAtUtc,p.ExpiresAtUtc
from dbo.RegistrationSettingPreviewLinks p with(updlock,holdlock)
where p.PreviewLinkId=@previewLinkId and p.DraftId=@candidateDraftId",
            new { previewLinkId, candidateDraftId }, transaction);
        return link ?? throw new DBConcurrencyException("The preview link no longer exists.");
    }

    private static int RevokeDraftPreviewLinks(
        SqlConnection connection,
        IDbTransaction transaction,
        long draftId,
        string? actorName)
    {
        return connection.Execute(@"
update dbo.RegistrationSettingPreviewLinks
set RevokedAtUtc=SYSUTCDATETIME(),RevokedBy=@actor,ModifiedAtUtc=SYSUTCDATETIME(),ModifiedBy=@actor
where DraftId=@draftId and RevokedAtUtc is null",
            new { draftId, actor = actorName ?? "unknown" }, transaction);
    }

    private static void LockDraftPreviewLinks(SqlConnection connection, IDbTransaction transaction, long draftId)
    {
        connection.Query<long>(
            "select PreviewLinkId from dbo.RegistrationSettingPreviewLinks with(updlock,holdlock) where DraftId=@draftId",
            new { draftId }, transaction).ToList();
    }

    private static void EnsureVersionRow(SqlConnection connection, IDbTransaction transaction, int organizationId, string formCode)
    {
        connection.Execute(@"
if not exists(select 1 from dbo.RegistrationSettingScopeVersions with(updlock,holdlock) where OrganizationId=@organizationId and FormCode=@formCode)
 insert dbo.RegistrationSettingScopeVersions(OrganizationId,FormCode,Version,ModifiedAtUtc) values(@organizationId,@formCode,0,SYSUTCDATETIME());",
            new { organizationId, formCode }, transaction);
    }

    private static long ReadVersion(SqlConnection connection, IDbTransaction transaction, int organizationId, string formCode) =>
        connection.QuerySingle<long>(
            "select Version from dbo.RegistrationSettingScopeVersions with(updlock,holdlock) where OrganizationId=@organizationId and FormCode=@formCode",
            new { organizationId, formCode }, transaction);

    private static void IncrementVersions(SqlConnection connection, IDbTransaction transaction, int organizationId, string formCode)
    {
        connection.Execute(
            "update dbo.RegistrationSettingScopeVersions set Version=Version+1,ModifiedAtUtc=SYSUTCDATETIME() where OrganizationId=@organizationId and FormCode=@formCode",
            new { organizationId, formCode }, transaction);
        IncrementCacheGeneration(connection, transaction);
    }

    private static void IncrementCacheGeneration(SqlConnection connection, IDbTransaction transaction)
    {
        BeforeLiveSettingsGenerationIncrementForTesting?.Invoke();
        connection.Execute(
            "update dbo.RegistrationSettingsCacheGeneration set Generation=Generation+1,ModifiedAtUtc=SYSUTCDATETIME() where Id=1",
            transaction: transaction);
    }

    private static void InsertAudit(SqlConnection connection, IDbTransaction? transaction, string eventType, bool succeeded, AuditContext audit,
        string? failureReason = null, long? draftId = null, long? previewLinkId = null, string? metadataJson = null,
        string? settingKey = null, string? previousValue = null, string? newValue = null, bool isSensitive = false)
    {
        connection.Execute(@"
insert dbo.RegistrationSettingAuditEvents(
 TimestampUtc,EventType,ActorId,ActorName,ActorOrganizationId,TargetOrganizationId,TargetLibraryId,FormCode,
 SettingKey,PreviousValue,NewValue,IsSensitive,DraftId,PreviewLinkId,CorrelationId,IpAddress,Succeeded,FailureReason,MetadataJson)
values(
 SYSUTCDATETIME(),@eventType,@ActorId,@ActorName,@ActorOrganizationId,@TargetOrganizationId,@TargetLibraryId,@FormCode,
 @settingKey,@previousValue,@newValue,@isSensitive,@draftId,@previewLinkId,@CorrelationId,@IpAddress,@succeeded,@failureReason,@metadataJson);",
            new
            {
                eventType,
                audit.ActorId,
                audit.ActorName,
                audit.ActorOrganizationId,
                audit.TargetOrganizationId,
                audit.TargetLibraryId,
                FormCode = FormCodeNormalizer.Normalize(audit.FormCode),
                settingKey,
                previousValue,
                newValue,
                isSensitive,
                draftId,
                previewLinkId,
                audit.CorrelationId,
                audit.IpAddress,
                succeeded,
                failureReason,
                metadataJson
            }, transaction);
    }

    private sealed record DraftRow(long DraftId, int OrganizationId, string FormCode, long BaselineVersion, long Revision, string Status);
    private sealed record DraftChangeRow(string SettingKey, string Operation, string? Value);
    private sealed record PreviewLinkModeRow(bool AllowLiveSubmission, int OperationalBranchId, DateTime? ExpiresAtUtc);
    private sealed record InactivePreviewLinkRow(bool AllowLiveSubmission, DateTime? RevokedAtUtc, DateTime? ExpiresAtUtc);
    private sealed record ActiveDraftRow(long DraftId, long BaselineVersion, long Revision);
    private sealed record ActiveDraftStateRow(string Status, long Revision);
}
