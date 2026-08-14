using Clc.PatronRegistration.Configuration;

namespace Clc.PatronRegistration;

public sealed record AgeBlockResult(bool IsBlocked, string Message);

public static class AgeBlockPolicy
{
    public const int MinimumAge = 18;

    public static AgeBlockResult Evaluate(
        ISettingProvider settings,
        DateTime? birthdate,
        DateOnly asOf) =>
        !settings.EnableAgeBlock || !birthdate.HasValue
            ? new(false, string.Empty)
            : DateOnly.FromDateTime(birthdate.Value) > asOf.AddYears(-MinimumAge)
                ? new(true, settings.AgeBlockText)
                : new(false, string.Empty);

    public static AgeBlockResult Evaluate(ISettingProvider settings, DateTime? birthdate) =>
        Evaluate(settings, birthdate, DateOnly.FromDateTime(DateTime.Today));
}
