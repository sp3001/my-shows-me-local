using Microsoft.Extensions.DependencyInjection;
using Shows.Application;
using Shows.Infrastructure.Import;
using Shows.Infrastructure.Persistence;

namespace Shows.Infrastructure.Jobs;

/// <summary>
/// The <c>Shows.Jobs.ScrapeMyShows</c> entry point (step 2.2). Lives in Infrastructure so the scrape is
/// testable through the Infrastructure reference the test project already has, while the job's
/// <c>Program.cs</c> stays a one-liner (ARCHITECTURE.md §3). Migrates through the lease-gated path, then
/// runs <see cref="ImdbIdScraper"/>, which takes its own <c>scrape_myshows</c> lease.
/// </summary>
public static class ScrapeJob
{
    // The console path: no progress sink, no cancellation. Delegates to the progress-aware overload.
    public static Task<int> RunAsync(IServiceProvider services, string[] args) =>
        RunAsync(services, args, null, CancellationToken.None);

    /// <summary>
    /// The progress-aware form. The console calls the two-argument overload; the app's Manage page calls
    /// this one so a run reports per-show progress and can be cancelled. Behavior is otherwise identical.
    /// </summary>
    public static async Task<int> RunAsync(
        IServiceProvider services, string[] args, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        await services.GetRequiredService<ShowsDatabaseMigrator>().MigrateAsync(cancellationToken);

        using var scope = services.CreateScope();
        var report = await scope.ServiceProvider
            .GetRequiredService<ImdbIdScraper>()
            .RunAsync(cancellationToken, progress);

        // A refused run (another writer holds the lease) is a non-success for a scheduled invocation.
        return report.Refused ? 1 : 0;
    }
}
