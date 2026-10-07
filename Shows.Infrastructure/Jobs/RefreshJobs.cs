using Microsoft.Extensions.DependencyInjection;
using Shows.Application;
using Shows.Infrastructure.Persistence;

namespace Shows.Infrastructure.Jobs;

/// <summary>
/// The <c>Shows.Jobs.EpisodeRefresh</c> entry point (step 7.1). Migrates through the lease-gated path,
/// then runs the shared <see cref="ShowRefreshJob"/> in <see cref="RefreshMode.EpisodeRefresh"/> mode,
/// which selects airing/unknown shows and refreshes their episode catalog through the mapper.
/// </summary>
public static class EpisodeRefreshJob
{
    // The console path: no progress sink, no cancellation. Delegates to the progress-aware overload so the
    // migrate/scope/report logic lives in one place.
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
            .GetRequiredService<ShowRefreshJob>()
            .RunAsync(RefreshMode.EpisodeRefresh, cancellationToken, progress);

        return report.Refused ? 1 : 0;
    }
}

/// <summary>
/// The <c>Shows.Jobs.StatusRecheck</c> entry point (step 7.2). Same body as EpisodeRefresh but selects
/// Ended/Canceled shows to catch one that resumed (SPEC R5/R6 re-evaluation happens inside the mapper's
/// progress recompute).
/// </summary>
public static class StatusRecheckJob
{
    public static Task<int> RunAsync(IServiceProvider services, string[] args) =>
        RunAsync(services, args, null, CancellationToken.None);

    /// <summary>The progress-aware form — see <see cref="EpisodeRefreshJob.RunAsync(IServiceProvider,string[],IProgress{JobProgress},CancellationToken)"/>.</summary>
    public static async Task<int> RunAsync(
        IServiceProvider services, string[] args, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        await services.GetRequiredService<ShowsDatabaseMigrator>().MigrateAsync(cancellationToken);

        using var scope = services.CreateScope();
        var report = await scope.ServiceProvider
            .GetRequiredService<ShowRefreshJob>()
            .RunAsync(RefreshMode.StatusRecheck, cancellationToken, progress);

        return report.Refused ? 1 : 0;
    }
}

/// <summary>
/// The <c>Shows.Jobs.RefreshUpdated</c> entry point. Migrates through the lease-gated path, then runs
/// <see cref="RefreshUpdatedJob"/>, which fetches TVmaze's <c>/updates/shows</c> map once and re-maps only
/// the tracked shows whose provider timestamp moved.
/// </summary>
public static class RefreshUpdatedJob
{
    // The console path: no progress sink, no cancellation. Delegates to the progress-aware overload.
    public static Task<int> RunAsync(IServiceProvider services, string[] args) =>
        RunAsync(services, args, null, CancellationToken.None);

    /// <summary>The progress-aware form — see <see cref="EpisodeRefreshJob.RunAsync(IServiceProvider,string[],IProgress{JobProgress},CancellationToken)"/>.</summary>
    public static async Task<int> RunAsync(
        IServiceProvider services, string[] args, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        await services.GetRequiredService<ShowsDatabaseMigrator>().MigrateAsync(cancellationToken);

        using var scope = services.CreateScope();
        var report = await scope.ServiceProvider
            .GetRequiredService<RefreshUpdatedShowJob>()
            .RunAsync(cancellationToken, progress);

        // Refused (lease held) or the provider unreachable both exit non-zero — neither refreshed anything.
        return report.Refused || report.ProviderUnavailable ? 1 : 0;
    }
}
