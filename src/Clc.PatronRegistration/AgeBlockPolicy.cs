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

        var isBlocked = DateOnly.FromDateTime(birthdate.Value) > asOf.AddYears(-MinimumAge);
        return new(isBlocked, isBlocked ? settings.AgeBlockText : string.Empty);
    }

    public static AgeBlockResult Evaluate(ISettingProvider settings, DateTime? birthdate) =>
        Evaluate(settings, birthdate, DateOnly.FromDateTime(DateTime.Today));
}
