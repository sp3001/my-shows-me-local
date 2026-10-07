using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shows.Application;
using Shows.Infrastructure;
using Shows.Infrastructure.Persistence;
using Xunit;

namespace Shows.Tests.Integration;

/// <summary>
/// Step 1.6 acceptance for the writer lease (ADR-028, SCHEMA.md §2). The lease is the only thing
/// standing between two bulk writers corrupting one transaction's worth of each other's work, so the
/// tests assert the exact failure modes the plan names: a live lease is never stolen, a released or
/// expired one is, and a renewal after a takeover reports the loss rather than silently succeeding.
/// </summary>
/// <remarks>
/// Each "process" is its own <see cref="ServiceProvider"/> over the same file — that is what makes two
/// distinct holders, since the holder string is derived from the process id plus a per-instance salt.
/// Two providers share the database path but not the holder, exactly as two OS processes would.
/// </remarks>
public sealed class WriterLeaseTests : IDisposable
{
    private readonly string _dbPath;

    public WriterLeaseTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"shows-lease-{Guid.NewGuid():N}.db");
        // Create the schema once so the writer_lease table exists before any lease touches it.
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<ShowsDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Second_acquire_fails_while_the_first_holds()
    {
        await using var a = BuildProvider();
        await using var b = BuildProvider();

        var leaseA = a.GetRequiredService<IWriterLease>();
        var leaseB = b.GetRequiredService<IWriterLease>();

        var held = await leaseA.TryAcquireAsync("episode_refresh", default);
        Assert.NotNull(held);

        // A live lease must not be overwritten — the conditional upsert matches neither insert nor the
        // expired-takeover branch, so B gets null.
        var stolen = await leaseB.TryAcquireAsync("episode_refresh", default);
        Assert.Null(stolen);

        await held!.DisposeAsync();
    }

    [Fact]
    public async Task Acquire_succeeds_after_the_holder_releases()
    {
        await using var a = BuildProvider();
        await using var b = BuildProvider();

        var held = await a.GetRequiredService<IWriterLease>()
            .TryAcquireAsync("status_recheck", default);
        Assert.NotNull(held);
        await held!.DisposeAsync(); // release

        var reacquired = await b.GetRequiredService<IWriterLease>()
            .TryAcquireAsync("status_recheck", default);
        Assert.NotNull(reacquired);
        await reacquired!.DisposeAsync();
    }

    [Fact]
    public async Task Acquire_succeeds_after_the_lease_expires()
    {
        await using var a = BuildProvider();
        await using var b = BuildProvider();

        var held = await a.GetRequiredService<IWriterLease>()
            .TryAcquireAsync("enrichment", default);
        Assert.NotNull(held);

        // Force the row's expiry into the past without releasing — this is what a killed process leaves
        // behind. Expiry, not a reaper, is the cleanup mechanism (SCHEMA.md §2).
        ForceExpiryIntoThePast("enrichment");

        var taken = await b.GetRequiredService<IWriterLease>()
            .TryAcquireAsync("enrichment", default);
        Assert.NotNull(taken);
        await taken!.DisposeAsync();
    }

    [Fact]
    public async Task Renewal_after_a_takeover_reports_the_loss()
    {
        await using var a = BuildProvider();
        await using var b = BuildProvider();

        var held = await a.GetRequiredService<IWriterLease>()
            .TryAcquireAsync("promote", default);
        Assert.NotNull(held);

        // A dies (lease goes stale), B takes over. A's next heartbeat must update zero rows — the row's
        // holder is now B — and the job must read that false as "stop writing now" (ADR-028).
        ForceExpiryIntoThePast("promote");
        var takenByB = await b.GetRequiredService<IWriterLease>()
            .TryAcquireAsync("promote", default);
        Assert.NotNull(takenByB);

        var renewed = await held!.RenewAsync(default);
        Assert.False(renewed);

        await takenByB!.DisposeAsync();
    }

    [Fact]
    public async Task Renewal_while_still_holding_succeeds()
    {
        await using var a = BuildProvider();

        var held = await a.GetRequiredService<IWriterLease>()
            .TryAcquireAsync("episode_refresh", default);
        Assert.NotNull(held);

        // The happy path: while we are the holder and the lease is live, renewal extends it.
        Assert.True(await held!.RenewAsync(default));

        await held.DisposeAsync();
    }

    [Fact]
    public async Task Release_never_deletes_another_holders_lease()
    {
        await using var a = BuildProvider();
        await using var b = BuildProvider();

        var heldByA = await a.GetRequiredService<IWriterLease>()
            .TryAcquireAsync("promote", default);
        Assert.NotNull(heldByA);
        ForceExpiryIntoThePast("promote");
        var heldByB = await b.GetRequiredService<IWriterLease>()
            .TryAcquireAsync("promote", default);
        Assert.NotNull(heldByB);

        // A's release must not delete B's lease — the DELETE is scoped to A's holder string. After A
        // disposes, B must still be able to renew.
        await heldByA!.DisposeAsync();
        Assert.True(await heldByB!.RenewAsync(default));

        await heldByB.DisposeAsync();
    }

    private void ForceExpiryIntoThePast(string name)
    {
        using var context = BuildProvider().GetRequiredService<ShowsDbContext>();
        context.Database.ExecuteSqlRaw(
            "UPDATE writer_lease SET expires_at = {1} WHERE name = {0};",
            name, DateTimeOffset.UtcNow.AddSeconds(-30));
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
