using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Clc.PatronRegistration.Models
{
    public class RegistrationSuccessModel
    {
        public string DisplayText { get; set; } = string.Empty;
        public int Branch { get; set; }
        public bool ResetForm { get; set; }

    }
}
