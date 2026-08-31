using TrueNasCommandCenter.Domain;
using TrueNasCommandCenter.Integrations.TrueNas;

namespace TrueNasCommandCenter.Services;

/// <summary>Provides the latest realtime performance sample for the connected TrueNAS host.</summary>
public interface ISystemPerformanceMonitor
{
    /// <summary>Raised after the realtime sample or availability state changes.</summary>
    event Action? Updated;

    /// <summary>Gets the most recent realtime TrueNAS performance sample.</summary>
    LiveSystemPerformance? Current { get; }

    /// <summary>Gets a user-safe availability message when realtime reporting is unavailable.</summary>
    string? LastError { get; }
}

/// <summary>Maintains one shared TrueNAS realtime reporting subscription for all UI components.</summary>
public sealed class SystemPerformanceMonitorService(ITrueNasPerformanceClient trueNasClient, TrueNasPerformanceService performanceService, SettingsService settingsService, TimeProvider timeProvider, ILogger<SystemPerformanceMonitorService> logger) : BackgroundService, ISystemPerformanceMonitor
{
    private PerformanceState state = new(null, null);

    /// <inheritdoc />
    public event Action? Updated;

    /// <inheritdoc />
    public LiveSystemPerformance? Current => Volatile.Read(ref state).Current;

    /// <inheritdoc />
    public string? LastError => Volatile.Read(ref state).LastError;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var settings = await settingsService.GetRecordAsync(stoppingToken);
                if (string.IsNullOrWhiteSpace(settings.TrueNasUsername) || string.IsNullOrWhiteSpace(settings.TrueNasApiKeyEncrypted))
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), timeProvider, stoppingToken);
                    continue;
                }

                await foreach (var statistics in trueNasClient.WatchSystemPerformanceAsync(5, stoppingToken))
                {
                    Volatile.Write(ref state, new PerformanceState(performanceService.MapRealtime(statistics), null));
                    NotifyUpdated();
                }

                Volatile.Write(ref state, new PerformanceState(Current, "Realtime TrueNAS performance is reconnecting."));
                NotifyUpdated();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "TrueNAS system performance monitoring was interrupted");
                Volatile.Write(ref state, new PerformanceState(Current, "Realtime TrueNAS performance is temporarily unavailable."));
                NotifyUpdated();
            }

            await Task.Delay(TimeSpan.FromSeconds(15), timeProvider, stoppingToken);
        }
    }

    private void NotifyUpdated()
    {
        var handlers = Updated?.GetInvocationList() ?? [];
        foreach (var handler in handlers.Cast<Action>())
        {
            try
            {
                handler();
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "A system performance monitor subscriber failed");
            }
        }
    }

    private sealed record PerformanceState(LiveSystemPerformance? Current, string? LastError);
}
