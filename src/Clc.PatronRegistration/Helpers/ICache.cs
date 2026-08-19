using Clc.Polaris.Api;
using Clc.PatronRegistration.Data;
using Clc.Polaris.Api.Models;
using System.Collections.Generic;
using Clc.PatronRegistration.Configuration;

namespace Clc.PatronRegistration.Helpers
{
    public sealed record CacheSnapshot(
        IReadOnlyList<RegistrationFormSetting> Settings,
        IReadOnlyList<OrganizationsGetRow> Organizations);

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
