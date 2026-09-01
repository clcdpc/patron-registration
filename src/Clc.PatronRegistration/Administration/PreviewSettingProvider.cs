using Clc.PatronRegistration.Configuration;
using Clc.PatronRegistration.Helpers;

namespace Clc.PatronRegistration.Administration;

/// <summary>Provides the normal setting surface with an active draft overlaid at its selected scope.</summary>
public sealed class PreviewSettingProvider : DbSettingProvider
{
    private readonly SettingsResolverSnapshot overlaidSettings;

    public PreviewSettingProvider(SettingDraft draft, int operationalBranchId, ICache cache, int systemOrganizationId)
        : this(draft, operationalBranchId, cache, CacheSnapshot.Capture(cache), systemOrganizationId)
    {
    }

    public PreviewSettingProvider(SettingDraft draft, int operationalBranchId, ICache cache, CacheSnapshot snapshot, int systemOrganizationId)
        : base(operationalBranchId, cache, snapshot, draft.FormCode, systemOrganizationId)
    {
        overlaidSettings = SettingsResolverSnapshot.CreateOverlay(
            SettingsSnapshot, draft.OrganizationId, draft.FormCode, draft.Changes);
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
        return overlaidSettings.RequiredKeys
            .Where(key => GetSetting(key, false))
            .Select(key => key["require.".Length..])
            .ToList();
    }
}
