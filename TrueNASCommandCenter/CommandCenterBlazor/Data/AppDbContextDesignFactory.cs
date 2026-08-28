using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TrueNasCommandCenter.Data;

public sealed class AppDbContextDesignFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=truenas-command-center.design.db")
            .Options;
        return new AppDbContext(options);
    }
}
