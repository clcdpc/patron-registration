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
public sealed record SettingDraft(long DraftId, int OrganizationId, string FormCode, long BaselineVersion, DraftStatus Status, IReadOnlyList<SettingMutation> Changes);
