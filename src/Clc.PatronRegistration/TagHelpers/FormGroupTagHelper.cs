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
    [HtmlTargetElement("div", Attributes = "asp-for")]
    [HtmlTargetElement("span", Attributes = "asp-for")]
    public class FormGroupTagHelper : TagHelper
    {
        [HtmlAttributeName("asp-for")]
        public ModelExpression For { get; set; } = default!;

        public override void Process(TagHelperContext context, TagHelperOutput output)
        {
            output.MergeAttribute("id", $"{For.Name}FieldGroup");
            output.MergeAttribute("class", "field-group");
        }
    }
}
