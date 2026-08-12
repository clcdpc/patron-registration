using System.Globalization;
using System.Net.Mail;
using System.Text.RegularExpressions;
using Clc.PatronRegistration.Configuration;

namespace Clc.PatronRegistration.Administration;

public enum SettingValueType
{
    Boolean,
    Integer,
    NullableInteger,
    Decimal,
    Date,
    NullableDate,
    ShortString,
    LongString,
    Html,
    EmailTemplate,
    EmailAddress,
    Uri,
    Enumeration
}

public enum SettingGroup
{
    Ordinary,
    Alert,
    Label,
    Require
}

public enum SettingCategory
{
    PageAppearanceAndInstructions,
    FormBehaviorAndFields,
    BranchAndPatronDefaults,
    ECardRegistration,
    EmailAndNotices,
    DuplicateChecking,
    AddressVerification,
    PolarisIntegrationAndRecordSets,
    KioskAndSessionBehavior
}

public static class SettingCategoryPresentation
{
    public static IReadOnlyList<SettingCategory> Ordered { get; } = Enum.GetValues<SettingCategory>();
    public static string DisplayName(this SettingCategory category) => category switch
    {
        SettingCategory.PageAppearanceAndInstructions => "Page content and appearance",
        SettingCategory.FormBehaviorAndFields => "Form fields and behavior",
        SettingCategory.BranchAndPatronDefaults => "Branch selection and patron defaults",
        SettingCategory.ECardRegistration => "E-card registration",
        SettingCategory.EmailAndNotices => "Email and communications",
        SettingCategory.DuplicateChecking => "Duplicate detection and workarounds",
        SettingCategory.AddressVerification => "Address verification",
        SettingCategory.PolarisIntegrationAndRecordSets => "Polaris patron creation and follow-up",
        SettingCategory.KioskAndSessionBehavior => "Kiosk and on-site behavior",
        _ => throw new ArgumentOutOfRangeException(nameof(category))
    };
}

public sealed record SettingDefinition(string Key, string DisplayName, string Description, SettingValueType ValueType,
    SettingGroup Group = SettingGroup.Ordinary, bool IsSensitive = false, bool AllowEmpty = true,
    IReadOnlyList<string>? AllowedValues = null, int SortOrder = 0, SettingCategory? Category = null)
{
    public const int MaximumExpirationDateYears = 100;
    private static readonly HashSet<string> PositiveIdentifierKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "add_to_record_set_id",
        "registration_logon_user_id",
        "ecard_patron_code_id",
        "teacher_patron_code_id",
        "student_patron_code_id",
        "valid_address_patron_code_id",
        "valid_address_plus_name_patron_code_id",
        "patron_code_id"
    };

    private static readonly HashSet<string> NonNegativeIdentifierKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "mailing_list_record_set_id",
        "valid_address_record_set_id",
        "valid_address_plus_name_record_set_id",
        "invalid_address_record_set_id"
    };

    public string? Validate(string? value)
    {
        if (value is null)
        {
            return "Enter a value for this setting.";
        }
        if (value.Length == 0)
        {
            return AllowEmpty && !IsSensitive
                ? null
                : "An empty value is not valid for this setting. Choose “Use inherited value” instead.";
        }
        if (Group == SettingGroup.Label)
        {
            if (value.Length > 200)
            {
                return "Labels cannot exceed 200 characters.";
            }
            if (value.Contains('<') || value.Contains('>') || value.Any(char.IsControl) ||
                Regex.IsMatch(value, @"\b(?:on[a-z]+|style|src|href)\s*=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                return "Labels must be single-line plain text without markup or control characters.";
            }
        }
        return ValueType switch
        {
            SettingValueType.NullableInteger when Key.Equals("expiration_date_years", StringComparison.OrdinalIgnoreCase) &&
                (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var expirationYears) ||
                 expirationYears < 0 || expirationYears > MaximumExpirationDateYears) =>
                $"Enter a whole number from 0 through {MaximumExpirationDateYears}, or leave empty.",
            SettingValueType.Boolean when !bool.TryParse(value, out _) => "Enter true or false.",
            SettingValueType.Integer when !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) => "Enter a whole number.",
            SettingValueType.NullableInteger when value.Length > 0 && !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) => "Enter a whole number or leave empty.",
            SettingValueType.Integer or SettingValueType.NullableInteger
                when PositiveIdentifierKeys.Contains(Key) && int.Parse(value, CultureInfo.InvariantCulture) <= 0 =>
                "Configured identifier must be a positive whole number.",
            SettingValueType.Integer
                when NonNegativeIdentifierKeys.Contains(Key) && int.Parse(value, CultureInfo.InvariantCulture) < 0 =>
                "Record-set ID cannot be negative; use zero to disable it.",
            SettingValueType.Decimal when !decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _) => "Enter a decimal number.",
            SettingValueType.Date or SettingValueType.NullableDate when value.Length > 0 && !DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) => "Enter a date as yyyy-MM-dd.",
            SettingValueType.EmailAddress when !MailAddress.TryCreate(value, out _) => "Enter a valid email address.",
            SettingValueType.Uri when !System.Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http") => "Enter an absolute HTTP or HTTPS URL.",
            SettingValueType.Enumeration when AllowedValues?.Contains(value, StringComparer.OrdinalIgnoreCase) != true => "Select a recognized value.",
            _ when value.Length > 100_000 => "Value is too long.",
            _ => null
        };
    }
}

public interface ISettingCatalog
{
    IReadOnlyList<SettingDefinition> All { get; }
    bool TryGet(string key, out SettingDefinition definition);
    IReadOnlyList<string> DynamicFieldSuffixes { get; }
    IReadOnlyList<string> LabelFieldSuffixes { get; }
    IReadOnlyList<string> RequiredFieldSuffixes { get; }
}

public sealed class SettingCatalog : ISettingCatalog
{
    public IReadOnlyList<string> DynamicFieldSuffixes { get; } =
    [
        "PatronBranchID", "NameFirst", "NameMiddle", "NameLast", "UseLegalName",
        "LegalNameFirst", "LegalNameMiddle", "LegalNameLast", "Birthdate", "DeliveryOptionId",
        "PhoneVoice1", "PhoneVoice2", "ReceiveEreceipts", "EmailAddress", "AltEmailAddress",
        "StreetOne", "StreetTwo", "City", "State", "PostalCode", "Password", "Password2",
        "RequestPickupBranchID", "User1", "User5", "DeliverCardToSchool", "IsStudent",
        "IsTeacher", "IsECard", "AddToMailingList"
    ];
    public static IReadOnlyDictionary<string, string> FieldNames { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PatronBranchID"] = "Home branch", ["NameFirst"] = "First name",
            ["NameMiddle"] = "Middle name", ["NameLast"] = "Last name",
            ["UseLegalName"] = "Legal name option", ["LegalNameFirst"] = "Legal first name",
            ["LegalNameMiddle"] = "Legal middle name", ["LegalNameLast"] = "Legal last name",
            ["Birthdate"] = "Birth date", ["DeliveryOptionId"] = "Notification method",
            ["PhoneVoice1"] = "Primary phone number", ["PhoneVoice2"] = "Secondary phone number",
            ["ReceiveEreceipts"] = "E-receipts", ["EmailAddress"] = "Email address",
            ["AltEmailAddress"] = "Alternate email address", ["StreetOne"] = "Address line 1",
            ["StreetTwo"] = "Address line 2", ["City"] = "City", ["State"] = "State",
            ["PostalCode"] = "ZIP code", ["Password"] = "PIN", ["Password2"] = "Confirm PIN",
            ["RequestPickupBranchID"] = "Preferred pickup location", ["User1"] = "School",
            ["User5"] = "Responsible person", ["DeliverCardToSchool"] = "Deliver card to school",
            ["IsStudent"] = "Student", ["IsTeacher"] = "Teacher", ["IsECard"] = "E-card option",
            ["AddToMailingList"] = "Mailing-list option"
        };
    public IReadOnlyList<string> LabelFieldSuffixes { get; } =
    [
        "PatronBranchID", "NameFirst", "NameMiddle", "NameLast", "UseLegalName",
        "LegalNameFirst", "LegalNameMiddle", "LegalNameLast", "Birthdate", "DeliveryOptionId",
        "PhoneVoice1", "PhoneVoice2", "ReceiveEreceipts", "EmailAddress", "StreetOne", "StreetTwo",
        "City", "State", "User5", "PostalCode", "Password", "Password2", "RequestPickupBranchID",
        "User1", "DeliverCardToSchool", "IsStudent", "IsTeacher", "IsECard", "AddToMailingList"
    ];
    public IReadOnlyList<string> RequiredFieldSuffixes { get; } =
    [
        "PhoneVoice1", "EmailAddress", "User5"
    ];
    public IReadOnlyList<SettingDefinition> All { get; }
    private readonly Dictionary<string, SettingDefinition> byKey;

    public SettingCatalog() : this(typeof(DbSettingProvider))
    {
    }

    /// <summary>
    /// Builds the ordinary catalog from explicitly attributed properties on a setting provider.
    /// The provider-type overload keeps catalog construction testable without changing the runtime source of truth.
    /// </summary>
    public SettingCatalog(Type providerType)
    {
        if (!typeof(DbSettingProvider).IsAssignableFrom(providerType))
        {
            throw new ArgumentException(
                $"The setting catalog provider type must derive from {typeof(DbSettingProvider).FullName}.",
                nameof(providerType));
        }

        var ordinaryMetadata = SettingPropertyMetadataCache.GetAll(providerType)
            .Where(metadata => metadata.Administration is not null)
            .OrderBy(metadata => metadata.Property.MetadataToken)
            .ToList();
        EnsureUniqueOrdinaryKeys(ordinaryMetadata, providerType);

        var list = ordinaryMetadata.Select((metadata, i) =>
        {
            var attribute = metadata.Administration!;
            var type = attribute.HasValueTypeOverride
                ? attribute.ValueType
                : InferValueType(metadata.Property.PropertyType, providerType, metadata.Property);
            var allowEmpty = attribute.HasAllowEmptyOverride ? attribute.AllowEmpty : AllowsEmpty(type);
            return new SettingDefinition(metadata.DatabaseKey, attribute.DisplayName, attribute.Description, type,
                IsSensitive: attribute.IsSensitive, AllowEmpty: allowEmpty, SortOrder: i,
                Category: attribute.Category);
        }).ToList();
        foreach (var suffix in DynamicFieldSuffixes)
        {
            list.Add(new($"alert.{suffix}", FieldNames[suffix], $"Validation message for {FieldNames[suffix].ToLowerInvariant()}; reserved for future registration-form integration.", SettingValueType.LongString, SettingGroup.Alert));
        }
        foreach (var suffix in LabelFieldSuffixes)
        {
            list.Add(new($"label.{suffix}", FieldNames[suffix], $"Label displayed for {FieldNames[suffix].ToLowerInvariant()} on the registration form.", SettingValueType.ShortString, SettingGroup.Label));
        }
        foreach (var suffix in RequiredFieldSuffixes)
        {
            list.Add(new($"require.{suffix}", $"Require {FieldNames[suffix].ToLowerInvariant()}", $"Makes {FieldNames[suffix].ToLowerInvariant()} required on the registration form.", SettingValueType.Boolean, SettingGroup.Require, AllowEmpty: false));
        }
        var duplicate = list
            .GroupBy(setting => setting.Key, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"Duplicate settings catalog key '{duplicate.Key}'.");
        }

        All = list.OrderBy(x => x.Group).ThenBy(x => x.Category).ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        byKey = list.ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);
    }

    public bool TryGet(string key, out SettingDefinition definition) => byKey.TryGetValue(key, out definition!);

    private static void EnsureUniqueOrdinaryKeys(
        IReadOnlyList<SettingPropertyMetadata> metadata,
        Type providerType)
    {
        foreach (var duplicate in metadata
            .GroupBy(item => item.DatabaseKey, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1))
        {
            var properties = string.Join(", ", duplicate.Select(item => item.Property.Name));
            throw new InvalidOperationException(
                $"Duplicate administration setting database key '{duplicate.Key}' on {providerType.FullName}: {properties}.");
        }
    }

    private static SettingValueType InferValueType(Type propertyType, Type providerType, System.Reflection.PropertyInfo property)
    {
        if (propertyType == typeof(bool))
        {
            return SettingValueType.Boolean;
        }
        if (propertyType == typeof(int))
        {
            return SettingValueType.Integer;
        }
        if (propertyType == typeof(int?))
        {
            return SettingValueType.NullableInteger;
        }
        if (propertyType == typeof(decimal))
        {
            return SettingValueType.Decimal;
        }
        if (propertyType == typeof(DateTime))
        {
            return SettingValueType.Date;
        }
        if (propertyType == typeof(DateTime?))
        {
            return SettingValueType.NullableDate;
        }
        if (propertyType == typeof(string))
        {
            return SettingValueType.ShortString;
        }

        throw new InvalidOperationException(
            $"Administration setting property '{providerType.FullName}.{property.Name}' has unsupported CLR type '{propertyType.FullName}'. " +
            "Specify AdminSettingAttribute.ValueType explicitly.");
    }

    private static bool AllowsEmpty(SettingValueType type) => type is SettingValueType.ShortString
        or SettingValueType.LongString
        or SettingValueType.Html
        or SettingValueType.EmailTemplate
        or SettingValueType.EmailAddress
        or SettingValueType.Uri
        or SettingValueType.NullableInteger
        or SettingValueType.NullableDate;
}
