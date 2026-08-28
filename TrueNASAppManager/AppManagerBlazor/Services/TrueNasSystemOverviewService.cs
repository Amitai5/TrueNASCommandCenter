using TrueNasAppManager.Domain;
using TrueNasAppManager.Integrations.TrueNas;

namespace TrueNasAppManager.Services;

/// <summary>Loads and maps independent TrueNAS system capabilities into display-safe models.</summary>
public sealed class TrueNasSystemOverviewService(ITrueNasSystemClient trueNasClient, IStoragePoolOverviewService storagePoolService, ILogger<TrueNasSystemOverviewService> logger) : ITrueNasSystemOverviewService
{
    /// <inheritdoc />
    public async Task<TrueNasSystemOverview> GetAsync(CancellationToken cancellationToken = default)
    {
        var hostTask = LoadHostAsync(cancellationToken);
        var updateTask = LoadUpdateAsync(cancellationToken);
        var alertTask = LoadAlertsAsync(cancellationToken);
        var storageTask = storagePoolService.GetAsync(cancellationToken);

        await Task.WhenAll(hostTask, updateTask, alertTask, storageTask);
        return new TrueNasSystemOverview(await hostTask, await updateTask, await alertTask, await storageTask);
    }

    private async Task<TrueNasHostOverview> LoadHostAsync(CancellationToken cancellationToken)
    {
        try
        {
            var host = await trueNasClient.GetSystemInfoAsync(cancellationToken);
            return new TrueNasHostOverview(new TrueNasHostInformation(
                host.Hostname,
                host.Version,
                host.CpuModel,
                host.PhysicalMemory,
                host.CoreCount,
                host.PhysicalCoreCount,
                LoadAverageAt(host.LoadAverage, 0),
                LoadAverageAt(host.LoadAverage, 1),
                LoadAverageAt(host.LoadAverage, 2),
                host.Uptime,
                host.BootTime.ToUniversalTime(),
                host.TimeZoneId,
                host.SystemManufacturer,
                host.SystemProduct,
                host.HasEccMemory));
        }
        catch (TrueNasClientException exception) when (IsPermissionFailure(exception))
        {
            logger.LogInformation("TrueNAS host information is unavailable because the service account does not have READONLY_ADMIN");
            return new TrueNasHostOverview(null, RequiresReadOnlyAdmin: true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "TrueNAS host information could not be loaded");
            return new TrueNasHostOverview(null, Error: "Host information is temporarily unavailable.");
        }
    }

    private async Task<TrueNasUpdateOverview> LoadUpdateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var update = await trueNasClient.GetUpdateStatusAsync(cancellationToken);
            var currentVersion = update.Status?.CurrentVersion;
            var availableVersion = update.Status?.NewVersion;
            var progress = update.DownloadProgress;
            var checkError = string.Equals(update.Code, "ERROR", StringComparison.OrdinalIgnoreCase)
                ? NormalizeText(update.Error?.Reason ?? "TrueNAS could not determine update availability.")
                : null;
            return new TrueNasUpdateOverview(new TrueNasUpdateInformation(
                currentVersion?.Train,
                currentVersion?.Profile,
                currentVersion is null ? null : currentVersion.MatchesProfile,
                availableVersion?.Version,
                NormalizeOptionalText(availableVersion?.ReleaseNotes),
                ParseHttpsUri(availableVersion?.ReleaseNotesUrl),
                progress is null ? null : Math.Clamp(progress.Percent, 0, 100),
                NormalizeOptionalText(progress?.Description),
                progress?.Version,
                checkError));
        }
        catch (TrueNasClientException exception) when (IsPermissionFailure(exception))
        {
            logger.LogInformation("TrueNAS update status is unavailable because the service account does not have SYSTEM_UPDATE_READ");
            return new TrueNasUpdateOverview(null, RequiresSystemUpdateRead: true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "TrueNAS update status could not be loaded");
            return new TrueNasUpdateOverview(null, Error: "Update status is temporarily unavailable.");
        }
    }

    private async Task<TrueNasAlertOverview> LoadAlertsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var alerts = await trueNasClient.ListAlertsAsync(cancellationToken);
            var mapped = alerts
                .Select(alert => new TrueNasSystemAlert(
                    FirstNotBlank(alert.Uuid, alert.Id, alert.ClassName, "unknown-alert"),
                    FirstNotBlank(alert.Source, "TrueNAS"),
                    FirstNotBlank(alert.ClassName, "Unknown"),
                    FirstNotBlank(alert.Node, "Local system"),
                    alert.CreatedAt.ToUniversalTime(),
                    alert.LastOccurrence.ToUniversalTime(),
                    alert.IsDismissed,
                    NormalizeText(FirstNotBlank(alert.Text, alert.ClassName, "TrueNAS reported an alert.")),
                    NormalizeSeverity(alert.Level),
                    alert.IsOneShot))
                .OrderBy(alert => alert.IsDismissed)
                .ThenBy(alert => SeverityPriority(alert.Severity))
                .ThenByDescending(alert => alert.LastOccurrenceUtc)
                .ToList();
            return new TrueNasAlertOverview(mapped);
        }
        catch (TrueNasClientException exception) when (IsPermissionFailure(exception))
        {
            logger.LogInformation("TrueNAS alerts are unavailable because the service account does not have ALERT_LIST_READ");
            return new TrueNasAlertOverview([], RequiresAlertListRead: true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "TrueNAS alerts could not be loaded");
            return new TrueNasAlertOverview([], Error: "System alerts are temporarily unavailable.");
        }
    }

    private static bool IsPermissionFailure(TrueNasClientException exception) =>
        exception.Code is "-32001" or "EACCES" or "EPERM" ||
        exception.Message.Contains("permission", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("authorized", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("role", StringComparison.OrdinalIgnoreCase);

    private static double? LoadAverageAt(IReadOnlyList<double> values, int index) => values.Count > index ? values[index] : null;

    private static string NormalizeSeverity(string? value) => string.IsNullOrWhiteSpace(value) ? "INFO" : value.Trim().ToUpperInvariant();

    private static int SeverityPriority(string severity) => severity switch
    {
        "EMERGENCY" => 0,
        "ALERT" => 1,
        "CRITICAL" => 2,
        "ERROR" => 3,
        "WARNING" => 4,
        "NOTICE" => 5,
        "INFO" => 6,
        "DEBUG" => 7,
        _ => 8
    };

    private static string FirstNotBlank(params string?[] values) => values.First(value => !string.IsNullOrWhiteSpace(value))!.Trim();

    private static string NormalizeText(string value)
    {
        const int maximumLength = 4_096;
        var normalized = value.Trim();
        return normalized.Length <= maximumLength ? normalized : $"{normalized[..(maximumLength - 1)]}…";
    }

    private static string? NormalizeOptionalText(string? value) => string.IsNullOrWhiteSpace(value) ? null : NormalizeText(value);

    private static Uri? ParseHttpsUri(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? uri : null;
}
