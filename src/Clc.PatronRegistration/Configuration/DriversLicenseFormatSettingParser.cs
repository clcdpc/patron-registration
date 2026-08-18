namespace Clc.PatronRegistration.Configuration;

public enum DriversLicenseFormatSettingState { Unconfigured, Barcode, Magstripe, Invalid }

public sealed record DriversLicenseFormatSettingResult(DriversLicenseFormatSettingState State);

public static class DriversLicenseFormatSettingParser
{
    public const string Barcode = "barcode";
    public const string Magstripe = "magstripe";

    public static DriversLicenseFormatSettingResult Parse(string? value)
    {
        if (value is null || value.Length == 0)
        {
            return new(DriversLicenseFormatSettingState.Unconfigured);
        }

        if (value.Equals(Barcode, StringComparison.OrdinalIgnoreCase))
        {
            return new(DriversLicenseFormatSettingState.Barcode);
        }

        if (value.Equals(Magstripe, StringComparison.OrdinalIgnoreCase))
        {
            return new(DriversLicenseFormatSettingState.Magstripe);
        }

        return new(DriversLicenseFormatSettingState.Invalid);
    }
}

public interface IDriversLicenseFormatSettingStateProvider
{
    DriversLicenseFormatSettingResult GetDriversLicenseFormatState();
}
