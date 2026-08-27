using Microsoft.EntityFrameworkCore;
using TrueNasAppManager.Data;
using TrueNasAppManager.Domain;

namespace TrueNasAppManager.Integrations.UptimeKuma;

public interface IUptimeKumaSyncService
{
    /// <summary>Imports the current read-only Uptime Kuma metrics into the local dashboard cache.</summary>
    /// <param name="force">Whether the request is an explicit synchronization.</param>
    /// <param name="cancellationToken">A token that cancels synchronization.</param>
    /// <returns>The synchronization result.</returns>
    Task<UptimeKumaSyncResult> SynchronizeAsync(bool force = false, CancellationToken cancellationToken = default);
}

public sealed class UptimeKumaSyncService(IDbContextFactory<AppDbContext> dbFactory, IUptimeKumaClient client, TimeProvider timeProvider, ILogger<UptimeKumaSyncService> logger) : IUptimeKumaSyncService
{
    private readonly SemaphoreSlim syncGate = new(1, 1);

    /// <inheritdoc cref="IUptimeKumaSyncService.SynchronizeAsync"/>
    public async Task<UptimeKumaSyncResult> SynchronizeAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        await syncGate.WaitAsync(cancellationToken);
        try
        {
            await using var initialDb = await dbFactory.CreateDbContextAsync(cancellationToken);
            var initialSettings = await initialDb.Settings.AsNoTracking().SingleAsync(item => item.Id == 1, cancellationToken);
            if (string.IsNullOrWhiteSpace(initialSettings.UptimeKumaBaseUrl))
            {
                return new UptimeKumaSyncResult(false, "Uptime Kuma is not configured.");
            }

            var now = timeProvider.GetUtcNow().UtcDateTime;
            try
            {
                var metrics = await client.GetMonitorMetricsAsync(cancellationToken);
                await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
                var settings = await db.Settings.SingleAsync(item => item.Id == 1, cancellationToken);
                var existing = await db.UptimeKumaMonitors.ToDictionaryAsync(monitor => monitor.MonitorId, cancellationToken);
                foreach (var monitor in existing.Values)
                {
                    monitor.IsPresent = false;
                }

                foreach (var metric in metrics)
                {
                    if (!existing.TryGetValue(metric.MonitorId, out var monitor))
                    {
                        monitor = new UptimeKumaMonitorRecord { MonitorId = metric.MonitorId };
                        db.UptimeKumaMonitors.Add(monitor);
                    }

                    ApplyMetric(monitor, metric, now);
                }

                settings.LastUptimeKumaSyncUtc = now;
                settings.LastUptimeKumaSuccessUtc = now;
                settings.LastUptimeKumaError = null;
                settings.UptimeKumaEnabled = true;
                await db.SaveChangesAsync(cancellationToken);
                return new UptimeKumaSyncResult(true, $"Imported {metrics.Count} Uptime Kuma monitor{(metrics.Count == 1 ? string.Empty : "s")}.", metrics.Count);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException or OperationCanceledException)
            {
                logger.LogWarning(exception, "Uptime Kuma synchronization failed");
                await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
                var settings = await db.Settings.SingleAsync(item => item.Id == 1, cancellationToken);
                settings.LastUptimeKumaSyncUtc = now;
                settings.LastUptimeKumaError = exception.Message;
                settings.UptimeKumaEnabled = true;
                await db.SaveChangesAsync(cancellationToken);
                return new UptimeKumaSyncResult(false, exception.Message);
            }
        }
        finally
        {
            syncGate.Release();
        }
    }

    private static void ApplyMetric(UptimeKumaMonitorRecord monitor, UptimeKumaMonitorMetric metric, DateTime now)
    {
        monitor.Name = metric.Name;
        monitor.Type = metric.Type;
        monitor.Url = metric.Url;
        monitor.Hostname = metric.Hostname;
        monitor.Port = metric.Port;
        monitor.Status = metric.Status;
        monitor.ResponseTimeMilliseconds = metric.ResponseTimeMilliseconds;
        monitor.UptimeRatio1Day = metric.UptimeRatio1Day;
        monitor.UptimeRatio30Days = metric.UptimeRatio30Days;
        monitor.UptimeRatio365Days = metric.UptimeRatio365Days;
        monitor.AverageResponseTimeMilliseconds1Day = metric.AverageResponseTimeMilliseconds1Day;
        monitor.AverageResponseTimeMilliseconds30Days = metric.AverageResponseTimeMilliseconds30Days;
        monitor.AverageResponseTimeMilliseconds365Days = metric.AverageResponseTimeMilliseconds365Days;
        monitor.CertificateIsValid = metric.CertificateIsValid;
        monitor.CertificateDaysRemaining = metric.CertificateDaysRemaining;
        monitor.IsPresent = true;
        monitor.LastSeenUtc = now;
    }
}
