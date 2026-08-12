using Microsoft.EntityFrameworkCore;
using TrueNasUpdateManager.Domain;

namespace TrueNasUpdateManager.Data;

public sealed class DatabaseInitializer(
    IServiceScopeFactory scopeFactory,
    DataPathOptions dataPath,
    ILogger<DatabaseInitializer> logger)
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(dataPath.Path);
        var lockPath = Path.Combine(dataPath.Path, ".migration.lock");

        await using var migrationLock = await AcquireLockAsync(lockPath, cancellationToken);
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await db.Database.MigrateAsync(cancellationToken);
        if (!await db.Settings.AnyAsync(cancellationToken))
        {
            db.Settings.Add(new SettingsRecord());
            await db.SaveChangesAsync(cancellationToken);
        }

        logger.LogInformation("Database initialization completed");
    }

    private static async Task<FileStream> AcquireLockAsync(string path, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            }
        }
    }
}

public sealed record DataPathOptions(string Path);
