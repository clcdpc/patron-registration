using Clc.Auth.AzureAd.Security;
using Clc.PatronRegistration.Data;
using Clc.PatronRegistration.Helpers;
using Clc.PatronRegistration.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Clc.PatronRegistration.Web.Controllers
{
    [Authorize(Roles = "Clc.CardReg.ViewHistory")]
    public class HistoryController(IDbHelper db) : Controller
    {
        public ActionResult Index(string searchTerm)
        {
            var orgid = User.Identity.GetClaimOrganization().GetValueOrDefault();
            var history = db.GetRegistrationHistory(User.Identity.GetClaimOrganization().GetValueOrDefault(), term: searchTerm);

            var model = new RegistrationHistoryIndexViewModel { Entries = history.ToList(), SearchTerm = searchTerm };

            // hack to fix searchTerm being required, should fix for real at some point
            ModelState.Clear();
            return View(model);
        }

        public ActionResult Details(int id)
        {
            var orgId = User.Identity.GetClaimOrganization();
            var entry = db.GetRegistrationHistoryEntry(id);

            entry.RegistrationBody = JToken.Parse(entry.RegistrationBody).ToString(Formatting.Indented);
            try
            {
                if (!string.IsNullOrWhiteSpace(entry.PapiResponse))
                {
                    entry.PapiResponse = JToken.Parse(entry.PapiResponse).Children().First().ToString().Trim();
                }
            }
            catch (Exception) { }
            try
            {
                if (!string.IsNullOrWhiteSpace(entry.MelissaResponse))
                {
                    entry.MelissaResponse = JToken.Parse(entry.MelissaResponse).Children().First().ToString().Trim();//.ToString(Formatting.Indented);
                }
            }
            catch (Exception) { }
            entry.SettingsSnapshot = JToken.Parse(entry.SettingsSnapshot).ToString(Formatting.Indented);

            var staffOrgId = HttpContext.User.GetClaimOrganization();
            if (staffOrgId.GetValueOrDefault() == -1)
            {
                staffOrgId = entry.PatronBranchId;
            }

            if (CacheHelper.GetOrg(staffOrgId.GetValueOrDefault(1)).ParentOrganizationID == CacheHelper.GetOrg(entry.PatronBranchId).ParentOrganizationID)
            {
                return View(entry);
            }

            return RedirectToAction("Index");
        }
    }
}
