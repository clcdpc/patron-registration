using System.Globalization;
using System.Text.RegularExpressions;
using Clc.PatronRegistration.Administration;
using Clc.PatronRegistration.Web.Settings;

namespace Clc.PatronRegistration.Web.Models;

public sealed class SettingsAuditViewModel
{
    public string SearchText { get; init; } = string.Empty;
    public bool IsGlobalAdministrator { get; init; }
    public IReadOnlyList<SettingsAuditEventViewModel> Events { get; init; } = [];
    public int ResultCount => Events.Count;
}

public sealed record AuditTechnicalDetail(string Label, string Value);

public sealed class SettingsAuditEventViewModel
{
    public long AuditEventId { get; init; }
    public DateTime TimestampUtc { get; init; }
    public string TimestampDisplay { get; init; } = string.Empty;
    public string TimestampDateTime { get; init; } = string.Empty;
    public string StaffMember { get; init; } = string.Empty;
    public string Activity { get; init; } = string.Empty;
    public string RawEventType { get; init; } = string.Empty;
    public string Target { get; init; } = string.Empty;
    public string Organization { get; init; } = string.Empty;
    public string Form { get; init; } = string.Empty;
    public string? Setting { get; init; }
    public string? PreviousValue { get; init; }
    public string? NewValue { get; init; }
    public bool Succeeded { get; init; }
    public string Result => Succeeded ? "Success" : "Failed";
    public string? FailureReason { get; init; }
    public IReadOnlyList<AuditTechnicalDetail> TechnicalDetails { get; init; } = [];
}

public static partial class SettingsAuditPresenter
{
    private static readonly IReadOnlyDictionary<string, string> ActivityLabels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DirectSave"] = "Saved settings",
            ["DraftCreated"] = "Created shared draft",
            ["DraftEdited"] = "Updated shared draft",
            ["DraftChangeRemoved"] = "Removed shared draft change",
            ["DraftCommitted"] = "Published shared draft",
            ["DraftDiscarded"] = "Discarded shared draft",
            ["DraftCommitConflict"] = "Shared draft publish conflicted",
            ["PreviewLinkCreated"] = "Created preview link",
            ["PreviewLinkRevoked"] = "Revoked preview link",
            ["PreviewLinkRevocationFailed"] = "Preview-link revocation failed",
            ["PreviewLinkModeReplaced"] = "Changed preview-link mode",
            ["PreviewLinksRevokedForRestrictedDraft"] = "Revoked restricted draft preview links",
            ["FormCodeCreated"] = "Created named form",
            ["FormCodeMetadataUpdated"] = "Updated named form",
            ["FormCodeUpdated"] = "Updated named form",
            ["FormCodeDeleted"] = "Deleted named form",
            ["ValidationFailed"] = "Change rejected",
            ["ConcurrencyConflict"] = "Change conflicted",
            ["AuthorizationRejected"] = "Unauthorized change rejected",
            ["RestrictedDraftOperationRejected"] = "Restricted draft change rejected",
            ["PreviewAccess"] = "Opened preview",
            ["SafePreviewSubmissionBlocked"] = "Blocked preview submission",
            ["LivePreviewSubmission"] = "Submitted preview registration"
        };

    public static string PresentActivity(string? eventType)
    {
        if (!string.IsNullOrWhiteSpace(eventType) && ActivityLabels.TryGetValue(eventType, out var label)) return label;
        if (string.IsNullOrWhiteSpace(eventType)) return "Unknown activity";
        var words = PascalCaseBoundary().Replace(eventType.Trim(), " ");
        return char.ToUpperInvariant(words[0]) + words[1..].ToLowerInvariant();
    }

    public static SettingsAuditEventViewModel Present(SettingsAuditRow row, bool isGlobal,
        int systemOrganizationId, Func<int, string?> organizationName,
        IReadOnlyDictionary<string, string> formNames, IReadOnlyDictionary<string, SettingDefinition> catalog)
    {
        var utc = row.TimestampUtc.Kind == DateTimeKind.Utc ? row.TimestampUtc : DateTime.SpecifyKind(row.TimestampUtc, DateTimeKind.Utc);
        var organization = row.TargetOrganizationId == systemOrganizationId ? "System defaults" :
            organizationName(row.TargetOrganizationId) ?? $"Organization {row.TargetOrganizationId}";
        var form = string.IsNullOrEmpty(row.FormCode) ? "Default form" :
            formNames.TryGetValue(row.FormCode, out var formName) ? formName : row.FormCode;
        var setting = string.IsNullOrWhiteSpace(row.SettingKey) ? null :
            catalog.TryGetValue(row.SettingKey, out var definition) ? definition.DisplayName : row.SettingKey;
        var technical = new List<AuditTechnicalDetail>();
        if (isGlobal)
        {
            technical.Add(new("Audit event ID", row.AuditEventId.ToString(CultureInfo.InvariantCulture)));
            Add(technical, "Raw event type", row.EventType);
            Add(technical, "Raw setting key", row.SettingKey);
            Add(technical, "Correlation ID", row.CorrelationId);
            Add(technical, "IP address", row.IpAddress);
        }
        return new SettingsAuditEventViewModel
        {
            AuditEventId = row.AuditEventId, TimestampUtc = utc,
            TimestampDisplay = utc.ToString("MMM d, yyyy, h:mm tt 'UTC'", CultureInfo.InvariantCulture),
            TimestampDateTime = utc.ToString("o", CultureInfo.InvariantCulture),
            StaffMember = string.IsNullOrWhiteSpace(row.ActorName) ? "Unknown staff member" : row.ActorName,
            Activity = PresentActivity(row.EventType), RawEventType = row.EventType,
            Organization = organization, Form = form, Target = $"{organization} — {form}", Setting = setting,
            PreviousValue = row.PreviousValue, NewValue = row.NewValue, Succeeded = row.Succeeded,
            FailureReason = row.Succeeded ? null : string.IsNullOrWhiteSpace(row.FailureReason) ? "The operation did not complete." : row.FailureReason,
            TechnicalDetails = technical
        };
    }

    private static void Add(List<AuditTechnicalDetail> details, string label, string? value)
    { if (!string.IsNullOrWhiteSpace(value)) details.Add(new(label, value)); }

    [GeneratedRegex(@"(?<=[a-z0-9])(?=[A-Z])|[_-]+")]
    private static partial Regex PascalCaseBoundary();
}
