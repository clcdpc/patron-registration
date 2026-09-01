using System.Globalization;
using Clc.PatronRegistration.Administration;

namespace Clc.PatronRegistration.Configuration;

public sealed record ResetSecondsSettingResult(BoundedIntegerSettingState State, int? Value);

public static class ResetSecondsSettingParser
{
    public static ResetSecondsSettingResult Parse(string? value)
    {
        if (value is null || value.Length == 0)
        {
            return new(BoundedIntegerSettingState.Unconfigured, null);
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) &&
               seconds is >= 0 and <= SettingDefinition.MaximumResetSeconds
            ? new(BoundedIntegerSettingState.Valid, seconds)
            : new(BoundedIntegerSettingState.Invalid, null);
    }

    public static int Normalize(int seconds) =>
        seconds is >= 0 and <= SettingDefinition.MaximumResetSeconds ? seconds : 0;

    public static long ToJavaScriptMilliseconds(int seconds) => Normalize(seconds) * 1_000L;
}

public interface IResetSecondsSettingStateProvider
{
    ResetSecondsSettingResult GetResetSecondsState();
}
