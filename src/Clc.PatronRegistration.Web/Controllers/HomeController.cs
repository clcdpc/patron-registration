//using Clc.Melissa;
//using Clc.PatronRegistration.Configuration;
//using Clc.PatronRegistration.Data;
//using Clc.PatronRegistration.Extensions;
//using Clc.PatronRegistration.Helpers;
//using Clc.Polaris.Api;
//using Microsoft.AspNetCore.Mvc;
//using Microsoft.AspNetCore.Mvc.Rendering;
//using Newtonsoft.Json;
//using System.Globalization;
//using System.Reflection;

//namespace Clc.PatronRegistration.Web.Controllers
//{
//    public class HomeController : Controller
//    {
//        IConfiguration config;
//        IPapiClient polaris;
//        IMelissaRestClient melissa;
//        IDbHelper db;
//        readonly ISettingProvider Settings;
//        //private static ILogger logger = null
//        //
//        ISession Session => ControllerContext.HttpContext.Session;

//        public HomeController(IConfiguration _config, IPapiClient _polaris, IMelissaRestClient _melissa, IDbHelper _db, ISettingProvider settings)
//        {
//            config = _config;
//            polaris = _polaris;
//            melissa = _melissa;
//            db = _db;
//            Settings = settings;
//        }

//        public IActionResult Index(int id = 7)
//        {
//            Session.SetInt32("orgid", id);
//            var model = BuildBaseRegistration(id);
//            return PartialView("mftest", model);
//        }

//        [HttpPost]
//        public string PostTest(Registration reg)
//        {
//            return JsonConvert.SerializeObject(reg);
//        }

//        public bool IsFieldRequired(string field)
//        {
//            return Settings.GetFieldRequired(field);
//        }

//        public bool ShouldAutoReset(ISettingProvider settings)
//        {
//            var ip = HttpContext.GetTrueClientIp();
//            //logger.Trace($"{ip} | Reset form setting: {settings.ResetForm} | Reset cookie: {Request.Cookies.AllKeys.Contains("enableReset")} | Reset session: {((bool?)Session["reset_form"]).GetValueOrDefault()}");
//            return (settings.ResetForm && CheckIp(settings.DriversLicenseButtonEnabledIpAddresses)) || ControllerContext.HttpContext.Request.Cookies.Keys.Contains("enableReset") || ControllerContext.HttpContext.Session.GetBoolean("reset_form").GetValueOrDefault();
//        }

//        public Registration BuildBaseRegistration(int orgId)
//        {
//            var r = new Registration
//            {
//                Settings = new DbSettingProvider(orgId),
//                State = "OH",
//                Genders = db.GetGendersToOrganizations(orgId).Select(g => new SelectListItem { Value = g.GenderID.ToString(), Text = g.Description }).ToList(),
//            };

//            r.ShowDlButton = r.Settings.EnableDriversLicenseSwipe && CheckIp(r.Settings.DriversLicenseButtonEnabledIpAddresses);

//            if (r.Settings.DisplayMailingListCheckbox) { r.AddToMailingList = true; }

//            var org = Cache.OrganizationCache.Single(o => o.OrganizationID == orgId);

//            r.PatronBranchID = org.OrganizationCodeID == 3 ? org.OrganizationID : (Cache.GetRegistrationBranches(orgId).MinBy(b => b.OrganizationID)?.OrganizationID).GetValueOrDefault(1);


//            r.RequestPickupBranchID = r.RequestPickupBranchID.GetValueOrDefault(r.PatronBranchID);
//            r.LogonUserID = r.Settings.RegistrationLogonUserId;

//            r.LibraryId = org.OrganizationCodeID == 3 ? org.ParentOrganizationID.GetValueOrDefault(r.PatronBranchID) : org.OrganizationID;

//            r.Branches = new SelectList(db.GetSelfRegistrationBranches(r.LibraryId), "OrganizationID", "Name");
//            r.PickupBranches = new SelectList(db.GetPickupBranches(r.LibraryId), "OrganizationID", "Name");

//            r.Months = DateTimeFormatInfo
//               .InvariantInfo
//               .MonthNames
//               .Where(m => !string.IsNullOrWhiteSpace(m))
//               .Select((monthName, index) => new SelectListItem
//               {
//                   Value = (index + 1).ToString(),
//                   Text = monthName
//               }).ToList();

//            return r;
//        }

//        bool CheckIp(IEnumerable<string> whitelist)
//        {
//            var sessionIp = HttpContextHelper.Current.Session.GetString("ip") ?? "";
//            var ipToCheck = string.IsNullOrWhiteSpace(sessionIp) ? HttpContextHelper.GetTrueClientIp() : sessionIp;
//            return whitelist.Any(i => ipToCheck.StartsWith(i));
//        }

//        public DateTime BuildBirthdate(int? day, int? month, int? year)
//        {
//            if (!day.HasValue || !month.HasValue || !year.HasValue) return DateTime.MinValue;

//            var birthDate = DateTime.MinValue;
//            if (!DateTime.TryParse(string.Format("{0}/{1}/{2}", month.ToString(), day.ToString(), year.ToString()), out birthDate))
//            {
//                return DateTime.MinValue;
//            }

//            return birthDate;
//        }
//    }
//}
