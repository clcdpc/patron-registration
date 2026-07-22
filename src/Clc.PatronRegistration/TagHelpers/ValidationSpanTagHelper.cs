using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Clc.PatronRegistration.TagHelpers
{
    [HtmlTargetElement("span", Attributes = "asp-validation-for")]
    public class ValidationSpanTagHelper : TagHelper
    {
        public override void Process(TagHelperContext context, TagHelperOutput output)
        {
            output.MergeAttribute("class", "danger");
        }
    }
}
