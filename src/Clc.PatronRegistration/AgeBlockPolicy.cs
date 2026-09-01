using Clc.PatronRegistration.Administration;
using Clc.PatronRegistration.Configuration;

namespace Clc.PatronRegistration;

public sealed record AgeBlockResult(bool IsBlocked, string Message);

public static class AgeBlockPolicy
{
    public const int MinimumAge = 18;

    public static AgeBlockResult Evaluate(ISettingProvider settings, DateTime? birthdate, DateOnly asOf)
    {
        if (!settings.EnableAgeBlock || !birthdate.HasValue)
        {
            return new(false, string.Empty);
        }

        var birthdateOnly = DateOnly.FromDateTime(birthdate.Value);
        if (birthdateOnly > asOf)
        {
            return new(false, string.Empty);
        }

        var isBlocked = birthdateOnly > asOf.AddYears(-MinimumAge);
        return new(isBlocked, isBlocked ? SafeHtmlPolicy.Sanitize(settings.AgeBlockText) : string.Empty);
    }

    public static AgeBlockResult Evaluate(ISettingProvider settings, DateTime? birthdate) =>
        Evaluate(settings, birthdate, DateOnly.FromDateTime(DateTime.Today));
}
