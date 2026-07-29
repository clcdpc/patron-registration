namespace Clc.PatronRegistration.Configuration;

/// <summary>Resolves settings using the documented branch/library/system precedence.</summary>
public sealed class SettingsResolver
{
    public ResolvedSetting Resolve(IEnumerable<RegistrationFormSetting> settings, string key,
        int organizationId, int libraryId, string? formCode, int systemOrganizationId,
        ISet<(int OrganizationId, string FormCode, string Key)>? removed = null)
    {
        formCode ??= string.Empty;
        var candidates = BuildPrecedence(organizationId, libraryId, systemOrganizationId, formCode);
        foreach (var candidate in candidates)
        {
            if (removed?.Contains((candidate.OrganizationId, candidate.FormCode, key)) == true) continue;
            var row = settings.FirstOrDefault(s => s.OrganizationID == candidate.OrganizationId
                && string.Equals(s.FormCode ?? string.Empty, candidate.FormCode, StringComparison.OrdinalIgnoreCase)
                && string.Equals(s.Setting, key, StringComparison.OrdinalIgnoreCase));
            if (row is not null)
            {
                var owns = row.OrganizationID == organizationId && string.Equals(row.FormCode, formCode, StringComparison.OrdinalIgnoreCase);
                return new(key, row.Value, row.OrganizationID, candidate.SourceType, row.FormCode ?? string.Empty,
                    owns, owns ? row.Value : null, !owns);
            }
        }
        return new(key, null, null, "Unconfigured", string.Empty, false, null, true);
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
