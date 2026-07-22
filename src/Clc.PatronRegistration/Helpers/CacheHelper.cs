using Clc.PatronRegistration.Configuration;
using Clc.Polaris.Api.Models;

namespace Clc.PatronRegistration.Helpers
{
    public static class CacheHelper
    {
        private static List<RegistrationFormSetting> _settingsCache = [];
        private static List<OrganizationsGetRow> _organizationCache = [];

        public static void Configure(ICache cache)
        {
            cache.RebuildCache();
            _settingsCache = cache.SettingsCache;
            _organizationCache = cache.OrganizationCache;
        }

        public static List<RegistrationFormSetting> SettingsCache => _settingsCache;
        public static List<OrganizationsGetRow> OrganizationCache => _organizationCache;
        public static OrganizationsGetRow GetOrg(int orgId) => OrganizationCache.Single(o => o.OrganizationID == orgId);
        public static List<OrganizationsGetRow> GetBranches(int orgId)
        {
            var org = GetOrg(orgId);
            var library = 0;

            if (org.OrganizationCodeID == 2) { library = orgId; }
            else { library = org.ParentOrganizationID.GetValueOrDefault(); }

            return OrganizationCache.Where(oc => oc.ParentOrganizationID == library).ToList();
        }
    }
}
