using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace TrueNasAppManager.Data;

public sealed class InitializationState
{
    public bool IsReady { get; set; }
}

public sealed class DatabaseReadyHealthCheck(
    IDbContextFactory<AppDbContext> dbFactory,
    InitializationState initializationState) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (!initializationState.IsReady)
        {
            return HealthCheckResult.Unhealthy("Application initialization is not complete.");
        }

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            return await db.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("The database is unavailable.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("The database readiness check failed.", exception);
        }
    }
}
