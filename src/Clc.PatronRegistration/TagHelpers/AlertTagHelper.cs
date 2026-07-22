using Clc.PatronRegistration.Configuration;
using Microsoft.AspNetCore.Mvc.Razor.TagHelpers;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using System.Text.Encodings.Web;

namespace Clc.PatronRegistration.TagHelpers
{
    [HtmlTargetElement("clcalert")]
    public class AlertTagHelper : UrlResolutionTagHelper
    {
        ISettingProvider Settings { get; set; } = default!;
        public AlertTagHelper(IUrlHelperFactory urlHelperFactory, HtmlEncoder htmlEncoder, ISettingProvider _settings) : base(urlHelperFactory, htmlEncoder)
        {
            Settings = _settings;
        }

        [HtmlAttributeName("asp-for")]
        public ModelExpression For { get; set; } = default!;

        [ViewContext]
        [HtmlAttributeNotBound]
        public new ViewContext ViewContext { get; set; } = default!;

        public override void Process(TagHelperContext context, TagHelperOutput output)
        {
            var urlHelper = UrlHelperFactory.GetUrlHelper(ViewContext);

            var groupSpan = new TagBuilder("span");
            groupSpan.Attributes.Add("id", $"{For.Name}AlertGroup");
            groupSpan.Attributes.Add("class", "regTooltip regAlert");

            var img = new TagBuilder("img");
            img.Attributes.Add("src", urlHelper.FullUrl("~/image/alert-icon-red-25x25.png"));

            var infoSpan = new TagBuilder("span");
            infoSpan.Attributes.Add("id", For.Name);
            infoSpan.Attributes.Add("class", "regTooltipText");
            infoSpan.InnerHtml.Append(Settings.GetFieldErrorMessage(For.Name));

            groupSpan.InnerHtml.AppendHtml(img);
            groupSpan.InnerHtml.AppendHtml(infoSpan);


            output.Content.SetHtmlContent(groupSpan);
        }
    }
}
