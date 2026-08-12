namespace Clc.PatronRegistration.Web.Settings;

public sealed record RegistrationHeaderImageSelection(int? AssetId, string? LegacyUrl)
{
    public bool UsesAsset => AssetId.HasValue;
}

/// <summary>
/// Resolves the effective public header image without changing settings inheritance.
/// A missing/stale asset ID deliberately falls through to the legacy URL.
/// </summary>
public sealed class RegistrationHeaderImageResolver(IRegistrationFormAssetRepository assets)
{
    public RegistrationHeaderImageSelection? Resolve(int? assetId, string? legacyUrl)
    {
        if (assetId is > 0 && assets.GetMetadata(assetId.Value) is not null)
        {
            return new RegistrationHeaderImageSelection(assetId, null);
        }

        return string.IsNullOrWhiteSpace(legacyUrl)
            ? null
            : new RegistrationHeaderImageSelection(null, legacyUrl);
    }
}
