using Clc.Polaris.Api;
using Clc.PatronRegistration.Data;
using Clc.Polaris.Api.Models;
using System.Collections.Generic;
using Clc.PatronRegistration.Configuration;

namespace Clc.PatronRegistration.Helpers
{
    public interface ICache
    {
        List<RegistrationFormSetting> SettingsCache { get; }
        List<OrganizationsGetRow> OrganizationCache { get; }
        void RebuildCache();
        OrganizationsGetRow GetOrg(int orgId);
        List<OrganizationsGetRow> GetBranches(int orgId);
    }
}
