using Clc.Melissa;
using Clc.Polaris.Api.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Clc.PatronRegistration.Configuration
{
    public class RegistrationConfiguration : IRegistrationConfiguration
    {
        public string db_hostname { get; set; } = string.Empty;
        public string db_name { get; set; } = string.Empty;
        public bool ForceKioskModeLocally { get; set; }
        public string[] CorsAllowedOrigins { get; set; } = Array.Empty<string>();
        public IPapiSettings Papi { get; set; } = new PapiSettings();
        public IMelissaClientSettings Melissa { get; set; } = new MelissaClientSettings();
    }

    public interface IRegistrationConfiguration : IDbHelperSettings
    {
        public string[] CorsAllowedOrigins { get; }
        public bool ForceKioskModeLocally { get; set; }
        IPapiSettings Papi { get; set; }
        IMelissaClientSettings Melissa { get; set; }
    }

    public interface IDbHelperSettings
    {
        string db_hostname { get; }
        string db_name { get; }
    }

    public class DbHelperSettings : IDbHelperSettings
    {
        public string db_hostname { get; } = string.Empty;
        public string db_name { get; } = string.Empty;
    }

}
