using System.Security.Cryptography;
using Dapper;
using Microsoft.Data.SqlClient;
using Clc.PatronRegistration.Administration;
using Clc.PatronRegistration.Configuration;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;

namespace Clc.PatronRegistration.Web.Settings;

public sealed record RegistrationFormAsset(
    int AssetId,
    string FileName,
    string ContentType,
    byte[] Content,
    string ContentHash,
    DateTime CreatedDate,
    DateTime ModifiedDate,
    int? UploadOrganizationId = null,
    string? UploadFormCode = null);

public sealed record RegistrationFormAssetMetadata(
    int AssetId,
    string FileName,
    string ContentType,
    string ContentHash,
    DateTime CreatedDate,
    DateTime ModifiedDate,
    int? UploadOrganizationId = null,
    string? UploadFormCode = null);

public interface IRegistrationFormAssetRepository
{
    RegistrationFormAsset Create(string fileName, string contentType, byte[] content,
        int uploadOrganizationId, string uploadFormCode);
    RegistrationFormAsset? Get(int assetId);
    RegistrationFormAssetMetadata? GetMetadata(int assetId);
    bool Exists(int assetId);
    bool IsPubliclyReferenced(int assetId);
    bool IsReferencedBySettings(int assetId, IReadOnlyList<SettingSource> sources);
    bool IsReferencedByActiveDraft(int assetId, int organizationId, string formCode);
}

/// <summary>
/// Validates the small set of image formats accepted by the registration header-image editor.
/// The declared content type is checked against the file signature; it is never trusted on its own.
/// </summary>
public static class RegistrationFormAssetUploadValidation
{
    public const int MaximumUploadBytes = 2 * 1024 * 1024;
    private const long MaximumDecodedPixelCount = 25_000_000;

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

        try
        {
            // Inspect at most two frame headers before decoding. A second frame
            // is enough to reject animation without allocating every frame's
            // pixel buffer during validation.
            var information = Image.Identify(new DecoderOptions { MaxFrames = 2 }, content);
            if (information is null || information.Width <= 0 || information.Height <= 0 ||
                (long)information.Width * information.Height > MaximumDecodedPixelCount)
            {
                error = "The uploaded file is not a complete, valid image.";
                return false;
            }

            // Loading the complete image through a maintained decoder rejects truncated
            // headers and malformed chunks without transforming the stored bytes. The
            // bounded frame load rejects animation while ensuring a malicious upload
            // cannot force every frame to be decompressed before it is rejected.
            using var image = Image.Load(new DecoderOptions { MaxFrames = 2, SkipMetadata = true }, content);
            if (image.Frames.Count > 1)
            {
                error = "Animated header images are not supported.";
                return false;
            }
        }
        catch (Exception exception) when (exception is ImageFormatException or InvalidImageContentException
            or UnknownImageFormatException or NotSupportedException or ArgumentException or OverflowException)
        {
            error = "The uploaded file is not a complete, valid image.";
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

    public RegistrationFormAsset Create(string fileName, string contentType, byte[] content,
        int uploadOrganizationId, string uploadFormCode)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (uploadOrganizationId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(uploadOrganizationId));
        }
        uploadFormCode = FormCodeNormalizer.Normalize(uploadFormCode);
        if (!RegistrationFormAssetUploadValidation.TryValidate(contentType, content, fileName,
                out var sanitizedFileName, out var validationError))
        {
            throw new ArgumentException(validationError ?? "The uploaded asset is invalid.", nameof(content));
        }

        using var connection = Open();
        var metadata = connection.QuerySingle<RegistrationFormAssetMetadata>(
            """
            insert dbo.RegistrationFormAssets
                (FileName, ContentType, Content, ContentHash, UploadOrganizationId, UploadFormCode)
            output inserted.AssetId, inserted.FileName, inserted.ContentType,
                   inserted.ContentHash, inserted.CreatedDate, inserted.ModifiedDate,
                   inserted.UploadOrganizationId, inserted.UploadFormCode
            values (@fileName, @contentType, @content, @contentHash, @uploadOrganizationId, @uploadFormCode);
            """,
            new
            {
                fileName = sanitizedFileName,
                contentType = contentType.Trim().ToLowerInvariant(),
                content,
                contentHash = RegistrationFormAssetUploadValidation.ComputeContentHash(content),
                uploadOrganizationId,
                uploadFormCode
            });
        return new RegistrationFormAsset(metadata.AssetId, metadata.FileName, metadata.ContentType, content,
            metadata.ContentHash, metadata.CreatedDate, metadata.ModifiedDate,
            metadata.UploadOrganizationId, metadata.UploadFormCode);
    }

    public RegistrationFormAsset? Get(int assetId)
    {
        using var connection = Open();
        return connection.QuerySingleOrDefault<RegistrationFormAsset>(
            """
            select AssetId, FileName, ContentType, Content, ContentHash, CreatedDate, ModifiedDate,
                   UploadOrganizationId, UploadFormCode
            from dbo.RegistrationFormAssets
            where AssetId = @assetId;
            """, new { assetId });
    }

    public RegistrationFormAssetMetadata? GetMetadata(int assetId)
    {
        using var connection = Open();
        return connection.QuerySingleOrDefault<RegistrationFormAssetMetadata>(
            """
            select AssetId, FileName, ContentType, ContentHash, CreatedDate, ModifiedDate,
                   UploadOrganizationId, UploadFormCode
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

    public bool IsPubliclyReferenced(int assetId)
    {
        using var connection = Open();
        return connection.ExecuteScalar<int>(
            """
            select case when exists
            (
                select 1
                from dbo.RegistrationFormSettings
                where Setting = 'header_image_asset_id'
                  and TRY_CONVERT(int, Value) = @assetId
            ) then 1 else 0 end;
            """, new { assetId }) == 1;
    }

    public bool IsReferencedBySettings(int assetId, IReadOnlyList<SettingSource> sources)
    {
        if (sources.Count == 0)
        {
            return false;
        }

        var parameters = new DynamicParameters(new { assetId });
        var sourceClauses = new List<string>(sources.Count);
        for (var index = 0; index < sources.Count; index++)
        {
            sourceClauses.Add($"(OrganizationID=@sourceOrganization{index} and isnull(FormCode,'')=@sourceForm{index})");
            parameters.Add($"sourceOrganization{index}", sources[index].OrganizationId);
            parameters.Add($"sourceForm{index}", sources[index].FormCode);
        }

        return ExecuteScalar($"""
            select case when exists
            (
                select 1
                from dbo.RegistrationFormSettings
                where Setting = 'header_image_asset_id'
                  and TRY_CONVERT(int, Value) = @assetId
                  and ({string.Join(" or ", sourceClauses)})
            ) then 1 else 0 end;
            """, parameters) == 1;
    }

    public bool IsReferencedByActiveDraft(int assetId, int organizationId, string formCode)
    {
        using var connection = Open();
        return connection.ExecuteScalar<int>("""
            select case when exists
            (
                select 1
                from dbo.RegistrationSettingDraftChanges c
                join dbo.RegistrationSettingDrafts d on d.DraftId = c.DraftId
                where d.OrganizationId = @organizationId
                  and isnull(d.FormCode,'') = @formCode
                  and d.Status = 'Active'
                  and c.SettingKey = 'header_image_asset_id'
                  and c.Operation = 'Upsert'
                  and TRY_CONVERT(int, c.Value) = @assetId
            ) then 1 else 0 end;
            """, new { assetId, organizationId, formCode = FormCodeNormalizer.Normalize(formCode) }) == 1;
    }

    private int ExecuteScalar(string sql, object parameters)
    {
        using var connection = Open();
        return connection.ExecuteScalar<int>(sql, parameters);
    }

    private SqlConnection Open()
    {
        var connection = new SqlConnection(connectionString);
        connection.Open();
        return connection;
    }
}
