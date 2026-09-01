using Clc.PatronRegistration.Administration;
using Clc.PatronRegistration.Configuration;
using Clc.PatronRegistration.Helpers;
using Clc.Polaris.Api;
using Microsoft.Extensions.Options;

namespace Clc.PatronRegistration.Web.Settings;

/// <summary>
/// Limits an asset to the settings scopes in which it can legitimately be used.
/// Upload scope metadata handles new assets; the reference checks preserve access
/// to assets created before scope metadata existed.
/// </summary>
public interface IRegistrationFormAssetAuthorization
{
    RegistrationFormAssetMetadata? GetAuthorizedMetadata(int assetId, int organizationId, string formCode);
}

public sealed class RegistrationFormAssetAuthorization(
    IRegistrationFormAssetRepository assets,
    ICache cache,
    IOptions<SettingsAdministrationOptions> options) : IRegistrationFormAssetAuthorization
{
    private readonly SettingsAdministrationOptions settingsOptions = options.Value;

    public RegistrationFormAssetMetadata? GetAuthorizedMetadata(int assetId, int organizationId, string formCode)
    {
        if (assetId <= 0 || organizationId <= 0)
        {
            return null;
        }

        var metadata = assets.GetMetadata(assetId);
        if (metadata is null || !CanUse(metadata, organizationId, formCode))
        {
            return null;
        }

        return metadata;
    }

    private bool CanUse(RegistrationFormAssetMetadata asset, int organizationId, string formCode)
    {
        formCode = FormCodeNormalizer.Normalize(formCode);

        // Upload ownership grants access only at the exact settings scope that
        // created the asset. This check intentionally precedes hierarchy lookup:
        // an exact-scope upload must be previewable even when no inheritance
        // calculation is needed for that request.
        if (asset.UploadOrganizationId == organizationId &&
            string.Equals(FormCodeNormalizer.Normalize(asset.UploadFormCode), formCode, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var systemOrganizationId = settingsOptions.SystemOrganizationId;
        int libraryId;
        try
        {
            libraryId = organizationId == systemOrganizationId
                ? systemOrganizationId
                : cache.OrganizationCache.GetLibrary(organizationId).OrganizationID;
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        var sources = SettingsResolver.BuildPrecedence(organizationId, libraryId, systemOrganizationId, formCode);
        // Assets at another scope, including unpublished upstream uploads, are
        // usable only after a persisted setting or this exact target's active
        // draft legitimately references them. Legacy assets with null scope
        // metadata follow the same compatibility rule.
        return assets.IsReferencedBySettings(asset.AssetId, sources) ||
            assets.IsReferencedByActiveDraft(asset.AssetId, organizationId, formCode);
    }
}
