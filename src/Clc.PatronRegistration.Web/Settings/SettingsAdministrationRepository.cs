using System.Data;
using Clc.PatronRegistration.Administration;
using Clc.PatronRegistration.Configuration;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Clc.PatronRegistration.Web.Settings;

public interface ISettingsAdministrationRepository
{
    long GetVersion(int organizationId, string formCode);
    SettingDraft? GetActiveDraft(int organizationId, string formCode);
    void DirectSave(int organizationId, string formCode, long expectedVersion, IReadOnlyList<SettingMutation> changes, string actor);
    IEnumerable<SettingsAuditRow> SearchAudit(int? libraryId, string? term);
}
public sealed record SettingsAuditRow(long AuditEventId, DateTime TimestampUtc, string EventType, int TargetOrganizationId, string FormCode, string? SettingKey, string? PreviousValue, string? NewValue, bool Succeeded, string? ActorName);

public sealed class SettingsAdministrationRepository(IDbHelperSettings settings) : ISettingsAdministrationRepository
{
    private SqlConnection Open() { var c = new SqlConnection($"Server={settings.db_hostname};Database={settings.db_name};Trusted_Connection=True;Encrypt=False;"); c.Open(); return c; }
    public long GetVersion(int organizationId, string formCode)
    {
        using var c = Open();
        return c.QuerySingleOrDefault<long>("select Version from dbo.RegistrationSettingScopeVersions where OrganizationId=@organizationId and FormCode=@formCode", new { organizationId, formCode });
    }
    public SettingDraft? GetActiveDraft(int organizationId, string formCode)
    {
        using var c = Open();
        var draft = c.QuerySingleOrDefault<dynamic>("select top (1) * from dbo.RegistrationSettingDrafts where OrganizationId=@organizationId and FormCode=@formCode and Status='Active'", new { organizationId, formCode });
        if (draft is null) return null;
        var changes = c.Query<(string SettingKey, string Operation, string? Value)>("select SettingKey, Operation, Value from dbo.RegistrationSettingDraftChanges where DraftId=@DraftId", new { draft.DraftId })
            .Select(x => new SettingMutation(x.SettingKey, Enum.Parse<DraftOperation>(x.Operation), x.Value)).ToList();
        return new((long)draft.DraftId, (int)draft.OrganizationId, (string)draft.FormCode, (long)draft.BaselineVersion, DraftStatus.Active, changes);
    }
    public void DirectSave(int organizationId, string formCode, long expectedVersion, IReadOnlyList<SettingMutation> changes, string actor)
    {
        using var c = Open(); using var tx = c.BeginTransaction(IsolationLevel.Serializable);
        c.Execute("if not exists(select 1 from dbo.RegistrationSettingScopeVersions with(updlock,holdlock) where OrganizationId=@organizationId and FormCode=@formCode) insert dbo.RegistrationSettingScopeVersions(OrganizationId,FormCode,Version,ModifiedAtUtc) values(@organizationId,@formCode,0,SYSUTCDATETIME())", new { organizationId, formCode }, tx);
        var current = c.QuerySingle<long>("select Version from dbo.RegistrationSettingScopeVersions with(updlock,holdlock) where OrganizationId=@organizationId and FormCode=@formCode", new { organizationId, formCode }, tx);
        if (current != expectedVersion) throw new DBConcurrencyException("Settings changed since this page was loaded. Reload and review the current values.");
        foreach (var change in changes)
        {
            var old = c.QuerySingleOrDefault<string>("select Value from dbo.RegistrationFormSettings where OrganizationID=@organizationId and FormCode=@formCode and Setting=@Key", new { organizationId, formCode, change.Key }, tx);
            var sensitive = change.Key.Equals("postmark_api_key", StringComparison.OrdinalIgnoreCase) || change.Key.Equals("melissa_data_api_key", StringComparison.OrdinalIgnoreCase);
            if (change.Operation == DraftOperation.RemoveOverride)
                c.Execute("delete dbo.RegistrationFormSettings where OrganizationID=@organizationId and FormCode=@formCode and Setting=@Key", new { organizationId, formCode, change.Key }, tx);
            else c.Execute("update dbo.RegistrationFormSettings set Value=@Value where OrganizationID=@organizationId and FormCode=@formCode and Setting=@Key; if @@ROWCOUNT=0 insert dbo.RegistrationFormSettings(OrganizationID,Setting,FormCode,Value) values(@organizationId,@Key,@formCode,@Value)", new { organizationId, formCode, change.Key, change.Value }, tx);
            c.Execute("insert dbo.RegistrationSettingAuditEvents(TimestampUtc,EventType,ActorName,TargetOrganizationId,FormCode,SettingKey,PreviousValue,NewValue,IsSensitive,Succeeded) values(SYSUTCDATETIME(),@eventType,@actor,@organizationId,@formCode,@Key,@previousValue,@newValue,@sensitive,1)", new { eventType = change.Operation == DraftOperation.RemoveOverride ? "OverrideRemoved" : old is null ? "OverrideCreated" : "OverrideUpdated", actor, organizationId, formCode, change.Key, previousValue = sensitive ? SensitiveValueMasker.Mask(old) : old, newValue = sensitive ? SensitiveValueMasker.Mask(change.Value) : change.Value, sensitive }, tx);
        }
        c.Execute("update dbo.RegistrationSettingScopeVersions set Version=Version+1,ModifiedAtUtc=SYSUTCDATETIME() where OrganizationId=@organizationId and FormCode=@formCode; update dbo.RegistrationSettingsCacheGeneration set Generation=Generation+1,ModifiedAtUtc=SYSUTCDATETIME() where Id=1", new { organizationId, formCode }, tx);
        tx.Commit();
    }
    public IEnumerable<SettingsAuditRow> SearchAudit(int? libraryId, string? term)
    {
        using var c = Open(); term = $"%{term ?? string.Empty}%";
        return c.Query<SettingsAuditRow>("select top(500) AuditEventId,TimestampUtc,EventType,TargetOrganizationId,FormCode,SettingKey,PreviousValue,NewValue,Succeeded,ActorName from dbo.RegistrationSettingAuditEvents where (@libraryId is null or TargetLibraryId=@libraryId) and (EventType like @term or SettingKey like @term or ActorName like @term) order by TimestampUtc desc", new { libraryId, term }).ToList();
    }
}
