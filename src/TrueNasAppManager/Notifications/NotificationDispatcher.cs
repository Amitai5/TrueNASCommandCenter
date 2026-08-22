using Microsoft.EntityFrameworkCore;
using TrueNasAppManager.Data;
using TrueNasAppManager.Domain;
using TrueNasAppManager.Services;

namespace TrueNasAppManager.Notifications;

public interface INotificationDispatcher
{
    Task DispatchAsync(NotificationEvent notification, CancellationToken cancellationToken = default);
}

public sealed class NotificationDispatcher(
    IDbContextFactory<AppDbContext> dbFactory,
    IEmailNotificationSender emailSender,
    IWebhookNotificationSender webhookSender,
    TimeProvider timeProvider) : INotificationDispatcher
{
    public async Task DispatchAsync(
        NotificationEvent notification,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var settings = await db.Settings.AsNoTracking().SingleAsync(item => item.Id == 1, cancellationToken);
        if (!IsEnabled(notification.EventType, settings))
        {
            return;
        }

        if (notification.EventType == NotificationEventType.TrueNasConnectionFailed)
        {
            var cutoff = timeProvider.GetUtcNow().UtcDateTime
                .AddMinutes(-settings.ConnectionFailureCooldownMinutes);
            var recentlyDelivered = await db.Notifications.AnyAsync(
                item => item.EventType == NotificationEventType.TrueNasConnectionFailed &&
                        item.Status == DeliveryStatus.Delivered &&
                        item.DeliveredUtc >= cutoff,
                cancellationToken);
            if (recentlyDelivered)
            {
                return;
            }
        }

        if (settings.EmailEnabled)
        {
            await DeliverAsync(notification, NotificationProvider.Email, emailSender.SendAsync, cancellationToken);
        }

        if (settings.WebhookEnabled)
        {
            await DeliverAsync(notification, NotificationProvider.Webhook, webhookSender.SendAsync, cancellationToken);
        }
    }

    private async Task DeliverAsync(
        NotificationEvent notification,
        NotificationProvider provider,
        Func<NotificationEvent, CancellationToken, Task<NotificationDeliveryResult>> sender,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var duplicate = await db.Notifications.AnyAsync(
            item => item.DeduplicationKey == notification.DeduplicationKey &&
                    item.Provider == provider &&
                    item.Status == DeliveryStatus.Delivered,
            cancellationToken);
        if (duplicate)
        {
            return;
        }

        var record = new NotificationRecord
        {
            EventId = notification.EventId,
            EventType = notification.EventType,
            AppId = notification.AppId,
            DeduplicationKey = notification.DeduplicationKey,
            Provider = provider,
            CreatedUtc = timeProvider.GetUtcNow().UtcDateTime
        };
        db.Notifications.Add(record);
        await db.SaveChangesAsync(cancellationToken);

        var result = await sender(notification, cancellationToken);
        record.Status = result.Success ? DeliveryStatus.Delivered : DeliveryStatus.Failed;
        record.DeliveredUtc = result.Success ? timeProvider.GetUtcNow().UtcDateTime : null;
        record.HttpStatusCode = result.HttpStatusCode;
        record.ErrorSummary = Sanitize(result.Error);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static bool IsEnabled(NotificationEventType eventType, SettingsRecord settings) =>
        eventType switch
        {
            NotificationEventType.AppDowntime => true,
            NotificationEventType.AppRecoverySucceeded => true,
            NotificationEventType.AppRecoveryFailed => true,
            NotificationEventType.ManualApprovalAvailable => settings.NotifyManualApproval,
            NotificationEventType.AutomaticUpdateFailed => settings.NotifyAutomaticFailure,
            NotificationEventType.AutomaticUpdateBlocked => settings.NotifyAutomaticBlocked,
            NotificationEventType.RollbackOccurred => settings.NotifyRollback,
            NotificationEventType.AutomaticUpdateSucceeded => settings.NotifyAutomaticSuccess,
            NotificationEventType.ScheduledCheckFailed => settings.NotifyScheduledCheckFailure,
            NotificationEventType.TrueNasConnectionFailed => settings.NotifyConnectionFailure,
            _ => false
        };

    private static string? Sanitize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= 1024 ? value : value[..1024];
}
