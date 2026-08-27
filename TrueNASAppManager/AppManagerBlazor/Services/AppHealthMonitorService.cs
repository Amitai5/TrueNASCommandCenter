using Microsoft.EntityFrameworkCore;
using TrueNasAppManager.Data;
using TrueNasAppManager.Domain;
using TrueNasAppManager.Notifications;

namespace TrueNasAppManager.Services;

public interface IAppHealthMonitorService
{
    /// <summary>Evaluates persisted app and container health, emits incident notifications, and performs configured recovery attempts.</summary>
    /// <param name="appIds">The refreshed application identifiers to evaluate.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The health evaluation counts.</returns>
    Task<AppHealthEvaluationResult> EvaluateAsync(IReadOnlyCollection<string> appIds, CancellationToken cancellationToken = default);
}

public sealed class AppHealthMonitorService(IDbContextFactory<AppDbContext> dbFactory, IAppManagementService appManagementService, INotificationDispatcher notifications, TimeProvider timeProvider) : IAppHealthMonitorService
{
    /// <inheritdoc cref="IAppHealthMonitorService.EvaluateAsync"/>
    public async Task<AppHealthEvaluationResult> EvaluateAsync(IReadOnlyCollection<string> appIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(appIds);
        var checkedCount = 0;
        var incidentsOpened = 0;
        var recovered = 0;
        var restartAttempts = 0;
        foreach (var appId in appIds.Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            checkedCount++;
            var transition = await EvaluateTransitionAsync(appId, cancellationToken);
            if (transition.OpenedIncident)
            {
                incidentsOpened++;
                await DispatchAsync(transition.App, NotificationEventType.AppDowntime, transition.IncidentId, "APP_NOT_RUNNING", $"{transition.App.Name} is {transition.App.HealthState.ToString().ToLowerInvariant()}: {transition.App.HealthMessage}", cancellationToken);
            }

            if (transition.RecoveredIncident)
            {
                recovered++;
                await DispatchAsync(transition.App, NotificationEventType.AppRecoverySucceeded, transition.IncidentId, "APP_RECOVERED", $"{transition.App.Name} is running again.", cancellationToken);
            }

            if (!transition.ShouldRestart)
            {
                continue;
            }

            restartAttempts++;
            var result = await appManagementService.ExecuteAutomaticRecoveryAsync(appId, cancellationToken);
            await DispatchAsync(transition.App, result.Success ? NotificationEventType.AppRecoverySucceeded : NotificationEventType.AppRecoveryFailed, transition.IncidentId, result.Success ? "APP_RECOVERY_SUCCEEDED" : result.ErrorCode ?? "APP_RECOVERY_FAILED", result.Message, cancellationToken);
            if (result.Success)
            {
                recovered++;
                await ClearIncidentAfterRecoveryAsync(appId, cancellationToken);
            }
        }

        return new AppHealthEvaluationResult(checkedCount, incidentsOpened, recovered, restartAttempts);
    }

    private async Task<HealthTransition> EvaluateTransitionAsync(string appId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var app = await db.Apps.SingleAsync(item => item.Id == appId, cancellationToken);
        var wasActive = app.HealthIncidentId is not null;
        var incidentId = app.HealthIncidentId ?? Guid.NewGuid();
        var unhealthy = app.HealthState is AppHealthState.Stopped or AppHealthState.Degraded;
        var monitorsDowntime = app.DowntimeAction != DowntimeAction.Ignore && !app.MaintenanceMode;
        var opened = unhealthy && monitorsDowntime && !wasActive;
        var recovered = !unhealthy && wasActive && !app.MaintenanceMode;
        var maintenanceClearsIncident = app.MaintenanceMode && wasActive;
        var shouldRestart = unhealthy && monitorsDowntime && app.DowntimeAction == DowntimeAction.RestartAndNotify && app.RecoveryAttemptedUtc is null;
        if (opened)
        {
            app.HealthIncidentId = incidentId;
            app.DowntimeNotificationActive = true;
        }

        if (shouldRestart)
        {
            app.RecoveryAttemptedUtc = timeProvider.GetUtcNow().UtcDateTime;
        }

        if (recovered || maintenanceClearsIncident)
        {
            app.HealthIncidentId = null;
            app.RecoveryAttemptedUtc = null;
            app.DowntimeNotificationActive = false;
        }

        await db.SaveChangesAsync(cancellationToken);
        return new HealthTransition(app, incidentId, opened, recovered, shouldRestart);
    }

    private async Task ClearIncidentAfterRecoveryAsync(string appId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var app = await db.Apps.SingleAsync(item => item.Id == appId, cancellationToken);
        app.HealthIncidentId = null;
        app.RecoveryAttemptedUtc = null;
        app.DowntimeNotificationActive = false;
        app.HealthState = AppHealthState.Running;
        app.HealthMessage = "The app is running after one automatic recovery attempt.";
        await db.SaveChangesAsync(cancellationToken);
    }

    private Task DispatchAsync(AppRecord app, NotificationEventType eventType, Guid incidentId, string reasonCode, string message, CancellationToken cancellationToken)
    {
        return notifications.DispatchAsync(new NotificationEvent(Guid.NewGuid(), eventType, timeProvider.GetUtcNow().UtcDateTime, $"{eventType}|{app.Id}|{incidentId:N}", $"{app.Name}: {eventType}", message, reasonCode, app.Id, app.Name, app.InstalledVersion), cancellationToken);
    }

    private sealed record HealthTransition(AppRecord App, Guid IncidentId, bool OpenedIncident, bool RecoveredIncident, bool ShouldRestart);
}
