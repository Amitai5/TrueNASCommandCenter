using System.Net;
using Microsoft.EntityFrameworkCore;
using TrueNasCommandCenter.Data;
using TrueNasCommandCenter.Domain;

namespace TrueNasCommandCenter.Notifications;

public interface IWebPushNotificationSender
{
    /// <summary>Reports whether at least one browser device can receive push notifications.</summary>
    /// <param name="cancellationToken">A token that cancels the subscription query.</param>
    /// <returns>True when at least one active subscription exists.</returns>
    Task<bool> HasSubscriptionsAsync(CancellationToken cancellationToken = default);

    /// <summary>Sends a privacy-preserving push wake-up to every registered browser device.</summary>
    /// <param name="notification">The local event that triggered the generic push alert.</param>
    /// <param name="cancellationToken">A token that cancels delivery.</param>
    /// <returns>The aggregate delivery result.</returns>
    Task<NotificationDeliveryResult> SendAsync(NotificationEvent notification, CancellationToken cancellationToken = default);
}

public sealed class WebPushNotificationSender(
    IWebPushSubscriptionService subscriptionService,
    IWebPushProtocolClient protocolClient,
    IDbContextFactory<AppDbContext> dbFactory,
    TimeProvider timeProvider,
    ILogger<WebPushNotificationSender> logger) : IWebPushNotificationSender
{
    /// <inheritdoc />
    public Task<bool> HasSubscriptionsAsync(CancellationToken cancellationToken = default) => subscriptionService.HasSubscriptionsAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<NotificationDeliveryResult> SendAsync(NotificationEvent notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var configuration = await subscriptionService.GetDeliveryConfigurationAsync(cancellationToken);
        if (configuration.Subscriptions.Count == 0)
        {
            return new NotificationDeliveryResult(false, Error: "No browser devices are subscribed to push notifications.");
        }

        var deliveries = new List<SubscriptionDelivery>(configuration.Subscriptions.Count);
        foreach (var subscription in configuration.Subscriptions)
        {
            try
            {
                var statusCode = await protocolClient.SendAsync(
                    new WebPushProtocolRequest(subscription.Endpoint, configuration.PublicKey, configuration.PrivateKey),
                    cancellationToken);
                deliveries.Add(new SubscriptionDelivery(subscription.Id, true, statusCode, false, null));
            }
            catch (WebPushProtocolException exception)
            {
                var isExpired = exception.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone;
                deliveries.Add(new SubscriptionDelivery(subscription.Id, false, (int)exception.StatusCode, isExpired, exception.Message));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var diagnosticId = Guid.NewGuid().ToString("N");
                logger.LogWarning("Browser push delivery failed for subscription {SubscriptionId}. Event {EventType}. Failure type {FailureType}. Diagnostic ID: {DiagnosticId}", subscription.Id, notification.EventType, exception.GetType().Name, diagnosticId);
                deliveries.Add(new SubscriptionDelivery(subscription.Id, false, null, false, $"Push delivery failed. Diagnostic ID: {diagnosticId}."));
            }
        }

        await PersistDeliveryStateAsync(deliveries, cancellationToken);
        var deliveredCount = deliveries.Count(item => item.Success);
        if (deliveredCount > 0)
        {
            var failedCount = deliveries.Count - deliveredCount;
            var summary = failedCount > 0 ? $"Delivered to {deliveredCount} device(s); {failedCount} device(s) failed." : null;
            return new NotificationDeliveryResult(true, Error: summary);
        }

        var firstFailure = deliveries.Select(item => item.Error).FirstOrDefault(error => !string.IsNullOrWhiteSpace(error));
        return new NotificationDeliveryResult(false, deliveries.Select(item => item.StatusCode).FirstOrDefault(status => status is not null), firstFailure ?? "Push delivery failed for every registered device.");
    }

    private async Task PersistDeliveryStateAsync(IReadOnlyCollection<SubscriptionDelivery> deliveries, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var ids = deliveries.Select(item => item.SubscriptionId).ToHashSet();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var subscriptions = await db.WebPushSubscriptions.Where(item => ids.Contains(item.Id)).ToListAsync(cancellationToken);
        foreach (var subscription in subscriptions)
        {
            var delivery = deliveries.Single(item => item.SubscriptionId == subscription.Id);
            if (delivery.IsExpired)
            {
                db.WebPushSubscriptions.Remove(subscription);
                continue;
            }

            if (delivery.Success)
            {
                subscription.LastSuccessUtc = now;
                subscription.ConsecutiveFailures = 0;
                subscription.LastError = null;
            }
            else
            {
                subscription.LastFailureUtc = now;
                subscription.ConsecutiveFailures++;
                subscription.LastError = Sanitize(delivery.Error);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static string? Sanitize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Length <= 512 ? value : value[..512];

    private sealed record SubscriptionDelivery(Guid SubscriptionId, bool Success, int? StatusCode, bool IsExpired, string? Error);
}
