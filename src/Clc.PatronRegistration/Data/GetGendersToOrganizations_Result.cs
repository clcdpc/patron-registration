using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Clc.PatronRegistration.Data
{
    public partial class GetGendersToOrganizations_Result
    {
        public int GenderID { get; set; }
        public string Description { get; set; } = string.Empty;
        public int DisplayOrder { get; set; }
        public bool Display { get; set; }
    }
}
