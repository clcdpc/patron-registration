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
            return RenderCreate(orgId, forceDl, agreementAccepted, p.PatronBranchID, p, renderFragment: true);
        }

        private IActionResult RenderCreate(
            int? orgId,
            bool forceDl,
            bool agreementAccepted,
            int? selectedBranchId = null,
            Registration? submittedRegistration = null,
            bool renderFragment = false)
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

            // A selected branch may supply the form's effective settings, but it
            // cannot broaden a route that did not allow branch selection. This
            // also keeps a forged ChangeBranch request on a disabled library
            // route from turning the fixed default into an editable selector.
            var branchSelectionEnabled = settings.EnablePatronBranchSelectOption &&
                renderSettings.EnablePatronBranchSelectOption;

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

            if (branchSelectionEnabled)
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

                var fixedBranch = organizations
                    .Where(organization => organization.OrganizationID == model.PatronBranchID)
                    .ToList();
                if (fixedBranch.Count == 0)
                {
                    return RegistrationUnavailableView();
                }

                // A model supplied by ChangeBranch does not carry the original
                // GET model's Branches collection. Keep the fixed branch present
                // so the replacement form remains submittable and can display
                // its authoritative home branch.
                model.Branches = new SelectList(
                    fixedBranch, "OrganizationID", "DisplayName", model.PatronBranchID);
            }

            model.BypassAgreement = agreementAccepted;
            ViewData["RegistrationBranchSelectionEnabled"] = branchSelectionEnabled;
            RegistrationSettingsContext.Set(HttpContext, renderSettings);

            if (renderFragment)
            {
                return PartialView("_RegistrationForm", model);
            }

            return HttpContext.IsInjectedForm() ? PartialView("Create", model) : View("Create", model);
        }


        [HttpPost]
        public RegistrationAttempt Submit([ValidateNever] Registration p)
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

            RevalidateAgainstSelectedBranch(p);

            // Revalidation must finish before any external client is constructed. The
            // registration domain repeats this guard, but the controller can avoid even
            // creating integration clients when MVC binding or selected-branch validation
            // has already rejected the request.
            if (p.HasHoneypotValue)
            {
                return new RegistrationAttempt { Status = RegistrationStatus.Error };
            }

            if (!ModelState.IsValid)
            {
                p.ModelErrors = RegistrationAttempt.ErrorsFromModelState(ModelState);
                return new RegistrationAttempt
                {
                    Status = RegistrationStatus.Error,
                    Message = "Please correct the validation errors and try again.",
                    Errors = p.ModelErrors
                };
            }

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

        private void RevalidateAgainstSelectedBranch(Registration registration)
        {
            var selectedModelState = new ModelStateDictionary();
            var originalRequestServices = HttpContext.RequestServices;
            HttpContext.RequestServices = new SettingProviderServiceProvider(
                originalRequestServices, registration.Settings);
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

            // Submit's parameter is marked ValidateNever so MVC has not yet written
            // object-validation errors into ModelState. The entries already present are
            // therefore the complete binding result and must remain untouched, including
            // message-only errors produced by value conversion/binding.
            foreach (var pair in selectedModelState)
            {
                foreach (var error in pair.Value.Errors)
                {
                    AddValidationError(pair.Key, error);
                }
            }
        }

        private void AddValidationError(string key, ModelError error)
        {
            if (error.Exception is not null)
            {
                ModelState.AddModelError(key, error.Exception.Message);
            }
            else if (!string.IsNullOrEmpty(error.ErrorMessage))
            {
                ModelState.AddModelError(key, error.ErrorMessage);
            }
            else
            {
                ModelState.AddModelError(key, "The submitted value is invalid.");
            }
        }

        private sealed class SettingProviderServiceProvider(
            IServiceProvider inner,
            ISettingProvider effectiveSettings) : IServiceProvider
        {
            public object? GetService(Type serviceType) =>
                serviceType == typeof(ISettingProvider)
                    ? effectiveSettings
                    : inner.GetService(serviceType);
        }

        private IActionResult RegistrationUnavailableView()
        {
            ViewData["Title"] = "Registration currently unavailable";
            return HttpContext.IsInjectedForm() ? PartialView("Unavailable") : View("Unavailable");
        }
    }
}
