using Clc.PatronRegistration.Administration;

namespace Clc.PatronRegistration.Configuration;

/// <summary>
/// One immutable view of the settings rows for a published cache generation.
/// The rows are retained for dynamic-key enumeration and the indexed lookup is
/// built once when the generation is published.
/// </summary>
public sealed class SettingsResolverSnapshot
{
    private readonly Dictionary<int, Dictionary<string, Dictionary<string, RegistrationFormSetting>>> settingsByScope = [];
    private readonly SettingsResolverSnapshot? fallback;
    private readonly int? overlayOrganizationId;
    private readonly string? overlayFormCode;
    private readonly Dictionary<string, RegistrationFormSetting>? overlayValues;
    private readonly HashSet<string>? removedOverlayKeys;

    public SettingsResolverSnapshot(IReadOnlyList<RegistrationFormSetting> settings)
    {
        Settings = settings;
        var requiredKeys = new List<string>();
        var requiredKeySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in settings)
        {
            var formCode = FormCodeNormalizer.Normalize(row.FormCode);
            if (!settingsByScope.TryGetValue(row.OrganizationID, out var forms))
            {
                forms = new Dictionary<string, Dictionary<string, RegistrationFormSetting>>(StringComparer.OrdinalIgnoreCase);
                settingsByScope.Add(row.OrganizationID, forms);
            }

            if (!forms.TryGetValue(formCode, out var keys))
            {
                keys = new Dictionary<string, RegistrationFormSetting>(StringComparer.OrdinalIgnoreCase);
                forms.Add(formCode, keys);
            }

            // SQL enforces one row per scope/key. Preserve the old
            // FirstOrDefault behavior for compatibility with test and legacy
            // providers that may expose duplicate rows.
            keys.TryAdd(row.Setting, row);

            if (row.Setting.StartsWith("require.", StringComparison.OrdinalIgnoreCase) &&
                requiredKeySet.Add(row.Setting))
            {
                requiredKeys.Add(row.Setting);
            }
        }

        RequiredKeys = Array.AsReadOnly(requiredKeys.ToArray());
    }

    private SettingsResolverSnapshot(
        SettingsResolverSnapshot fallback,
        int organizationId,
        string formCode,
        Dictionary<string, RegistrationFormSetting> overlayValues,
        HashSet<string> removedOverlayKeys,
        IReadOnlyList<string> requiredKeys)
    {
        settingsByScope = [];
        Settings = fallback.Settings;
        RequiredKeys = Array.AsReadOnly(requiredKeys.ToArray());
        this.fallback = fallback;
        overlayOrganizationId = organizationId;
        overlayFormCode = formCode;
        this.overlayValues = overlayValues;
        this.removedOverlayKeys = removedOverlayKeys;
    }

    public IReadOnlyList<RegistrationFormSetting> Settings { get; }

    /// <summary>Distinct persisted <c>require.*</c> keys in this generation.</summary>
    public IReadOnlyList<string> RequiredKeys { get; }

    public static SettingsResolverSnapshot CreateOverlay(
        SettingsResolverSnapshot baseline,
        int organizationId,
        string? formCode,
        IReadOnlyList<SettingMutation> changes)
    {
        var normalizedFormCode = FormCodeNormalizer.Normalize(formCode);
        var values = new Dictionary<string, RegistrationFormSetting>(StringComparer.OrdinalIgnoreCase);
        var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes)
        {
            if (change.Operation == DraftOperation.Upsert)
            {
                values.TryAdd(change.Key, new RegistrationFormSetting
                {
                    OrganizationID = organizationId,
                    FormCode = normalizedFormCode,
                    Setting = change.Key,
                    Value = change.Value ?? string.Empty
                });
            }
            else if (change.Operation == DraftOperation.RemoveOverride)
            {
                removed.Add(change.Key);
            }
        }

        var requiredKeys = baseline.RequiredKeys.ToList();
        var requiredKeySet = new HashSet<string>(requiredKeys, StringComparer.OrdinalIgnoreCase);
        foreach (var key in values.Keys.Where(key => key.StartsWith("require.", StringComparison.OrdinalIgnoreCase)))
        {
            if (requiredKeySet.Add(key))
            {
                requiredKeys.Add(key);
            }
        }

        return new SettingsResolverSnapshot(
            baseline, organizationId, normalizedFormCode, values, removed, requiredKeys);
    }

    public bool TryGet(int organizationId, string? formCode, string key, out RegistrationFormSetting row)
    {
        row = null!;
        if (overlayValues is not null && organizationId == overlayOrganizationId &&
            string.Equals(FormCodeNormalizer.Normalize(formCode), overlayFormCode, StringComparison.OrdinalIgnoreCase))
        {
            if (overlayValues.TryGetValue(key, out row!))
            {
                return true;
            }
            if (removedOverlayKeys!.Contains(key))
            {
                return false;
            }
        }

        if (fallback is not null)
        {
            return fallback.TryGet(organizationId, formCode, key, out row);
        }

        return settingsByScope.TryGetValue(organizationId, out var forms) &&
            forms.TryGetValue(FormCodeNormalizer.Normalize(formCode), out var keys) &&
            keys.TryGetValue(key, out row!);
    }
}

/// <summary>Resolves settings using the documented branch/library/system precedence.</summary>
public sealed class SettingsResolver
{
    public ResolvedSetting Resolve(SettingsResolverSnapshot settings, string key,
        int organizationId, int libraryId, string? formCode, int systemOrganizationId,
        ISet<(int OrganizationId, string FormCode, string Key)>? removed = null)
    {
        formCode = FormCodeNormalizer.Normalize(formCode);
        var candidates = BuildPrecedence(organizationId, libraryId, systemOrganizationId, formCode);
        foreach (var candidate in candidates)
        {
            if (IsRemoved(removed, candidate, key)) continue;
            if (settings.TryGet(candidate.OrganizationId, candidate.FormCode, key, out var row))
            {
                var owns = row.OrganizationID == organizationId && string.Equals(row.FormCode, formCode, StringComparison.OrdinalIgnoreCase);
                return new(key, row.Value, row.OrganizationID, candidate.SourceType, row.FormCode ?? string.Empty,
                    owns, owns ? row.Value : null, !owns);
            }
        }
        return new(key, null, null, "Unconfigured", string.Empty, false, null, true);
    }

    public ResolvedSetting Resolve(IEnumerable<RegistrationFormSetting> settings, string key,
        int organizationId, int libraryId, string? formCode, int systemOrganizationId,
        ISet<(int OrganizationId, string FormCode, string Key)>? removed = null)
        => Resolve(new SettingsResolverSnapshot(settings.ToArray()), key, organizationId, libraryId, formCode, systemOrganizationId, removed);

    private static bool IsRemoved(ISet<(int OrganizationId, string FormCode, string Key)>? removed,
        SettingSource candidate, string key)
    {
        if (removed is null)
        {
            return false;
        }

        return removed.Contains((candidate.OrganizationId, candidate.FormCode, key)) ||
            removed.Any(item => item.OrganizationId == candidate.OrganizationId &&
                string.Equals(item.FormCode, candidate.FormCode, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<SettingSource> BuildPrecedence(int organizationId, int libraryId,
        int systemOrganizationId, string? formCode)
    {
        formCode ??= string.Empty;
        var result = new List<SettingSource>();
        void Add(int id, string code, string type)
        {
            if (!result.Any(x => x.OrganizationId == id && string.Equals(x.FormCode, code, StringComparison.OrdinalIgnoreCase)))
                result.Add(new(id, code, type));
        }
        var branch = organizationId != libraryId && organizationId != systemOrganizationId;
        if (branch && formCode.Length > 0) Add(organizationId, formCode, "Branch");
        if (branch) Add(organizationId, string.Empty, "Branch");
        if (libraryId != systemOrganizationId && formCode.Length > 0) Add(libraryId, formCode, "Library");
        if (libraryId != systemOrganizationId) Add(libraryId, string.Empty, "Library");
        if (formCode.Length > 0) Add(systemOrganizationId, formCode, "System");
        Add(systemOrganizationId, string.Empty, "System");
        return result;
    }
}

public sealed record SettingSource(int OrganizationId, string FormCode, string SourceType);
public sealed record ResolvedSetting(string Key, string? EffectiveValue, int? SourceOrganizationId,
    string SourceOrganizationType, string SourceFormCode, bool OwnsOverride, string? CurrentOverrideValue,
    bool IsInherited);
