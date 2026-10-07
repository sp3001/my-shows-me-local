using Microsoft.Extensions.DependencyInjection;
using Shows.Application;
using Shows.Infrastructure.Import;
using Shows.Infrastructure.Persistence;

namespace Shows.Infrastructure.Jobs;

/// <summary>
/// The <c>Shows.Jobs.ImdbImport</c> entry point (step 7.4). Migrates through the lease-gated path, then
/// runs <see cref="ImdbImporter"/>, which downloads any dataset not already on disk and loads only rows
/// reachable from active tracked shows.
/// </summary>
public static class ImdbImportJob
{
    // The console path: no progress sink, no cancellation. Delegates to the progress-aware overload.
    public static Task<int> RunAsync(IServiceProvider services, string[] args) =>
        RunAsync(services, args, null, CancellationToken.None);

    /// <summary>
    /// The progress-aware form. The console calls the two-argument overload; the app's Manage page calls
    /// this one so a run reports progress and can be cancelled. Behavior is otherwise identical.
    /// </summary>
    public static async Task<int> RunAsync(
        IServiceProvider services, string[] args, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        await services.GetRequiredService<ShowsDatabaseMigrator>().MigrateAsync(cancellationToken);

        using var scope = services.CreateScope();
        var report = await scope.ServiceProvider
            .GetRequiredService<ImdbImporter>()
            .RunAsync(cancellationToken, progress);

        return report.Refused ? 1 : 0;
    }
}
