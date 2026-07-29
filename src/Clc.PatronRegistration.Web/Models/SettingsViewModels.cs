using Clc.PatronRegistration.Administration;
using Clc.PatronRegistration.Configuration;

namespace Clc.PatronRegistration.Web.Models;

public sealed class SettingsIndexViewModel
{
    public int OrganizationId { get; set; }
    public int LibraryId { get; set; }
    public string FormCode { get; set; } = string.Empty;
    public long ScopeVersion { get; set; }
    public long? ActiveDraftId { get; set; }
    public bool IsGlobal { get; set; }
    public List<SettingRowViewModel> Settings { get; set; } = [];
}
public sealed record SettingRowViewModel(SettingDefinition Definition, ResolvedSetting Resolution, string? DraftValue, DraftOperation? DraftOperation);
public sealed class SaveSettingsRequest
{
    public int OrganizationId { get; set; }
    public string FormCode { get; set; } = string.Empty;
    public long ExpectedVersion { get; set; }
    public List<SettingMutationInput> Changes { get; set; } = [];
}
public sealed class SettingMutationInput { public string Key { get; set; } = string.Empty; public string Operation { get; set; } = "Upsert"; public string? Value { get; set; } }
