using TrueNasAppManager.Services;

namespace TrueNasAppManager.Integrations.UptimeKuma;

public sealed class UptimeKumaSyncBackgroundService(SettingsService settingsService, IUptimeKumaSyncService syncService, ILogger<UptimeKumaSyncBackgroundService> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(30);
            try
            {
                var settings = await settingsService.GetRecordAsync(stoppingToken);
                if (settings.UptimeKumaEnabled && !string.IsNullOrWhiteSpace(settings.UptimeKumaBaseUrl))
                {
                    await syncService.SynchronizeAsync(cancellationToken: stoppingToken);
                    delay = TimeSpan.FromSeconds(Math.Clamp(settings.UptimeKumaRefreshIntervalSeconds, 30, 3600));
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Unexpected Uptime Kuma background synchronization failure");
            }

            await Task.Delay(delay, stoppingToken);
        }
    }
}
