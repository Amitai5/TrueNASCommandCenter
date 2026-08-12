using Microsoft.EntityFrameworkCore;
using TrueNasUpdateManager.Domain;

namespace TrueNasUpdateManager.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<AppRecord> Apps => Set<AppRecord>();
    public DbSet<UpdateRun> UpdateRuns => Set<UpdateRun>();
    public DbSet<UpdateAttempt> UpdateAttempts => Set<UpdateAttempt>();
    public DbSet<NotificationRecord> Notifications => Set<NotificationRecord>();
    public DbSet<SettingsRecord> Settings => Set<SettingsRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AppRecord>()
            .Property(app => app.Policy)
            .HasConversion<string>();
        modelBuilder.Entity<AppRecord>()
            .Property(app => app.VersionScope)
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
    }
}
