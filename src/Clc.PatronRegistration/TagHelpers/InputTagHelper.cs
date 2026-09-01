using Clc.PatronRegistration.Configuration;
using Clc.PatronRegistration.Helpers;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using System.ComponentModel.DataAnnotations;

namespace Clc.PatronRegistration.TagHelpers
{
    [HtmlTargetElement("input", Attributes = "asp-for")]
    [HtmlTargetElement("select", Attributes = "asp-for")]
    public class InputTagHelper : TagHelper
    {
        [HtmlAttributeName("asp-for")]
        public ModelExpression For { get; set; } = default!;

        [ViewContext]
        [HtmlAttributeNotBound]
        public ViewContext ViewContext { get; set; } = default!;

        ISettingProvider Settings;

        public InputTagHelper(ISettingProvider settings)
        {
            Settings = settings;
        }

        public override void Process(TagHelperContext context, TagHelperOutput output)
        {
            var settings = RegistrationSettingsContext.Get(ViewContext.HttpContext, Settings);
            var metadata = For.Metadata;
            var hasRequiredAttribute = For.Metadata.ContainerType?.GetProperty(For.Name)?.CustomAttributes.Any(ca => ca.AttributeType == typeof(RequiredAttribute)) ?? false;

            if ((metadata.ModelType.IsValueType && hasRequiredAttribute)
                || (!metadata.ModelType.IsValueType && metadata.IsRequired)
                || settings.GetFieldRequired(For.Name))
            {
                output.MergeAttribute("aria-required", "true");
            }
        }
    }
}
