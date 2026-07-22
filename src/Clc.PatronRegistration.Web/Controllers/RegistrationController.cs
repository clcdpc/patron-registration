using Clc.Melissa;
using Clc.Melissa.Models;
using Clc.PatronRegistration.Configuration;
using Clc.PatronRegistration.Data;
using Clc.PatronRegistration.Helpers;
using Clc.PatronRegistration.Models;
using Clc.PatronRegistration.Web.Models;
using Clc.Polaris.Api;
using Clc.Polaris.Api.Models;
using Clc.Rest;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NLog;
using System.Globalization;
using System.Text.RegularExpressions;
using static Dapper.SqlMapper;

namespace Clc.PatronRegistration.Web.Controllers
{
    public class RegistrationController(IPapiClient papi, IMelissaRestClient melissa, IDbHelper db, ISettingProvider settings, IEmailSender emailSender) : Controller
    {
        private static readonly NLog.ILogger logger = LogManager.GetCurrentClassLogger();

        public IActionResult Create(int? orgId, bool forceDl = false, bool agreementAccepted = false)
        {
            if (!orgId.HasValue || !db.GetSelfRegistrationOrganizations().Any(o=>o.OrganizationID == orgId)) { return RedirectToAction("SelectLibrary"); }

            var model = Registration.BuildBaseRegistration(orgId.Value, forceDl, Request.GetTrueClientIP(), settings, db);

            if (agreementAccepted) { model.BypassAgreement = true; }

            return HttpContext.IsInjectedForm() ? PartialView("Create", model) : View("Create", model);
        }


        [HttpPost]
        public RegistrationAttempt Submit(Registration p) => p.CreateRegistration(Request.GetTrueClientIP(), ModelState, settings, db, papi, melissa, emailSender);

        public string ViewIp() => Request.GetTrueClientIP();

        public IActionResult SelectLibrary() => View(db.GetSelfRegistrationLibraries().OrderBy(l => l.DisplayName).ToList());

        public DupeCheckResult DupeCheck(Registration p) { return p.DupeCheck(db, papi); }

        public JsonResult dl(string dlinfo)
        {
            if (string.IsNullOrWhiteSpace(dlinfo) || dlinfo == "null")
            {
                return Json("");
            }

            logger.Trace(dlinfo);

            if (settings.DriversLicenseFormat.Equals("barcode", StringComparison.OrdinalIgnoreCase))
            {
                return Json(DriverLicenseHelper.ProcessDlBarcode(dlinfo));
            }

            return Json(DriverLicenseHelper.ProcessDlMagstripe(dlinfo));
        }
    }
}
