using Clc.PatronRegistration.Configuration;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using System.ComponentModel.DataAnnotations;
using System.Reflection;

namespace Clc.PatronRegistration.TagHelpers
{
    [HtmlTargetElement("label", Attributes = "asp-for")]
    [HtmlTargetElement("legend", Attributes = "asp-for")]
    public class LabelTagHelper : TagHelper
    {
        [HtmlAttributeName("asp-for")]
        public ModelExpression For { get; set; } = default!;

        ISettingProvider Settings;

        public LabelTagHelper(ISettingProvider settings)
        {
            Settings = settings;
        }

        public override void Process(TagHelperContext context, TagHelperOutput output)
        {
            var metadata = For.Metadata;
            var hasRequiredAttribute = For.Metadata.ContainerType?.GetProperty(For.Name)?.CustomAttributes.Any(ca => ca.AttributeType == typeof(RequiredAttribute)) ?? false;

            if ((metadata.ModelType.IsValueType && hasRequiredAttribute)
                || (!metadata.ModelType.IsValueType && metadata.IsRequired)
                || Settings.GetFieldRequired(For.Name))
            {
                output.MergeAttribute("class", "required");
            }

            output.Attributes.SetAttribute("id", $"{For.Name}{System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(output.TagName)}");
        }
    }
}
