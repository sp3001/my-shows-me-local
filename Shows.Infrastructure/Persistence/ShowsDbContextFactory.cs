using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Shows.Infrastructure.Persistence;

/// <summary>
/// Design-time factory so <c>dotnet ef migrations add</c> works without the MAUI head or a running
/// host (ARCHITECTURE.md §3 — the head is blocked on the workload, but migrations must not be).
/// </summary>
/// <remarks>
/// Migrations are generated against a throwaway file, not the user's database — generating a
/// migration must never touch real data. The PRAGMA interceptor is registered so the design-time
/// model matches the runtime model exactly (the model, not the connection, is what EF snapshots).
/// </remarks>
public sealed class ShowsDbContextFactory : IDesignTimeDbContextFactory<ShowsDbContext>
{
    public ShowsDbContext CreateDbContext(string[] args)
    {
        var path = Path.Combine(Path.GetTempPath(), "shows-designtime.migrations.db");

        var options = new DbContextOptionsBuilder<ShowsDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = path }.ToString())
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(new SqlitePragmaInterceptor())
            .Options;

        return new ShowsDbContext(options);
    }
}
