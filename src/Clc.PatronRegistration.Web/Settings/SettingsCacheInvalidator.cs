using Clc.PatronRegistration.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Clc.PatronRegistration.Web.Settings;

public interface ISettingsCacheInvalidator
{
    void LiveSettingsChanged(string? operation = null);
    Task CheckForRemoteChangesAsync(CancellationToken cancellationToken = default);
}

public sealed class SettingsCacheInvalidator(
    ICache cache,
    ISettingsAdministrationRepository repository,
    ILogger<SettingsCacheInvalidator>? suppliedLogger = null) : ISettingsCacheInvalidator
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ILogger<SettingsCacheInvalidator> logger = suppliedLogger ?? NullLogger<SettingsCacheInvalidator>.Instance;
    private long? observedGeneration;
    private bool refreshPending;

    public void LiveSettingsChanged(string? operation = null)
    {
        gate.Wait();
        try
        {
            try
            {
                RebuildUntilStable();
            }
            catch (Exception exception)
            {
                // The repository call that precedes this method has already
                // committed. Keep the request successful and leave a durable
                // generation mismatch for the hosted worker to retry.
                refreshPending = true;
                long? generation = null;
                try
                {
                    generation = repository.GetCacheGeneration();
                    observedGeneration = generation == long.MinValue ? null : generation - 1;
                }
                catch (Exception generationException)
                {
                    observedGeneration = null;
                    logger.LogWarning(generationException,
                        "Could not read the registration-settings cache generation after an immediate refresh failure.");
                }

                logger.LogError(exception,
                    "Immediate registration-settings cache refresh failed after committed operation {Operation}; " +
                    "the generation-check worker will retry. Pending generation: {Generation}.",
                    operation ?? "unspecified", generation);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task CheckForRemoteChangesAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var current = repository.GetCacheGeneration();
            if (refreshPending)
            {
                RebuildUntilStable(current);
                return;
            }
            if (!observedGeneration.HasValue)
            {
                if (cache.IsInitialized)
                {
                    RebuildUntilStable(current);
                }
                else
                {
                    observedGeneration = current;
                }
                return;
            }
            if (current != observedGeneration.Value)
            {
                RebuildUntilStable(current);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private void RebuildUntilStable(long? generationBefore = null)
    {
        const int maximumRebuilds = 3;
        var before = generationBefore ?? repository.GetCacheGeneration();
        for (var attempt = 0; attempt < maximumRebuilds; attempt++)
        {
            try
            {
                if (cache is IGenerationAwareCacheSnapshotProvider generationAwareCache)
                {
                    generationAwareCache.RebuildCacheAtGeneration(before);
                }
                else
                {
                    cache.RebuildCache();
                }
            }
            catch (CacheSnapshotConsistencyException)
            {
                // A publication raced the rebuild. Do not publish the data
                // that was read around that commit; retry against the newest
                // authoritative generation instead.
                before = repository.GetCacheGeneration();
                continue;
            }

            var after = repository.GetCacheGeneration();
            if (after == before)
            {
                observedGeneration = after;
                refreshPending = false;
                return;
            }
            before = after;
        }

        // Keep the pre-rebuild value so the next scheduled check detects the mismatch and retries.
        observedGeneration = before == long.MinValue ? null : before - 1;
        refreshPending = true;
    }
}

public sealed class SettingsCacheGenerationWorker(
    ISettingsCacheInvalidator invalidator,
    IOptions<SettingsAdministrationOptions> options,
    ILogger<SettingsCacheGenerationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, options.Value.GenerationCheckSeconds));
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await invalidator.CheckForRemoteChangesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Unable to check the registration-settings cache generation.");
            }
        }
    }
}
