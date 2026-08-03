using System.Data;
using System.Security.Cryptography;
using System.Text;
using Clc.PatronRegistration.Administration;
using Clc.PatronRegistration.Configuration;
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
    int OperationalBranchId);

public sealed record PreviewContextSnapshot(PreviewLinkRecord Link, SettingDraft Draft);

public enum PreviewLockStep
{
    CandidateLookupOutsideTransaction,
    Draft,
    PreviewLink,
    DraftChanges
}

public static class PreviewLockOrder
{
    public static IReadOnlyList<PreviewLockStep> Required { get; } =
    [
        PreviewLockStep.CandidateLookupOutsideTransaction,
        PreviewLockStep.Draft,
        PreviewLockStep.PreviewLink,
        PreviewLockStep.DraftChanges
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
public enum FormCodeDeletionLockStep { Drafts, PreviewLinks, Metadata, ScopeVersions, Settings }
public static class FormCodeDeletionLockOrder
{
    public static IReadOnlyList<FormCodeDeletionLockStep> Required { get; } =
    [
        FormCodeDeletionLockStep.Drafts,
        FormCodeDeletionLockStep.PreviewLinks,
        FormCodeDeletionLockStep.Metadata,
        FormCodeDeletionLockStep.ScopeVersions,
        FormCodeDeletionLockStep.Settings
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

public sealed record SaveToDraftResult(long DraftId, bool DraftCreated);

public interface ISettingsAdministrationRepository
{
    long GetVersion(int organizationId, string formCode);
    long GetCacheGeneration();
    SettingDraft? GetDraft(long draftId);
    SettingDraft? GetActiveDraft(int organizationId, string formCode);
    SaveToDraftResult SaveToSharedDraft(int organizationId, string formCode, long expectedVersion, long? expectedDraftId,
        IReadOnlyList<SettingMutation> changes, IReadOnlyDictionary<string, SettingDefinition> catalog, AuditContext audit);
    void RemoveDraftChange(long draftId, string settingKey, IReadOnlyDictionary<string, SettingDefinition> catalog, bool canManageSensitive, AuditContext audit);
    void CommitDraft(long draftId, IReadOnlyDictionary<string, SettingDefinition> catalog, bool canManageSensitive, AuditContext audit);
    void DiscardDraft(long draftId, IReadOnlyDictionary<string, SettingDefinition> catalog, bool canManageSensitive, AuditContext audit);
    void DirectSave(int organizationId, string formCode, long expectedVersion, IReadOnlyList<SettingMutation> changes, IReadOnlyDictionary<string, SettingDefinition> catalog, AuditContext audit);
    long CreatePreviewLink(long draftId, byte[] tokenHash, bool allowLiveSubmission, int operationalBranchId,
        DateTime nowUtc, int lifetimeHours, IReadOnlyDictionary<string, SettingDefinition> catalog, bool canManageSensitive, AuditContext audit);
    PreviewContextSnapshot? ResolvePreviewContext(byte[] tokenHash, DateTime nowUtc);
    PreviewLinkRecord? GetPreviewLink(long previewLinkId);
    IReadOnlyList<PreviewLinkRecord> GetPreviewLinks(long draftId);
    void RevokePreviewLink(long previewLinkId, IReadOnlyDictionary<string, SettingDefinition> catalog, bool canManageSensitive, AuditContext audit);
    long? ReplacePreviewLinkMode(long previewLinkId, byte[] replacementTokenHash, bool allowLiveSubmission, IReadOnlyDictionary<string, SettingDefinition> catalog, bool canManageSensitive, AuditContext audit);
    IReadOnlyList<FormCodeMetadata> GetFormCodes(int libraryId, int systemOrganizationId);
    IReadOnlyList<FormCodeMetadata> GetFormCodesForLibraries(IReadOnlyCollection<int> libraryIds, int systemOrganizationId);
    IReadOnlyList<LegacyFormCodeRow> GetLegacyFormCodes();
    void SaveFormCode(FormCodeMetadata metadata, bool isCreate, AuditContext audit);
    FormCodeImpact GetFormCodeImpact(int ownerOrganizationId, string formCode, IReadOnlyCollection<int> affectedOrganizations);
    FormCodeDeletionSnapshot? GetFormCodeDeletionSnapshot(int ownerOrganizationId, string formCode, int systemOrganizationId, IReadOnlyCollection<int> knownOrganizations);
    void DeleteFormCode(FormCodeDeletionTarget expectedTarget, string expectedFingerprint, int systemOrganizationId, IReadOnlyCollection<int> knownOrganizations, AuditContext audit);
    IEnumerable<SettingsAuditRow> SearchAudit(int? libraryId, bool includeSensitive, string? term);
    void WriteAudit(string eventType, bool succeeded, AuditContext audit, string? failureReason = null, long? draftId = null, long? previewLinkId = null, string? metadataJson = null);
}

public sealed class SettingsAdministrationRepository : ISettingsAdministrationRepository
{
    private readonly string connectionString;

    public SettingsAdministrationRepository(IDbHelperSettings settings)
        : this($"Server={settings.db_hostname};Database={settings.db_name};Trusted_Connection=True;Encrypt=False;")
    {
    }

    internal SettingsAdministrationRepository(string connectionString) => this.connectionString = connectionString;

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
            "select DraftId,OrganizationId,FormCode,BaselineVersion,Status from dbo.RegistrationSettingDrafts where DraftId=@draftId",
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
        return new SettingDraft(row.DraftId, row.OrganizationId, row.FormCode, row.BaselineVersion, Enum.Parse<DraftStatus>(row.Status), changes);
    }

    public SaveToDraftResult SaveToSharedDraft(int organizationId, string formCode, long expectedVersion, long? expectedDraftId,
        IReadOnlyList<SettingMutation> changes, IReadOnlyDictionary<string, SettingDefinition> catalog, AuditContext audit)
    {
        if (changes.Count == 0) throw new InvalidOperationException("Submit at least one setting change.");
        DraftOperationValidation.RequireSupported(changes);
        formCode = FormCodeNormalizer.Normalize(formCode);
        using var connection = Open();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var activeDraft = connection.QuerySingleOrDefault<ActiveDraftRow>(
            "select DraftId,BaselineVersion from dbo.RegistrationSettingDrafts with(updlock,holdlock) where OrganizationId=@organizationId and FormCode=@formCode and Status='Active'",
            new { organizationId, formCode }, transaction);
        var existing = activeDraft?.DraftId;
        if (expectedDraftId.HasValue && existing != expectedDraftId)
        {
            throw new DBConcurrencyException("The expected shared draft is no longer active.");
        }
        var created = !existing.HasValue;
        var draftId = existing.GetValueOrDefault();
        if (created)
        {
            // Lock order: draft range, scope version, then draft changes.
            EnsureVersionRow(connection, transaction, organizationId, formCode);
            var version = ReadVersion(connection, transaction, organizationId, formCode);
            if (version != expectedVersion)
            {
                throw new DBConcurrencyException("Live settings changed after this page was loaded.");
            }
            draftId = connection.QuerySingle<long>(@"
insert dbo.RegistrationSettingDrafts(OrganizationId,FormCode,BaselineVersion,Status,CreatedBy,ModifiedBy)
output inserted.DraftId values(@organizationId,@formCode,@expectedVersion,'Active',@actor,@actor)",
                new { organizationId, formCode, expectedVersion, actor = audit.ActorName ?? "unknown" }, transaction);
            InsertAudit(connection, transaction, "DraftCreated", true, audit, draftId: draftId);
        }
        else if (!expectedDraftId.HasValue && activeDraft!.BaselineVersion != expectedVersion)
        {
            throw new DBConcurrencyException("Live settings changed after this page was loaded.");
        }
        var containedSensitiveChanges = DraftContainsSensitiveChanges(connection, transaction, draftId, catalog);
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
        connection.Execute(
            "update dbo.RegistrationSettingDrafts set ModifiedAtUtc=SYSUTCDATETIME(),ModifiedBy=@actor where DraftId=@draftId",
            new { draftId, actor = audit.ActorName ?? "unknown" }, transaction);
        if (!containedSensitiveChanges && DraftContainsSensitiveChanges(connection, transaction, draftId, catalog))
        {
            RevokeDraftPreviewLinks(connection, transaction, draftId, audit.ActorName);
            InsertAudit(connection, transaction, "PreviewLinksRevokedForRestrictedDraft", true, audit, draftId: draftId);
        }
        InsertAudit(connection, transaction, "DraftEdited", true, audit, draftId: draftId, metadataJson: $"{{\"changeCount\":{changes.Count}}}");
        transaction.Commit();
        return new SaveToDraftResult(draftId, created);
    }

    public void RemoveDraftChange(long draftId, string settingKey, IReadOnlyDictionary<string, SettingDefinition> catalog, bool canManageSensitive, AuditContext audit)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        EnsureActiveDraft(connection, transaction, draftId);
        var definition = DraftChangeAuditClassification.RequireDefinition(settingKey, catalog);
        if (!canManageSensitive && definition.IsSensitive)
        {
            throw RestrictedDraftException();
        }
        var removed = connection.Execute(
            "delete dbo.RegistrationSettingDraftChanges where DraftId=@draftId and SettingKey=@settingKey",
            new { draftId, settingKey }, transaction);
        if (removed != 1)
        {
            throw new DBConcurrencyException("The staged draft mutation no longer exists.");
        }
        connection.Execute(
            "update dbo.RegistrationSettingDrafts set ModifiedAtUtc=SYSUTCDATETIME(),ModifiedBy=@actor where DraftId=@draftId",
            new { draftId, actor = audit.ActorName ?? "unknown" }, transaction);
        InsertAudit(connection, transaction, "DraftChangeRemoved", true, audit, draftId: draftId,
            settingKey: settingKey, isSensitive: definition.IsSensitive);
        transaction.Commit();
    }

    public void DirectSave(int organizationId, string formCode, long expectedVersion, IReadOnlyList<SettingMutation> changes, IReadOnlyDictionary<string, SettingDefinition> catalog, AuditContext audit)
    {
        DraftOperationValidation.RequireSupported(changes);
        formCode = FormCodeNormalizer.Normalize(formCode);
        using var connection = Open();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
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

    public void CommitDraft(long draftId, IReadOnlyDictionary<string, SettingDefinition> catalog, bool canManageSensitive, AuditContext audit)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        EnsureActiveDraft(connection, transaction, draftId);
        var draft = ReadDraft(connection, draftId, transaction) ??
            throw new DBConcurrencyException("The shared draft no longer exists. Reload the settings page.");
        DraftOperationValidation.RequireSupported(draft.Changes);
        EnsureCanManageRestrictedDraft(connection, transaction, draftId, catalog, canManageSensitive);

        EnsureVersionRow(connection, transaction, draft.OrganizationId, draft.FormCode);
        if (ReadVersion(connection, transaction, draft.OrganizationId, draft.FormCode) != draft.BaselineVersion)
        {
            InsertAudit(connection, transaction, "DraftCommitConflict", false, audit, "Draft baseline version was stale.", draftId);
            transaction.Commit();
            throw new DBConcurrencyException("The live settings changed after this draft was created. Reload and review before creating a new draft.");
        }

        ApplyChanges(connection, transaction, draft.OrganizationId, draft.FormCode, draft.Changes, catalog, audit, draftId);
        IncrementVersions(connection, transaction, draft.OrganizationId, draft.FormCode);
        connection.Execute(@"
update dbo.RegistrationSettingDrafts set Status='Committed',CommittedAtUtc=SYSUTCDATETIME(),CommittedBy=@actor,ModifiedAtUtc=SYSUTCDATETIME(),ModifiedBy=@actor where DraftId=@draftId;
update dbo.RegistrationSettingPreviewLinks set RevokedAtUtc=coalesce(RevokedAtUtc,SYSUTCDATETIME()),RevokedBy=coalesce(RevokedBy,@actor),ModifiedAtUtc=SYSUTCDATETIME(),ModifiedBy=@actor where DraftId=@draftId;",
            new { draftId, actor = audit.ActorName ?? "unknown" }, transaction);
        InsertAudit(connection, transaction, "DraftCommitted", true, audit, draftId: draftId);
        transaction.Commit();
    }

    public void DiscardDraft(long draftId, IReadOnlyDictionary<string, SettingDefinition> catalog, bool canManageSensitive, AuditContext audit)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        EnsureActiveDraft(connection, transaction, draftId);
        EnsureCanManageRestrictedDraft(connection, transaction, draftId, catalog, canManageSensitive);
        connection.Execute(@"
update dbo.RegistrationSettingDrafts set Status='Discarded',DiscardedAtUtc=SYSUTCDATETIME(),DiscardedBy=@actor,ModifiedAtUtc=SYSUTCDATETIME(),ModifiedBy=@actor where DraftId=@draftId;
update dbo.RegistrationSettingPreviewLinks set RevokedAtUtc=coalesce(RevokedAtUtc,SYSUTCDATETIME()),RevokedBy=coalesce(RevokedBy,@actor),ModifiedAtUtc=SYSUTCDATETIME(),ModifiedBy=@actor where DraftId=@draftId;",
            new { draftId, actor = audit.ActorName ?? "unknown" }, transaction);
        InsertAudit(connection, transaction, "DraftDiscarded", true, audit, draftId: draftId);
        transaction.Commit();
    }

    public long CreatePreviewLink(long draftId, byte[] tokenHash, bool allowLiveSubmission, int operationalBranchId,
        DateTime nowUtc, int lifetimeHours, IReadOnlyDictionary<string, SettingDefinition> catalog, bool canManageSensitive, AuditContext audit)
    {
        if (nowUtc.Kind != DateTimeKind.Utc) throw new ArgumentException("Preview-link creation time must be UTC.", nameof(nowUtc));
        if (lifetimeHours is < 1 or > SettingsAdministrationOptions.MaximumPreviewLinkLifetimeHours)
            throw new ArgumentOutOfRangeException(nameof(lifetimeHours));
        var expiresAtUtc = nowUtc.AddHours(lifetimeHours);
        using var connection = Open();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        EnsureActiveDraft(connection, transaction, draftId);
        EnsureCanManageRestrictedDraft(connection, transaction, draftId, catalog, canManageSensitive);
        var previewLinkId = connection.QuerySingle<long>(@"
insert dbo.RegistrationSettingPreviewLinks(DraftId,TokenHash,AllowLiveSubmission,OperationalBranchId,CreatedBy,ModifiedBy,ExpiresAtUtc)
output inserted.PreviewLinkId values(@draftId,@tokenHash,@allowLiveSubmission,@operationalBranchId,@actor,@actor,@expiresAtUtc)",
            new { draftId, tokenHash, allowLiveSubmission, operationalBranchId, expiresAtUtc, actor = audit.ActorName ?? "unknown" }, transaction);
        InsertAudit(connection, transaction, "PreviewLinkCreated", true, audit, draftId: draftId, previewLinkId: previewLinkId);
        transaction.Commit();
        return previewLinkId;
    }

    public PreviewContextSnapshot? ResolvePreviewContext(byte[] tokenHash, DateTime nowUtc)
    {
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
 d.OrganizationId,d.FormCode,d.Status DraftStatus,p.OperationalBranchId
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

        transaction.Commit();
        return new PreviewContextSnapshot(link, draft);
    }

    public PreviewLinkRecord? GetPreviewLink(long previewLinkId)
    {
        using var connection = Open();
        return connection.QuerySingleOrDefault<PreviewLinkRecord>(@"
select p.PreviewLinkId,p.DraftId,p.TokenHash,p.AllowLiveSubmission,p.RevokedAtUtc,p.ExpiresAtUtc,d.OrganizationId,d.FormCode,d.Status DraftStatus,p.OperationalBranchId
from dbo.RegistrationSettingPreviewLinks p join dbo.RegistrationSettingDrafts d on d.DraftId=p.DraftId
where p.PreviewLinkId=@previewLinkId", new { previewLinkId });
    }

    public IReadOnlyList<PreviewLinkRecord> GetPreviewLinks(long draftId)
    {
        using var connection = Open();
        return connection.Query<PreviewLinkRecord>(@"
select p.PreviewLinkId,p.DraftId,p.TokenHash,p.AllowLiveSubmission,p.RevokedAtUtc,p.ExpiresAtUtc,d.OrganizationId,d.FormCode,d.Status DraftStatus,p.OperationalBranchId
from dbo.RegistrationSettingPreviewLinks p join dbo.RegistrationSettingDrafts d on d.DraftId=p.DraftId
where p.DraftId=@draftId order by p.PreviewLinkId desc", new { draftId }).ToList();
    }

    public void RevokePreviewLink(long previewLinkId, IReadOnlyDictionary<string, SettingDefinition> catalog, bool canManageSensitive, AuditContext audit)
    {
        using var connection = Open();
        var candidateDraftId = FindPreviewLinkDraftCandidate(connection, previewLinkId);
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var draftId = LockPreviewLinkDraft(connection, transaction, previewLinkId, candidateDraftId, DateTime.UtcNow);
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

    public long? ReplacePreviewLinkMode(long previewLinkId, byte[] replacementTokenHash, bool allowLiveSubmission,
        IReadOnlyDictionary<string, SettingDefinition> catalog, bool canManageSensitive, AuditContext audit)
    {
        using var connection = Open();
        var candidateDraftId = FindPreviewLinkDraftCandidate(connection, previewLinkId);
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var draftId = LockPreviewLinkDraft(connection, transaction, previewLinkId, candidateDraftId, DateTime.UtcNow);
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
 DraftId,TokenHash,AllowLiveSubmission,OperationalBranchId,CreatedBy,ModifiedBy,ExpiresAtUtc)
output inserted.PreviewLinkId
values(@draftId,@replacementTokenHash,@allowLiveSubmission,@operationalBranchId,@actor,@actor,@expiresAtUtc)",
            new
            {
                draftId,
                replacementTokenHash,
                allowLiveSubmission,
                current.OperationalBranchId,
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

    public void SaveFormCode(FormCodeMetadata metadata, bool isCreate, AuditContext audit)
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
            var updated = connection.Execute(@"
update dbo.RegistrationFormCodeMetadata set DisplayName=@DisplayName,Description=@Description,ModifiedAtUtc=SYSUTCDATETIME(),ModifiedBy=@ModifiedBy
where OrganizationId=@OrganizationId and FormCode=@FormCode", metadata, transaction);
            if (updated == 0)
            {
                throw new InvalidOperationException("The form-code metadata no longer exists. Reload the page and try again.");
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
        connection.Execute(
            "update dbo.RegistrationSettingsCacheGeneration set Generation=Generation+1,ModifiedAtUtc=SYSUTCDATETIME() where Id=1",
            transaction: transaction);
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

    private static void EnsureActiveDraft(SqlConnection connection, IDbTransaction transaction, long draftId)
    {
        var status = connection.QuerySingleOrDefault<string>(
            "select Status from dbo.RegistrationSettingDrafts with(updlock,holdlock) where DraftId=@draftId",
            new { draftId }, transaction);
        if (status != "Active")
        {
            throw new DBConcurrencyException("The shared draft is no longer active. Reload the settings page.");
        }
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

    private static void RevokeDraftPreviewLinks(
        SqlConnection connection,
        IDbTransaction transaction,
        long draftId,
        string? actorName)
    {
        connection.Execute(@"
update dbo.RegistrationSettingPreviewLinks
set RevokedAtUtc=SYSUTCDATETIME(),RevokedBy=@actor,ModifiedAtUtc=SYSUTCDATETIME(),ModifiedBy=@actor
where DraftId=@draftId and RevokedAtUtc is null",
            new { draftId, actor = actorName ?? "unknown" }, transaction);
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
        connection.Execute(@"
update dbo.RegistrationSettingScopeVersions set Version=Version+1,ModifiedAtUtc=SYSUTCDATETIME() where OrganizationId=@organizationId and FormCode=@formCode;
update dbo.RegistrationSettingsCacheGeneration set Generation=Generation+1,ModifiedAtUtc=SYSUTCDATETIME() where Id=1;",
            new { organizationId, formCode }, transaction);
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

    private sealed record DraftRow(long DraftId, int OrganizationId, string FormCode, long BaselineVersion, string Status);
    private sealed record DraftChangeRow(string SettingKey, string Operation, string? Value);
    private sealed record PreviewLinkModeRow(bool AllowLiveSubmission, int OperationalBranchId, DateTime? ExpiresAtUtc);
    private sealed record ActiveDraftRow(long DraftId, long BaselineVersion);
}
