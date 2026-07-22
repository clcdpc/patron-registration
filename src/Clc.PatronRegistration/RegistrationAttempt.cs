namespace Clc.PatronRegistration
{
    public class RegistrationAttempt
    {
        public List<KeyValuePair<string, string>> Errors { get; set; } = new List<KeyValuePair<string, string>>();
        public string Message { get; set; } = string.Empty;
        public RegistrationStatus Status { get; set; }
        public bool IsSuccess => (int)Status == 1;
    }
}