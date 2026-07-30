namespace Clc.PatronRegistration.Administration;

public static class FormCodeNormalizer
{
    public static string Normalize(string? formCode) => formCode ?? string.Empty;
}
