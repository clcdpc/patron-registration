using Clc.PatronRegistration.Configuration;
using Clc.PatronRegistration.Helpers;

namespace Clc.PatronRegistration.Administration;

/// <summary>Provides the normal setting surface with an active draft overlaid at its selected scope.</summary>
public sealed class PreviewSettingProvider : DbSettingProvider
{
    private readonly IReadOnlyList<RegistrationFormSetting> overlaidSettings;

    public PreviewSettingProvider(SettingDraft draft, int operationalBranchId, ICache cache, int systemOrganizationId)
        : base(operationalBranchId, cache, draft.FormCode, systemOrganizationId)
    {
        var rows = SettingsSnapshot
            .Where(row => !(row.OrganizationID == draft.OrganizationId &&
                            row.FormCode.Equals(draft.FormCode, StringComparison.OrdinalIgnoreCase) &&
                            draft.Changes.Any(change => change.Key.Equals(row.Setting, StringComparison.OrdinalIgnoreCase))))
            .ToList();
        rows.AddRange(draft.Changes
            .Where(change => change.Operation == DraftOperation.Upsert)
            .Select(change => new RegistrationFormSetting
            {
                OrganizationID = draft.OrganizationId,
                FormCode = draft.FormCode,
                Setting = change.Key,
                Value = change.Value ?? string.Empty
            }));
        overlaidSettings = rows;
    }

    public override T GetSetting<T>(string name, T defaultValue = default!)
    {
        var value = new SettingsResolver().Resolve(
            overlaidSettings,
            name,
            OrganizationId,
            LibraryId,
            FormCode,
            SystemOrganizationId).EffectiveValue;
        if (typeof(T) == typeof(string) && SafeHtmlPolicy.IsHtmlExecutionContext(name))
        {
            value = SafeHtmlPolicy.SanitizeIfHtml(name, value);
        }
        return ConvertToType(value, defaultValue);
    }

    public override List<string> GetRequiredFields()
    {
        return overlaidSettings
            .Where(setting => setting.Setting.StartsWith("require.", StringComparison.OrdinalIgnoreCase))
            .Select(setting => setting.Setting)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(key => GetSetting(key, false))
            .Select(key => key["require.".Length..])
            .ToList();
    }
}
