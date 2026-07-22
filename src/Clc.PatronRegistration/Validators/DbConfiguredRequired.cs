using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.DependencyInjection;
using Clc.PatronRegistration.Configuration;
using Clc.PatronRegistration;

namespace Clc.PatronRegistration.Validators
{
    public class DbConfiguredRequired : ValidationAttribute, IClientModelValidator
    {
        protected override ValidationResult IsValid(object? value, ValidationContext context)
        {
            var settings = context.GetService<ISettingProvider>()!;
            var reg = context.ObjectInstance as Registration;

            if (reg == null) { return new ValidationResult("invalid model object"); }

            var memberName = context.MemberName ?? string.Empty;
            return settings.GetFieldRequired(memberName) && string.IsNullOrWhiteSpace(value?.ToString())
                ? new ValidationResult($"{settings.GetFieldLabel(memberName)} is required.")
                : ValidationResult.Success!;
        }

        public void AddValidation(ClientModelValidationContext context)
        {
            var settings = context.GetService<ISettingProvider>()!;

            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            context.Attributes.TryAdd("data-val", "true");
            context.Attributes.TryAdd("data-val-dbrequired", $"{settings.GetFieldLabel(context.ModelMetadata.Name ?? "")} is required.");
        }
    }
}
