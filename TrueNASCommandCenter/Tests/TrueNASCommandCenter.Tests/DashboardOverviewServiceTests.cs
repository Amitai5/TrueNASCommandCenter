using TrueNasCommandCenter.Domain;
using TrueNasCommandCenter.Services;

namespace TrueNasCommandCenter.Tests;

[TestClass]
public sealed class DashboardOverviewServiceTests
{
    /// <summary>Verifies that the dashboard reports actionable app and Kuma outages while excluding maintenance.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task GetAsync_CurrentOperationalState_ReturnsActionableAlertsAndLatestUpdateRun()
    {
        await using var database = new TestDatabase();
        await database.InitializeAsync(settings =>
        {
            settings.OnboardingCompleted = true;
            settings.TimeZoneId = "UTC";
        });
        var olderRun = new UpdateRun
        {
            Trigger = RunTrigger.CheckNow,
            Status = RunStatus.Failed,
            StartedUtc = new DateTime(2026, 8, 27, 21, 0, 0, DateTimeKind.Utc)
        };
        var latestRun = new UpdateRun
        {
            Trigger = RunTrigger.CheckAndUpdateNow,
            Status = RunStatus.Succeeded,
            StartedUtc = new DateTime(2026, 8, 27, 22, 0, 0, DateTimeKind.Utc),
            EndedUtc = new DateTime(2026, 8, 27, 22, 2, 0, DateTimeKind.Utc),
            CheckedCount = 3,
            SucceededCount = 1
        };
        await using (var db = database.CreateDbContext())
        {
            db.Apps.AddRange(
                new AppRecord { Id = "running", Name = "Running app", GroupName = "Core", HealthState = AppHealthState.Running, HumanVersion = "1.2.3", IsFavorite = true, IsInstalled = true },
                new AppRecord { Id = "stopped", Name = "Stopped app", HealthState = AppHealthState.Stopped, HealthMessage = "App is stopped.", IsInstalled = true },
                new AppRecord { Id = "maintenance", Name = "Maintenance app", HealthState = AppHealthState.Maintenance, IsInstalled = true });
            db.UptimeKumaMonitors.AddRange(
                new UptimeKumaMonitorRecord { MonitorId = "up", Name = "Healthy monitor", Status = UptimeKumaMonitorStatus.Up, IsPresent = true },
                new UptimeKumaMonitorRecord { MonitorId = "down", AppId = "stopped", Name = "Down monitor", Status = UptimeKumaMonitorStatus.Down, IsPresent = true });
            db.UpdateRuns.AddRange(olderRun, latestRun);
            await db.SaveChangesAsync();
        }
        var service = new DashboardOverviewService(database);

        var result = await service.GetAsync();

        Assert.AreEqual(3, result.AppCount);
        Assert.AreEqual(1, result.RunningAppCount);
        Assert.HasCount(1, result.AppAlerts);
        Assert.AreEqual("stopped", result.AppAlerts[0].AppId);
        Assert.HasCount(1, result.FavoriteApps);
        Assert.AreEqual("running", result.FavoriteApps[0].AppId);
        Assert.AreEqual("Core", result.FavoriteApps[0].GroupName);
        Assert.AreEqual("1.2.3", result.FavoriteApps[0].Version);
        Assert.AreEqual(2, result.MonitorCount);
        Assert.AreEqual(1, result.MonitorsUp);
        Assert.HasCount(1, result.MonitorAlerts);
        Assert.AreEqual("down", result.MonitorAlerts[0].MonitorId);
        Assert.IsNotNull(result.LastRun);
        Assert.AreEqual(latestRun.Id, result.LastRun.Id);
        Assert.AreEqual(RunStatus.Succeeded, result.LastRun.Status);
    }

    /// <summary>Verifies that lifecycle-only activity is not mislabeled as the last update run.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task GetAsync_OnlyLifecycleRun_ReturnsNoUpdateRun()
    {
        await using var database = new TestDatabase();
        await database.InitializeAsync();
        await using (var db = database.CreateDbContext())
        {
            db.UpdateRuns.Add(new UpdateRun
            {
                Trigger = RunTrigger.Lifecycle,
                Status = RunStatus.Succeeded,
                StartedUtc = new DateTime(2026, 8, 27, 22, 0, 0, DateTimeKind.Utc)
            });
            await db.SaveChangesAsync();
        }
        var service = new DashboardOverviewService(database);

        var result = await service.GetAsync();

        Assert.IsNull(result.LastRun);
        Assert.HasCount(0, result.FavoriteApps);
    }

    /// <summary>Verifies that all shared frontend timestamps use a 12-hour clock with an AM or PM marker.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void Format_UtcTimestamp_UsesTwelveHourClock()
    {
        var value = new DateTime(2026, 8, 27, 23, 15, 0, DateTimeKind.Utc);

        var result = DisplayTimeFormatter.Format(value, "UTC");

        Assert.AreEqual("Aug 27, 2026 11:15 PM UTC", result);
    }
}
