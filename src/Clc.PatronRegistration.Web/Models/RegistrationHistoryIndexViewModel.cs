using Clc.PatronRegistration.Data;
using System.ComponentModel.DataAnnotations;

namespace Clc.PatronRegistration.Web.Models
{
    public class RegistrationHistoryIndexViewModel
    {
        [Required(AllowEmptyStrings = true)]
        public string SearchTerm { get; set; } = string.Empty;
        public List<RegistrationHistoryEntry> Entries { get; set; } = [];
    }
}
