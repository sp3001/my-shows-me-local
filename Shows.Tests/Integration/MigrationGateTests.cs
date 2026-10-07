using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shows.Infrastructure;
using Shows.Infrastructure.Persistence;
using Xunit;

namespace Shows.Tests.Integration;

/// <summary>
/// Step 1.6 acceptance for the migration gate (ADR-028): <c>Migrate()</c> must refuse to run while any
/// writer lease is live, and surface the holding job's name rather than a stack trace. A migration that
/// rewrites a table under a mid-flight bulk job is the corruption the lease exists to prevent.
/// </summary>
public sealed class MigrationGateTests : IDisposable
{
    private readonly string _dbPath;

    public MigrationGateTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"shows-gate-{Guid.NewGuid():N}.db");
    }

    [Fact]
    public async Task Migrate_refuses_while_a_lease_is_live_and_names_the_job()
    {
        await using var provider = BuildProvider();

        // Bring the schema up to the latest so the writer_lease table exists, then drop back to a
        // "pending migration" posture by leaving a live lease in place.
        await provider.GetRequiredService<ShowsDatabaseMigrator>().MigrateAsync();

        InsertLiveLease(provider, "enrichment");

        var ex = await Assert.ThrowsAsync<LiveLeaseException>(
            () => provider.GetRequiredService<ShowsDatabaseMigrator>().MigrateAsync());

        // The app surfaces the job name, not a stack trace (plan step 1.6).
        Assert.Contains("enrichment", ex.Message, StringComparison.Ordinal);
        Assert.Contains("enrichment", ex.LiveLeaseNames);
    }

    [Fact]
    public async Task Migrate_succeeds_when_no_lease_is_live()
    {
        await using var provider = BuildProvider();

        // No lease at all — the gate must not block the normal path.
        await provider.GetRequiredService<ShowsDatabaseMigrator>().MigrateAsync();

        // An expired lease (a killed process's leftover) must not block it either.
        InsertLease(provider, "episode_refresh", expiresAt: DateTimeOffset.UtcNow.AddSeconds(-10));
        await provider.GetRequiredService<ShowsDatabaseMigrator>().MigrateAsync();
    }

    private void InsertLiveLease(ServiceProvider provider, string name) =>
        InsertLease(provider, name, DateTimeOffset.UtcNow.AddSeconds(90));

    private static void InsertLease(ServiceProvider provider, string name, DateTimeOffset expiresAt)
    {
        using var context = provider.GetRequiredService<ShowsDbContext>();
        context.Database.ExecuteSqlRaw(
            "INSERT OR REPLACE INTO writer_lease (name, holder, acquired_at, expires_at) VALUES ({0}, {1}, {2}, {3});",
            name, "pid-test@manual", DateTimeOffset.UtcNow, expiresAt);
    }

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Shows:Data:BaseDirectory"] = Path.GetDirectoryName(_dbPath),
                ["Shows:Data:DatabasePath"] = _dbPath,
            })
            .Build();

        services.AddShowsInfrastructure(configuration);
        return services.BuildServiceProvider();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }
}
