using Clc.PatronRegistration.Configuration;
using Clc.Polaris.Api.Models;
using Clc.PatronRegistration.Helpers;

namespace Clc.PatronRegistration.Tests
{
    public class TestCache : ICache
    {
        public List<RegistrationFormSetting> SettingsCache { get; set; } = [];
        public List<OrganizationsGetRow> OrganizationCache
        {
            get
            {
                return [new() { OrganizationID = 1, Name = "System", OrganizationCodeID = 1, Abbreviation = "SYS" },
                        new() { OrganizationID = 2, Name = "Library", OrganizationCodeID = 2, Abbreviation = "LIB", ParentOrganizationID = 1 },
                        new() { OrganizationID = 3, Name = "Branch", OrganizationCodeID = 3, Abbreviation = "BRA", ParentOrganizationID = 2 }];
            }
            set { }
        }

        public List<OrganizationsGetRow> GetBranches(int orgId) => OrganizationCache.Where(o => o.OrganizationCodeID == 3).ToList();

        public OrganizationsGetRow GetOrg(int orgId) => OrganizationCache.Single(o => o.OrganizationID == orgId);
        public void RebuildCache()
        {
        }
    }
}