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
        if (asset.UploadOrganizationId is int uploadOrganizationId)
        {
            return sources.Any(source => source.OrganizationId == uploadOrganizationId &&
                IsFormMatch(source.FormCode, asset.UploadFormCode));
        }

        // Assets created before scope metadata was introduced are usable only when
        // the target's effective settings or active draft already references them.
        return assets.IsReferencedBySettings(asset.AssetId, sources) ||
            assets.IsReferencedByActiveDraft(asset.AssetId, organizationId, formCode);
    }

    private static bool IsFormMatch(string sourceFormCode, string? uploadFormCode) =>
        string.IsNullOrEmpty(uploadFormCode) ||
        string.Equals(sourceFormCode, uploadFormCode, StringComparison.OrdinalIgnoreCase);
}
