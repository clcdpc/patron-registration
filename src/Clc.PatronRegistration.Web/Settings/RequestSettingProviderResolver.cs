using Clc.PatronRegistration.Configuration;
using Clc.PatronRegistration;
using Clc.PatronRegistration.Helpers;
using Clc.Polaris.Api;
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
    ISettingsAuthorizationService settingsAuthorization,
    IFormCodeAvailabilityService formCodeAvailability,
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
            var snapshot = GetRequestSnapshot(httpContext);
            var brandingOrganizationId = branding.OrganizationId;
            var formCode = string.Empty;
            if (int.TryParse(httpContext.Request.Query["organizationId"].ToString(), out var selectedOrganizationId) &&
                settingsAuthorization.CanManage(httpContext.User, selectedOrganizationId) &&
                snapshot.Organizations.Any(organization => organization.OrganizationID == selectedOrganizationId))
            {
                var selectedFormCode = httpContext.Request.Query["formCode"].ToString();
                if (formCodeAvailability.IsAvailable(selectedOrganizationId, selectedFormCode))
                {
                    brandingOrganizationId = selectedOrganizationId;
                    formCode = selectedFormCode;
                }
            }

            var libraryId = brandingOrganizationId == options.Value.SystemOrganizationId
                ? options.Value.SystemOrganizationId
                : snapshot.Organizations.GetLibrary(brandingOrganizationId).OrganizationID;

            return new DbSettingProvider(
                brandingOrganizationId,
                cache,
                snapshot,
                formCode,
                options.Value.SystemOrganizationId,
                libraryId);
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
        return new DbSettingProvider(organizationId, cache, GetRequestSnapshot(httpContext), formCode, options.Value.SystemOrganizationId);
    }

    private static readonly object SnapshotItemKey = new();

    private CacheSnapshot GetRequestSnapshot(HttpContext httpContext)
    {
        if (httpContext.Items.TryGetValue(SnapshotItemKey, out var existing) && existing is CacheSnapshot snapshot)
        {
            return snapshot;
        }

        var captured = CacheSnapshot.Capture(cache);
        httpContext.Items[SnapshotItemKey] = captured;
        return captured;
    }
}
