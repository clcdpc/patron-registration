using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Clc.PatronRegistration.Configuration
{
    public partial class RegistrationFormSetting
    {
        public int OrganizationID { get; set; }
        public string Setting { get; set; } = string.Empty;
        public string FormCode { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;

        public override string ToString()
        {
            return $"{OrganizationID} - {(string.IsNullOrWhiteSpace(FormCode) ? "" : $"{FormCode} - ")}{Setting} - {Value}";
        }
    }
}
