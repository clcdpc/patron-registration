using Clc.PatronRegistration.Configuration;
using Clc.PatronRegistration.Data;
using Clc.Polaris.Api;
using Clc.Polaris.Api.Configuration;
using Clc.Polaris.Api.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Clc.PatronRegistration.Helpers
{
    public class MemoryCache(
        IPapiClient papi,
        IDbHelper db,
        ISettingsCacheGenerationProvider? generationProvider = null) : ICache, IGenerationAwareCacheSnapshotProvider
    {
        private sealed record PublishedCacheSnapshot(
            List<RegistrationFormSetting> Settings,
            List<OrganizationsGetRow> Organizations,
            CacheSnapshot Snapshot);

        private readonly object rebuildLock = new();
        private PublishedCacheSnapshot? snapshot;
        public bool IsInitialized => Volatile.Read(ref snapshot) is not null;
        public List<RegistrationFormSetting> SettingsCache
        {
            get
            {
                var current = Volatile.Read(ref snapshot);
                if (current is null)
                {
                    RebuildCache();
                    current = Volatile.Read(ref snapshot);
                }
                return current?.Settings ?? [];
            }
        }
        public List<OrganizationsGetRow> OrganizationCache
        {
            get
            {
                var current = Volatile.Read(ref snapshot);
                if (current is null)
                {
                    RebuildCache();
                    current = Volatile.Read(ref snapshot);
                }
                return current?.Organizations ?? [];
            }
        }

        public CacheSnapshot GetSnapshot()
        {
            var current = Volatile.Read(ref snapshot);
            if (current is null)
            {
                RebuildCache();
                current = Volatile.Read(ref snapshot);
            }

            return current?.Snapshot ?? new CacheSnapshot([], []);
        }

        public void RebuildCache()
        {
            lock (rebuildLock)
            {
                PublishLoadedCache(null);
            }
        }

        public CacheSnapshot GetSnapshotAtGeneration(long generation)
        {
            var current = Volatile.Read(ref snapshot);
            if (current?.Snapshot.Generation == generation)
            {
                return current.Snapshot;
            }

            RebuildCacheAtGeneration(generation);
            current = Volatile.Read(ref snapshot);
            if (current?.Snapshot.Generation != generation)
            {
                throw new CacheSnapshotConsistencyException(
                    $"The local settings cache did not publish generation {generation}.");
            }

            return current.Snapshot;
        }

        public void RebuildCacheAtGeneration(long generation)
        {
            lock (rebuildLock)
            {
                if (generationProvider is null)
                {
                    throw new CacheSnapshotConsistencyException(
                        "An authoritative settings-cache generation provider is not configured.");
                }
                var before = generationProvider.GetCacheGeneration();
                if (before != generation)
                {
                    throw new CacheSnapshotConsistencyException(
                        $"The requested settings-cache generation {generation} is no longer current; current generation is {before}.");
                }

                var loaded = LoadCache();

                var after = generationProvider.GetCacheGeneration();
                if (after != generation)
                {
                    throw new CacheSnapshotConsistencyException(
                        $"The settings-cache generation changed while generation {generation} was being rebuilt.");
                }

                PublishLoadedCache(loaded, generation);
            }
        }

        private void PublishLoadedCache(long? generation)
        {
            PublishLoadedCache(LoadCache(), generation);
        }

        private void PublishLoadedCache(
            (List<RegistrationFormSetting> Settings, List<OrganizationsGetRow> Organizations) loaded,
            long? generation)
        {
            var settings = loaded.Settings;
            var organizations = loaded.Organizations;
            var organizationsSnapshot = Array.AsReadOnly(organizations.ToArray());
            var settingsSnapshot = Array.AsReadOnly(settings.ToArray());
            Volatile.Write(ref snapshot, new PublishedCacheSnapshot(
                settings,
                organizations,
                new CacheSnapshot(settingsSnapshot, organizationsSnapshot, generation)));
        }

        private (List<RegistrationFormSetting> Settings, List<OrganizationsGetRow> Organizations) LoadCache()
        {
            var orgResult = papi.OrganizationsGet(OrganizationType.All);
            var organizations = orgResult.Data.OrganizationsGetRows.ToList();
            var settings = db.GetAllSettings().ToList();
            return (settings, organizations);
        }
        public OrganizationsGetRow GetOrg(int orgId) => OrganizationCache.Single(o => o.OrganizationID == orgId);
        public List<OrganizationsGetRow> GetBranches(int orgId)
        {
            var org = GetOrg(orgId);
            var library = 0;

            if (org.OrganizationCodeID == 2) { library = orgId; }
            else { library = org.ParentOrganizationID.GetValueOrDefault(); }

            return OrganizationCache.Where(oc => oc.ParentOrganizationID == library).ToList();
        }
    }
}
