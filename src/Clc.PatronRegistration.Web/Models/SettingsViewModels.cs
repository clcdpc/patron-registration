using Clc.PatronRegistration.Administration;
using Clc.PatronRegistration.Configuration;
using Clc.PatronRegistration.Web.Settings;
using System.Text.RegularExpressions;

namespace Clc.PatronRegistration.Web.Models;

public enum ScopeOptionGroup { System, Libraries, Branches }
public sealed record ScopeOption(int OrganizationId, string DisplayName,
    ScopeOptionGroup Group = ScopeOptionGroup.Branches, string SortParent = "");
public sealed record FormCodeOption(string FormCode, string DisplayName, string? Description, int OwnerOrganizationId, bool IsRegistered = true);

public static class PreviewLinkMode
{
    public static bool AllowsLiveSubmission(bool? value) => value == true;
}

public static class SettingEditorDefaults
{
    public static string ValueFor(SettingDefinition definition, string? value) =>
        definition.ValueType == SettingValueType.Boolean && string.IsNullOrEmpty(value) ? "false" : value ?? string.Empty;
}

public static class SettingValuePresentation
{
    public static string Format(SettingDefinition definition, string? value, bool hasValue)
    {
        if (!hasValue)
        {
            return "Not configured";
        }
        if (definition.IsSensitive)
        {
            return "Hidden";
        }
        if (definition.ValueType == SettingValueType.Image)
        {
            return string.IsNullOrWhiteSpace(value) ? "No image" : "Uploaded image";
        }
        if (value is null)
        {
            return "Not configured";
        }
        if (value.Length == 0)
        {
            return "Blank";
        }
        return definition.ValueType switch
        {
            SettingValueType.Boolean when bool.TryParse(value, out var booleanValue) => booleanValue ? "Yes" : "No",
            SettingValueType.LongString => Preview(value),
            SettingValueType.Html => "HTML configured",
            SettingValueType.EmailTemplate => "Email template configured",
            _ => value
        };
    }

    public static SettingRowPresentation ForRow(SettingRowViewModel row)
    {
        if (row.DraftOperation == DraftOperation.Upsert)
        {
            return new(SettingPresentationState.DraftChange, Format(row.Definition, row.DraftValue, true),
                "Shared draft", Format(row.Definition, row.Resolution.EffectiveValue, row.Resolution.SourceOrganizationId.HasValue));
        }
        if (row.DraftOperation == DraftOperation.RemoveOverride)
        {
            return new(SettingPresentationState.DraftChange, "Use inherited value", "Shared draft",
                Format(row.Definition, row.Resolution.EffectiveValue, row.Resolution.SourceOrganizationId.HasValue));
        }
        if (row.Resolution.OwnsOverride)
        {
            var value = Format(row.Definition, row.Resolution.EffectiveValue, true);
            return new(SettingPresentationState.Customized, value, "Customized", value);
        }
        if (row.Resolution.SourceOrganizationId.HasValue)
        {
            var value = Format(row.Definition, row.Resolution.EffectiveValue, true);
            return new(SettingPresentationState.Inherited, value, "Inherited", value);
        }
        return new(SettingPresentationState.NotSet, "—", "Not set", "Not set");
    }

    private static string Preview(string value)
    {
        const int maximumLength = 160;
        var normalized = Regex.Replace(value, @"\s+", " ").Trim();
        return normalized.Length <= maximumLength ? normalized : $"{normalized[..maximumLength].TrimEnd()}…";
    }
}

public enum SettingPresentationState { DraftChange, Customized, Inherited, NotSet }
public sealed record SettingRowPresentation(SettingPresentationState State, string Value, string Status,
    string CurrentValue);

public sealed record SettingAssetPresentation(int AssetId, string FileName, string PreviewUrl);

public sealed class SettingsIndexViewModel
{
    public int OrganizationId { get; set; }
    public string OrganizationName { get; set; } = string.Empty;
    public int LibraryId { get; set; }
    public string FormCode { get; set; } = string.Empty;
    public string FormDisplayName { get; set; } = string.Empty;
    public long ScopeVersion { get; set; }
    public SettingDraft? ActiveDraft { get; set; }
    public bool HasRestrictedDraftChanges { get; set; }
    public bool CanManageRestrictedDraft { get; set; }
    public IReadOnlyList<PreviewLinkRecord> PreviewLinks { get; set; } = [];
    public IReadOnlyList<ScopeOption> PreviewBranches { get; set; } = [];
    public bool IsGlobal { get; set; }
    public List<ScopeOption> Scopes { get; set; } = [];
    public List<FormCodeOption> FormCodes { get; set; } = [];
    public List<SettingRowViewModel> Settings { get; set; } = [];
}

public sealed record SettingsHelpViewModel(int? OrganizationId, string FormCode);

public sealed record SettingRowViewModel(
    string Token,
    SettingDefinition Definition,
    ResolvedSetting Resolution,
    string? DraftValue,
    DraftOperation? DraftOperation,
    long? DraftId,
    string SourceDescription = "No value is configured",
    string? InheritedValue = null,
    bool HasInheritedValue = false,
    SettingAssetPresentation? EffectiveAsset = null,
    bool EffectiveAssetMissing = false,
    SettingAssetPresentation? StagedAsset = null,
    bool StagedAssetMissing = false,
    SettingAssetPresentation? InheritedAsset = null,
    bool InheritedAssetMissing = false,
    string? InheritedSourceDescription = null);

public static class SettingInheritancePresentation
{
    public static string MessageFor(SettingRowViewModel row)
    {
        const string prefix = "Choosing Use inherited value will remove this customization.";
        if (!row.HasInheritedValue)
        {
            return $"{prefix} No inherited value is configured.";
        }
        var source = row.InheritedSourceDescription ?? "the inherited scope";
        if (row.Definition.IsSensitive)
        {
            return $"{prefix} Use the inherited value from {source}.";
        }

        var inheritedValue = SettingValuePresentation.Format(row.Definition, row.InheritedValue, true);
        var valueDescription = row.Definition.ValueType is SettingValueType.Html or SettingValueType.EmailTemplate
            ? $"use the value from {source}"
            : $"use “{inheritedValue}” from {source}";
        return $"{prefix} The setting will {valueDescription}.";
    }
}

public static class SettingReviewPresentation
{
    public static string Live(SettingRowViewModel row)
    {
        if (row.Definition.IsSensitive)
        {
            return row.Resolution.SourceOrganizationId.HasValue ? "configured" : "not configured";
        }
        if (!row.Resolution.SourceOrganizationId.HasValue)
        {
            return "not configured";
        }
        return row.Resolution.OwnsOverride
            ? SettingValuePresentation.Format(row.Definition, row.Resolution.EffectiveValue, true)
            : $"inherited from {row.SourceDescription}";
    }

    public static string Proposed(SettingRowViewModel row)
    {
        if (row.DraftOperation == DraftOperation.RemoveOverride)
        {
            if (!row.HasInheritedValue)
            {
                return "remove customization; no inherited value configured";
            }
            var source = row.InheritedSourceDescription ?? "the inherited scope";
            return row.Definition.IsSensitive
                ? $"use inherited value from {source}"
                : $"use {SettingValuePresentation.Format(row.Definition, row.InheritedValue, true)} from {source}";
        }
        if (row.Definition.IsSensitive)
        {
            return "replacement value entered";
        }
        return SettingValuePresentation.Format(row.Definition, row.DraftValue, true);
    }
}

public sealed class SaveSettingsRequest
{
    private string formCode = string.Empty;
    public int OrganizationId { get; set; }
    public string FormCode { get => formCode; set => formCode = FormCodeNormalizer.Normalize(value); }
    public long ExpectedVersion { get; set; }
    public List<SettingMutationInput> Changes { get; set; } = [];
}

public sealed class SettingMutationInput
{
    public string Key { get; set; } = string.Empty;
    public string Operation { get; set; } = "Upsert";
    public string? Value { get; set; }
}

public sealed class SaveToSharedDraftRequest
{
    private string formCode = string.Empty;
    public int OrganizationId { get; set; }
    public string FormCode { get => formCode; set => formCode = FormCodeNormalizer.Normalize(value); }
    public long ExpectedVersion { get; set; }
    public long? ExpectedDraftId { get; set; }
    public List<SettingMutationInput> Changes { get; set; } = [];
}

public sealed record PreviewLinkCreatedViewModel(
    string PreviewUrl, long DraftId, int OrganizationId, string OrganizationDisplayName,
    string FormCode, string FormDisplayName, int OperationalBranchId,
    string OperationalBranchDisplayName, bool AllowLiveSubmission);

public sealed class PreviewLinkRequest
{
    private string formCode = string.Empty;
    public int OrganizationId { get; set; }
    public string FormCode { get => formCode; set => formCode = FormCodeNormalizer.Normalize(value); }
    public bool AllowLiveSubmission { get; set; }
    public int? OperationalBranchId { get; set; }
}

public sealed class FormsViewModel
{
    public int LibraryId { get; set; }
    public string LibraryName { get; set; } = string.Empty;
    public int SystemOrganizationId { get; set; }
    public bool IsGlobal { get; set; }
    public IReadOnlyList<FormCodeMetadata> Forms { get; set; } = [];
    public IReadOnlyList<FormCodeOption> LegacyForms { get; set; } = [];
    public IReadOnlyDictionary<int, string> OrganizationNames { get; set; } = new Dictionary<int, string>();
}

public sealed class FormCodeRequest
{
    private string formCode = string.Empty;
    public int OrganizationId { get; set; }
    public string FormCode { get => formCode; set => formCode = FormCodeNormalizer.Normalize(value); }
    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
}

public sealed class DeleteFormCodeViewModel
{
    public int OrganizationId { get; set; }
    public string OwnerOrganizationName { get; set; } = string.Empty;
    public string FormCode { get; set; } = string.Empty;
    public FormCodeDeletionKind Kind { get; set; }
    public string KindDisplayName => Kind switch
    {
        FormCodeDeletionKind.SystemDefinition => "System definition",
        FormCodeDeletionKind.LibraryDefinition => "Library definition",
        FormCodeDeletionKind.LibraryCustomization => "Library customization",
        _ => Kind.ToString()
    };
    public bool IsLegacy { get; set; }
    public string SnapshotFingerprint { get; set; } = string.Empty;
    public IReadOnlyList<string> AffectedOrganizationNames { get; set; } = [];
    public FormCodeImpact Impact { get; set; } = new(0, 0, 0, 0);
}
