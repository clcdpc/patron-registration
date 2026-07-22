namespace Clc.PatronRegistration.Web.Models
{
    public class DupeCheckResult
    {
        public bool IsDupe { get; set; }
        public string Message { get; set; } = "";

        public static DupeCheckResult False() => new() { IsDupe = false };
        public static DupeCheckResult True(string message) => new() { IsDupe = true, Message = message };
    }
}
