using Clc.PatronRegistration.Helpers;

namespace Clc.PatronRegistration.Configuration
{
    public class ForcedKioskModeDbSettingProvider(int orgId, ICache cache) : DbSettingProvider(orgId, cache, "kiosk"), ISettingProvider
    {
        public new bool ResetForm => true;
    }
}
