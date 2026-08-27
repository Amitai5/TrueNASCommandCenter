using Microsoft.EntityFrameworkCore;
using TrueNasAppManager.Data;
using TrueNasAppManager.Domain;
using TrueNasAppManager.Services;

namespace TrueNasAppManager.Scheduling;

public sealed class RunSchedulerBackgroundService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<RunSchedulerBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await WaitAndRunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    "Scheduler iteration failed with {ErrorType}",
                    exception.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(30), timeProvider, stoppingToken);
            }
        }
    }

    private async Task WaitAndRunAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var settingsService = scope.ServiceProvider.GetRequiredService<SettingsService>();
        var scheduleService = scope.ServiceProvider.GetRequiredService<IScheduleService>();
        var settings = await settingsService.GetRecordAsync(cancellationToken);

        if (!settings.SchedulerEnabled ||
            string.IsNullOrWhiteSpace(settings.CronExpression) ||
            string.IsNullOrWhiteSpace(settings.TimeZoneId))
        {
            await Task.Delay(TimeSpan.FromSeconds(30), timeProvider, cancellationToken);
            return;
        }

        var now = timeProvider.GetUtcNow();
        var next = scheduleService.GetNextRun(settings.CronExpression, settings.TimeZoneId, now);
        if (next is null)
        {
            await Task.Delay(TimeSpan.FromMinutes(1), timeProvider, cancellationToken);
            return;
        }

        var delay = next.Value - now;
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, timeProvider, cancellationToken);
        }

        await using var runScope = scopeFactory.CreateAsyncScope();
        var currentSettings = await runScope.ServiceProvider
            .GetRequiredService<SettingsService>()
            .GetRecordAsync(cancellationToken);
        if (!currentSettings.SchedulerEnabled ||
            !string.Equals(currentSettings.CronExpression, settings.CronExpression, StringComparison.Ordinal) ||
            !string.Equals(currentSettings.TimeZoneId, settings.TimeZoneId, StringComparison.Ordinal))
        {
            return;
        }

        await runScope.ServiceProvider
            .GetRequiredService<IUpdateCoordinator>()
            .CheckAndUpdateAsync(RunTrigger.Scheduled, true, cancellationToken: cancellationToken);
        await ApplyRetentionAsync(runScope.ServiceProvider, currentSettings, cancellationToken);
    }

    private static async Task ApplyRetentionAsync(
        IServiceProvider services,
        SettingsRecord settings,
        CancellationToken cancellationToken)
    {
        if (settings.HistoryRetentionDays is null)
        {
            return;
        }

        var timeProvider = services.GetRequiredService<TimeProvider>();
        var cutoff = timeProvider.GetUtcNow().UtcDateTime.AddDays(-settings.HistoryRetentionDays.Value);
        var dbFactory = services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await db.UpdateRuns.Where(run => run.StartedUtc < cutoff).ExecuteDeleteAsync(cancellationToken);
        await db.Notifications.Where(record => record.CreatedUtc < cutoff).ExecuteDeleteAsync(cancellationToken);
    }
}
