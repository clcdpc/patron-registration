using System.ComponentModel.DataAnnotations;

namespace Clc.PatronRegistration.Validators;

public sealed class BirthdateNotInFutureAttribute : ValidationAttribute
{
    public BirthdateNotInFutureAttribute()
    {
        ErrorMessage = "Please enter a valid birth date.";
    }

    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value is not DateTime birthdate)
        {
            return ValidationResult.Success;
        }

        return DateOnly.FromDateTime(birthdate) <= DateOnly.FromDateTime(DateTime.Today)
            ? ValidationResult.Success
            : new ValidationResult(ErrorMessage, [validationContext.MemberName!]);
    }
}
