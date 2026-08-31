using Microsoft.EntityFrameworkCore;
using TrueNasCommandCenter.Data;
using TrueNasCommandCenter.Domain;
using TrueNasCommandCenter.Services;

namespace TrueNasCommandCenter.Notifications;

public interface INotificationDispatcher
{
    Task DispatchAsync(NotificationEvent notification, CancellationToken cancellationToken = default);
}

public sealed class NotificationDispatcher(
    IDbContextFactory<AppDbContext> dbFactory,
    IEmailNotificationSender emailSender,
    IWebhookNotificationSender webhookSender,
    IWebPushNotificationSender pushSender,
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

        if (ShouldSendExternalProvider(notification.EventType) && settings.EmailEnabled)
        {
            await DeliverAsync(notification, NotificationProvider.Email, emailSender.SendAsync, cancellationToken);
        }

        if (ShouldSendExternalProvider(notification.EventType) && settings.WebhookEnabled)
        {
            await DeliverAsync(notification, NotificationProvider.Webhook, webhookSender.SendAsync, cancellationToken);
        }

        if (ShouldSendPush(notification.EventType) && await pushSender.HasSubscriptionsAsync(cancellationToken))
        {
            await DeliverAsync(notification, NotificationProvider.Push, pushSender.SendAsync, cancellationToken);
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
            NotificationEventType.OperationsInboxIncident => true,
            _ => false
        };

    private static bool ShouldSendPush(NotificationEventType eventType) =>
        eventType is NotificationEventType.AppDowntime or
            NotificationEventType.AppRecoveryFailed or
            NotificationEventType.ManualApprovalAvailable or
            NotificationEventType.AutomaticUpdateFailed or
            NotificationEventType.AutomaticUpdateBlocked or
            NotificationEventType.RollbackOccurred or
            NotificationEventType.ScheduledCheckFailed or
            NotificationEventType.TrueNasConnectionFailed or
            NotificationEventType.OperationsInboxIncident;

    private static bool ShouldSendExternalProvider(NotificationEventType eventType) =>
        eventType != NotificationEventType.OperationsInboxIncident;

    private static string? Sanitize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= 1024 ? value : value[..1024];
}
