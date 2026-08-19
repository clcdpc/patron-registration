using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.DependencyInjection;
using Clc.PatronRegistration.Configuration;
using Clc.PatronRegistration.Data;
using Clc.PatronRegistration.Helpers;

namespace Clc.PatronRegistration.Validators
{
    public class VerifyDeliveryOptionAttribute : ValidationAttribute, IClientModelValidator
    {
        protected override ValidationResult IsValid(object? value, ValidationContext context)
        {
            if (context.ObjectInstance is not Registration reg) { return new ValidationResult("invalid model object"); }

            var isDoField = (context.MemberName ?? "") == nameof(reg.DeliveryOptionId);

            switch (reg.DeliveryOptionId)
            {
                case 2:
                    return string.IsNullOrWhiteSpace(reg.EmailAddress)
                        ? new ValidationResult(isDoField ? "Delivery method value not supplied" : "Email is required if selected for notice delivery")
                        : ValidationResult.Success!;
                case 3:
                    return string.IsNullOrWhiteSpace(reg.PhoneVoice1)
                        ? new ValidationResult(isDoField ? "Delivery method value not supplied" : "Phone is required if selected for notice delivery")
                        : ValidationResult.Success!;
                case 8:
                    goto case 3;
                default:
                    return ValidationResult.Success!;
            }
        }

        public void AddValidation(ClientModelValidationContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            var settings = RegistrationSettingsContext.Get(
                context.ActionContext.HttpContext,
                context.GetService<ISettingProvider>()!);
            var label = settings.GetFieldLabel(context.ModelMetadata.Name ?? "");

            context.Attributes.TryAdd("data-val", "true");
            var message = "";

            if (context.ModelMetadata.Name == nameof(Registration.DeliveryOptionId))
            {
                message = "Delivery method value not supplied";
            }
            else
            {
                if (string.IsNullOrWhiteSpace(label))
                {
                    message = "Required if selected for notice delivery";
                }
                else
                {
                    message = $"{label} is required if selected for notice delivery";
                }
            }

            context.Attributes.TryAdd("data-val-verifydeliveryoption", message);
        }
    }

    public class VerifyEmailProvidedForEreceiptsAttribute : ValidationAttribute, IClientModelValidator
    {
        protected override ValidationResult IsValid(object? value, ValidationContext context)
        {
            if (context.ObjectInstance is not Registration reg) { return new ValidationResult("invalid model object"); }

            if (reg.ReceiveEreceipts && string.IsNullOrWhiteSpace(reg.EmailAddress))
            {
                return new ValidationResult("Email is required to receive e-receipts");
            }
            else
            {
                return ValidationResult.Success!;
            }
        }

        public void AddValidation(ClientModelValidationContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            context.Attributes.TryAdd("data-val", "true");
            context.Attributes.TryAdd("data-val-verifyereceiptemail", "Email is required to receive e-receipts");
        }
    }
}
