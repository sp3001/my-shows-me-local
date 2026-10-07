using Microsoft.Extensions.Logging;
using Shows.Application;
using Shows.Infrastructure.Persistence;

namespace Shows.Infrastructure.Jobs;

/// <summary>
/// The in-process implementation of <see cref="IJobRunner"/>: it drives the real job bodies through the
/// same entry points the console jobs use, so running a job from the Manage page is not a forked
/// implementation — it is the console code path, executed against the app's own container.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this lives in Infrastructure and is reached through <see cref="IJobRunner"/>.</b> The entry
/// points and job bodies are Infrastructure types the UI may not name (<c>ARCHITECTURE.md</c> §2, arch
/// rule 4). The UI depends only on the Application seam; this class is the one place that turns a
/// <see cref="JobKind"/> into a call into Infrastructure, registered as the seam's implementation.
/// </para>
/// <para>
/// <b>Reuse over duplication.</b> Each branch calls the console entry point (which migrates through the
/// lease-gated path, opens its own scope, and maps a refused run to exit 1) rather than re-implementing
/// that sequence. The app already migrated at startup, so the per-run migrate is a no-op unless the
/// schema changed; a live foreign lease makes it throw <see cref="LiveLeaseException"/>, which is caught
/// and surfaced as a refused result — the same "another writer is running" signal the console would exit
/// non-zero on.
/// </para>
/// <para>
/// <b>Threading.</b> The caller runs this off the UI thread and marshals the <see cref="IProgress{T}"/>
/// reports back itself; the bodies report on whatever thread their awaits resume on. This class does no
/// marshalling — it just forwards the caller's progress sink into the body.
/// </para>
/// </remarks>
internal sealed class JobRunner(IServiceProvider services, ILogger<JobRunner> logger) : IJobRunner
{
    public async Task<JobRunResult> RunAsync(
        JobKind kind,
        IProgress<JobProgress> progress,
        CancellationToken cancellationToken)
    {
        var name = kind.ToString();
        logger.LogInformation("Manage: starting {Job} in-process.", name);

        try
        {
            var exit = kind switch
            {
                JobKind.EpisodeRefresh => await EpisodeRefreshJob.RunAsync(services, [], progress, cancellationToken),
                JobKind.StatusRecheck => await StatusRecheckJob.RunAsync(services, [], progress, cancellationToken),
                JobKind.RefreshUpdated => await RefreshUpdatedJob.RunAsync(services, [], progress, cancellationToken),
                JobKind.EnrichAkas => await EnrichmentJob.RunAsync(services, ["--akas"], progress, cancellationToken),
                JobKind.EnrichImdb => await EnrichmentJob.RunAsync(services, ["--imdb"], progress, cancellationToken),
                JobKind.EnrichCountry => await EnrichmentJob.RunAsync(services, ["--country"], progress, cancellationToken),
                JobKind.ImdbImport => await ImdbImportJob.RunAsync(services, [], progress, cancellationToken),
                JobKind.ScrapeMyShows => await ScrapeJob.RunAsync(services, [], progress, cancellationToken),
                JobKind.ImportDryRun => await ImportJob.RunAsync(services, ["--dry-run"], progress, cancellationToken),
                JobKind.ImportStage => await ImportJob.RunAsync(services, ["--stage"], progress, cancellationToken),
                JobKind.ImportResolve => await ImportJob.RunAsync(services, ["--resolve"], progress, cancellationToken),
                JobKind.ImportReport => await ImportJob.RunAsync(services, ["--report"], progress, cancellationToken),
                JobKind.ImportPromote => await ImportJob.RunAsync(services, ["--promote"], progress, cancellationToken),
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown job kind."),
            };

            // exit 0 = success; exit 1 = a refused run (lease held, blocking rows remain, a foreign lease
            // live). Both are "the job ran and reported"; a genuine crash is the exception path below.
            var succeeded = exit == 0;
            logger.LogInformation("Manage: {Job} finished with exit {Exit}.", name, exit);
            return new JobRunResult(
                succeeded,
                exit,
                succeeded ? $"{name} completed." : $"{name} refused or reported a failure — see the log for detail.");
        }
        catch (LiveLeaseException ex)
        {
            // The lease-gated migrate refused because another writer is mid-run. This is expected and
            // recoverable (wait for it and retry), so it is a warning, not an error.
            logger.LogWarning(ex, "Manage: {Job} refused — a writer lease is live.", name);
            return new JobRunResult(false, 1, $"{name} refused: another job holds a writer lease. Wait for it to finish and retry.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The user stopped the job (or the app is closing). Not a failure — the body's per-unit work is
            // transactional, so a cancelled run leaves no partial show and is safe to re-run.
            logger.LogInformation("Manage: {Job} was cancelled.", name);
            return new JobRunResult(false, 1, $"{name} was cancelled.");
        }
        catch (Exception ex)
        {
            // An unexpected failure (bad input file, a data error the body did not guard). This breaks the
            // run, so it is an error with the exception on the line (no swallow — global rule).
            logger.LogError(ex, "Manage: {Job} failed.", name);
            return new JobRunResult(false, 1, $"{name} failed: {ex.Message}");
        }
    }
}
