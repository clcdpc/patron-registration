namespace Clc.PatronRegistration.Administration;

public static class SensitiveDraftPolicy
{
    public static bool ContainsSensitiveChange(
        IEnumerable<string> settingKeys,
        IReadOnlyDictionary<string, SettingDefinition> catalog) =>
        settingKeys.Any(key => catalog.TryGetValue(key, out var definition) && definition.IsSensitive);

    public static bool BecameSensitive(
        IEnumerable<string> previousKeys,
        IEnumerable<string> currentKeys,
        IReadOnlyDictionary<string, SettingDefinition> catalog) =>
        !ContainsSensitiveChange(previousKeys, catalog) && ContainsSensitiveChange(currentKeys, catalog);
}
