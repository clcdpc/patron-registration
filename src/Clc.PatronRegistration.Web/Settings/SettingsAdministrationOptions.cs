namespace Clc.PatronRegistration.Web.Settings;

public sealed class SettingsAdministrationOptions
{
    public const int MaximumPreviewLinkLifetimeHours = 168;
    public const string SectionName = "SettingsAdministration";
    public string RequiredRole { get; set; } = "Clc.CardReg.ManageSettings";
    public int GlobalOrganizationId { get; set; } = -1;
    public int SystemOrganizationId { get; set; } = 1;
    public int GenerationCheckSeconds { get; set; } = 30;
    public int PreviewLinkLifetimeHours { get; set; } = 24;
    public int AssetOrphanGracePeriodHours { get; set; } = 48;
    public int AssetOrphanCleanupIntervalHours { get; set; } = 6;
    public int AssetOrphanCleanupBatchSize { get; set; } = 100;

    public static bool IsValidPreviewLinkLifetime(int hours) =>
        hours is > 0 and <= MaximumPreviewLinkLifetimeHours;
}
