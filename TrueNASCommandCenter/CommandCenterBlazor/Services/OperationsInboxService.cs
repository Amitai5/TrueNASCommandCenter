using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TrueNasCommandCenter.Data;
using TrueNasCommandCenter.Domain;
using TrueNasCommandCenter.Integrations.TrueNas;
using TrueNasCommandCenter.Notifications;

namespace TrueNasCommandCenter.Services;

public interface IOperationsInboxService
{
    /// <summary>Refreshes all available operations sources and reconciles their durable inbox state.</summary>
    /// <param name="cancellationToken">A token that cancels the refresh.</param>
    /// <returns>The partial-success refresh result.</returns>
    Task<OperationsInboxRefreshResult> RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns inbox items and summary counts matching the supplied filters.</summary>
    /// <param name="query">The search and state filters.</param>
    /// <param name="cancellationToken">A token that cancels the query.</param>
    /// <returns>The matching operations inbox snapshot.</returns>
    Task<OperationsInboxSnapshot> GetSnapshotAsync(OperationsInboxQuery query, CancellationToken cancellationToken = default);

    /// <summary>Returns the state-transition history for one inbox item.</summary>
    /// <param name="itemId">The inbox item identifier.</param>
    /// <param name="cancellationToken">A token that cancels the query.</param>
    /// <returns>The item's history in chronological order.</returns>
    Task<IReadOnlyList<OperationsInboxHistoryRecord>> GetHistoryAsync(Guid itemId, CancellationToken cancellationToken = default);

    /// <summary>Marks an unresolved inbox item as acknowledged by the local operator.</summary>
    /// <param name="itemId">The inbox item identifier.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>True when the item was changed.</returns>
    Task<bool> AcknowledgeAsync(Guid itemId, CancellationToken cancellationToken = default);

    /// <summary>Marks an inbox item as resolved without discarding its history.</summary>
    /// <param name="itemId">The inbox item identifier.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>True when the item was changed.</returns>
    Task<bool> ResolveAsync(Guid itemId, CancellationToken cancellationToken = default);
}

public sealed class OperationsInboxService(
    IDbContextFactory<AppDbContext> dbFactory,
    ITrueNasSystemClient trueNasClient,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<OperationsInboxService> logger) : IOperationsInboxService
{
    private const string TrueNasAlertsGroup = "truenas-alerts";
    private const string TrueNasJobsGroup = "truenas-active-jobs";
    private const string PoolScansGroup = "pool-active-scans";
    private const string KumaOutagesGroup = "kuma-outages";
    private readonly SemaphoreSlim refreshGate = new(1, 1);

    /// <inheritdoc />
    public async Task<OperationsInboxRefreshResult> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await refreshGate.WaitAsync(cancellationToken);
        try
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var observations = new List<ObservedOperation>();
            var successfulGroups = new HashSet<string>(StringComparer.Ordinal);
            var warnings = new List<string>();

            await CollectTrueNasAlertsAsync(observations, successfulGroups, warnings, cancellationToken);
            await CollectTrueNasJobsAsync(observations, successfulGroups, warnings, now, cancellationToken);
            await CollectPoolScansAsync(observations, successfulGroups, warnings, now, cancellationToken);
            await CollectLocalSourcesAsync(observations, successfulGroups, now, cancellationToken);

            var reconciliation = await ReconcileAsync(observations, successfulGroups, now, cancellationToken);
            foreach (var itemId in reconciliation.PushItemIds)
            {
                await SendPushAsync(itemId, cancellationToken);
            }

            return new OperationsInboxRefreshResult(observations.Count, reconciliation.ChangedCount, warnings);
        }
        finally
        {
            refreshGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<OperationsInboxSnapshot> GetSnapshotAsync(OperationsInboxQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var itemsQuery = db.OperationsInboxItems.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            itemsQuery = itemsQuery.Where(item => item.Title.Contains(search) || item.Summary.Contains(search) || (item.Details != null && item.Details.Contains(search)) || (item.SourceReference != null && item.SourceReference.Contains(search)));
        }

        if (query.Status is not null)
        {
            itemsQuery = itemsQuery.Where(item => item.Status == query.Status);
        }

        if (query.Source is not null)
        {
            itemsQuery = itemsQuery.Where(item => item.Source == query.Source);
        }

        if (query.Severity is not null)
        {
            itemsQuery = itemsQuery.Where(item => item.Severity == query.Severity);
        }

        if (query.SinceUtc is not null)
        {
            itemsQuery = itemsQuery.Where(item => item.OccurredUtc >= query.SinceUtc);
        }

        var limit = Math.Clamp(query.Limit, 1, 1000);
        var items = await itemsQuery.OrderByDescending(item => item.OccurredUtc).Take(limit).ToListAsync(cancellationToken);
        items = items
            .OrderBy(item => item.Status == OperationsInboxStatus.Resolved ? 1 : 0)
            .ThenByDescending(item => item.Severity)
            .ThenByDescending(item => item.LastObservedUtc)
            .ToList();

        var openCount = await db.OperationsInboxItems.CountAsync(item => item.Status == OperationsInboxStatus.Open, cancellationToken);
        var criticalCount = await db.OperationsInboxItems.CountAsync(item => item.Status != OperationsInboxStatus.Resolved && item.Severity == OperationsInboxSeverity.Critical, cancellationToken);
        var acknowledgedCount = await db.OperationsInboxItems.CountAsync(item => item.Status == OperationsInboxStatus.Acknowledged, cancellationToken);
        var resolvedCount = await db.OperationsInboxItems.CountAsync(item => item.Status == OperationsInboxStatus.Resolved, cancellationToken);
        var lastObservedUtc = await db.OperationsInboxItems.MaxAsync(item => (DateTime?)item.LastObservedUtc, cancellationToken);
        return new OperationsInboxSnapshot(items, openCount, criticalCount, acknowledgedCount, resolvedCount, lastObservedUtc);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OperationsInboxHistoryRecord>> GetHistoryAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.OperationsInboxHistory.AsNoTracking().Where(history => history.InboxItemId == itemId).OrderBy(history => history.TimestampUtc).ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> AcknowledgeAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        await refreshGate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var item = await db.OperationsInboxItems.SingleOrDefaultAsync(candidate => candidate.Id == itemId, cancellationToken);
            if (item is null || item.Status != OperationsInboxStatus.Open)
            {
                return false;
            }

            var now = timeProvider.GetUtcNow().UtcDateTime;
            item.Status = OperationsInboxStatus.Acknowledged;
            item.AcknowledgedUtc = now;
            AddHistory(db, item, OperationsInboxHistoryAction.Acknowledged, now, "Operator", "Acknowledged in TrueNAS Command Center.");
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        finally
        {
            refreshGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> ResolveAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        await refreshGate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var item = await db.OperationsInboxItems.SingleOrDefaultAsync(candidate => candidate.Id == itemId, cancellationToken);
            if (item is null || item.Status == OperationsInboxStatus.Resolved)
            {
                return false;
            }

            var now = timeProvider.GetUtcNow().UtcDateTime;
            item.Status = OperationsInboxStatus.Resolved;
            item.ResolvedUtc = now;
            AddHistory(db, item, OperationsInboxHistoryAction.Resolved, now, "Operator", "Resolved in TrueNAS Command Center.");
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        finally
        {
            refreshGate.Release();
        }
    }

    private async Task CollectTrueNasAlertsAsync(List<ObservedOperation> observations, ISet<string> successfulGroups, ICollection<string> warnings, CancellationToken cancellationToken)
    {
        try
        {
            var alerts = await trueNasClient.ListAlertsAsync(cancellationToken);
            successfulGroups.Add(TrueNasAlertsGroup);
            foreach (var alert in alerts.Where(alert => !alert.IsDismissed))
            {
                var reference = FirstNotEmpty(alert.Uuid, alert.Id, $"{alert.ClassName}:{alert.CreatedAt:O}");
                observations.Add(new ObservedOperation(
                    Fingerprint("truenas-alert", reference),
                    TrueNasAlertsGroup,
                    OperationsInboxSource.TrueNas,
                    OperationsInboxKind.TrueNasAlert,
                    MapAlertSeverity(alert.Level),
                    string.IsNullOrWhiteSpace(alert.ClassName) ? "TrueNAS alert" : Humanize(alert.ClassName),
                    Sanitize(alert.Text, 1024) ?? "TrueNAS reported an active alert.",
                    BuildDetails(("Source", alert.Source), ("Node", alert.Node), ("Level", alert.Level)),
                    reference,
                    null,
                    "/system#system-alerts",
                    alert.CreatedAt.UtcDateTime,
                    true,
                    null,
                    null));
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            warnings.Add(CreateSourceWarning("TrueNAS alerts", exception));
        }
    }

    private async Task CollectTrueNasJobsAsync(List<ObservedOperation> observations, ISet<string> successfulGroups, ICollection<string> warnings, DateTime now, CancellationToken cancellationToken)
    {
        try
        {
            var jobs = await trueNasClient.ListJobsAsync(cancellationToken: cancellationToken);
            successfulGroups.Add(TrueNasJobsGroup);
            foreach (var job in jobs)
            {
                var state = job.State.ToUpperInvariant();
                var isActive = state is "WAITING" or "RUNNING";
                var isFailed = state == "FAILED";
                if (!isActive && !isFailed)
                {
                    continue;
                }

                var relatedAppId = TryGetRelatedAppId(job);
                var title = string.IsNullOrWhiteSpace(job.Description) ? $"TrueNAS job {job.Id}" : Sanitize(job.Description, 256)!;
                var summary = isFailed ? Sanitize(job.Error, 1024) ?? "The TrueNAS job failed without an error message." : job.Progress?.Description ?? $"Job is {state.ToLowerInvariant()}.";
                observations.Add(new ObservedOperation(
                    Fingerprint("truenas-job", job.Id.ToString(CultureInfo.InvariantCulture)),
                    isActive ? TrueNasJobsGroup : null,
                    OperationsInboxSource.TrueNas,
                    OperationsInboxKind.TrueNasJob,
                    isFailed ? OperationsInboxSeverity.Error : OperationsInboxSeverity.Info,
                    title,
                    Sanitize(summary, 1024) ?? summary,
                    BuildDetails(("Method", job.Method), ("State", state), ("Job ID", job.Id.ToString(CultureInfo.InvariantCulture))),
                    job.Id.ToString(CultureInfo.InvariantCulture),
                    relatedAppId,
                    relatedAppId is null ? "/system" : AppLink(relatedAppId),
                    ReadDateTime(job.TimeStarted) ?? now,
                    isActive,
                    job.Progress?.Percent,
                    null));
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            warnings.Add(CreateSourceWarning("TrueNAS jobs", exception));
        }
    }

    private async Task CollectPoolScansAsync(List<ObservedOperation> observations, ISet<string> successfulGroups, ICollection<string> warnings, DateTime now, CancellationToken cancellationToken)
    {
        try
        {
            var pools = await trueNasClient.QueryPoolsAsync(cancellationToken);
            successfulGroups.Add(PoolScansGroup);
            foreach (var pool in pools.Where(pool => pool.Scan is not null))
            {
                var scan = pool.Scan!;
                var function = scan.Function?.ToUpperInvariant() ?? string.Empty;
                if (function is not "SCRUB" and not "RESILVER")
                {
                    continue;
                }

                var state = scan.State?.ToUpperInvariant() ?? string.Empty;
                var startedUtc = ReadDateTime(scan.StartTime) ?? now;
                var isActive = state is "SCANNING" or "RUNNING" or "PAUSED";
                var isFinished = state is "FINISHED" or "CANCELED" or "CANCELLED";
                if (!isActive && !isFinished)
                {
                    continue;
                }

                var kind = function == "RESILVER" ? OperationsInboxKind.PoolResilver : OperationsInboxKind.PoolScrub;
                var noun = function == "RESILVER" ? "resilver" : "scrub";
                var errorCount = scan.Errors ?? 0;
                var severity = errorCount > 0 ? OperationsInboxSeverity.Error : function == "RESILVER" && isActive ? OperationsInboxSeverity.Warning : OperationsInboxSeverity.Info;
                var summary = isActive ? $"{pool.Name} {noun} is {state.ToLowerInvariant()}." : $"{pool.Name} {noun} {state.ToLowerInvariant()} with {errorCount} error{(errorCount == 1 ? string.Empty : "s")}.";
                observations.Add(new ObservedOperation(
                    Fingerprint("pool-scan", $"{pool.Name}:{function}:{startedUtc:O}"),
                    isActive ? PoolScansGroup : null,
                    OperationsInboxSource.Storage,
                    kind,
                    severity,
                    $"{pool.Name} {noun}",
                    summary,
                    BuildDetails(("Pool", pool.Name), ("State", state), ("Errors", errorCount.ToString(CultureInfo.InvariantCulture)), ("Estimated seconds remaining", scan.TotalSecondsLeft?.ToString(CultureInfo.InvariantCulture))),
                    pool.Name,
                    null,
                    "/system#storage-pools",
                    startedUtc,
                    isActive,
                    scan.Percentage,
                    isFinished ? OperationsInboxStatus.Resolved : null));
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            warnings.Add(CreateSourceWarning("Pool scan activity", exception));
        }
    }

    private async Task CollectLocalSourcesAsync(List<ObservedOperation> observations, ISet<string> successfulGroups, DateTime now, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var cutoff = now.AddDays(-90);
        var apps = await db.Apps.AsNoTracking().ToDictionaryAsync(app => app.Id, app => app.Name, cancellationToken);
        var attempts = await db.UpdateAttempts.AsNoTracking().Where(attempt => attempt.StartedUtc >= cutoff && (attempt.Status == AttemptStatus.Failed || attempt.Status == AttemptStatus.Succeeded)).OrderBy(attempt => attempt.StartedUtc).ToListAsync(cancellationToken);
        foreach (var attempt in attempts.Where(attempt => attempt.Status == AttemptStatus.Failed))
        {
            var recovered = attempts.Any(candidate => candidate.AppId == attempt.AppId && candidate.Kind == attempt.Kind && candidate.Status == AttemptStatus.Succeeded && candidate.StartedUtc > attempt.StartedUtc);
            var appName = apps.GetValueOrDefault(attempt.AppId, attempt.AppId);
            observations.Add(new ObservedOperation(
                Fingerprint("app-update-failure", attempt.Id.ToString("N")),
                null,
                OperationsInboxSource.Apps,
                OperationsInboxKind.AppUpdateFailure,
                OperationsInboxSeverity.Error,
                $"{appName} update failed",
                Sanitize(attempt.ReasonMessage, 1024) ?? "The app update failed.",
                BuildDetails(("Reason code", attempt.ReasonCode), ("Target", attempt.ToVersion), ("TrueNAS job", attempt.TrueNasJobId?.ToString(CultureInfo.InvariantCulture)), ("Diagnostic", attempt.ErrorDetails)),
                attempt.Id.ToString("N"),
                attempt.AppId,
                $"/history?app={Uri.EscapeDataString(attempt.AppId)}",
                attempt.StartedUtc,
                false,
                null,
                recovered ? OperationsInboxStatus.Resolved : null));
        }

        var settings = await db.Settings.AsNoTracking().SingleAsync(item => item.Id == 1, cancellationToken);
        var kumaIsCurrent = string.IsNullOrWhiteSpace(settings.UptimeKumaBaseUrl) || string.IsNullOrWhiteSpace(settings.LastUptimeKumaError) || settings.LastUptimeKumaSuccessUtc >= settings.LastUptimeKumaSyncUtc;
        if (kumaIsCurrent)
        {
            successfulGroups.Add(KumaOutagesGroup);
        }

        var downMonitors = await db.UptimeKumaMonitors.AsNoTracking().Where(monitor => monitor.IsPresent && monitor.Status == UptimeKumaMonitorStatus.Down).ToListAsync(cancellationToken);
        foreach (var monitor in downMonitors)
        {
            observations.Add(new ObservedOperation(
                Fingerprint("kuma-outage", monitor.MonitorId),
                KumaOutagesGroup,
                OperationsInboxSource.UptimeKuma,
                OperationsInboxKind.UptimeKumaOutage,
                OperationsInboxSeverity.Error,
                $"{monitor.Name} is down",
                FirstNotEmpty(monitor.Url, monitor.Hostname, "Uptime Kuma reports this monitor as down."),
                BuildDetails(("Monitor type", monitor.Type), ("Host", monitor.Hostname), ("Port", monitor.Port?.ToString(CultureInfo.InvariantCulture))),
                monitor.MonitorId,
                monitor.AppId,
                "/monitoring",
                monitor.LastSeenUtc == default ? now : monitor.LastSeenUtc,
                true,
                null,
                null));
        }

        var notifications = await db.Notifications.AsNoTracking().Where(record => record.CreatedUtc >= cutoff && (record.Status == DeliveryStatus.Failed || record.Status == DeliveryStatus.Delivered)).OrderBy(record => record.CreatedUtc).ToListAsync(cancellationToken);
        foreach (var notification in notifications.Where(record => record.Status == DeliveryStatus.Failed))
        {
            var recovered = notifications.Any(candidate => candidate.EventId == notification.EventId && candidate.Provider == notification.Provider && candidate.Status == DeliveryStatus.Delivered && candidate.CreatedUtc > notification.CreatedUtc);
            observations.Add(new ObservedOperation(
                Fingerprint("notification-failure", notification.Id.ToString("N")),
                null,
                OperationsInboxSource.Notifications,
                OperationsInboxKind.NotificationFailure,
                OperationsInboxSeverity.Error,
                $"{Humanize(notification.Provider.ToString())} notification failed",
                Sanitize(notification.ErrorSummary, 1024) ?? "Notification delivery failed without an error message.",
                BuildDetails(("Event", Humanize(notification.EventType.ToString())), ("Provider", notification.Provider.ToString()), ("HTTP status", notification.HttpStatusCode?.ToString(CultureInfo.InvariantCulture))),
                notification.Id.ToString("N"),
                notification.AppId,
                "/history",
                notification.CreatedUtc,
                false,
                null,
                recovered ? OperationsInboxStatus.Resolved : null));
        }
    }

    private async Task<ReconciliationResult> ReconcileAsync(IReadOnlyCollection<ObservedOperation> observations, ISet<string> successfulGroups, DateTime now, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var fingerprints = observations.Select(observation => observation.Fingerprint).ToHashSet(StringComparer.Ordinal);
        var existingItems = await db.OperationsInboxItems.Where(item => fingerprints.Contains(item.Fingerprint) || item.IsSourceActive).ToDictionaryAsync(item => item.Fingerprint, StringComparer.Ordinal, cancellationToken);
        var pushItemIds = new List<Guid>();
        var changedCount = 0;

        foreach (var observation in observations)
        {
            if (!existingItems.TryGetValue(observation.Fingerprint, out var item))
            {
                item = CreateItem(observation, now);
                db.OperationsInboxItems.Add(item);
                AddHistory(db, item, OperationsInboxHistoryAction.Detected, now, "System", observation.DesiredStatus == OperationsInboxStatus.Resolved ? "Imported as already resolved." : "Detected by unified operations refresh.");
                existingItems[item.Fingerprint] = item;
                changedCount++;
                if (ShouldPush(item))
                {
                    pushItemIds.Add(item.Id);
                }

                continue;
            }

            var wasSourceActive = item.IsSourceActive;
            ApplyObservation(item, observation, now);
            if (observation.DesiredStatus == OperationsInboxStatus.Resolved && item.Status != OperationsInboxStatus.Resolved)
            {
                ResolveItem(db, item, now, "System", "The source reported a successful recovery or completion.");
                changedCount++;
            }
            else if (observation.IsSourceActive && item.Status == OperationsInboxStatus.Resolved && !wasSourceActive)
            {
                item.Status = OperationsInboxStatus.Open;
                item.AcknowledgedUtc = null;
                item.ResolvedUtc = null;
                item.OccurrenceCount++;
                item.PushState = OperationsInboxPushState.NotRequested;
                item.PushAttemptedUtc = null;
                item.PushError = null;
                AddHistory(db, item, OperationsInboxHistoryAction.Reopened, now, "System", "The source recovered previously and is reporting the condition again.");
                changedCount++;
                if (ShouldPush(item))
                {
                    pushItemIds.Add(item.Id);
                }
            }
        }

        var activeFingerprints = observations.Where(observation => observation.IsSourceActive).Select(observation => observation.Fingerprint).ToHashSet(StringComparer.Ordinal);
        foreach (var item in existingItems.Values.Where(item => item.IsSourceActive && item.CorrelationGroup is not null && successfulGroups.Contains(item.CorrelationGroup) && !activeFingerprints.Contains(item.Fingerprint)))
        {
            item.IsSourceActive = false;
            if (item.Status != OperationsInboxStatus.Resolved)
            {
                ResolveItem(db, item, now, "System", "The source no longer reports this condition.");
                changedCount++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ReconciliationResult(changedCount, pushItemIds);
    }

    private async Task SendPushAsync(Guid itemId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var pushSender = scope.ServiceProvider.GetRequiredService<IWebPushNotificationSender>();
        if (!await pushSender.HasSubscriptionsAsync(cancellationToken))
        {
            await SetPushStateAsync(itemId, OperationsInboxPushState.NoSubscription, null, cancellationToken);
            return;
        }

        OperationsInboxItem item;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            item = await db.OperationsInboxItems.AsNoTracking().SingleAsync(candidate => candidate.Id == itemId, cancellationToken);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var deduplicationKey = $"operations-inbox:{item.Id:N}:{item.OccurrenceCount}";
        await SetPushStateAsync(itemId, OperationsInboxPushState.Pending, null, cancellationToken);
        try
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<INotificationDispatcher>();
            await dispatcher.DispatchAsync(new NotificationEvent(
                Guid.NewGuid(),
                NotificationEventType.OperationsInboxIncident,
                now,
                deduplicationKey,
                item.Title,
                item.Summary,
                $"OPERATIONS_INBOX_{item.Kind.ToString().ToUpperInvariant()}",
                item.RelatedAppId), cancellationToken);

            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var delivery = await db.Notifications.AsNoTracking().Where(record => record.DeduplicationKey == deduplicationKey && record.Provider == NotificationProvider.Push).OrderByDescending(record => record.CreatedUtc).FirstOrDefaultAsync(cancellationToken);
            if (delivery?.Status == DeliveryStatus.Delivered)
            {
                await SetPushStateAsync(itemId, OperationsInboxPushState.Delivered, null, cancellationToken);
            }
            else
            {
                await SetPushStateAsync(itemId, OperationsInboxPushState.Failed, delivery?.ErrorSummary ?? "Push delivery did not complete.", cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Operations inbox push delivery failed for item {InboxItemId}", itemId);
            await SetPushStateAsync(itemId, OperationsInboxPushState.Failed, "Push delivery failed. Check notification history.", cancellationToken);
        }
    }

    private async Task SetPushStateAsync(Guid itemId, OperationsInboxPushState state, string? error, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var item = await db.OperationsInboxItems.SingleAsync(candidate => candidate.Id == itemId, cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        item.PushState = state;
        item.PushAttemptedUtc = now;
        item.PushError = Sanitize(error, 512);
        if (state is OperationsInboxPushState.Delivered or OperationsInboxPushState.Failed)
        {
            AddHistory(db, item, state == OperationsInboxPushState.Delivered ? OperationsInboxHistoryAction.PushDelivered : OperationsInboxHistoryAction.PushFailed, now, "System", state == OperationsInboxPushState.Delivered ? "Browser push delivered." : item.PushError ?? "Browser push failed.");
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static OperationsInboxItem CreateItem(ObservedOperation observation, DateTime now) => new()
    {
        Fingerprint = observation.Fingerprint,
        CorrelationGroup = observation.CorrelationGroup,
        Source = observation.Source,
        Kind = observation.Kind,
        Severity = observation.Severity,
        Status = observation.DesiredStatus ?? OperationsInboxStatus.Open,
        Title = observation.Title,
        Summary = observation.Summary,
        Details = observation.Details,
        SourceReference = observation.SourceReference,
        RelatedAppId = observation.RelatedAppId,
        DeepLink = observation.DeepLink,
        OccurredUtc = observation.OccurredUtc,
        LastObservedUtc = now,
        ResolvedUtc = observation.DesiredStatus == OperationsInboxStatus.Resolved ? now : null,
        IsSourceActive = observation.IsSourceActive,
        ProgressPercent = observation.ProgressPercent
    };

    private static void ApplyObservation(OperationsInboxItem item, ObservedOperation observation, DateTime now)
    {
        item.CorrelationGroup = observation.CorrelationGroup;
        item.Source = observation.Source;
        item.Kind = observation.Kind;
        item.Severity = observation.Severity;
        item.Title = observation.Title;
        item.Summary = observation.Summary;
        item.Details = observation.Details;
        item.SourceReference = observation.SourceReference;
        item.RelatedAppId = observation.RelatedAppId;
        item.DeepLink = observation.DeepLink;
        item.OccurredUtc = observation.OccurredUtc;
        item.LastObservedUtc = now;
        item.IsSourceActive = observation.IsSourceActive;
        item.ProgressPercent = observation.ProgressPercent;
    }

    private static void ResolveItem(AppDbContext db, OperationsInboxItem item, DateTime now, string actor, string message)
    {
        item.Status = OperationsInboxStatus.Resolved;
        item.ResolvedUtc = now;
        AddHistory(db, item, OperationsInboxHistoryAction.Resolved, now, actor, message);
    }

    private static void AddHistory(AppDbContext db, OperationsInboxItem item, OperationsInboxHistoryAction action, DateTime timestampUtc, string actor, string message)
    {
        db.OperationsInboxHistory.Add(new OperationsInboxHistoryRecord
        {
            InboxItem = item,
            InboxItemId = item.Id,
            Action = action,
            TimestampUtc = timestampUtc,
            Actor = actor,
            Message = message
        });
    }

    private static bool ShouldPush(OperationsInboxItem item) => item.Source != OperationsInboxSource.Notifications && item.Status == OperationsInboxStatus.Open && item.Severity >= OperationsInboxSeverity.Warning;

    private string CreateSourceWarning(string source, Exception exception)
    {
        var diagnosticId = Guid.NewGuid().ToString("N");
        var code = exception is TrueNasClientException trueNasException ? trueNasException.Code : "SOURCE_UNAVAILABLE";
        logger.LogWarning(exception, "Operations inbox source {Source} failed. Error code {ErrorCode}. Diagnostic ID {DiagnosticId}", source, code, diagnosticId);
        return $"{source} could not be refreshed. Error code: {code}. Diagnostic ID: {diagnosticId}.";
    }

    private static OperationsInboxSeverity MapAlertSeverity(string value) => value.ToUpperInvariant() switch
    {
        "EMERGENCY" or "ALERT" or "CRITICAL" => OperationsInboxSeverity.Critical,
        "ERROR" => OperationsInboxSeverity.Error,
        "WARNING" or "WARN" => OperationsInboxSeverity.Warning,
        _ => OperationsInboxSeverity.Info
    };

    private static string? TryGetRelatedAppId(TrueNasJobDto job)
    {
        if (!job.Method.StartsWith("app.", StringComparison.OrdinalIgnoreCase) || job.Arguments.ValueKind != JsonValueKind.Array || job.Arguments.GetArrayLength() == 0)
        {
            return null;
        }

        var first = job.Arguments[0];
        return first.ValueKind == JsonValueKind.String ? Sanitize(first.GetString(), 256) : null;
    }

    private static DateTime? ReadDateTime(JsonElement element)
    {
        if (element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(element.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed.UtcDateTime;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var unixValue))
        {
            return (unixValue > 9_999_999_999 ? DateTimeOffset.FromUnixTimeMilliseconds(unixValue) : DateTimeOffset.FromUnixTimeSeconds(unixValue)).UtcDateTime;
        }

        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("$date", out var dateValue))
        {
            return ReadDateTime(dateValue);
        }

        return null;
    }

    private static string Fingerprint(string prefix, string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return $"{prefix}:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private static string AppLink(string appId) => $"/apps/{Uri.EscapeDataString(appId)}";

    private static string FirstNotEmpty(params string?[] values) => values.First(value => !string.IsNullOrWhiteSpace(value))!;

    private static string Humanize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if ((character == '_' || character == '-') && builder.Length > 0)
            {
                builder.Append(' ');
            }
            else if (index > 0 && char.IsUpper(character) && char.IsLower(value[index - 1]))
            {
                builder.Append(' ').Append(character);
            }
            else
            {
                builder.Append(character);
            }
        }

        var result = builder.ToString().Trim();
        return result.Length == 0 ? value : char.ToUpperInvariant(result[0]) + result[1..];
    }

    private static string? BuildDetails(params (string Label, string? Value)[] fields)
    {
        var lines = fields.Where(field => !string.IsNullOrWhiteSpace(field.Value)).Select(field => $"{field.Label}: {field.Value}").ToList();
        return lines.Count == 0 ? null : Sanitize(string.Join(Environment.NewLine, lines), 4096);
    }

    private static string? Sanitize(string? value, int maximumLength) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length <= maximumLength ? value.Trim() : value.Trim()[..maximumLength];

    private sealed record ObservedOperation(
        string Fingerprint,
        string? CorrelationGroup,
        OperationsInboxSource Source,
        OperationsInboxKind Kind,
        OperationsInboxSeverity Severity,
        string Title,
        string Summary,
        string? Details,
        string? SourceReference,
        string? RelatedAppId,
        string DeepLink,
        DateTime OccurredUtc,
        bool IsSourceActive,
        double? ProgressPercent,
        OperationsInboxStatus? DesiredStatus);

    private sealed record ReconciliationResult(int ChangedCount, IReadOnlyList<Guid> PushItemIds);
}
