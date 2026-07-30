using Clc.PatronRegistration.Configuration;
using Clc.PatronRegistration.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

namespace Clc.PatronRegistration.Security
{
    public class IsClcUserRequirement : IAuthorizationRequirement
    {

    }

    public class IsClcUserCheckHandler : AuthorizationHandler<IsClcUserRequirement>, IAuthorizationRequirement
    {
        AuthDbHelper db;

        public IsClcUserCheckHandler(AppSettings settings) : this(settings.Database.Hostname, settings.ApplicationName)
        {

        }

        public IsClcUserCheckHandler(string dbHostname, string appName)
        {
            db = new AuthDbHelper(dbHostname, appName);
        }

        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, IsClcUserRequirement requirement)
        {
            var ci = (ClaimsIdentity)context.User.Identity;
            if (ci.IsAuthenticated)
            {
                var domain = context.User.Identity.Name.Split('@')[1];
                if (ci.IsAuthenticated && db.GetDomains().Any(d => d.Equals(domain, StringComparison.OrdinalIgnoreCase))) { context.Succeed(requirement); }
            }
            return Task.FromResult(0);
        }
    }
}