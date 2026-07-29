using Clc.PatronRegistration.Helpers;
using Microsoft.Extensions.Options;

namespace Clc.PatronRegistration.Web.Settings;

public interface ISettingsCacheInvalidator
{
    void LiveSettingsChanged();
    Task CheckForRemoteChangesAsync(CancellationToken cancellationToken = default);
}

public sealed class SettingsCacheInvalidator(
    ICache cache,
    ISettingsAdministrationRepository repository) : ISettingsCacheInvalidator
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private long? observedGeneration;

    public void LiveSettingsChanged()
    {
        gate.Wait();
        try
        {
            cache.RebuildCache();
            observedGeneration = repository.GetCacheGeneration();
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
            if (observedGeneration.HasValue && current != observedGeneration.Value)
            {
                cache.RebuildCache();
            }
            observedGeneration = current;
        }
        finally
        {
            gate.Release();
        }
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
