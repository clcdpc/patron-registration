using System.Security.Cryptography;
using Dapper;
using Microsoft.Data.SqlClient;
using Clc.PatronRegistration.Configuration;

namespace Clc.PatronRegistration.Web.Settings;

public sealed record RegistrationFormAsset(
    int AssetId,
    string FileName,
    string ContentType,
    byte[] Content,
    string ContentHash,
    DateTime CreatedDate,
    DateTime ModifiedDate);

public sealed record RegistrationFormAssetMetadata(
    int AssetId,
    string FileName,
    string ContentType,
    string ContentHash,
    DateTime CreatedDate,
    DateTime ModifiedDate);

public interface IRegistrationFormAssetRepository
{
    RegistrationFormAsset Create(string fileName, string contentType, byte[] content);
    RegistrationFormAsset? Get(int assetId);
    RegistrationFormAssetMetadata? GetMetadata(int assetId);
    bool Exists(int assetId);
}

/// <summary>
/// Validates the small set of image formats accepted by the registration header-image editor.
/// The declared content type is checked against the file signature; it is never trusted on its own.
/// </summary>
public static class RegistrationFormAssetUploadValidation
{
    public const int MaximumUploadBytes = 2 * 1024 * 1024;

    public static bool TryValidate(
        string? declaredContentType,
        ReadOnlySpan<byte> content,
        string? fileName,
        out string sanitizedFileName,
        out string? error)
    {
        sanitizedFileName = SanitizeFileName(fileName);
        error = null;

        if (content.Length == 0)
        {
            error = "Choose a non-empty image file.";
            return false;
        }
        if (content.Length > MaximumUploadBytes)
        {
            error = $"Image files must be {MaximumUploadBytes / 1024 / 1024} MB or smaller.";
            return false;
        }

        var normalizedType = declaredContentType?.Trim().ToLowerInvariant();
        if (normalizedType is not ("image/png" or "image/jpeg" or "image/webp"))
        {
            error = "Only PNG, JPEG, and WebP header images are supported.";
            return false;
        }

        var detectedType = DetectContentType(content);
        if (!string.Equals(normalizedType, detectedType, StringComparison.Ordinal))
        {
            error = "The file content does not match its declared image type.";
            return false;
        }

        return true;
    }

    public static string SanitizeFileName(string? fileName)
    {
        var source = fileName ?? string.Empty;
        var separator = source.LastIndexOfAny(['/', '\\']);
        var leaf = separator >= 0 ? source[(separator + 1)..] : source;
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(leaf
            .Select(character => char.IsControl(character) || invalid.Contains(character) ? '_' : character)
            .ToArray())
            .Trim();

        if (sanitized.Length == 0)
        {
            sanitized = "header-image";
        }

        return sanitized.Length <= 255 ? sanitized : sanitized[..255];
    }

    public static string ComputeContentHash(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static string? DetectContentType(ReadOnlySpan<byte> content)
    {
        if (content.Length >= 8 && content[..8].SequenceEqual(new byte[]
            { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }))
        {
            return "image/png";
        }
        if (content.Length >= 3 && content[0] == 0xff && content[1] == 0xd8 && content[2] == 0xff)
        {
            return "image/jpeg";
        }
        if (content.Length >= 12 && content[..4].SequenceEqual("RIFF"u8) && content[8..12].SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }
        return null;
    }
}

public sealed class RegistrationFormAssetRepository : IRegistrationFormAssetRepository
{
    private readonly string connectionString;

    public RegistrationFormAssetRepository(IDbHelperSettings settings)
        : this($"Server={settings.db_hostname};Database={settings.db_name};Trusted_Connection=True;Encrypt=False;")
    {
    }

    internal RegistrationFormAssetRepository(string connectionString)
    {
        this.connectionString = connectionString;
    }

    public RegistrationFormAsset Create(string fileName, string contentType, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!RegistrationFormAssetUploadValidation.TryValidate(contentType, content, fileName,
                out var sanitizedFileName, out var validationError))
        {
            throw new ArgumentException(validationError ?? "The uploaded asset is invalid.", nameof(content));
        }

        using var connection = Open();
        return connection.QuerySingle<RegistrationFormAsset>(
            """
            insert dbo.RegistrationFormAssets
                (FileName, ContentType, Content, ContentHash)
            output inserted.AssetId, inserted.FileName, inserted.ContentType, inserted.Content,
                   inserted.ContentHash, inserted.CreatedDate, inserted.ModifiedDate
            values (@fileName, @contentType, @content, @contentHash);
            """,
            new
            {
                fileName = sanitizedFileName,
                contentType = contentType.Trim().ToLowerInvariant(),
                content,
                contentHash = RegistrationFormAssetUploadValidation.ComputeContentHash(content)
            });
    }

    public RegistrationFormAsset? Get(int assetId)
    {
        using var connection = Open();
        return connection.QuerySingleOrDefault<RegistrationFormAsset>(
            """
            select AssetId, FileName, ContentType, Content, ContentHash, CreatedDate, ModifiedDate
            from dbo.RegistrationFormAssets
            where AssetId = @assetId;
            """, new { assetId });
    }

    public RegistrationFormAssetMetadata? GetMetadata(int assetId)
    {
        using var connection = Open();
        return connection.QuerySingleOrDefault<RegistrationFormAssetMetadata>(
            """
            select AssetId, FileName, ContentType, ContentHash, CreatedDate, ModifiedDate
            from dbo.RegistrationFormAssets
            where AssetId = @assetId;
            """, new { assetId });
    }

    public bool Exists(int assetId)
    {
        using var connection = Open();
        return connection.ExecuteScalar<int>(
            "select case when exists (select 1 from dbo.RegistrationFormAssets where AssetId=@assetId) then 1 else 0 end",
            new { assetId }) == 1;
    }

    private SqlConnection Open()
    {
        var connection = new SqlConnection(connectionString);
        connection.Open();
        return connection;
    }
}
