namespace Clc.PatronRegistration
{
    public enum RegistrationStatus
    {
        Success = 1,
        Error,
        Duplicate,
        ZipMismatch,
        ZipMismatchRetry,
        Disabled
    }
}
