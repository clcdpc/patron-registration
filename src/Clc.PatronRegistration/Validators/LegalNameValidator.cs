using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.DependencyInjection;
using Clc.PatronRegistration.Configuration;
using Clc.PatronRegistration;
using Clc.PatronRegistration.Helpers;
using System.Diagnostics.CodeAnalysis;

namespace Clc.PatronRegistration.Validators
{
    public class LegalNameValidator : ValidationAttribute, IClientModelValidator
    {
        protected override ValidationResult IsValid(object? value, ValidationContext context)
        {
            if (context.ObjectInstance is not Registration reg) { return new ValidationResult("invalid model object"); }

            var settings = reg.Settings ?? context.GetService<ISettingProvider>()!;

            if (!reg.UseLegalName) { return ValidationResult.Success!; }

            return string.IsNullOrWhiteSpace(value?.ToString())
                ? new ValidationResult($"{settings.GetFieldLabel(context.MemberName ?? "")} is required.")
                : ValidationResult.Success!;
        }

        public void AddValidation(ClientModelValidationContext context)
        {
            var settings = RegistrationSettingsContext.Get(
                context.ActionContext.HttpContext,
                context.GetService<ISettingProvider>()!);

            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            context.Attributes.TryAdd("data-val", "true");
            context.Attributes.TryAdd("data-val-legalname", $"{settings.GetFieldLabel(context.ModelMetadata.Name ?? "")} is required.");
        }
    }
}
