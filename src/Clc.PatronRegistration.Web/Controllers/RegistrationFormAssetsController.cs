using Clc.PatronRegistration.Web.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Clc.PatronRegistration.Web.Controllers;

/// <summary>Serves image bytes for assets that the settings/runtime layers have selected.</summary>
[AllowAnonymous]
[Route("assets")]
public sealed class RegistrationFormAssetsController(IRegistrationFormAssetRepository repository) : ControllerBase
{
    [HttpGet("{id:int}", Name = "RegistrationFormAsset")]
    public IActionResult Get(int id)
    {
        // Read metadata first so a conditional request does not load varbinary(max) content unnecessarily.
        var metadata = repository.GetMetadata(id);
        if (metadata is null)
        {
            return NotFound();
        }

        var etag = $"\"{metadata.ContentHash}\"";
        Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        Response.Headers.ETag = etag;

        if (MatchesIfNoneMatch(etag))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        var asset = repository.Get(id);
        if (asset is null)
        {
            return NotFound();
        }

        return File(asset.Content, asset.ContentType);
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
