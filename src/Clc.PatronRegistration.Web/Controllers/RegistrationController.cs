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
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
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
        IDbHelper db,
        ISettingProvider settings,
        IEmailSenderFactory emailSenderFactory,
        IMelissaClientFactory melissaClientFactory,
        IObjectModelValidator objectModelValidator,
        IRegistrationScopeResolver registrationScopeResolver) : Controller
    {
        private static readonly NLog.ILogger logger = LogManager.GetCurrentClassLogger();

        public IActionResult Create(
            int? orgId,
            bool forceDl = false,
            bool agreementAccepted = false,
            int? selectedBranchId = null) =>
            RenderCreate(orgId, forceDl, agreementAccepted, selectedBranchId);

        [HttpPost]
        public IActionResult ChangeBranch(
            Registration p,
            int? orgId,
            bool forceDl = false,
            bool agreementAccepted = false)
        {
            // The branch-switch request contains the live form only long enough to
            // render the selected branch's settings. It must never become a cacheable
            // response that a shared browser can reuse after the form is abandoned.
            Response.Headers.CacheControl = "no-store";
            Response.Headers.Pragma = "no-cache";
            Response.Headers.Expires = "0";

            if (!orgId.HasValue || p.PatronBranchID <= 0)
            {
                return RegistrationUnavailableView();
            }

            // A branch change is not a registration submission. Do not carry binding
            // or validation errors into the newly rendered branch-specific form.
            ModelState.Clear();
            return RenderCreate(orgId, forceDl, agreementAccepted, p.PatronBranchID, p);
        }

        private IActionResult RenderCreate(
            int? orgId,
            bool forceDl,
            bool agreementAccepted,
            int? selectedBranchId = null,
            Registration? submittedRegistration = null)
        {
            var organizations = db.GetSelfRegistrationOrganizations().ToList();
            if (!orgId.HasValue) { return RedirectToAction("SelectLibrary"); }

            var organizationId = orgId.Value;
            var routeOrganization = organizations.FirstOrDefault(organization => organization.OrganizationID == organizationId);
            if (routeOrganization is null) { return RedirectToAction("SelectLibrary"); }

            ViewData["RegistrationForceDl"] = forceDl;
            ViewData["RegistrationAgreementAccepted"] = agreementAccepted;
            ViewData["RegistrationScopeOrganizationId"] = organizationId;

            var renderSettings = settings;
            var effectiveSelectedBranchId = selectedBranchId ??
                (routeOrganization.OrganizationCodeID == 3 ? routeOrganization.OrganizationID : null);
            if (effectiveSelectedBranchId.HasValue)
            {
                var selectedBranchResolution = registrationScopeResolver.ResolveForSubmission(
                    HttpContext, settings, effectiveSelectedBranchId.Value);
                if (!selectedBranchResolution.IsValid || selectedBranchResolution.Settings.DisableBranch)
                {
                    return RegistrationUnavailableView();
                }

                renderSettings = selectedBranchResolution.Settings;
            }

            var model = submittedRegistration ?? Registration.BuildBaseRegistration(
                effectiveSelectedBranchId ?? organizationId,
                forceDl, Request.GetTrueClientIP(), renderSettings, db);

            if (submittedRegistration is not null)
            {
                model.UseSettings(renderSettings);
                model.Genders = db.GetGendersToOrganizations(effectiveSelectedBranchId ?? organizationId)
                    .Select(g => new SelectListItem { Value = g.GenderID.ToString(), Text = g.Description })
                    .ToList();
                model.PickupBranches = renderSettings.DisplayPreferredPickupLocation
                    ? new SelectList(
                        db.GetPickupBranches(renderSettings.LibraryId),
                        "OrganizationID",
                        "DisplayName")
                    : new SelectList(Array.Empty<string>());
                model.ShowDlButton = forceDl || renderSettings.EnableDriversLicenseSwipe &&
                    Registration.CheckIp(Request.GetTrueClientIP(), renderSettings.DriversLicenseButtonEnabledIpAddresses);
                if (renderSettings.ForceEcardRemotely)
                {
                    model.IsECard = !Registration.CheckIp(
                        Request.GetTrueClientIP(), renderSettings.DriversLicenseButtonEnabledIpAddresses);
                }
                else if (renderSettings.DisplayMailingListCheckbox &&
                    (!Request.HasFormContentType ||
                     !Request.Form.ContainsKey(nameof(Registration.AddToMailingList))))
                {
                    model.AddToMailingList = true;
                }
            }

            if (renderSettings.EnablePatronBranchSelectOption)
            {
                var availableBranches = registrationScopeResolver.GetAvailableBranches(HttpContext, settings);
                model.Branches = new SelectList(availableBranches, "OrganizationID", "DisplayName");
                if (availableBranches.Count == 0)
                {
                    return RegistrationUnavailableView();
                }

                var selectedBranch = availableBranches
                    .FirstOrDefault(branch => branch.OrganizationID == effectiveSelectedBranchId);
                if (effectiveSelectedBranchId.HasValue && selectedBranch is null)
                {
                    return RegistrationUnavailableView();
                }

                if (selectedBranch is not null)
                {
                    model.PatronBranchID = selectedBranch.OrganizationID;
                }
                else if (availableBranches.Count == 1)
                {
                    model.PatronBranchID = availableBranches[0].OrganizationID;
                }
            }
            else
            {
                var resolution = registrationScopeResolver.ResolveForSubmission(
                    HttpContext, settings, model.PatronBranchID);
                if (!resolution.IsValid || resolution.Settings.DisableBranch)
                {
                    return RegistrationUnavailableView();
                }

                renderSettings = resolution.Settings;
                model.UseSettings(renderSettings);
                model.LibraryId = renderSettings.LibraryId;
            }

            model.BypassAgreement = agreementAccepted;
            RegistrationSettingsContext.Set(HttpContext, renderSettings);

            return HttpContext.IsInjectedForm() ? PartialView("Create", model) : View("Create", model);
        }


        [HttpPost]
        public RegistrationAttempt Submit(Registration p)
        {
            var resolution = registrationScopeResolver.ResolveForSubmission(
                HttpContext, settings, p.PatronBranchID);
            if (!resolution.IsValid)
            {
                return new RegistrationAttempt
                {
                    Status = RegistrationStatus.Error,
                    Message = Registration.RegistrationUnavailableMessage
                };
            }

            ApplyRegistrationScope(p, resolution.Settings);
            if (resolution.Settings.DisableBranch)
            {
                return new RegistrationAttempt
                {
                    Status = RegistrationStatus.Disabled,
                    Message = Registration.RegistrationUnavailableMessage
                };
            }

            RevalidateAgainstSelectedBranch(p, resolution.Settings);
            var melissa = RegistrationClientProvider.CreateMelissa(resolution.Settings, melissaClientFactory);
            var emailSender = RegistrationClientProvider.CreateEmail(resolution.Settings, emailSenderFactory);
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

        public IActionResult dl(string dlinfo, int? patronBranchId = null)
        {
            if (string.IsNullOrWhiteSpace(dlinfo) || dlinfo == "null")
            {
                return Json("");
            }

            logger.Trace(dlinfo);

            var format = DriversLicenseFormatSettingParser.Parse(ResolveEndpointSettings(patronBranchId).DriversLicenseFormat);
            if (format.State == DriversLicenseFormatSettingState.Invalid)
            {
                return BadRequest("Driver’s-license scanner format is not configured with a supported value.");
            }

            if (format.State == DriversLicenseFormatSettingState.Barcode)
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

        private void RevalidateAgainstSelectedBranch(Registration registration, ISettingProvider effectiveSettings)
        {
            var bindingErrors = ModelState
                .SelectMany(pair => pair.Value?.Errors.Select(error => (pair.Key, error)) ?? [])
                .Where(item => item.error.Exception is not null)
                .ToList();
            var selectedModelState = new ModelStateDictionary();
            var originalRequestServices = HttpContext.RequestServices;
            HttpContext.RequestServices = new SettingProviderServiceProvider(originalRequestServices, effectiveSettings);
            try
            {
                var actionContext = new ActionContext(
                    HttpContext,
                    ControllerContext.RouteData ?? new Microsoft.AspNetCore.Routing.RouteData(),
                    ControllerContext.ActionDescriptor ?? new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor(),
                    selectedModelState);
                objectModelValidator.Validate(actionContext, null, string.Empty, registration);
            }
            finally
            {
                HttpContext.RequestServices = originalRequestServices;
            }

            ModelState.Clear();
            foreach (var (key, error) in bindingErrors)
            {
                ModelState.AddModelError(key, error.Exception?.Message ?? "The supplied value is invalid.");
            }
            foreach (var pair in selectedModelState)
            {
                foreach (var error in pair.Value.Errors)
                {
                    if (error.Exception is not null)
                    {
                        ModelState.AddModelError(pair.Key, error.Exception.Message);
                    }
                    else if (!string.IsNullOrEmpty(error.ErrorMessage))
                    {
                        ModelState.AddModelError(pair.Key, error.ErrorMessage);
                    }
                }
            }
        }

        private sealed class SettingProviderServiceProvider(IServiceProvider inner, ISettingProvider effectiveSettings) : IServiceProvider
        {
            public object? GetService(Type serviceType) =>
                serviceType == typeof(ISettingProvider) ? effectiveSettings : inner.GetService(serviceType);
        }

        private IActionResult RegistrationUnavailableView()
        {
            ViewData["Title"] = "Registration currently unavailable";
            return HttpContext.IsInjectedForm() ? PartialView("Unavailable") : View("Unavailable");
        }
    }
}
