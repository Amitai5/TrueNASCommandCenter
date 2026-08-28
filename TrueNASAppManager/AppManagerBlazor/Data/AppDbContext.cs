using Microsoft.EntityFrameworkCore;
using TrueNasAppManager.Domain;

namespace TrueNasAppManager.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    private static readonly SemaphoreSlim writeGate = new(1, 1);

    public DbSet<AppRecord> Apps => Set<AppRecord>();
    public DbSet<UpdateRun> UpdateRuns => Set<UpdateRun>();
    public DbSet<UpdateAttempt> UpdateAttempts => Set<UpdateAttempt>();
    public DbSet<NotificationRecord> Notifications => Set<NotificationRecord>();
    public DbSet<SettingsRecord> Settings => Set<SettingsRecord>();
    public DbSet<AppPortRecord> AppPorts => Set<AppPortRecord>();
    public DbSet<AppPortalRecord> AppPortals => Set<AppPortalRecord>();
    public DbSet<AppContainerRecord> AppContainers => Set<AppContainerRecord>();
    public DbSet<GitHubRepositoryCache> GitHubRepositories => Set<GitHubRepositoryCache>();
    public DbSet<UptimeKumaMonitorRecord> UptimeKumaMonitors => Set<UptimeKumaMonitorRecord>();
    public DbSet<WebPushSubscriptionRecord> WebPushSubscriptions => Set<WebPushSubscriptionRecord>();

    /// <inheritdoc />
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        writeGate.Wait();
        try
        {
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }
        finally
        {
            writeGate.Release();
        }
    }

    /// <inheritdoc />
    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }
        finally
        {
            writeGate.Release();
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AppRecord>()
            .Property(app => app.Policy)
            .HasConversion<string>();
        modelBuilder.Entity<AppRecord>()
            .Property(app => app.VersionScope)
            .HasConversion<string>();
        modelBuilder.Entity<AppRecord>()
            .Property(app => app.DowntimeAction)
            .HasConversion<string>();
        modelBuilder.Entity<AppRecord>()
            .Property(app => app.HealthState)
            .HasConversion<string>();
        modelBuilder.Entity<UptimeKumaMonitorRecord>()
            .Property(monitor => monitor.Status)
            .HasConversion<string>();
        modelBuilder.Entity<UpdateRun>()
            .Property(run => run.Trigger)
            .HasConversion<string>();
        modelBuilder.Entity<UpdateRun>()
            .Property(run => run.Status)
            .HasConversion<string>();
        modelBuilder.Entity<UpdateAttempt>()
            .Property(attempt => attempt.Kind)
            .HasConversion<string>();
        modelBuilder.Entity<UpdateAttempt>()
            .Property(attempt => attempt.Status)
            .HasConversion<string>();
        modelBuilder.Entity<UpdateAttempt>()
            .Property(attempt => attempt.PolicyAtExecution)
            .HasConversion<string>();
        modelBuilder.Entity<UpdateAttempt>()
            .Property(attempt => attempt.ScopeAtExecution)
            .HasConversion<string>();
        modelBuilder.Entity<NotificationRecord>()
            .Property(notification => notification.EventType)
            .HasConversion<string>();
        modelBuilder.Entity<NotificationRecord>()
            .Property(notification => notification.Provider)
            .HasConversion<string>();
        modelBuilder.Entity<NotificationRecord>()
            .Property(notification => notification.Status)
            .HasConversion<string>();
        modelBuilder.Entity<SettingsRecord>()
            .Property(settings => settings.SmtpSecurity)
            .HasConversion<string>();

        modelBuilder.Entity<UpdateAttempt>()
            .HasOne(attempt => attempt.Run)
            .WithMany(run => run.Attempts)
            .HasForeignKey(attempt => attempt.RunId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<UpdateAttempt>()
            .HasOne(attempt => attempt.App)
            .WithMany(app => app.Attempts)
            .HasForeignKey(attempt => attempt.AppId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<AppPortRecord>()
            .HasOne(port => port.App)
            .WithMany(app => app.Ports)
            .HasForeignKey(port => port.AppId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<AppPortalRecord>()
            .HasOne(portal => portal.App)
            .WithMany(app => app.Portals)
            .HasForeignKey(portal => portal.AppId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<AppContainerRecord>()
            .HasOne(container => container.App)
            .WithMany(app => app.Containers)
            .HasForeignKey(container => container.AppId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<UptimeKumaMonitorRecord>()
            .HasOne(monitor => monitor.App)
            .WithMany(app => app.UptimeKumaMonitors)
            .HasForeignKey(monitor => monitor.AppId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<NotificationRecord>()
            .HasIndex(notification => new
            {
                notification.DeduplicationKey,
                notification.Provider,
                notification.Status
            });
        modelBuilder.Entity<UpdateAttempt>()
            .HasIndex(attempt => new { attempt.AppId, attempt.StartedUtc });
        modelBuilder.Entity<UpdateRun>()
            .HasIndex(run => run.StartedUtc);
        modelBuilder.Entity<AppPortRecord>()
            .HasIndex(port => new { port.AppId, port.HostPort, port.Protocol });
        modelBuilder.Entity<AppPortalRecord>()
            .HasIndex(portal => portal.AppId);
        modelBuilder.Entity<AppContainerRecord>()
            .HasIndex(container => new { container.AppId, container.ContainerId });
        modelBuilder.Entity<UptimeKumaMonitorRecord>()
            .HasIndex(monitor => monitor.AppId);
        modelBuilder.Entity<WebPushSubscriptionRecord>()
            .HasIndex(subscription => subscription.Endpoint)
            .IsUnique();
    }
}
