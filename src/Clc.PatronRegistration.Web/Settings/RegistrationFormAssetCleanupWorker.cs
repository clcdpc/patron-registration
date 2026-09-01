using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Clc.PatronRegistration.Web.Settings;

/// <summary>
/// Periodically removes uploaded image rows that were never made reachable by
/// either a live setting or an active draft. The repository performs the
/// global reference check and applies the batch bound in SQL.
/// </summary>
public sealed class RegistrationFormAssetCleanupWorker(
    IRegistrationFormAssetRepository repository,
    IOptions<SettingsAdministrationOptions> options,
    ILogger<RegistrationFormAssetCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await CleanupAsync(stoppingToken);

        var interval = TimeSpan.FromHours(Math.Max(1, options.Value.AssetOrphanCleanupIntervalHours));
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await CleanupAsync(stoppingToken);
        }
    }

    private Task CleanupAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.CompletedTask;
        }

        try
        {
            var gracePeriod = TimeSpan.FromHours(Math.Max(1, options.Value.AssetOrphanGracePeriodHours));
            var cutoffUtc = DateTime.UtcNow - gracePeriod;
            var batchSize = Math.Max(1, options.Value.AssetOrphanCleanupBatchSize);
            var deleted = repository.DeleteOrphanedAssets(cutoffUtc, batchSize);
            if (deleted > 0)
            {
                logger.LogInformation(
                    "Registration-form asset cleanup removed {DeletedCount} globally unreferenced assets older than {CutoffUtc}.",
                    deleted, cutoffUtc);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Unable to run registration-form asset cleanup; it will run again later.");
        }

        return Task.CompletedTask;
    }
}
