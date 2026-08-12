using Clc.PatronRegistration.Configuration;
using Clc.PatronRegistration.Administration;
using Clc.PatronRegistration.Web.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Clc.PatronRegistration.Web.Controllers;

/// <summary>Serves a known asset to an authorized settings administrator for editing/preview.</summary>
[Authorize]
[Route("settings/assets")]
public sealed class SettingsRegistrationFormAssetsController(
    ISettingsAuthorizationService authorization,
    IFormCodeAvailabilityService formCodeAvailability,
    IRegistrationFormAssetRepository repository) : ControllerBase
{
    [HttpGet("{id:int}", Name = "SettingsRegistrationFormAsset")]
    public IActionResult Get(int id, int organizationId, string formCode = "")
    {
        if (id <= 0)
        {
            return NotFound();
        }
        formCode = FormCodeNormalizer.Normalize(formCode);
        var principal = authorization.Describe(User);
        if (!principal.HasRole || !principal.OrganizationId.HasValue ||
            !authorization.CanManage(User, organizationId) ||
            !formCodeAvailability.IsAvailable(organizationId, formCode))
        {
            return Forbid();
        }

        var metadata = repository.GetMetadata(id);
        if (metadata is null)
        {
            return NotFound();
        }

        var etag = $"\"{metadata.ContentHash}\"";
        Response.Headers.CacheControl = "private, max-age=31536000, immutable";
        Response.Headers.ETag = etag;
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (MatchesIfNoneMatch(etag))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        var asset = repository.Get(id);
        return asset is null ? NotFound() : File(asset.Content, asset.ContentType);
    }

    private bool MatchesIfNoneMatch(string etag)
    {
        var header = Request.Headers.IfNoneMatch.ToString();
        return header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(candidate => candidate == "*" || NormalizeEntityTag(candidate).Equals(etag, StringComparison.Ordinal));
    }

    private static string NormalizeEntityTag(string value) =>
        value.StartsWith("W/", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
}
