using Azure.Core;
using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Clc.PatronRegistration.Helpers;

namespace Clc.PatronRegistration.Configuration
{
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
    public class DbConfiguredDisplayNameAttribute([CallerMemberName] string propertyName = "") : DisplayNameAttribute
    {
        public override string DisplayName => new HttpContextAccessor().HttpContext!.RequestServices.GetService<ISettingProvider>()!.GetFieldLabel(propertyName);
    }
}
