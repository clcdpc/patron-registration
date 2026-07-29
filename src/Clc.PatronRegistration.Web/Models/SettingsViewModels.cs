using Clc.PatronRegistration.Administration;
using Clc.PatronRegistration.Configuration;
using Clc.PatronRegistration.Web.Settings;

namespace Clc.PatronRegistration.Web.Models;

public sealed record ScopeOption(int OrganizationId, string DisplayName);
public sealed record FormCodeOption(string FormCode, string DisplayName, string? Description, int OwnerOrganizationId, bool IsRegistered = true);

public sealed class SettingsIndexViewModel
{
    public int OrganizationId { get; set; }
    public string OrganizationName { get; set; } = string.Empty;
    public int LibraryId { get; set; }
    public string FormCode { get; set; } = string.Empty;
    public long ScopeVersion { get; set; }
    public SettingDraft? ActiveDraft { get; set; }
    public IReadOnlyList<PreviewLinkRecord> PreviewLinks { get; set; } = [];
    public IReadOnlyList<ScopeOption> PreviewBranches { get; set; } = [];
    public bool IsGlobal { get; set; }
    public List<ScopeOption> Scopes { get; set; } = [];
    public List<FormCodeOption> FormCodes { get; set; } = [];
    public List<SettingRowViewModel> Settings { get; set; } = [];
}

public sealed record SettingRowViewModel(
    string Token,
    SettingDefinition Definition,
    ResolvedSetting Resolution,
    string? DraftValue,
    DraftOperation? DraftOperation,
    long? DraftId);

public sealed class SaveSettingsRequest
{
    public int OrganizationId { get; set; }
    public string FormCode { get; set; } = string.Empty;
    public long ExpectedVersion { get; set; }
    public List<SettingMutationInput> Changes { get; set; } = [];
}

public sealed class SettingMutationInput
{
    public string Key { get; set; } = string.Empty;
    public string Operation { get; set; } = "Upsert";
    public string? Value { get; set; }
}

public sealed class DraftChangesRequest
{
    public int OrganizationId { get; set; }
    public string FormCode { get; set; } = string.Empty;
    public List<SettingMutationInput> Changes { get; set; } = [];
}

public sealed class PreviewLinkRequest
{
    public int OrganizationId { get; set; }
    public string FormCode { get; set; } = string.Empty;
    public bool AllowLiveSubmission { get; set; }
    public int? OperationalBranchId { get; set; }
}

public sealed class FormsViewModel
{
    public int LibraryId { get; set; }
    public int SystemOrganizationId { get; set; }
    public bool IsGlobal { get; set; }
    public IReadOnlyList<FormCodeMetadata> Forms { get; set; } = [];
    public IReadOnlyList<FormCodeOption> LegacyForms { get; set; } = [];
}

public sealed class FormCodeRequest
{
    public int OrganizationId { get; set; }
    public string FormCode { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
}

public sealed class DeleteFormCodeViewModel
{
    public int OrganizationId { get; set; }
    public string FormCode { get; set; } = string.Empty;
    public FormCodeImpact Impact { get; set; } = new(0, 0, 0, 0);
}
