using Clc.Polaris.Api;
using Clc.PatronRegistration.Data;
using Clc.Polaris.Api.Models;
using System.Collections.Generic;
using Clc.PatronRegistration.Configuration;

namespace Clc.PatronRegistration.Helpers
{
    public sealed record CacheSnapshot
    {
        public CacheSnapshot(
            IReadOnlyList<RegistrationFormSetting> settings,
            IReadOnlyList<OrganizationsGetRow> organizations)
        {
            Settings = settings;
            Organizations = organizations;
            IndexedSettings = new SettingsResolverSnapshot(settings);
        }

        public IReadOnlyList<RegistrationFormSetting> Settings { get; }
        public IReadOnlyList<OrganizationsGetRow> Organizations { get; }
        public SettingsResolverSnapshot IndexedSettings { get; }

        public static CacheSnapshot Capture(ICache cache) =>
            cache is ICacheSnapshotProvider snapshotProvider
                ? snapshotProvider.GetSnapshot()
                : new CacheSnapshot(cache.SettingsCache.ToArray(), cache.OrganizationCache.ToArray());
    }

    /// <summary>Provides one atomically published cache generation to request-scoped consumers.</summary>
    public interface ICacheSnapshotProvider
    {
        CacheSnapshot GetSnapshot();
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
