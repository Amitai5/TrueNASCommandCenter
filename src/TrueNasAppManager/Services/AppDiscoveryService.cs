using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TrueNasAppManager.Data;
using TrueNasAppManager.Integrations.TrueNas;

namespace TrueNasAppManager.Services;

public interface IAppDiscoveryService
{
    /// <summary>Refreshes and reconciles the complete installed application inventory.</summary>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>A summary of the refreshed inventory.</returns>
    Task<Domain.InventoryRefreshResult> RefreshAsync(CancellationToken cancellationToken = default);
    /// <summary>Discovers all installed applications and returns their persisted records.</summary>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The installed application records.</returns>
    Task<IReadOnlyList<Domain.AppRecord>> DiscoverAsync(CancellationToken cancellationToken = default);
    /// <summary>Refreshes one installed application.</summary>
    /// <param name="appId">The TrueNAS application identifier.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The persisted application record.</returns>
    Task<Domain.AppRecord> DiscoverAppAsync(string appId, CancellationToken cancellationToken = default);
}

public sealed class AppDiscoveryService(
    ITrueNasClient trueNasClient,
    IDbContextFactory<AppDbContext> dbFactory,
    TimeProvider timeProvider) : IAppDiscoveryService
{
    public async Task<Domain.InventoryRefreshResult> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var discovered = await DiscoverAsync(cancellationToken);
        var discoveredIds = discovered.Select(app => app.Id).ToHashSet(StringComparer.Ordinal);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var apps = await db.Apps.ToListAsync(cancellationToken);
        foreach (var app in apps.Where(app => !discoveredIds.Contains(app.Id)))
        {
            if (app.IsInstalled)
            {
                app.MissingSinceUtc = now;
            }

            app.IsInstalled = false;
            app.HealthState = Domain.AppHealthState.Unknown;
            app.HealthMessage = "This app was not returned by the latest TrueNAS inventory refresh.";
        }

        var settings = await db.Settings.SingleAsync(item => item.Id == 1, cancellationToken);
        settings.LastInventoryRefreshUtc = now;
        await db.SaveChangesAsync(cancellationToken);
        return new Domain.InventoryRefreshResult(discovered.Count, apps.Count(app => !app.IsInstalled), discovered.Select(app => app.Id).ToList());
    }

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

    public async Task<Domain.AppRecord> DiscoverAppAsync(string appId, CancellationToken cancellationToken = default)
    {
        var app = await trueNasClient.GetAppAsync(appId, cancellationToken);
        return await UpsertAsync(app, cancellationToken);
    }

    private async Task<Domain.AppRecord> UpsertAsync(TrueNasAppDto source, CancellationToken cancellationToken)
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
        var app = await db.Apps.AsSplitQuery().Include(item => item.Ports).Include(item => item.Portals).Include(item => item.Containers).SingleOrDefaultAsync(item => item.Id == source.Id, cancellationToken);
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
        app.IsInstalled = true;
        app.MissingSinceUtc = null;
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
        MapMetadata(app, source.Metadata);
        ReplaceWorkloads(db, app, source);
        app.HealthState = DetermineHealth(app, source.ActiveWorkloads);
        app.LastHealthCheckUtc = now;
        app.HealthMessage = HealthMessage(app.HealthState);
        SetStatus(app);

        await db.SaveChangesAsync(cancellationToken);
        return app;
    }

    private static bool IsDown(string state) =>
        string.Equals(state, "STOPPED", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(state, "CRASHED", StringComparison.OrdinalIgnoreCase);

    private static void MapMetadata(Domain.AppRecord app, JsonElement metadata)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        app.Description = ReadString(metadata, "description") ?? app.Description;
        app.HomeUrl = ReadString(metadata, "home") ?? ReadString(metadata, "homepage") ?? app.HomeUrl;
        app.IconUrl = ReadString(metadata, "icon") ?? app.IconUrl;
        app.Train = ReadString(metadata, "train") ?? app.Train;
        if (metadata.TryGetProperty("sources", out var sources) && sources.ValueKind == JsonValueKind.Array)
        {
            var urls = sources.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()).Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            app.SourceUrlsJson = urls.Count == 0 ? null : JsonSerializer.Serialize(urls);
        }
    }

    private static void ReplaceWorkloads(AppDbContext db, Domain.AppRecord app, TrueNasAppDto source)
    {
        db.AppPorts.RemoveRange(app.Ports);
        db.AppPortals.RemoveRange(app.Portals);
        db.AppContainers.RemoveRange(app.Containers);
        app.Ports = ParsePorts(source.ActiveWorkloads, app.Id);
        app.Portals = ParsePortals(source.Portals, app.Id);
        app.Containers = ParseContainers(source.ActiveWorkloads, app.Id);
    }

    private static List<Domain.AppPortRecord> ParsePorts(JsonElement workloads, string appId)
    {
        var result = new List<Domain.AppPortRecord>();
        if (TryGetArray(workloads, "container_details", out var containers))
        {
            foreach (var container in containers.EnumerateArray())
            {
                if (TryGetArray(container, "port_config", out var containerPorts))
                {
                    ParsePortMappings(containerPorts, appId, ReadString(container, "service_name"), result);
                }
            }
        }

        if (TryGetArray(workloads, "used_ports", out var ports))
        {
            ParsePortMappings(ports, appId, null, result);
        }

        return result.GroupBy(port => new { port.HostPort, port.Protocol }).Select(group => group.OrderByDescending(port => !string.IsNullOrWhiteSpace(port.ContainerName)).First()).ToList();
    }

    private static void ParsePortMappings(JsonElement ports, string appId, string? containerName, ICollection<Domain.AppPortRecord> result)
    {
        foreach (var port in ports.EnumerateArray())
        {
            if (port.ValueKind == JsonValueKind.Number && port.TryGetInt32(out var number))
            {
                result.Add(new Domain.AppPortRecord { AppId = appId, ContainerName = containerName, HostPort = number });
                continue;
            }

            if (port.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var protocol = ReadString(port, "protocol")?.ToLowerInvariant() ?? "tcp";
            var containerPort = ReadInt(port, "container_port");
            if (TryGetArray(port, "host_ports", out var hostPorts))
            {
                foreach (var hostPort in hostPorts.EnumerateArray())
                {
                    var hostPortNumber = ReadInt(hostPort, "host_port");
                    if (hostPortNumber is not null)
                    {
                        result.Add(CreatePort(appId, containerName, hostPort, hostPortNumber.Value, containerPort, protocol));
                    }
                }

                continue;
            }

            var flatHostPort = ReadInt(port, "host_port") ?? ReadInt(port, "port");
            if (flatHostPort is not null)
            {
                result.Add(CreatePort(appId, containerName ?? ReadString(port, "container") ?? ReadString(port, "service"), port, flatHostPort.Value, containerPort, protocol));
            }
        }
    }

    private static Domain.AppPortRecord CreatePort(string appId, string? containerName, JsonElement hostPort, int hostPortNumber, int? containerPort, string protocol) => new()
    {
        AppId = appId,
        ContainerName = containerName,
        HostIp = ReadString(hostPort, "host_ip"),
        HostPort = hostPortNumber,
        ContainerPort = containerPort,
        Protocol = protocol
    };

    private static List<Domain.AppPortalRecord> ParsePortals(JsonElement portals, string appId)
    {
        if (portals.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var result = new List<Domain.AppPortalRecord>();
        foreach (var portal in portals.EnumerateObject())
        {
            var url = portal.Value.ValueKind == JsonValueKind.String ? portal.Value.GetString() : ReadString(portal.Value, "url");
            if (Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https")
            {
                result.Add(new Domain.AppPortalRecord { AppId = appId, Name = portal.Name, Url = parsed.AbsoluteUri });
            }
        }

        return result;
    }

    private static List<Domain.AppContainerRecord> ParseContainers(JsonElement workloads, string appId)
    {
        if (!TryGetArray(workloads, "container_details", out var containers))
        {
            return [];
        }

        var result = new List<Domain.AppContainerRecord>();
        foreach (var container in containers.EnumerateArray())
        {
            if (container.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = ReadString(container, "id") ?? ReadString(container, "container_id") ?? string.Empty;
            result.Add(new Domain.AppContainerRecord
            {
                AppId = appId,
                ContainerId = id,
                Name = ReadString(container, "service_name") ?? ReadString(container, "name") ?? id,
                Image = ReadString(container, "image"),
                State = ReadString(container, "state") ?? ReadString(container, "status") ?? "UNKNOWN",
                NetworksJson = SerializeProperty(container, "networks") ?? SerializeProperty(workloads, "networks"),
                VolumesJson = SerializeProperty(container, "volume_mounts") ?? SerializeProperty(container, "volumes") ?? SerializeProperty(workloads, "volumes")
            });
        }

        return result;
    }

    private static Domain.AppHealthState DetermineHealth(Domain.AppRecord app, JsonElement workloads)
    {
        if (app.MaintenanceMode)
        {
            return Domain.AppHealthState.Maintenance;
        }

        if (IsDown(app.State))
        {
            return Domain.AppHealthState.Stopped;
        }

        if (!string.Equals(app.State, "RUNNING", StringComparison.OrdinalIgnoreCase))
        {
            return Domain.AppHealthState.Unknown;
        }

        if (!TryGetArray(workloads, "container_details", out var containers))
        {
            return Domain.AppHealthState.Running;
        }

        return containers.EnumerateArray().Any(IsCrashedContainer) ? Domain.AppHealthState.Degraded : Domain.AppHealthState.Running;
    }

    private static bool IsCrashedContainer(JsonElement container) => string.Equals(ReadString(container, "state") ?? ReadString(container, "status"), "CRASHED", StringComparison.OrdinalIgnoreCase);

    private static string HealthMessage(Domain.AppHealthState state) => state switch
    {
        Domain.AppHealthState.Running => "TrueNAS reports the app running. Completed one-shot containers may remain exited.",
        Domain.AppHealthState.Degraded => "The app is running, but TrueNAS reports at least one crashed container.",
        Domain.AppHealthState.Stopped => "The app is stopped or crashed.",
        Domain.AppHealthState.Maintenance => "Monitoring is paused for an intentional maintenance stop.",
        _ => "TrueNAS did not report enough workload information to determine health."
    };

    private static bool TryGetArray(JsonElement element, string name, out JsonElement array)
    {
        array = default;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out array) && array.ValueKind == JsonValueKind.Array;
    }

    private static string? ReadString(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? ReadInt(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;

    private static string? SerializeProperty(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined ? value.GetRawText() : null;

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
        else if (app.HealthState is Domain.AppHealthState.Stopped or Domain.AppHealthState.Degraded)
        {
            app.StatusLabel = app.HealthState == Domain.AppHealthState.Stopped ? "Stopped" : "Degraded";
            app.StatusMessage = app.HealthMessage;
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
