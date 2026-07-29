using System.Data;
using Clc.PatronRegistration.Administration;
using Clc.PatronRegistration.Configuration;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Clc.PatronRegistration.Web.Settings;

public sealed record AuditContext(
    string? ActorId,
    string? ActorName,
    int? ActorOrganizationId,
    int TargetOrganizationId,
    int TargetLibraryId,
    string FormCode,
    string? CorrelationId,
    string? IpAddress);

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

public sealed record FormCodeImpact(int MetadataRows, int OverrideRows, int Drafts, int PreviewLinks);
public sealed record LegacyFormCodeRow(int OrganizationId, string FormCode);

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
    bool Succeeded,
    string? ActorName,
    string? FailureReason,
    string? CorrelationId,
    string? IpAddress);

public interface ISettingsAdministrationRepository
{
    long GetVersion(int organizationId, string formCode);
    long GetCacheGeneration();
    SettingDraft? GetDraft(long draftId);
    SettingDraft? GetActiveDraft(int organizationId, string formCode);
    long CreateDraft(int organizationId, string formCode, AuditContext audit);
    void SaveDraftChanges(long draftId, IReadOnlyList<SettingMutation> changes, IReadOnlyDictionary<string, SettingDefinition> catalog, AuditContext audit);
    void RemoveDraftChange(long draftId, string settingKey, IReadOnlyDictionary<string, SettingDefinition> catalog, bool canManageSensitive, AuditContext audit);
    void CommitDraft(long draftId, IReadOnlyDictionary<string, SettingDefinition> catalog, bool canManageSensitive, AuditContext audit);
    void DiscardDraft(long draftId, IReadOnlyDictionary<string, SettingDefinition> catalog, bool canManageSensitive, AuditContext audit);
    void DirectSave(int organizationId, string formCode, long expectedVersion, IReadOnlyList<SettingMutation> changes, IReadOnlyDictionary<string, SettingDefinition> catalog, AuditContext audit);
    long CreatePreviewLink(long draftId, byte[] tokenHash, bool allowLiveSubmission, int operationalBranchId, IReadOnlyDictionary<string, SettingDefinition> catalog, bool canManageSensitive, AuditContext audit);
    PreviewLinkRecord? FindPreviewLink(byte[] tokenHash);
    PreviewLinkRecord? GetPreviewLink(long previewLinkId);
    IReadOnlyList<PreviewLinkRecord> GetPreviewLinks(long draftId);
    void RevokePreviewLink(long previewLinkId, IReadOnlyDictionary<string, SettingDefinition> catalog, bool canManageSensitive, AuditContext audit);
    void TogglePreviewLiveSubmission(long previewLinkId, bool allowLiveSubmission, IReadOnlyDictionary<string, SettingDefinition> catalog, bool canManageSensitive, AuditContext audit);
    IReadOnlyList<FormCodeMetadata> GetFormCodes(int libraryId, int systemOrganizationId);
    IReadOnlyList<LegacyFormCodeRow> GetLegacyFormCodes();
    void SaveFormCode(FormCodeMetadata metadata, bool isCreate, AuditContext audit);
    FormCodeImpact GetFormCodeImpact(int ownerOrganizationId, string formCode, IReadOnlyCollection<int> affectedOrganizations);
    void DeleteFormCode(int ownerOrganizationId, string formCode, IReadOnlyCollection<int> affectedOrganizations, AuditContext audit);
    IEnumerable<SettingsAuditRow> SearchAudit(int? libraryId, string? term);
    void WriteAudit(string eventType, bool succeeded, AuditContext audit, string? failureReason = null, long? draftId = null, long? previewLinkId = null, string? metadataJson = null);
}

public sealed class SettingsAdministrationRepository(IDbHelperSettings settings) : ISettingsAdministrationRepository
{
    private SqlConnection Open()
    {
        var connection = new SqlConnection($"Server={settings.db_hostname};Database={settings.db_name};Trusted_Connection=True;Encrypt=False;");
        connection.Open();
        return connection;
    }

    public long GetVersion(int organizationId, string formCode)
    {
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

    public long CreateDraft(int organizationId, string formCode, AuditContext audit)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        EnsureVersionRow(connection, transaction, organizationId, formCode);
        var existing = connection.QuerySingleOrDefault<long?>(
            "select DraftId from dbo.RegistrationSettingDrafts with(updlock,holdlock) where OrganizationId=@organizationId and FormCode=@formCode and Status='Active'",
            new { organizationId, formCode }, transaction);
        if (existing.HasValue)
        {
            transaction.Commit();
            return existing.Value;
        }

        var version = ReadVersion(connection, transaction, organizationId, formCode);
        var draftId = connection.QuerySingle<long>(@"
insert dbo.RegistrationSettingDrafts(OrganizationId,FormCode,BaselineVersion,Status,CreatedBy,ModifiedBy)
output inserted.DraftId values(@organizationId,@formCode,@version,'Active',@actor,@actor)",
            new { organizationId, formCode, version, actor = audit.ActorName ?? "unknown" }, transaction);
        InsertAudit(connection, transaction, "DraftCreated", true, audit, draftId: draftId);
        transaction.Commit();
        return draftId;
    }

    public void SaveDraftChanges(long draftId, IReadOnlyList<SettingMutation> changes, IReadOnlyDictionary<string, SettingDefinition> catalog, AuditContext audit)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        EnsureActiveDraft(connection, transaction, draftId);
        var containedSensitiveChanges = DraftContainsSensitiveChanges(connection, transaction, draftId, catalog);
        foreach (var change in changes)
        {
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
    }

    public void RemoveDraftChange(long draftId, string settingKey, IReadOnlyDictionary<string, SettingDefinition> catalog, bool canManageSensitive, AuditContext audit)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        EnsureActiveDraft(connection, transaction, draftId);
        if (!canManageSensitive && catalog.TryGetValue(settingKey, out var definition) && definition.IsSensitive)
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
        InsertAudit(connection, transaction, "DraftChangeRemoved", true, audit, draftId: draftId, settingKey: settingKey);
        transaction.Commit();
    }

    public void DirectSave(int organizationId, string formCode, long expectedVersion, IReadOnlyList<SettingMutation> changes, IReadOnlyDictionary<string, SettingDefinition> catalog, AuditContext audit)
    {
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
        var draft = ReadDraft(connection, draftId, transaction) ?? throw new InvalidOperationException("Draft does not exist.");
        if (draft.Status != DraftStatus.Active)
        {
            throw new InvalidOperationException("Only an active draft can be committed.");
        }
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

    public long CreatePreviewLink(long draftId, byte[] tokenHash, bool allowLiveSubmission, int operationalBranchId, IReadOnlyDictionary<string, SettingDefinition> catalog, bool canManageSensitive, AuditContext audit)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        EnsureActiveDraft(connection, transaction, draftId);
        EnsureCanManageRestrictedDraft(connection, transaction, draftId, catalog, canManageSensitive);
        var previewLinkId = connection.QuerySingle<long>(@"
insert dbo.RegistrationSettingPreviewLinks(DraftId,TokenHash,AllowLiveSubmission,OperationalBranchId,CreatedBy,ModifiedBy)
output inserted.PreviewLinkId values(@draftId,@tokenHash,@allowLiveSubmission,@operationalBranchId,@actor,@actor)",
            new { draftId, tokenHash, allowLiveSubmission, operationalBranchId, actor = audit.ActorName ?? "unknown" }, transaction);
        InsertAudit(connection, transaction, "PreviewLinkCreated", true, audit, draftId: draftId, previewLinkId: previewLinkId);
        transaction.Commit();
        return previewLinkId;
    }

    public PreviewLinkRecord? FindPreviewLink(byte[] tokenHash)
    {
        using var connection = Open();
        return connection.QuerySingleOrDefault<PreviewLinkRecord>(@"
select p.PreviewLinkId,p.DraftId,p.TokenHash,p.AllowLiveSubmission,p.RevokedAtUtc,p.ExpiresAtUtc,d.OrganizationId,d.FormCode,d.Status DraftStatus,p.OperationalBranchId
from dbo.RegistrationSettingPreviewLinks p join dbo.RegistrationSettingDrafts d on d.DraftId=p.DraftId
where p.TokenHash=@tokenHash", new { tokenHash });
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
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var draftId = LockPreviewLinkDraft(connection, transaction, previewLinkId);
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

    public void TogglePreviewLiveSubmission(long previewLinkId, bool allowLiveSubmission, IReadOnlyDictionary<string, SettingDefinition> catalog, bool canManageSensitive, AuditContext audit)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var draftId = LockPreviewLinkDraft(connection, transaction, previewLinkId);
        EnsureCanManageRestrictedDraft(connection, transaction, draftId, catalog, canManageSensitive);
        var updated = connection.Execute(@"
update dbo.RegistrationSettingPreviewLinks set AllowLiveSubmission=@allowLiveSubmission,ModifiedAtUtc=SYSUTCDATETIME(),ModifiedBy=@actor
where PreviewLinkId=@previewLinkId and RevokedAtUtc is null
 and exists(select 1 from dbo.RegistrationSettingDrafts d where d.DraftId=dbo.RegistrationSettingPreviewLinks.DraftId and d.Status='Active')",
            new { previewLinkId, allowLiveSubmission, actor = audit.ActorName ?? "unknown" }, transaction);
        if (updated != 1)
        {
            InsertAudit(connection, transaction, "PreviewLiveSubmissionToggleFailed", false, audit, "The preview link was revoked or invalidated.", previewLinkId: previewLinkId);
            transaction.Commit();
            throw new DBConcurrencyException("The preview link was revoked or invalidated.");
        }
        InsertAudit(connection, transaction, "PreviewLiveSubmissionToggled", true, audit, previewLinkId: previewLinkId, metadataJson: $"{{\"enabled\":{allowLiveSubmission.ToString().ToLowerInvariant()}}}");
        transaction.Commit();
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

    public void DeleteFormCode(int ownerOrganizationId, string formCode, IReadOnlyCollection<int> affectedOrganizations, AuditContext audit)
    {
        if (string.IsNullOrWhiteSpace(formCode))
        {
            throw new ArgumentException("The default form code cannot be deleted.");
        }
        using var connection = Open();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        connection.Execute(@"
delete p from dbo.RegistrationSettingPreviewLinks p join dbo.RegistrationSettingDrafts d on d.DraftId=p.DraftId where d.OrganizationId in @affectedOrganizations and d.FormCode=@formCode;
delete from dbo.RegistrationSettingDrafts where OrganizationId in @affectedOrganizations and FormCode=@formCode;
delete from dbo.RegistrationFormSettings where OrganizationID in @affectedOrganizations and FormCode=@formCode;
delete from dbo.RegistrationFormCodeMetadata where OrganizationId in @affectedOrganizations and FormCode=@formCode;",
            new { ownerOrganizationId, formCode, affectedOrganizations }, transaction);
        foreach (var organizationId in AffectedVersionScopes(affectedOrganizations))
        {
            EnsureVersionRow(connection, transaction, organizationId, formCode);
            connection.Execute(
                "update dbo.RegistrationSettingScopeVersions set Version=Version+1,ModifiedAtUtc=SYSUTCDATETIME() where OrganizationId=@organizationId and FormCode=@formCode",
                new { organizationId, formCode }, transaction);
        }
        connection.Execute(
            "update dbo.RegistrationSettingsCacheGeneration set Generation=Generation+1,ModifiedAtUtc=SYSUTCDATETIME() where Id=1",
            transaction: transaction);
        InsertAudit(connection, transaction, "FormCodeDeleted", true, audit);
        transaction.Commit();
    }

    public static IReadOnlyList<int> AffectedVersionScopes(IEnumerable<int> affectedOrganizations) =>
        affectedOrganizations.Distinct().ToList();

    public IEnumerable<SettingsAuditRow> SearchAudit(int? libraryId, string? term)
    {
        using var connection = Open();
        var pattern = $"%{term ?? string.Empty}%";
        return connection.Query<SettingsAuditRow>(@"
select top(500) AuditEventId,TimestampUtc,EventType,TargetOrganizationId,TargetLibraryId,FormCode,SettingKey,
 PreviousValue,NewValue,Succeeded,ActorName,FailureReason,CorrelationId,IpAddress
from dbo.RegistrationSettingAuditEvents
where (@libraryId is null or TargetLibraryId=@libraryId)
 and (EventType like @pattern or SettingKey like @pattern or ActorName like @pattern or FormCode like @pattern)
order by TimestampUtc desc", new { libraryId, pattern }).ToList();
    }

    public void WriteAudit(string eventType, bool succeeded, AuditContext audit, string? failureReason = null, long? draftId = null, long? previewLinkId = null, string? metadataJson = null)
    {
        using var connection = Open();
        InsertAudit(connection, null, eventType, succeeded, audit, failureReason, draftId, previewLinkId, metadataJson);
    }

    private static void ApplyChanges(SqlConnection connection, IDbTransaction transaction, int organizationId, string formCode, IReadOnlyList<SettingMutation> changes, IReadOnlyDictionary<string, SettingDefinition> catalog, AuditContext audit, long? draftId = null)
    {
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
            throw new InvalidOperationException("Only an active draft can be changed.");
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

    private static long LockPreviewLinkDraft(SqlConnection connection, IDbTransaction transaction, long previewLinkId)
    {
        var draftId = connection.QuerySingleOrDefault<long?>(
            "select DraftId from dbo.RegistrationSettingPreviewLinks where PreviewLinkId=@previewLinkId",
            new { previewLinkId }, transaction);
        if (!draftId.HasValue)
        {
            throw new DBConcurrencyException("The preview link was already revoked or invalidated.");
        }

        // All draft operations lock the draft before its links. Keeping that order avoids
        // deadlocks with the transaction that revokes links when a draft becomes restricted.
        EnsureActiveDraft(connection, transaction, draftId.Value);
        var activeLink = connection.QuerySingleOrDefault<long?>(@"
select PreviewLinkId
from dbo.RegistrationSettingPreviewLinks with(updlock,holdlock)
where PreviewLinkId=@previewLinkId and DraftId=@draftId and RevokedAtUtc is null",
            new { previewLinkId, draftId = draftId.Value }, transaction);
        if (!activeLink.HasValue)
        {
            throw new DBConcurrencyException("The preview link was already revoked or invalidated.");
        }

        return draftId.Value;
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
                audit.FormCode,
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
}
