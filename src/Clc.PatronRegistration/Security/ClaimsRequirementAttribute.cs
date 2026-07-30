using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;

namespace Clc.PatronRegistration.Security
{
    //public class ClaimRequirementAttribute : TypeFilterAttribute
    //{
    //    public ClaimRequirementAttribute(string claimType, string claimValue) : base(typeof(ClaimRequirementFilter))
    //    {
    //        Arguments = new object[] { new Claim(claimType, claimValue) };
    //    }
    //}

    //public class ClaimRequirementFilter : IAuthorizationFilter
    //{
    //    readonly Claim _claim;

    //    public ClaimRequirementFilter(Claim claim)
    //    {
    //        _claim = claim;
    //    }

    //    public void OnAuthorization(AuthorizationFilterContext context)
    //    {
    //        var hasClaim = context.HttpContext.User.Claims.Any(c => c.Type.Equals(_claim.Type, StringComparison.OrdinalIgnoreCase) && c.Value.Equals(_claim.Value, StringComparison.OrdinalIgnoreCase));
    //        if (!hasClaim)
    //        {
    //            context.Result = new ContentResult { Content = $"You require the claim {_claim.Value} to view this resource.", ContentType = "text/html", StatusCode = 403 };// new ForbidResult();
    //        }
    //    }
    //}

    public class HasRoleClaimAttribute : TypeFilterAttribute
    {
        public HasRoleClaimAttribute(string claimValue) : base(typeof(ClaimRequirementFilter))
        {
            Arguments = new object[] { new Claim("Role", claimValue) };
        }
    }

    public class ClaimRequirementFilter : IAuthorizationFilter
    {
        readonly Claim _claim;

        public ClaimRequirementFilter(Claim claim)
        {
            _claim = claim;
        }

        public void OnAuthorization(AuthorizationFilterContext context)
        {
            var hasClaim = context.HttpContext.User.Claims.Any(c => c.Type.Equals(_claim.Type, StringComparison.OrdinalIgnoreCase) && c.Value.Equals(_claim.Value, StringComparison.OrdinalIgnoreCase));
            if (!hasClaim)
            {
                //context.Result = new ContentResult { Content = $"You require the claim {_claim.Value} to view this resource.", ContentType = "text/html", StatusCode = 403 };// new ForbidResult();
                context.Result = new ForbidResult();
            }
        }
    }
}
