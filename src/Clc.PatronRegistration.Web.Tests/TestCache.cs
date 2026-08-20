using Clc.PatronRegistration.Configuration;
using Clc.Polaris.Api.Models;
using Clc.PatronRegistration.Helpers;

namespace Clc.PatronRegistration.Tests
{
    public class TestCache : ICache, ICacheSnapshotProvider
    {
        public bool IsInitialized { get; set; } = true;
        private List<RegistrationFormSetting> settings = [];
        private List<OrganizationsGetRow> organizations =
        [
            new() { OrganizationID = 1, Name = "System", OrganizationCodeID = 1, Abbreviation = "SYS" },
            new() { OrganizationID = 2, Name = "Library", OrganizationCodeID = 2, Abbreviation = "LIB", ParentOrganizationID = 1 },
            new() { OrganizationID = 3, Name = "Branch", OrganizationCodeID = 3, Abbreviation = "BRA", ParentOrganizationID = 2 }
        ];

        public List<OrganizationsGetRow> OrganizationCache
        {
            get => organizations;
            set
            {
                organizations = value;
                snapshot = null;
            }
        }

        private CacheSnapshot? snapshot;

        public CacheSnapshot GetSnapshot() => snapshot ??= new(
            Array.AsReadOnly(SettingsCache.ToArray()),
            Array.AsReadOnly(OrganizationCache.ToArray()));

        public List<RegistrationFormSetting> SettingsCache
        {
            get => settings;
            set
            {
                settings = value;
                snapshot = null;
            }
        }

        public List<OrganizationsGetRow> GetBranches(int orgId) => OrganizationCache.Where(o => o.OrganizationCodeID == 3).ToList();

        public OrganizationsGetRow GetOrg(int orgId) => OrganizationCache.Single(o => o.OrganizationID == orgId);
        public void RebuildCache()
        {
        }
    }
}
