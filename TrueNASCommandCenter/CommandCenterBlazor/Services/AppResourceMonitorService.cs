using TrueNasCommandCenter.Domain;
using TrueNasCommandCenter.Integrations.TrueNas;

namespace TrueNasCommandCenter.Services;

/// <summary>Provides the latest live TrueNAS resource usage for installed applications.</summary>
public interface IAppResourceMonitor
{
    /// <summary>Raised after the resource snapshot or its availability state changes.</summary>
    event Action? Updated;

    /// <summary>Gets the most recent app usage values keyed by TrueNAS application ID.</summary>
    IReadOnlyDictionary<string, AppResourceUsage> Current { get; }

    /// <summary>Gets the time of the most recent successful resource update.</summary>
    DateTimeOffset? LastUpdatedUtc { get; }

    /// <summary>Gets a user-safe availability message when live statistics are unavailable.</summary>
    string? LastError { get; }
}

/// <summary>Maintains one shared TrueNAS app statistics subscription for all dashboard components.</summary>
public sealed class AppResourceMonitorService(ITrueNasSystemClient trueNasClient, SettingsService settingsService, TimeProvider timeProvider, ILogger<AppResourceMonitorService> logger) : BackgroundService, IAppResourceMonitor
{
    private ResourceState state = new(new Dictionary<string, AppResourceUsage>(StringComparer.OrdinalIgnoreCase), null, null);

    /// <inheritdoc />
    public event Action? Updated;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, AppResourceUsage> Current => Volatile.Read(ref state).Current;

    /// <inheritdoc />
    public DateTimeOffset? LastUpdatedUtc => Volatile.Read(ref state).LastUpdatedUtc;

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

                await foreach (var statistics in trueNasClient.WatchAppStatsAsync(5, stoppingToken))
                {
                    var observedAt = timeProvider.GetUtcNow();
                    var current = new Dictionary<string, AppResourceUsage>(StringComparer.OrdinalIgnoreCase);
                    foreach (var app in statistics.Where(app => !string.IsNullOrWhiteSpace(app.AppName)))
                    {
                        current[app.AppName] = new AppResourceUsage(
                            app.AppName,
                            Math.Max(0, app.CpuUsage),
                            Math.Max(0, app.Memory),
                            app.Networks.Sum(network => Math.Max(0, network.ReceiveBytes)),
                            app.Networks.Sum(network => Math.Max(0, network.TransmitBytes)),
                            Math.Max(0, app.BlockIo.ReadBytes),
                            Math.Max(0, app.BlockIo.WriteBytes),
                            observedAt);
                    }

                    Volatile.Write(ref state, new ResourceState(current, observedAt, null));
                    NotifyUpdated();
                }

                Volatile.Write(ref state, new ResourceState(Current, LastUpdatedUtc, "Live TrueNAS resource usage is reconnecting."));
                NotifyUpdated();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "TrueNAS application resource monitoring was interrupted");
                Volatile.Write(ref state, new ResourceState(Current, LastUpdatedUtc, "Live TrueNAS resource usage is temporarily unavailable."));
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
                logger.LogDebug(exception, "An application resource monitor subscriber failed");
            }
        }
    }

    private sealed record ResourceState(IReadOnlyDictionary<string, AppResourceUsage> Current, DateTimeOffset? LastUpdatedUtc, string? LastError);
}
