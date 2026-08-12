using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;

namespace Clc.PatronRegistration.Administration;

/// <summary>
/// Opts a <see cref="Configuration.DbSettingProvider"/> property into settings administration.
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
/// Shared, per-provider-type cache for property metadata used by both setting reads and catalog construction.
/// </summary>
internal static class SettingPropertyMetadataCache
{
    private static readonly ConcurrentDictionary<Type, IReadOnlyDictionary<string, SettingPropertyMetadata>> Cache = new();

    public static SettingPropertyMetadata Get(Type providerType, string propertyName)
    {
        var properties = Cache.GetOrAdd(providerType, Build);
        if (!properties.TryGetValue(propertyName, out var metadata))
        {
            throw new InvalidOperationException(
                $"The setting provider type '{providerType.FullName}' has no readable property named '{propertyName}'.");
        }

        return metadata;
    }

    public static IReadOnlyList<SettingPropertyMetadata> GetAll(Type providerType) =>
        Cache.GetOrAdd(providerType, Build).Values.ToArray();

    public static string InferDatabaseKey(string propertyName) =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(propertyName);

    private static IReadOnlyDictionary<string, SettingPropertyMetadata> Build(Type providerType)
    {
        var properties = new Dictionary<string, SettingPropertyMetadata>(StringComparer.Ordinal);
        foreach (var property in providerType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.GetMethod is null || property.GetIndexParameters().Length != 0)
            {
                continue;
            }

            var administration = property.GetCustomAttribute<AdminSettingAttribute>(inherit: false)
                ?? FindHiddenBaseAdministrationAttribute(property);
            var databaseKey = administration?.Key is { } explicitKey
                ? ValidateExplicitKey(providerType, property, explicitKey)
                : InferDatabaseKey(property.Name);
            properties[property.Name] = new(property, administration, databaseKey);
        }

        return properties;
    }

    private static AdminSettingAttribute? FindHiddenBaseAdministrationAttribute(PropertyInfo property)
    {
        for (var baseType = property.DeclaringType?.BaseType;
             baseType is not null;
             baseType = baseType.BaseType)
        {
            var baseProperty = baseType.GetProperty(
                property.Name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            var administration = baseProperty?.GetCustomAttribute<AdminSettingAttribute>(inherit: false);
            if (administration is not null)
            {
                return administration;
            }
        }

        return null;
    }

    private static string ValidateExplicitKey(Type providerType, PropertyInfo property, string explicitKey)
    {
        if (string.IsNullOrWhiteSpace(explicitKey))
        {
            throw new InvalidOperationException(
                $"Administration setting property '{providerType.FullName}.{property.Name}' specifies an empty database key.");
        }

        return explicitKey;
    }
}
