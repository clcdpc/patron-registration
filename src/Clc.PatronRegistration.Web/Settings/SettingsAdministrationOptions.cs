namespace Clc.PatronRegistration.Web.Settings;

public sealed class SettingsAdministrationOptions
{
    public const string SectionName = "SettingsAdministration";
    public string RequiredRole { get; set; } = "Clc.CardReg.ManageSettings";
    public int GlobalOrganizationId { get; set; } = -1;
    public int SystemOrganizationId { get; set; } = 1;
    public int GenerationCheckSeconds { get; set; } = 30;
}
