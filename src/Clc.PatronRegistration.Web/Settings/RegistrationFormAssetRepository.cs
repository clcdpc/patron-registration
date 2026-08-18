using System.Buffers.Binary;
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
    int DeleteOrphanedAssets(DateTime olderThanUtc, int batchSize);
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
        if (!TryValidateUploadEnvelope(declaredContentType, content, fileName,
                out sanitizedFileName, out error))
        {
            return false;
        }

        try
        {
            var normalizedType = declaredContentType!.Trim().ToLowerInvariant();
            switch (DetectAnimation(normalizedType, content))
            {
                case AnimationDetection.Animated:
                    error = "Animated header images are not supported.";
                    return false;
                case AnimationDetection.Invalid:
                    error = "The uploaded file is not a complete, valid image.";
                    return false;
            }

            // The format-level detector has ruled out animation without allocating
            // any pixel buffers. Identify only the single static frame's dimensions
            // before allowing the full decoder to run.
            var information = Image.Identify(new DecoderOptions { MaxFrames = 1 }, content);
            if (information is null || information.Width <= 0 || information.Height <= 0)
            {
                error = "The uploaded file is not a complete, valid image.";
                return false;
            }
            if ((long)information.Width * information.Height > MaximumDecodedPixelCount)
            {
                error = "The uploaded image dimensions exceed the safe pixel limit.";
                return false;
            }

            // The identification pass has established that this is a single-frame
            // image within the pixel budget. Decode it fully so truncated or
            // malformed image data is still rejected before storage.
            using var image = Image.Load(new DecoderOptions { SkipMetadata = true }, content);
        }
        catch (Exception exception) when (exception is ImageFormatException or InvalidImageContentException
            or UnknownImageFormatException or NotSupportedException or ArgumentException or OverflowException)
        {
            error = "The uploaded file is not a complete, valid image.";
            return false;
        }

        return true;
    }

    private enum AnimationDetection
    {
        Static,
        Animated,
        Invalid
    }

    private static AnimationDetection DetectAnimation(string contentType, ReadOnlySpan<byte> content) =>
        contentType switch
        {
            "image/png" => DetectPngAnimation(content),
            "image/webp" => DetectWebpAnimation(content),
            "image/jpeg" => AnimationDetection.Static,
            _ => AnimationDetection.Invalid
        };

    private static AnimationDetection DetectWebpAnimation(ReadOnlySpan<byte> content)
    {
        if (content.Length < 12 || !content[..4].SequenceEqual("RIFF"u8) ||
            !content[8..12].SequenceEqual("WEBP"u8))
        {
            return AnimationDetection.Invalid;
        }

        var riffSize = BinaryPrimitives.ReadUInt32LittleEndian(content[4..8]);
        var riffEnd = 8UL + riffSize;
        if (riffEnd != (ulong)content.Length)
        {
            return AnimationDetection.Invalid;
        }

        var offset = 12;
        var end = (int)riffEnd;
        while (offset < end)
        {
            if (end - offset < 8)
            {
                return AnimationDetection.Invalid;
            }

            var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(content.Slice(offset + 4, 4));
            var next = (ulong)offset + 8UL + chunkSize + (chunkSize & 1U);
            if (next > (ulong)end)
            {
                return AnimationDetection.Invalid;
            }

            var chunkType = content.Slice(offset, 4);
            if (chunkType.SequenceEqual("VP8X"u8))
            {
                // VP8X has a fixed ten-byte payload. Do not trust an oversized
                // declaration to skip physical chunks that ImageSharp will scan.
                if (chunkSize != 10)
                {
                    return AnimationDetection.Invalid;
                }

                // The VP8X animation feature is bit 1 of the first payload byte.
                if ((content[offset + 8] & 0x02) != 0)
                {
                    return AnimationDetection.Animated;
                }
            }
            else if (chunkType.SequenceEqual("ANIM"u8) || chunkType.SequenceEqual("ANMF"u8))
            {
                return AnimationDetection.Animated;
            }

            offset = (int)next;
        }

        return offset == end ? AnimationDetection.Static : AnimationDetection.Invalid;
    }

    private static AnimationDetection DetectPngAnimation(ReadOnlySpan<byte> content)
    {
        if (content.Length < 8 || !content[..8].SequenceEqual(
                new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }))
        {
            return AnimationDetection.Invalid;
        }

        var offset = 8;
        while (offset < content.Length)
        {
            if (content.Length - offset < 12)
            {
                return AnimationDetection.Invalid;
            }

            var chunkSize = BinaryPrimitives.ReadUInt32BigEndian(content.Slice(offset, 4));
            var next = (ulong)offset + 12UL + chunkSize;
            if (next > (ulong)content.Length)
            {
                return AnimationDetection.Invalid;
            }

            var chunkType = content.Slice(offset + 4, 4);
            if (chunkType.SequenceEqual("acTL"u8) || chunkType.SequenceEqual("fcTL"u8) ||
                chunkType.SequenceEqual("fdAT"u8))
            {
                return AnimationDetection.Animated;
            }

            offset = (int)next;
            if (chunkType.SequenceEqual("IEND"u8))
            {
                return AnimationDetection.Static;
            }
        }

        return AnimationDetection.Invalid;
    }

    /// <summary>
    /// Performs request-level checks that do not decode image pixels. The repository
    /// repeats these checks and remains the authoritative image validator.
    /// </summary>
    internal static bool TryValidateUploadEnvelope(
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
    public const int MaximumOrphanCleanupBatchSize = 1_000;

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

    public int DeleteOrphanedAssets(DateTime olderThanUtc, int batchSize)
    {
        if (batchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        }

        batchSize = Math.Min(batchSize, MaximumOrphanCleanupBatchSize);
        using var connection = Open();
        return connection.Execute("""
            delete top (@batchSize)
            from dbo.RegistrationFormAssets
            where CreatedDate < @olderThanUtc
              and not exists
              (
                  select 1
                  from dbo.RegistrationFormSettings
                  where Setting = 'header_image_asset_id'
                    and TRY_CONVERT(int, Value) = RegistrationFormAssets.AssetId
              )
              and not exists
              (
                  select 1
                  from dbo.RegistrationSettingDraftChanges as draftChange
                  join dbo.RegistrationSettingDrafts as draft
                    on draft.DraftId = draftChange.DraftId
                  where draft.Status = 'Active'
                    and draftChange.SettingKey = 'header_image_asset_id'
                    and draftChange.Operation = 'Upsert'
                    and TRY_CONVERT(int, draftChange.Value) = RegistrationFormAssets.AssetId
              );
            """, new { olderThanUtc, batchSize });
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
