using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Clc.PatronRegistration
{
    public class RegistrationAttempt
    {
        public List<KeyValuePair<string, string>> Errors { get; set; } = new List<KeyValuePair<string, string>>();
        public string Message { get; set; } = string.Empty;
        public RegistrationStatus Status { get; set; }
        public bool IsSuccess => (int)Status == 1;

        public static List<KeyValuePair<string, string>> ErrorsFromModelState(ModelStateDictionary modelState) =>
            modelState
                .SelectMany(entry => entry.Value?.Errors.Select(error => new KeyValuePair<string, string>(
                    entry.Key,
                    string.IsNullOrWhiteSpace(error.ErrorMessage) ? "The submitted value is invalid." : error.ErrorMessage)) ?? [])
                .Distinct()
                .ToList();
    }
}
