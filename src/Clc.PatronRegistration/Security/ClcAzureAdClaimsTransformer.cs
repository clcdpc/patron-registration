using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
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
        private const string OrganizationClaimType = "Clc.OrganizationId";

        readonly IAuthDbHelper db;

        public ClcAzureAdClaimsTransformer(AppSettings settings) : this(settings.Database.Hostname, settings.ApplicationName)
        {

        }

        public ClcAzureAdClaimsTransformer(string dbHostname, string appName)
        {
            AppSettings.Require(dbHostname);
            db = new AuthDbHelper(dbHostname, appName);
        }

        public ClcAzureAdClaimsTransformer(IAuthDbHelper db)
        {
            this.db = db ?? throw new ArgumentNullException(nameof(db));
        }

        public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
        {
            if (principal.Identity is not ClaimsIdentity ci)
            {
                return Task.FromResult(principal);
            }

            var loginIdentifier = GetLoginIdentifier(ci);
            var roles = db.GetRolesForUser(loginIdentifier);

            foreach (var role in roles)
            {
                if (!string.IsNullOrWhiteSpace(role))
                {
                    AddClaimIfMissing(ci, ci.RoleClaimType, role);
                }
            }

            ci.GetGroups()
                .Where(g => g.Value.StartsWith("Clc.", StringComparison.OrdinalIgnoreCase))
                .ToList()
                .ForEach(g => AddClaimIfMissing(ci, ClaimTypes.Role, g.Value));

            foreach (var claim in ci.Claims
                .Where(claim => string.Equals(claim.Type, OrganizationClaimType, StringComparison.OrdinalIgnoreCase))
                .ToList())
            {
                ci.RemoveClaim(claim);
            }

            var organizationId = db.GetOrgForUser(loginIdentifier);
            if (organizationId.HasValue)
            {
                ci.AddClaim(new Claim(OrganizationClaimType, organizationId.Value.ToString(CultureInfo.InvariantCulture)));
            }

            return Task.FromResult(principal);
        }

        private static string? GetLoginIdentifier(ClaimsIdentity identity)
        {
            return string.IsNullOrWhiteSpace(identity.Name)
                ? identity.FindFirst("preferred_username")?.Value
                : identity.Name;
        }

        private static void AddClaimIfMissing(ClaimsIdentity identity, string claimType, string claimValue)
        {
            if (!identity.HasClaim(claimType, claimValue))
            {
                identity.AddClaim(new Claim(claimType, claimValue));
            }
        }
    }

    public static class IIdentityExtensions
    {
        public static string GetPreferredUsername(this IIdentity identity)
        {
            var ci = identity as ClaimsIdentity;

            var username = ci?.FindFirst("preferred_username")?.Value ?? identity?.Name ?? "";
            return username;
        }

        public static string GetDomain(this IIdentity identity)
        {
            return AuthDbHelper.TryGetEmailDomain(identity.GetPreferredUsername(), out var domain) ? domain : "";
        }

        public static T GetClaim<T>(this IIdentity identity, string claimName)
        {
            var ci = (ClaimsIdentity)identity;
            return (T)TypeDescriptor.GetConverter(typeof(T)).ConvertFromInvariantString(ci.FindFirst(claimName)?.Value?.ToString());
        }
        public static string GetPreferredUsername(this ClaimsPrincipal principal) => principal?.Identity?.GetPreferredUsername() ?? "";
        public static int? GetClaimOrganization(this ClaimsPrincipal principal) => principal.Identity?.GetClaim<int?>("Clc.OrganizationId");
        public static int? GetNothing(this ClaimsPrincipal principal) => principal.Identity?.GetClaim<int?>("Clc.asfddsafsad");

        public static string GetPreferredUsername(this IPrincipal principal)
        {
            return principal?.Identity?.GetPreferredUsername() ?? "";
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
