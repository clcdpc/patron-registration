using Clc.PatronRegistration.Configuration;
using Clc.PatronRegistration;
using Clc.PatronRegistration.Helpers;
using Microsoft.Extensions.Options;

namespace Clc.PatronRegistration.Web.Settings;

public interface IRequestSettingProviderResolver
{
    ISettingProvider Resolve(HttpContext httpContext);
    ISettingProvider ResolveForOrganization(HttpContext httpContext, int organizationId);
}

public sealed class RequestSettingProviderResolver(
    IPreviewRequestContextAccessor previewContext,
    ISettingsPageBrandingContextAccessor settingsPageBrandingContext,
    ICache cache,
    IOptions<SettingsAdministrationOptions> options,
    IRegistrationConfiguration registrationConfiguration) : IRequestSettingProviderResolver
{
    public ISettingProvider Resolve(HttpContext httpContext)
    {
        if (previewContext.IsPreviewRequest)
        {
            return previewContext.Current?.Settings
                ?? throw new InvalidOperationException("An invalid preview request cannot resolve live settings.");
        }

        if (settingsPageBrandingContext.Current is { } branding)
        {
            return new DbSettingProvider(
                branding.OrganizationId,
                cache,
                string.Empty,
                options.Value.SystemOrganizationId,
                branding.LibraryId);
        }

        var routeValues = httpContext.Request.RouteValues;
        var organizationId = int.TryParse(routeValues["orgId"]?.ToString(), out var parsed) ? parsed : options.Value.SystemOrganizationId;
        return ResolveForOrganization(httpContext, organizationId);
    }

    public ISettingProvider ResolveForOrganization(HttpContext httpContext, int organizationId)
    {
        if (previewContext.IsPreviewRequest)
        {
            var current = previewContext.Current
                ?? throw new InvalidOperationException("An invalid preview request cannot resolve registration settings.");
            if (current.Link.OperationalBranchId != organizationId)
            {
                throw new InvalidOperationException("A preview registration scope cannot be changed.");
            }
            return current.Settings;
        }

        var routeValues = httpContext.Request.RouteValues;
        var formCode = routeValues["formCode"]?.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(formCode) &&
            (httpContext.Request.IsFromPublicWebBrowser() ||
             (registrationConfiguration.ForceKioskModeLocally && httpContext.IsFromLocalOrOplinIp())))
        {
            formCode = "kiosk";
        }
        return new DbSettingProvider(organizationId, cache, formCode, options.Value.SystemOrganizationId);
    }
}
