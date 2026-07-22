using Clc.Postmark;
using Clc.Postmark.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Clc.PatronRegistration
{
    public interface IEmailSender
    {
        bool Send(string To, string From, string ReplyTo, string Subject, string HtmlBody, string TextBody);
    }
    public class PostmarkEmailSender : IEmailSender
    {
        PostmarkClient postmark;
        public PostmarkEmailSender(string apiKey)
        {
            postmark = new PostmarkClient(apiKey);
        }
        public bool Send(string to, string from, string replyTo, string subject, string htmlBody, string textBody)
        {
            var message = new EmailMessage { To = new[] { to }, From = from, Subject = subject, HtmlBody = string.IsNullOrWhiteSpace(htmlBody) ? textBody : htmlBody, TextBody = textBody ?? "" };
            var result = postmark.Send(message, replyTo);
            return result?.Data?.ErrorCode == 0;
        }
    }
}
