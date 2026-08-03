using System.Globalization;
using Clc.PatronRegistration.Administration;

namespace Clc.PatronRegistration.Configuration;

public enum BoundedIntegerSettingState { Unconfigured, Valid, Invalid }

public sealed record ExpirationDateYearsSettingResult(BoundedIntegerSettingState State, int? Value);

public static class ExpirationDateYearsSettingParser
{
    public static ExpirationDateYearsSettingResult Parse(string? value)
    {
        if (value is null || value.Length == 0)
            return new(BoundedIntegerSettingState.Unconfigured, null);

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var years) &&
               years is >= 0 and <= SettingDefinition.MaximumExpirationDateYears
            ? new(BoundedIntegerSettingState.Valid, years)
            : new(BoundedIntegerSettingState.Invalid, null);
    }
}

public interface IExpirationDateYearsSettingStateProvider
{
    ExpirationDateYearsSettingResult GetExpirationDateYearsState();
}
