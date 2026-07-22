using Clc.PatronRegistration.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.AspNetCore.Mvc.Razor.Infrastructure;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.TagHelpers;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Clc.PatronRegistration.TagHelpers
{
    [HtmlTargetElement("link", Attributes = "href", TagStructure = TagStructure.WithoutEndTag)]
    public class LinkTagHelper : Microsoft.AspNetCore.Mvc.TagHelpers.LinkTagHelper
    {
        public IRegistrationConfiguration config { get; set; } = default!;

        public LinkTagHelper(IWebHostEnvironment hostingEnvironment, TagHelperMemoryCacheProvider cacheProvider, IFileVersionProvider fileVersionProvider, HtmlEncoder htmlEncoder, JavaScriptEncoder javaScriptEncoder, IUrlHelperFactory urlHelperFactory, IRegistrationConfiguration _config) : base(hostingEnvironment, cacheProvider, fileVersionProvider, htmlEncoder, javaScriptEncoder, urlHelperFactory)
        {
            config = _config;
        }

        public override void Process(TagHelperContext context, TagHelperOutput output)
        {
            base.Process(context, output);

            if (Href.IsRelativeUrl())
            {
                var urlHelper = UrlHelperFactory.GetUrlHelper(ViewContext);
                output.Attributes.SetAttribute("href", urlHelper.BuildUrl(Href));
            }
        }
    }
}
