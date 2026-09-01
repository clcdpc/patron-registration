using Clc.PatronRegistration.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Clc.PatronRegistration.Administration;

public static class SettingsSnapshotSerializer
{
    public static string Serialize(ISettingProvider provider, ISettingCatalog? catalog = null)
    {
        catalog ??= new SettingCatalog();
        var sensitiveNames = catalog.All
            .Where(setting => setting.IsSensitive)
            .Select(setting => Normalize(setting.Key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return JsonConvert.SerializeObject(provider, new JsonSerializerSettings
        {
            ContractResolver = new SensitiveSettingContractResolver(sensitiveNames)
        });
    }

    private static string Normalize(string value) => value.Replace("_", string.Empty, StringComparison.Ordinal);

    private sealed class SensitiveSettingContractResolver(IReadOnlySet<string> sensitiveNames) : DefaultContractResolver
    {
        protected override JsonProperty CreateProperty(System.Reflection.MemberInfo member, MemberSerialization memberSerialization)
        {
            var property = base.CreateProperty(member, memberSerialization);
            if (sensitiveNames.Contains(Normalize(property.PropertyName ?? member.Name)))
            {
                property.Ignored = true;
            }
            return property;
        }
    }
}
