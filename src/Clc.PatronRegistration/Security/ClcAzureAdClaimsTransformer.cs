using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Security.Claims;
using System.Security.Principal;
using System.Threading.Tasks;
using Clc.PatronRegistration.Configuration;
using Clc.PatronRegistration.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace Clc.PatronRegistration.Security
{
    public class ClcAzureAdClaimsTransformer : IClaimsTransformation
    {
        readonly AuthDbHelper db;

        public ClcAzureAdClaimsTransformer(AppSettings settings) : this(settings.Database.Hostname, settings.ApplicationName)
        {

        }

        public ClcAzureAdClaimsTransformer(string dbHostname, string appName)
        {
            AppSettings.Require(dbHostname);
            db = new AuthDbHelper(dbHostname, appName);
        }

        public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
        {
            var ci = (ClaimsIdentity)principal.Identity;
            var roles = db.GetRolesForUser(ci.Name);

            foreach (var role in roles)
            {
                ci.AddClaim(new Claim(ci.RoleClaimType, role));
            }

            ci.GetGroups().Where(g => g.Value.StartsWith("Clc.", StringComparison.OrdinalIgnoreCase)).ToList().ForEach(g => ci.AddClaim(new Claim(ClaimTypes.Role, g.Value)));

            ci.AddClaim(new Claim("Clc.OrganizationId", db.GetOrgForUser(ci.Name).ToString()));            

            return Task.FromResult(principal);
        }
    }

    public static class IIdentityExtensions
    {
        public static string GetPreferredUsername(this IIdentity identity)
        {
            var ci = (ClaimsIdentity)identity;

            var username = ci?.FindFirst("preferred_username")?.Value ?? identity?.Name ?? "";
            return username;
        }

        public static string GetDomain(this IIdentity identity)
        {
            return identity.GetPreferredUsername().Split('@')[1];
        }

        public static T GetClaim<T>(this IIdentity identity, string claimName)
        {
            var ci = (ClaimsIdentity)identity;
            return (T)TypeDescriptor.GetConverter(typeof(T)).ConvertFromInvariantString(ci.FindFirst(claimName)?.Value?.ToString());
        }
        public static string GetPreferredUsername(this ClaimsPrincipal principal) => principal.Identity?.GetClaim<string>("preferred_username");
        public static int? GetClaimOrganization(this ClaimsPrincipal principal) => principal.Identity?.GetClaim<int?>("Clc.OrganizationId");
        public static int? GetNothing(this ClaimsPrincipal principal) => principal.Identity?.GetClaim<int?>("Clc.asfddsafsad");

        public static string GetPreferredUsername(this IPrincipal principal)
        {
            var ci = (ClaimsIdentity)principal.Identity;

            var username = ci?.FindFirst("preferred_username")?.Value ?? principal?.Identity?.Name ?? "";
            return username;
        }

        //public static int? GetClaimOrganization(this IPrincipal principal) => principal.Identity.GetClaim<int?>("Clc.OrganizationId");
        public static int? GetClaimOrganization(this IIdentity identity) => ((ClaimsIdentity)identity).GetClaim<int?>("Clc.OrganizationId");

        public static T GetClaim<T>(this IPrincipal principal, string claimName)
        {
            var cp = (ClaimsPrincipal)principal;
            
            try { return (T)Convert.ChangeType(cp.FindFirst(claimName), typeof(T)); }
            catch { return default; }
        }
    }
}