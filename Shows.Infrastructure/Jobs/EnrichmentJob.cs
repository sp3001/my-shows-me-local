using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shows.Application;
using Shows.Infrastructure.Persistence;

namespace Shows.Infrastructure.Jobs;

/// <summary>
/// The <c>Shows.Jobs.Enrichment</c> entry point (steps 7.3 and 7.5, improvement 21). Migrates through the
/// lease-gated path, then runs the selected mode: <c>--akas</c> (localized titles from TVmaze, step 7.5),
/// <c>--imdb</c> (episode gap-fill and mismatch flagging from the IMDb source tables, step 7.3) or
/// <c>--country</c> (re-derive the country from the payloads already on disk, improvement 21). With no flag
/// it logs that no mode was selected and exits 0.
/// </summary>
public static class EnrichmentJob
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
        var akas = args.Any(a => a.Trim().Equals("--akas", StringComparison.OrdinalIgnoreCase));
        var imdb = args.Any(a => a.Trim().Equals("--imdb", StringComparison.OrdinalIgnoreCase));
        var country = args.Any(a => a.Trim().Equals("--country", StringComparison.OrdinalIgnoreCase));
        if (!akas && !imdb && !country)
        {
            services.GetRequiredService<ILoggerFactory>()
                .CreateLogger("Enrichment")
                .LogInformation("Enrichment: no mode selected (pass --akas, --imdb and/or --country).");
            return 0;
        }

        await services.GetRequiredService<ShowsDatabaseMigrator>().MigrateAsync(cancellationToken);

        var exit = 0;

        if (akas)
        {
            using var scope = services.CreateScope();
            var report = await scope.ServiceProvider
                .GetRequiredService<AkasJob>()
                .RunAsync(cancellationToken, progress);
            exit |= report.Refused ? 1 : 0;
        }

        if (imdb)
        {
            using var scope = services.CreateScope();
            var report = await scope.ServiceProvider
                .GetRequiredService<ImdbEnrichmentJob>()
                .RunAsync(cancellationToken, progress);
            exit |= report.Refused ? 1 : 0;
        }

        if (country)
        {
            using var scope = services.CreateScope();
            var report = await scope.ServiceProvider
                .GetRequiredService<CountryBackfillJob>()
                .RunAsync(cancellationToken, progress);
            exit |= report.Refused ? 1 : 0;
        }

        return exit;
    }
}
