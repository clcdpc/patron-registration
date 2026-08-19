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
    public class MemoryCache(IPapiClient papi, IDbHelper db) : ICache, ICacheSnapshotProvider
    {
        private sealed record PublishedCacheSnapshot(
            List<RegistrationFormSetting> Settings,
            List<OrganizationsGetRow> Organizations);

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

            return new CacheSnapshot(current?.Settings.ToArray() ?? [], current?.Organizations.ToArray() ?? []);
        }

        public void RebuildCache()
        {
            lock (rebuildLock)
            {
                var orgResult = papi.OrganizationsGet(OrganizationType.All);
                var organizations = orgResult.Data.OrganizationsGetRows.ToList();
                var settings = db.GetAllSettings().ToList();
                Volatile.Write(ref snapshot, new PublishedCacheSnapshot(settings, organizations));
            }
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
