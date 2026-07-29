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

public static class DraftChangeAuditClassification
{
    public static SettingDefinition RequireDefinition(
        string settingKey,
        IReadOnlyDictionary<string, SettingDefinition> catalog) =>
        catalog.TryGetValue(settingKey, out var definition)
            ? definition
            : throw new InvalidOperationException("The staged setting is not recognized.");
}
