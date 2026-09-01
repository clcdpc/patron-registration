using Clc.Polaris.Api;
using Clc.PatronRegistration.Data;
using Clc.Polaris.Api.Models;
using System.Collections.Generic;
using Clc.PatronRegistration.Configuration;

namespace Clc.PatronRegistration.Helpers
{
    public sealed class CacheSnapshotConsistencyException : InvalidOperationException
    {
        public CacheSnapshotConsistencyException(string message)
            : base(message)
        {
        }

        public CacheSnapshotConsistencyException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    public sealed record CacheSnapshot
    {
        public CacheSnapshot(
            IReadOnlyList<RegistrationFormSetting> settings,
            IReadOnlyList<OrganizationsGetRow> organizations,
            long? generation = null)
        {
            Settings = settings;
            Organizations = organizations;
            Generation = generation;
            IndexedSettings = new SettingsResolverSnapshot(settings);
        }

        public IReadOnlyList<RegistrationFormSetting> Settings { get; }
        public IReadOnlyList<OrganizationsGetRow> Organizations { get; }
        public long? Generation { get; }
        public SettingsResolverSnapshot IndexedSettings { get; }

        public static CacheSnapshot Capture(ICache cache) =>
            cache is ICacheSnapshotProvider snapshotProvider
                ? snapshotProvider.GetSnapshot()
                : new CacheSnapshot(cache.SettingsCache.ToArray(), cache.OrganizationCache.ToArray());

        public static CacheSnapshot CaptureAtGeneration(ICache cache, long generation)
        {
            try
            {
                if (cache is IGenerationAwareCacheSnapshotProvider generationProvider)
                {
                    var requestedSnapshot = generationProvider.GetSnapshotAtGeneration(generation);
                    if (requestedSnapshot.Generation != generation)
                    {
                        throw new CacheSnapshotConsistencyException(
                            $"The local settings cache returned generation {requestedSnapshot.Generation?.ToString() ?? "none"} instead of requested generation {generation}.");
                    }
                    return requestedSnapshot;
                }

                var snapshot = Capture(cache);
                if (snapshot.Generation == generation)
                {
                    return snapshot;
                }

                throw new CacheSnapshotConsistencyException(
                    $"The local settings cache cannot supply generation {generation}.");
            }
            catch (CacheSnapshotConsistencyException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new CacheSnapshotConsistencyException(
                    $"The local settings cache could not supply generation {generation}.", exception);
            }
        }
    }

    /// <summary>Provides one atomically published cache generation to request-scoped consumers.</summary>
    public interface ICacheSnapshotProvider
    {
        CacheSnapshot GetSnapshot();
    }

    /// <summary>Provides immutable cache snapshots for an explicitly requested SQL generation.</summary>
    public interface IGenerationAwareCacheSnapshotProvider : ICacheSnapshotProvider
    {
        CacheSnapshot GetSnapshotAtGeneration(long generation);
        void RebuildCacheAtGeneration(long generation);
    }

    /// <summary>Reads the authoritative cross-process settings-cache generation.</summary>
    public interface ISettingsCacheGenerationProvider
    {
        long GetCacheGeneration();
    }

    public interface ICache
    {
        List<RegistrationFormSetting> SettingsCache { get; }
        List<OrganizationsGetRow> OrganizationCache { get; }
        bool IsInitialized { get; }
        void RebuildCache();
        OrganizationsGetRow GetOrg(int orgId);
        List<OrganizationsGetRow> GetBranches(int orgId);
    }
}
