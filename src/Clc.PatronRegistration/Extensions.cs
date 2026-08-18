using Clc.PatronRegistration;
using Clc.PatronRegistration.Helpers;
using Clc.PatronRegistration.Security;
using Clc.Polaris.Api.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.TagHelpers;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;


namespace Clc.PatronRegistration
{
    public static class ServiceProviderExtensions
    {
        public static T ResolveWith<T>(this IServiceProvider provider, params object[] parameters) where T : class =>
            ActivatorUtilities.CreateInstance<T>(provider, parameters);

        public static WebApplicationBuilder AddClcConfigFolder(this WebApplicationBuilder builder)
        {
            builder.Configuration.SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("Config\\settings.json", false, true)
                .AddJsonFile($"Config\\settings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")}.json", false, true)
                .AddEnvironmentVariables();

            return builder;
        }

        private static readonly string[] publicWebBrowserUserAgentIndicators = ["pwb", "sitekiosk"];

        public static bool IsFromPublicWebBrowser(this HttpRequest request) => publicWebBrowserUserAgentIndicators.Any(ua => request.Headers.UserAgent.Any(s => s?.IndexOf(ua, StringComparison.OrdinalIgnoreCase) > 0));

        public static string GetTrueClientIp(this HttpContext context) => context.Request.GetTrueClientIP();

        private static readonly string[] localAndOplinIpRanges = ["10.", "172.16", "172.17", "172.18", "172.19", "172.20", "172.21", "172.22", "172.23", "172.24", "172.25", "172.26", "172.27", "172.28", "172.29", "172.20", "172.31", "172.32", "192.168", "66.213", "127", "::1"];
        public static bool IsFromLocalOrOplinIp(this HttpContext context) => localAndOplinIpRanges.Any(i => context.GetTrueClientIp().StartsWith(i));

        public static string GetApplicationUrl(this ViewContext ViewContext)
        {
            return $"{ViewContext.HttpContext.Request.Scheme}://{ViewContext.HttpContext.Request.Host}";
        }

        public static bool IsInjectedForm(this HttpContext context) => context.Request.Method.Equals("post", StringComparison.OrdinalIgnoreCase);

        public static string BaseUrl(this IUrlHelper helper)
        {
            var url = string.Format("{0}://{1}", helper.ActionContext.HttpContext.Request.Scheme, helper.ActionContext.HttpContext.Request.Host.ToUriComponent());
            return url;
        }

        public static string FullUrl(this IUrlHelper helper, string virtualPath)
        {
            var url = string.Format("{0}://{1}{2}", helper.ActionContext.HttpContext.Request.Scheme, helper.ActionContext.HttpContext.Request.Host.ToUriComponent(), helper.Content(virtualPath));

            return url;
        }

        public static string BuildUrl(this IUrlHelper helper, string virtualPath)
        {
            var prefix = "";
            if (virtualPath.IsRelativeUrl())
            {
                prefix = string.Format("{0}://{1}", helper.ActionContext.HttpContext.Request.Scheme, helper.ActionContext.HttpContext.Request.Host.ToUriComponent());
            }
            var url = string.Format("{0}{1}", prefix, helper.Content(virtualPath));

            return url;
        }

        public static string FormatTemplate(this object obj, string template)
        {
            if (string.IsNullOrWhiteSpace(template)) { return ""; }

            var output = template;
            var matches = Regex.Matches(template, "{{(.*?)}}");
            foreach (Match match in matches)
            {
                var value = obj.GetPropertyValue<string>(match.Groups[1].Value);
                output = output.Replace(match.Groups[0].Value, value);
            }

            return output;
        }

        public static T GetPropertyValue<T>(this object obj, string property) => (T)obj.GetType().GetProperty(property)?.GetValue(obj, null)! ?? default!;
        

        public static string ToJavascriptBool(this bool value)
        {
            return value.ToString().ToLower();
        }

        public static string BuildAction(this RazorPageBase page, string path)
        {            
            _ = int.TryParse(page.ViewContext.HttpContext.Request.RouteValues["orgId"]?.ToString(), out int orgId);
            var formCode = page.ViewContext.HttpContext.Request.RouteValues["formCode"];

            var urlHelperFactory = page.ViewContext.HttpContext.RequestServices.GetRequiredService<IUrlHelperFactory>();
            var urlHelper = urlHelperFactory.GetUrlHelper(page.ViewContext);
            var url = urlHelper.BuildUrl(urlHelper.Action(path, new { orgId, formCode }) ?? "");
            return url;
        }

        public static string BuildUrl(this RazorPageBase page, string path)
        {
            var url = new UrlHelper(page.ViewContext);
            return url.BuildUrl(path);
        }

        public static void MergeAttribute(this TagHelperOutput output, string attributeName, string value)
        {
            var helper = new TagBuilder(output.TagName);
            helper.Attributes.Add(attributeName, value);

            output.MergeAttributes(helper);
        }

        public static bool IsRelativeUrl(this string url)
        {
            return url.StartsWith('~') || url.StartsWith('/');
        }

        public static T? GetService<T>(this ClientModelValidationContext context) => context.ActionContext.HttpContext.RequestServices.GetService<T>();

        public static string GetTrueClientIP(this HttpRequest request)
        {
            request.Headers.TryGetValue("CF-Connecting-IP", out var cfHeaderVal);
            var cfClientIp = cfHeaderVal.FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(cfClientIp))
            {
                return cfClientIp;
            }

            return request.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";
        }

        public static string ToJson(this object obj, JsonSerializerSettings? settings = null)
        {
            return settings == null ? JsonConvert.SerializeObject(obj) : JsonConvert.SerializeObject(obj, settings);
        }

        private static readonly JsonSerializerOptions javascriptStringOptions = new()
        {
            Encoder = JavaScriptEncoder.Default
        };

        public static string ToJavascriptString(this string s) =>
            System.Text.Json.JsonSerializer.Serialize(s ?? string.Empty, javascriptStringOptions);


        public static OrganizationsGetRow GetLibrary(this List<OrganizationsGetRow> orgs, int orgId)
        {
            var org = orgs.Single(o => o.OrganizationID == orgId);
            return org.OrganizationCodeID == 1 ? orgs.Single(o => o.OrganizationCodeID == 1) : org.OrganizationCodeID == 2 ? org : orgs.Single(o => o.OrganizationID == org.ParentOrganizationID);
        }


        public static bool IsAdmin(this ClaimsPrincipal user)
        {
            var ci = (ClaimsIdentity)user.Identity;
            return ci.HasClaim(ci.RoleClaimType, "Admin");
        }

        public static List<Claim> GetGroups(this ClaimsIdentity ci)
        {
            return ci.FindAll("groups").ToList();
        }

        public static IServiceCollection AddRequireAuthenticatedClcUser(this IServiceCollection services)
        {
            services.AddAuthorization(options =>
            {
                options.DefaultPolicy = new AuthorizationPolicyBuilder()
                   .RequireAuthenticatedUser()
                   .AddRequirements(new IsClcUserRequirement())
                   .Build();

                options.FallbackPolicy = options.DefaultPolicy;
            });

            return services;
        }
    }
}

