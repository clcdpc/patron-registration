using Clc.PatronRegistration.Configuration;
using Microsoft.AspNetCore.Http;

namespace Clc.PatronRegistration.Helpers;

/// <summary>
/// Carries the effective registration settings to view-time validators and tag helpers.
/// The value is request-scoped and contains configuration only; it never contains patron data.
/// </summary>
public static class RegistrationSettingsContext
{
    private static readonly object ItemKey = new();

    public static void Set(HttpContext httpContext, ISettingProvider settings) =>
        httpContext.Items[ItemKey] = settings;

    public static ISettingProvider Get(HttpContext httpContext, ISettingProvider fallback) =>
        httpContext.Items.TryGetValue(ItemKey, out var value) && value is ISettingProvider settings
            ? settings
            : fallback;
}
