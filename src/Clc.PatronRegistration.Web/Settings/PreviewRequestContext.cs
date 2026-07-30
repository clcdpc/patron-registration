using Clc.PatronRegistration.Administration;
using Clc.PatronRegistration.Helpers;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace Clc.PatronRegistration.Web.Settings;

public sealed record PreviewRequestContext(PreviewLinkRecord Link, SettingDraft Draft, PreviewSettingProvider Settings);

public interface IPreviewRequestContextAccessor
{
    bool IsPreviewRequest { get; set; }
    string? PlaintextToken { get; set; }
    PreviewRequestContext? Current { get; set; }
}

public sealed class PreviewRequestContextAccessor : IPreviewRequestContextAccessor
{
    public bool IsPreviewRequest { get; set; }
    public string? PlaintextToken { get; set; }
    public PreviewRequestContext? Current { get; set; }
}

public interface IPreviewContextResolver
{
    PreviewRequestContext? Resolve(string token);
}

public sealed class PreviewContextResolver(
    ISettingsAdministrationRepository repository,
    IPreviewTokenService tokenService,
    IPreviewBranchEligibilityService branchEligibility,
    ICache cache,
    IOptions<SettingsAdministrationOptions> options) : IPreviewContextResolver
{
    public PreviewRequestContext? Resolve(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }
        var snapshot = repository.ResolvePreviewContext(tokenService.Hash(token), DateTime.UtcNow);
        if (snapshot is null)
        {
            return null;
        }
        var link = snapshot.Link;
        var draft = snapshot.Draft;
        if (!branchEligibility.IsEligible(draft.OrganizationId, link.OperationalBranchId, options.Value.SystemOrganizationId))
        {
            return null;
        }
        return new(link, draft, new PreviewSettingProvider(draft, link.OperationalBranchId, cache, options.Value.SystemOrganizationId));
    }
}

public sealed class PreviewRequestContextMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext httpContext,
        IPreviewRequestContextAccessor accessor,
        IPreviewContextResolver resolver)
    {
        var controller = httpContext.GetRouteValue("controller")?.ToString();
        if (!string.Equals(controller, "Preview", StringComparison.OrdinalIgnoreCase))
        {
            await next(httpContext);
            return;
        }

        accessor.IsPreviewRequest = true;
        var token = httpContext.GetRouteValue("token")?.ToString();
        accessor.PlaintextToken = token;
        RedactPreviewTarget(httpContext);
        accessor.Current = token is null ? null : resolver.Resolve(token);
        if (accessor.Current is null)
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            httpContext.Response.Headers.CacheControl = "no-store";
            httpContext.Response.Headers["Referrer-Policy"] = "no-referrer";
            return;
        }
        await next(httpContext);
    }

    public static void RedactPreviewTarget(HttpContext context)
    {
        const string redactedPath = "/preview/[redacted]";
        context.Request.Path = redactedPath;
        context.Request.RouteValues["token"] = "[redacted]";
        var requestFeature = context.Features.Get<IHttpRequestFeature>();
        if (requestFeature is not null)
        {
            requestFeature.RawTarget = redactedPath;
        }
    }
}
