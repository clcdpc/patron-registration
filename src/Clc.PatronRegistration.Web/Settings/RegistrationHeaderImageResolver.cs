using Microsoft.Extensions.Caching.Memory;

namespace Clc.PatronRegistration.Web.Settings;

public sealed record RegistrationHeaderImageSelection(int? AssetId, string? LegacyUrl)
{
    public bool UsesAsset => AssetId.HasValue;
}

/// <summary>
/// Resolves the effective public header image without changing settings inheritance.
/// A missing/stale asset ID deliberately falls through to the legacy URL.
/// </summary>
public sealed class RegistrationHeaderImageResolver(
    IRegistrationFormAssetRepository assets,
    IMemoryCache? metadataCache = null)
{
    private const int MaximumCachedAssetMetadataEntries = 512;
    private static readonly TimeSpan MetadataCacheLifetime = TimeSpan.FromMinutes(5);
    private readonly IMemoryCache cache = metadataCache ?? new MemoryCache(new MemoryCacheOptions
    {
        SizeLimit = MaximumCachedAssetMetadataEntries
    });

    public RegistrationHeaderImageSelection? Resolve(int? assetId, string? legacyUrl)
    {
        if (assetId is > 0 && GetMetadata(assetId.Value) is not null)
        {
            return new RegistrationHeaderImageSelection(assetId, null);
        }

        return string.IsNullOrWhiteSpace(legacyUrl)
            ? null
            : new RegistrationHeaderImageSelection(null, legacyUrl);
    }

    private RegistrationFormAssetMetadata? GetMetadata(int assetId)
    {
        var cached = cache.GetOrCreate($"registration-header-asset:{assetId}", entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = MetadataCacheLifetime;
            entry.Size = 1;
            return new CachedMetadata(assets.GetMetadata(assetId));
        });

        return cached?.Metadata;
    }

    private sealed record CachedMetadata(RegistrationFormAssetMetadata? Metadata);
}
