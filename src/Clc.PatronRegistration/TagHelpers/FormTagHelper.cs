using Clc.PatronRegistration.Configuration;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Clc.PatronRegistration.TagHelpers
{
    [HtmlTargetElement("form", Attributes = "asp-for")]
    public class FormTagHelper : TagHelper
    {
        [HtmlAttributeName("asp-for")]
        public ModelExpression For { get; set; } = default!;

        private ISettingProvider Settings;

        public FormTagHelper(ISettingProvider settings)
        {
            Settings = settings;
        }

        public override void Process(TagHelperContext context, TagHelperOutput output)
        {
            if (!string.IsNullOrWhiteSpace(Settings.WarningText))
            {
                output.MergeAttribute("class", "hidden");
                output.MergeAttribute("aria-hidden", "true");
            }
        }
    }
}
