using System.Security.Cryptography;

namespace Clc.PatronRegistration.Administration;

public sealed record PreviewToken(string Plaintext, byte[] Hash);

public interface IPreviewTokenService
{
    PreviewToken Create();
    byte[] Hash(string token);
    bool Matches(string token, byte[] expectedHash);
}

public sealed class PreviewTokenService : IPreviewTokenService
{
    public PreviewToken Create()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return new(token, SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
    }
    public byte[] Hash(string token) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
    public bool Matches(string token, byte[] expectedHash) => CryptographicOperations.FixedTimeEquals(Hash(token), expectedHash);
}

public static class SensitiveValueMasker
{
    public static string Mask(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value.Length == 1) return "…";
        var visible = Math.Min(8, value.Length / 2);
        var prefix = Math.Min(4, (visible + 1) / 2);
        var suffix = Math.Min(4, visible - prefix);
        return value[..prefix] + "…" + (suffix == 0 ? string.Empty : value[^suffix..]);
    }
}

public static class AuditValueFormatter
{
    public static string? Format(string? value, bool isSensitive) =>
        isSensitive ? SensitiveValueMasker.Mask(value) : value;
}

public enum DraftStatus { Active, Committed, Discarded, Invalidated }
public enum DraftOperation { Upsert, RemoveOverride }
public sealed record SettingMutation(string Key, DraftOperation Operation, string? Value);
public static class DraftOperationValidation
{
    public static bool IsSupported(DraftOperation operation) =>
        operation is DraftOperation.Upsert or DraftOperation.RemoveOverride;

    public static bool TryParseSupported(string? value, out DraftOperation operation)
    {
        if (value == nameof(DraftOperation.Upsert))
        {
            operation = DraftOperation.Upsert;
            return true;
        }
        if (value == nameof(DraftOperation.RemoveOverride))
        {
            operation = DraftOperation.RemoveOverride;
            return true;
        }
        operation = default;
        return false;
    }

    public static void RequireSupported(IEnumerable<SettingMutation> changes)
    {
        if (changes.Any(change => !IsSupported(change.Operation)))
        {
            throw new InvalidOperationException("A submitted setting operation is invalid.");
        }
    }
}
public sealed record SettingDraft(long DraftId, int OrganizationId, string FormCode, long BaselineVersion, DraftStatus Status, IReadOnlyList<SettingMutation> Changes);
