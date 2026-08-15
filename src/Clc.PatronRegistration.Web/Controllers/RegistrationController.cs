using Clc.Melissa;
using Clc.Melissa.Models;
using Clc.PatronRegistration.Configuration;
using Clc.PatronRegistration.Data;
using Clc.PatronRegistration.Helpers;
using Clc.PatronRegistration.Models;
using Clc.PatronRegistration.Web.Models;
using Clc.PatronRegistration.Web.Settings;
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
    public class RegistrationController(
        IPapiClient papi,
        IMelissaRestClient melissa,
        IDbHelper db,
        ISettingProvider settings,
        IEmailSender emailSender,
        IRegistrationScopeResolver registrationScopeResolver) : Controller
    {
        private static readonly NLog.ILogger logger = LogManager.GetCurrentClassLogger();

        public IActionResult Create(int? orgId, bool forceDl = false, bool agreementAccepted = false)
        {
            if (!orgId.HasValue || !db.GetSelfRegistrationOrganizations().Any(o=>o.OrganizationID == orgId)) { return RedirectToAction("SelectLibrary"); }

            var model = Registration.BuildBaseRegistration(orgId.Value, forceDl, Request.GetTrueClientIP(), settings, db);

            if (settings.EnablePatronBranchSelectOption)
            {
                var availableBranches = registrationScopeResolver.GetAvailableBranches(HttpContext, settings);
                model.Branches = new SelectList(availableBranches, "OrganizationID", "DisplayName");
                if (availableBranches.Count == 0)
                {
                    return RegistrationUnavailableView();
                }
                if (availableBranches.Count == 1)
                {
                    model.PatronBranchID = availableBranches[0].OrganizationID;
                }
            }
            else
            {
                var resolution = registrationScopeResolver.ResolveForSubmission(HttpContext, settings, model.PatronBranchID);
                if (!resolution.IsValid || resolution.Settings.DisableBranch)
                {
                    return RegistrationUnavailableView();
                }
            }

            if (agreementAccepted) { model.BypassAgreement = true; }

            return HttpContext.IsInjectedForm() ? PartialView("Create", model) : View("Create", model);
        }


        [HttpPost]
        public RegistrationAttempt Submit(Registration p)
        {
            var resolution = registrationScopeResolver.ResolveForSubmission(HttpContext, settings, p.PatronBranchID);
            if (!resolution.IsValid)
            {
                return new RegistrationAttempt
                {
                    Status = RegistrationStatus.Error,
                    Message = Registration.RegistrationUnavailableMessage
                };
            }

            ApplyRegistrationScope(p, resolution.Settings);
            return p.CreateRegistration(Request.GetTrueClientIP(), ModelState, resolution.Settings, db, papi, melissa, emailSender);
        }

        [HttpPost]
        public JsonResult AgeBlockCheck(DateTime? birthdate, int? patronBranchId = null) =>
            Json(AgeBlockPolicy.Evaluate(ResolveEndpointSettings(patronBranchId), birthdate));

        public string ViewIp() => Request.GetTrueClientIP();

        public IActionResult SelectLibrary() => View(db.GetSelfRegistrationLibraries().OrderBy(l => l.DisplayName).ToList());

        public DupeCheckResult DupeCheck(Registration p)
        {
            var resolution = registrationScopeResolver.ResolveForSubmission(HttpContext, settings, p.PatronBranchID);
            if (!resolution.IsValid || resolution.Settings.DisableBranch)
            {
                return DupeCheckResult.False();
            }

            ApplyRegistrationScope(p, resolution.Settings);
            return p.DupeCheck(db, papi);
        }

        public JsonResult dl(string dlinfo, int? patronBranchId = null)
        {
            if (string.IsNullOrWhiteSpace(dlinfo) || dlinfo == "null")
            {
                return Json("");
            }

            logger.Trace(dlinfo);

            if (ResolveEndpointSettings(patronBranchId).DriversLicenseFormat.Equals("barcode", StringComparison.OrdinalIgnoreCase))
            {
                return Json(DriverLicenseHelper.ProcessDlBarcode(dlinfo));
            }

            return Json(DriverLicenseHelper.ProcessDlMagstripe(dlinfo));
        }

        private ISettingProvider ResolveEndpointSettings(int? patronBranchId)
        {
            if (patronBranchId is not > 0)
            {
                return settings;
            }

            var resolution = registrationScopeResolver.ResolveForSubmission(HttpContext, settings, patronBranchId.Value);
            return resolution.IsValid ? resolution.Settings : settings;
        }

        private static void ApplyRegistrationScope(Registration registration, ISettingProvider effectiveSettings)
        {
            registration.UseSettings(effectiveSettings);
            registration.LibraryId = effectiveSettings.LibraryId;
        }

        private IActionResult RegistrationUnavailableView()
        {
            ViewData["Title"] = "Registration currently unavailable";
            return HttpContext.IsInjectedForm() ? PartialView("Unavailable") : View("Unavailable");
        }
    }
}
