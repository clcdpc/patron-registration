using Clc.PatronRegistration.Configuration;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.TagHelpers;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using System.Text.Encodings.Web;

namespace Clc.PatronRegistration.TagHelpers
{
    [HtmlTargetElement("img", Attributes = "src", TagStructure = TagStructure.WithoutEndTag)]
    [HtmlTargetElement("script", Attributes = "src", TagStructure = TagStructure.NormalOrSelfClosing)]
    public class ImageTagHelper : Microsoft.AspNetCore.Mvc.TagHelpers.ImageTagHelper
    {

        public ImageTagHelper(IFileVersionProvider fileVersionProvider, HtmlEncoder htmlEncoder, IUrlHelperFactory urlHelperFactory) : base(fileVersionProvider, htmlEncoder, urlHelperFactory)
        {
        }

        public override void Process(TagHelperContext context, TagHelperOutput output)
        {
            base.Process(context, output);

            if (Src.IsRelativeUrl())
            {
                var urlHelper = UrlHelperFactory.GetUrlHelper(ViewContext);
                output.Attributes.SetAttribute("src", urlHelper.BuildUrl(Src));
            }
        }
    }
}
