using Ganss.Xss;

namespace Clc.PatronRegistration.Administration;

/// <summary>
/// The only HTML policy used for administrator-controlled content that reaches
/// a browser or an HTML email. It is deliberately an allowlist: formatting
/// elements and ordinary links/images are retained, while every active-content
/// element, executable URL scheme, event handler, style attribute, and DOM
/// clobbering identifier is removed.
/// </summary>
public static class SafeHtmlPolicy
{
    private static readonly string[] AllowedTags =
    [
        "a", "abbr", "b", "blockquote", "br", "caption", "cite", "code", "col", "colgroup",
        "dd", "del", "div", "dl", "dt", "em", "h1", "h2", "h3", "h4", "h5", "h6",
        "hr", "i", "img", "ins", "kbd", "li", "mark", "ol", "p", "pre", "q", "s",
        "small", "span", "strong", "sub", "sup", "table", "tbody", "td", "tfoot", "th",
        "thead", "tr", "u", "ul", "var"
    ];

    private static readonly string[] AllowedAttributes =
    [
        "alt", "class", "colspan", "datetime", "height", "href", "loading", "rel", "rowspan",
        "scope", "src", "target", "title", "width"
    ];

    private static readonly string[] HtmlSettingKeys =
    [
        "warning_text", "custom_form_footer_html", "duplicate_patron_message_html", "registration_form_header",
        "age_block_text", "mailing_list_description_html", "sms_notice_information_html",
        "registration_text", "kiosk_registration_text", "ecard_registration_text",
        "valid_address_registration_text", "valid_address_plus_name_registration_text", "out_of_state_block_message",
        "ecard_welcome_email_template_html", "welcome_email_template_html", "responsible_person_disclaimer"
    ];

    public static bool IsHtmlExecutionContext(SettingDefinition definition) =>
        definition.ValueType == SettingValueType.Html ||
        definition.IsHtmlExecutionContext ||
        HtmlSettingKeys.Contains(definition.Key, StringComparer.OrdinalIgnoreCase);

    public static bool IsHtmlExecutionContext(string databaseKey) =>
        HtmlSettingKeys.Contains(databaseKey, StringComparer.OrdinalIgnoreCase) ||
        SettingPropertyMetadataCache.IsHtmlExecutionContext(databaseKey);

    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        var sanitizer = new HtmlSanitizer();
        sanitizer.AllowedTags.Clear();
        foreach (var tag in AllowedTags)
        {
            sanitizer.AllowedTags.Add(tag);
        }

        sanitizer.AllowedAttributes.Clear();
        foreach (var attribute in AllowedAttributes)
        {
            sanitizer.AllowedAttributes.Add(attribute);
        }

        sanitizer.AllowedSchemes.Clear();
        sanitizer.AllowedSchemes.Add("http");
        sanitizer.AllowedSchemes.Add("https");
        sanitizer.AllowedSchemes.Add("mailto");
        return sanitizer.Sanitize(value);
    }

    public static string SanitizeForSetting(SettingDefinition definition, string? value) =>
        IsHtmlExecutionContext(definition) ? Sanitize(value) : value ?? string.Empty;

    public static SettingMutation SanitizeMutation(
        SettingMutation mutation,
        IReadOnlyDictionary<string, SettingDefinition> catalog)
    {
        if (mutation.Operation != DraftOperation.Upsert ||
            !catalog.TryGetValue(mutation.Key, out var definition))
        {
            return mutation;
        }

        return mutation with { Value = SanitizeForSetting(definition, mutation.Value) };
    }

    public static IReadOnlyList<SettingMutation> SanitizeMutations(
        IEnumerable<SettingMutation> mutations,
        IReadOnlyDictionary<string, SettingDefinition> catalog) =>
        mutations.Select(mutation => SanitizeMutation(mutation, catalog)).ToList();

    public static string SanitizeIfHtml(string settingKey, string? value) =>
        IsHtmlExecutionContext(settingKey) ? Sanitize(value) : value ?? string.Empty;

    public static bool IsSafeStylesheetReference(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) || value.Contains('\\'))
        {
            return false;
        }

        var candidate = value.Trim();
        if (candidate.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        var isHttpUrl = candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
        var isHttpsUrl = candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        if (isHttpUrl || isHttpsUrl)
        {
            return Uri.TryCreate(candidate, UriKind.Absolute, out var absolute) &&
                absolute.Scheme is "http" or "https" &&
                !string.IsNullOrEmpty(absolute.Host);
        }

        // A local reference is classified lexically so root-relative paths are
        // not reinterpreted as file: URIs on Unix. Colons are not needed in the
        // supported local path forms and rejecting them also rejects every
        // executable or otherwise unsupported URI scheme.
        return candidate.IndexOf(':') < 0;
    }
}
