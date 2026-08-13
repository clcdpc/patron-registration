using Microsoft.Extensions.Caching.Memory;

namespace Clc.PatronRegistration.Web.Settings;

/// <summary>
/// Bounds and expires the small metadata cache used while resolving public header images.
/// The cache is intentionally separate from any application-wide IMemoryCache so its size
/// requirements cannot affect unrelated cache consumers.
/// </summary>
public sealed class RegistrationHeaderImageMetadataCache : IDisposable
{
    public const int DefaultMaximumEntries = 512;
    public static readonly TimeSpan EntryLifetime = TimeSpan.FromMinutes(5);

    private readonly MemoryCache cache;

    public RegistrationHeaderImageMetadataCache()
        : this(DefaultMaximumEntries)
    {
    }

    public RegistrationHeaderImageMetadataCache(int maximumEntries)
    {
        if (maximumEntries <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        }

        cache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = maximumEntries
        });
    }

    public RegistrationFormAssetMetadata? GetOrCreate(
        int assetId,
        Func<RegistrationFormAssetMetadata?> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var cached = cache.GetOrCreate(assetId, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = EntryLifetime;
            entry.Size = 1;
            return new CachedMetadata(factory());
        });

        return cached?.Metadata;
    }

    public void Dispose() => cache.Dispose();

    private sealed record CachedMetadata(RegistrationFormAssetMetadata? Metadata);
}
