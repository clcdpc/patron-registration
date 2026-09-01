namespace Clc.PatronRegistration.Web.Settings;

/// <summary>Identifies the default-form settings used to brand a settings-administration page.</summary>
public sealed record SettingsPageBrandingContext(int OrganizationId, int LibraryId);

public interface ISettingsPageBrandingContextAccessor
{
    SettingsPageBrandingContext? Current { get; }
    void Set(int organizationId, int libraryId);
}

/// <remarks>This accessor is registered as scoped, so its state is isolated to one request.</remarks>
public sealed class SettingsPageBrandingContextAccessor : ISettingsPageBrandingContextAccessor
{
    public SettingsPageBrandingContext? Current { get; private set; }

    public void Set(int organizationId, int libraryId) =>
        Current = new SettingsPageBrandingContext(organizationId, libraryId);
}
