using Microsoft.EntityFrameworkCore;
using TrueNasAppManager.Data;
using TrueNasAppManager.Domain;

namespace TrueNasAppManager.Services;

/// <summary>Builds the read-only operational summary shown on the dashboard.</summary>
public sealed class DashboardOverviewService(IDbContextFactory<AppDbContext> dbFactory)
{
    /// <summary>Loads current app, monitoring, schedule, and automation status from persisted state.</summary>
    /// <param name="cancellationToken">The token that cancels the database query.</param>
    /// <returns>The current dashboard summary.</returns>
    public async Task<DashboardOverview> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var apps = await db.Apps.AsNoTracking()
            .Where(app => app.IsInstalled)
            .OrderBy(app => app.Name)
            .ToListAsync(cancellationToken);
        var monitors = await db.UptimeKumaMonitors.AsNoTracking()
            .Where(monitor => monitor.IsPresent)
            .OrderBy(monitor => monitor.Name)
            .ToListAsync(cancellationToken);
        var settings = await db.Settings.AsNoTracking()
            .SingleAsync(item => item.Id == 1, cancellationToken);
        var lastRun = await db.UpdateRuns.AsNoTracking()
            .Where(run => run.Trigger == RunTrigger.Scheduled ||
                          run.Trigger == RunTrigger.CheckNow ||
                          run.Trigger == RunTrigger.CheckAndUpdateNow ||
                          run.Trigger == RunTrigger.UpdateNow)
            .OrderByDescending(run => run.StartedUtc)
            .FirstOrDefaultAsync(cancellationToken);

        var appAlerts = apps
            .Where(app => app.HealthState is not AppHealthState.Running and not AppHealthState.Maintenance)
            .OrderBy(app => AppAlertPriority(app.HealthState))
            .ThenBy(app => app.Name)
            .Select(app => new DashboardAppAlert(app.Id, app.Name, app.HealthState, app.HealthMessage))
            .ToList();
        var monitorAlerts = monitors
            .Where(monitor => monitor.Status == UptimeKumaMonitorStatus.Down)
            .Select(monitor => new DashboardMonitorAlert(monitor.MonitorId, monitor.AppId, monitor.Name, monitor.Url ?? monitor.Hostname, monitor.LastSeenUtc))
            .ToList();
        var favoriteApps = apps
            .Where(app => app.IsFavorite)
            .Select(app => new DashboardFavoriteApp(app.Id, app.Name, app.GroupName, app.HealthState, app.HumanVersion ?? app.InstalledVersion, app.CatalogUpdateAvailable || app.ImageUpdateAvailable))
            .ToList();

        return new DashboardOverview(
            apps.Count,
            apps.Count(app => app.HealthState == AppHealthState.Running),
            apps.Count(app => app.CatalogUpdateAvailable || app.ImageUpdateAvailable),
            appAlerts,
            favoriteApps,
            monitors.Count,
            monitors.Count(monitor => monitor.Status == UptimeKumaMonitorStatus.Up),
            monitorAlerts,
            lastRun is null ? null : new DashboardRunSummary(lastRun.Id, lastRun.Trigger, lastRun.Status, lastRun.StartedUtc, lastRun.EndedUtc, lastRun.CheckedCount, lastRun.SucceededCount, lastRun.FailedCount, lastRun.SkippedCount, lastRun.ErrorSummary),
            new DashboardSettingsSummary(
                settings.OnboardingCompleted,
                settings.SchedulerEnabled,
                settings.CronExpression,
                settings.TimeZoneId,
                settings.LastConnectionSuccessUtc,
                settings.LastConnectionErrorCode,
                settings.LastInventoryRefreshUtc,
                settings.LastCompletedCheckUtc,
                settings.LastUptimeKumaSuccessUtc,
                settings.LastUptimeKumaError));
    }

    private static int AppAlertPriority(AppHealthState healthState) => healthState switch
    {
        AppHealthState.Stopped => 0,
        AppHealthState.Degraded => 1,
        _ => 2
    };
}

/// <summary>Contains the current high-level operational state for the dashboard.</summary>
public sealed record DashboardOverview(int AppCount, int RunningAppCount, int UpdatesAvailable, IReadOnlyList<DashboardAppAlert> AppAlerts, IReadOnlyList<DashboardFavoriteApp> FavoriteApps, int MonitorCount, int MonitorsUp, IReadOnlyList<DashboardMonitorAlert> MonitorAlerts, DashboardRunSummary? LastRun, DashboardSettingsSummary Settings);

/// <summary>Describes an installed app that requires operator attention.</summary>
public sealed record DashboardAppAlert(string AppId, string Name, AppHealthState HealthState, string? Message);

/// <summary>Describes an installed app pinned to the dashboard by the operator.</summary>
public sealed record DashboardFavoriteApp(string AppId, string Name, string? GroupName, AppHealthState HealthState, string? Version, bool HasUpdate);

/// <summary>Describes a currently down Uptime Kuma monitor.</summary>
public sealed record DashboardMonitorAlert(string MonitorId, string? AppId, string Name, string? Target, DateTime LastSeenUtc);

/// <summary>Summarizes the most recent check or update run.</summary>
public sealed record DashboardRunSummary(Guid Id, RunTrigger Trigger, RunStatus Status, DateTime StartedUtc, DateTime? EndedUtc, int CheckedCount, int SucceededCount, int FailedCount, int SkippedCount, string? ErrorSummary);

/// <summary>Contains connection and schedule timestamps needed by the dashboard.</summary>
public sealed record DashboardSettingsSummary(bool OnboardingCompleted, bool SchedulerEnabled, string? CronExpression, string? TimeZoneId, DateTime? LastConnectionSuccessUtc, string? LastConnectionErrorCode, DateTime? LastInventoryRefreshUtc, DateTime? LastCompletedCheckUtc, DateTime? LastUptimeKumaSuccessUtc, string? LastUptimeKumaError);
