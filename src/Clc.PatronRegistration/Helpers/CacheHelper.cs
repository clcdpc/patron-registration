using Clc.PatronRegistration.Configuration;
using Clc.Polaris.Api.Models;

namespace Clc.PatronRegistration.Helpers
{
    public static class CacheHelper
    {
        private static ICache? _cache;

        public static void Configure(ICache cache)
        {
            cache.RebuildCache();
            Volatile.Write(ref _cache, cache);
        }

        private static ICache Current => Volatile.Read(ref _cache)
            ?? throw new InvalidOperationException("CacheHelper has not been configured.");

        public static List<RegistrationFormSetting> SettingsCache => Current.SettingsCache;
        public static List<OrganizationsGetRow> OrganizationCache => Current.OrganizationCache;
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
