using System.Globalization;

namespace Clc.PatronRegistration.Configuration;

public enum IdentifierSettingState
{
    Missing,
    Zero,
    Positive,
    Negative,
    Malformed
}

public sealed record IdentifierSettingResult(IdentifierSettingState State, int? Value)
{
    public bool IsPositive => State == IdentifierSettingState.Positive;
    public bool IsInvalid => State is IdentifierSettingState.Negative or IdentifierSettingState.Malformed;
}

public static class IdentifierSettingParser
{
    public static IdentifierSettingResult Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new(IdentifierSettingState.Missing, null);
        }
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return new(IdentifierSettingState.Malformed, null);
        }
        return parsed switch
        {
            > 0 => new(IdentifierSettingState.Positive, parsed),
            0 => new(IdentifierSettingState.Zero, 0),
            _ => new(IdentifierSettingState.Negative, parsed)
        };
    }
}

public interface IIdentifierSettingStateProvider
{
    IdentifierSettingResult GetIdentifierState(string key);
}
