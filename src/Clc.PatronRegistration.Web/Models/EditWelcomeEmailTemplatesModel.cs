namespace Clc.PatronRegistration.Web.Models
{
    public class EditWelcomeEmailTemplatesModel
    {
        public string Subject { get; set; } = string.Empty;
        public string HtmlTemplate { get; set; } = string.Empty;
        public string TextTemplate { get; set; } = string.Empty;
    }
}
