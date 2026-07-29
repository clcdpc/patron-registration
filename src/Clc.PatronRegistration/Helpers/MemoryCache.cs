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
    public class MemoryCache(IPapiClient papi, IDbHelper db) : ICache
    {
        private readonly object rebuildLock = new();
        public bool IsInitialized => _settingsCache is not null;
        private List<RegistrationFormSetting> _settingsCache = null!;
        public List<RegistrationFormSetting> SettingsCache
        {
            get
            {
                if (_settingsCache == null)
                {
                    RebuildCache();
                }
                return _settingsCache ?? [];
            }
        }
        private List<OrganizationsGetRow> _organizationCache = null!;
        public List<OrganizationsGetRow> OrganizationCache
        {
            get
            {
                if (_organizationCache == null)
                {
                    RebuildCache();
                }
                return _organizationCache ?? [];
            }
        }

        public void RebuildCache()
        {
            lock (rebuildLock)
            {
                var orgResult = papi.OrganizationsGet(OrganizationType.All);
                _organizationCache = orgResult.Data.OrganizationsGetRows;
                _settingsCache = db.GetAllSettings().ToList();
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
