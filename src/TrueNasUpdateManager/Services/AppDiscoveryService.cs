using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TrueNasUpdateManager.Data;
using TrueNasUpdateManager.Integrations.TrueNas;

namespace TrueNasUpdateManager.Services;

public interface IAppDiscoveryService
{
    Task<IReadOnlyList<Domain.AppRecord>> DiscoverAsync(CancellationToken cancellationToken = default);
    Task<Domain.AppRecord> DiscoverAppAsync(string appId, CancellationToken cancellationToken = default);
}

public sealed class AppDiscoveryService(
    ITrueNasClient trueNasClient,
    IDbContextFactory<AppDbContext> dbFactory,
    TimeProvider timeProvider) : IAppDiscoveryService
{
    public async Task<IReadOnlyList<Domain.AppRecord>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var apps = await trueNasClient.QueryAppsAsync(cancellationToken);
        var discovered = new List<Domain.AppRecord>(apps.Count);
        foreach (var app in apps.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            discovered.Add(await UpsertAsync(app, cancellationToken));
        }

        return discovered;
    }

    public async Task<Domain.AppRecord> DiscoverAppAsync(
        string appId,
        CancellationToken cancellationToken = default)
    {
        var app = await trueNasClient.GetAppAsync(appId, cancellationToken);
        return await UpsertAsync(app, cancellationToken);
    }

    private async Task<Domain.AppRecord> UpsertAsync(
        TrueNasAppDto source,
        CancellationToken cancellationToken)
    {
        TrueNasUpgradeSummaryDto? summary = null;
        IReadOnlyList<string> outdatedImages = [];

        if (source.UpgradeAvailable)
        {
            try
            {
                summary = await trueNasClient.GetUpgradeSummaryAsync(source.Id, cancellationToken: cancellationToken);
            }
            catch (TrueNasClientException)
            {
                // Discovery remains useful when a per-app summary is temporarily unavailable.
            }
        }

        if (source.ImageUpdatesAvailable)
        {
            try
            {
                outdatedImages = await trueNasClient.GetOutdatedImagesAsync(source.Id, cancellationToken);
            }
            catch (TrueNasClientException)
            {
                // Preserve the update flag even if TrueNAS cannot list individual images.
            }
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var app = await db.Apps.SingleOrDefaultAsync(item => item.Id == source.Id, cancellationToken);
        if (app is null)
        {
            app = new Domain.AppRecord
            {
                Id = source.Id,
                Policy = null
            };
            db.Apps.Add(app);
        }

        app.Name = string.IsNullOrWhiteSpace(source.Name) ? source.Id : source.Name;
        app.IsCustom = source.CustomApp;
        app.State = source.State;
        app.InstalledVersion = source.Version;
        app.HumanVersion = source.HumanVersion;
        app.LatestVersion = summary?.UpgradeVersion ?? source.LatestVersion;
        app.LatestHumanVersion = summary?.UpgradeHumanVersion ?? source.LatestAppVersion;
        app.CatalogUpdateAvailable = source.UpgradeAvailable;
        app.ImageUpdateAvailable = source.ImageUpdatesAvailable;
        app.OutdatedImagesJson = outdatedImages.Count == 0 ? null : JsonSerializer.Serialize(outdatedImages);
        app.ActionRequired = source.ActionRequired;
        app.LastSeenUtc = now;
        app.LastCheckUtc = now;
        SetStatus(app);

        await db.SaveChangesAsync(cancellationToken);
        return app;
    }

    private static void SetStatus(Domain.AppRecord app)
    {
        if (app.ActionRequired)
        {
            app.StatusLabel = "Blocked: action required";
            app.StatusMessage = "TrueNAS requires administrator action.";
        }
        else if (app.Policy is null)
        {
            app.StatusLabel = "Needs configuration";
            app.StatusMessage = "Choose an update policy.";
        }
        else if (app.State == "STOPPED")
        {
            app.StatusLabel = "Stopped";
            app.StatusMessage = null;
        }
        else if (app.CatalogUpdateAvailable)
        {
            app.StatusLabel = "Update available";
            app.StatusMessage = null;
        }
        else if (app.ImageUpdateAvailable)
        {
            app.StatusLabel = "Image update";
            app.StatusMessage = null;
        }
        else
        {
            app.StatusLabel = "Up to date";
            app.StatusMessage = null;
        }
    }
}
