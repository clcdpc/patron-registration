using System.Reflection;
using System.Text.Json;

namespace Clc.PatronRegistration.Administration;

/// <summary>
/// Opts an <see cref="Configuration.ISettingProvider"/> property into settings administration.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class AdminSettingAttribute : Attribute
{
    private bool allowEmpty = true;
    private SettingValueType valueType = (SettingValueType)(-1);

    public AdminSettingAttribute(SettingCategory category, string displayName, string description)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException("An administration setting display name is required.", nameof(displayName));
        }
        if (string.IsNullOrWhiteSpace(description))
        {
            throw new ArgumentException("An administration setting description is required.", nameof(description));
        }

        Category = category;
        DisplayName = displayName;
        Description = description;
    }

    public SettingCategory Category { get; }
    public string DisplayName { get; }
    public string Description { get; }

    /// <summary>Overrides the snake_case database key inferred from the property name.</summary>
    public string? Key { get; set; }

    /// <summary>Overrides the value type inferred from the property CLR type.</summary>
    public SettingValueType ValueType
    {
        get => valueType;
        set
        {
            valueType = value;
            HasValueTypeOverride = true;
        }
    }

    internal bool HasValueTypeOverride { get; private set; }

    public bool IsSensitive { get; set; }

    /// <summary>Allowed values for an enumeration setting.</summary>
    public string[]? AllowedValues { get; set; }

    /// <summary>
    /// Controls whether an explicitly empty value is valid. If this named argument is omitted,
    /// the catalog derives the default from the resolved value type.
    /// </summary>
    public bool AllowEmpty
    {
        get => allowEmpty;
        set
        {
            allowEmpty = value;
            HasAllowEmptyOverride = true;
        }
    }

    internal bool HasAllowEmptyOverride { get; private set; }
}

internal sealed record SettingPropertyMetadata(PropertyInfo Property, AdminSettingAttribute? Administration, string DatabaseKey);

/// <summary>
/// Cached metadata for the fixed <see cref="Configuration.ISettingProvider"/> setting contract.
/// </summary>
internal static class SettingPropertyMetadataCache
{
    private static readonly IReadOnlyDictionary<string, SettingPropertyMetadata> Metadata = Build();
    private static readonly IReadOnlyList<SettingPropertyMetadata> MetadataList = Metadata.Values.ToArray();

    public static SettingPropertyMetadata Get(string propertyName)
    {
        if (!Metadata.TryGetValue(propertyName, out var metadata))
        {
            throw new InvalidOperationException(
                $"The ISettingProvider contract has no readable property named '{propertyName}'.");
        }

        return metadata;
    }

    public static IReadOnlyList<SettingPropertyMetadata> GetAll() => MetadataList;

    public static string InferDatabaseKey(string propertyName) =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(propertyName);

    private static IReadOnlyDictionary<string, SettingPropertyMetadata> Build()
    {
        var properties = new Dictionary<string, SettingPropertyMetadata>(StringComparer.Ordinal);
        foreach (var property in typeof(Configuration.ISettingProvider).GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.GetMethod is null || property.GetIndexParameters().Length != 0)
            {
                continue;
            }

            var administration = property.GetCustomAttribute<AdminSettingAttribute>(inherit: false);
            var databaseKey = administration?.Key is { } explicitKey
                ? ValidateExplicitKey(property, explicitKey)
                : InferDatabaseKey(property.Name);
            properties[property.Name] = new(property, administration, databaseKey);
        }

        return properties;
    }

    private static string ValidateExplicitKey(PropertyInfo property, string explicitKey)
    {
        if (string.IsNullOrWhiteSpace(explicitKey))
        {
            throw new InvalidOperationException(
                $"Administration setting property 'ISettingProvider.{property.Name}' specifies an empty database key.");
        }

        return explicitKey;
    }
}
