using Clc.PatronRegistration.Helpers;
using Clc.PatronRegistration.Web.Models;
using Clc.Polaris.Api;
using Microsoft.Extensions.Options;

namespace Clc.PatronRegistration.Web.Settings;

public interface IFormCodeAvailabilityService
{
    IReadOnlyList<FormCodeOption> GetAvailable(int libraryId);
    IReadOnlyList<FormCodeOption> GetLegacy(int libraryId);
    bool IsAvailable(int organizationId, string formCode);
}

public sealed class FormCodeAvailabilityService(
    ISettingsAdministrationRepository repository,
    ICache cache,
    IOptions<SettingsAdministrationOptions> options) : IFormCodeAvailabilityService
{
    private readonly int systemOrganizationId = options.Value.SystemOrganizationId;

    public IReadOnlyList<FormCodeOption> GetAvailable(int libraryId)
    {
        var result = new List<FormCodeOption> { new(string.Empty, "Default form", null, systemOrganizationId) };
        foreach (var group in repository.GetFormCodes(libraryId, systemOrganizationId).GroupBy(form => form.FormCode, StringComparer.OrdinalIgnoreCase))
        {
            var preferred = group.FirstOrDefault(form => form.OrganizationId == libraryId) ?? group.First();
            result.Add(new(preferred.FormCode, preferred.DisplayName, preferred.Description, preferred.OrganizationId));
        }
        foreach (var legacy in GetLegacy(libraryId).Where(legacy => result.All(form => !form.FormCode.Equals(legacy.FormCode, StringComparison.OrdinalIgnoreCase))))
        {
            result.Add(legacy);
        }
        return result.OrderBy(form => form.DisplayName).ToList();
    }

    public IReadOnlyList<FormCodeOption> GetLegacy(int libraryId)
    {
        var metadata = repository.GetFormCodes(libraryId, systemOrganizationId);
        var result = new List<FormCodeOption>();
        foreach (var row in repository.GetLegacyFormCodes())
        {
            var ownerOrganizationId = InferOwner(row.OrganizationId);
            if (!ownerOrganizationId.HasValue ||
                (ownerOrganizationId != systemOrganizationId && ownerOrganizationId != libraryId) ||
                metadata.Any(item => item.OrganizationId == ownerOrganizationId && item.FormCode.Equals(row.FormCode, StringComparison.OrdinalIgnoreCase)) ||
                result.Any(item => item.OwnerOrganizationId == ownerOrganizationId && item.FormCode.Equals(row.FormCode, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            result.Add(new(row.FormCode, $"{row.FormCode} (legacy/unregistered)", "Existing production settings without metadata.", ownerOrganizationId.Value, false));
        }
        return result.OrderBy(item => item.FormCode).ToList();
    }

    public bool IsAvailable(int organizationId, string formCode)
    {
        if (string.IsNullOrEmpty(formCode))
        {
            return true;
        }
        var libraryId = organizationId == systemOrganizationId ? systemOrganizationId : InferOwner(organizationId);
        return libraryId.HasValue && GetAvailable(libraryId.Value)
            .Any(form => form.FormCode.Equals(formCode, StringComparison.OrdinalIgnoreCase));
    }

    private int? InferOwner(int organizationId)
    {
        if (organizationId == systemOrganizationId)
        {
            return systemOrganizationId;
        }
        try
        {
            return cache.OrganizationCache.GetLibrary(organizationId).OrganizationID;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
