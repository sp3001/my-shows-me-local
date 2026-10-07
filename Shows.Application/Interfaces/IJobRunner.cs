namespace Shows.Application;

/// <summary>
/// The set of bulk jobs the app can run in-process from the Manage page. Each member maps to exactly one
/// console job invocation (the same code path <c>dotnet run --project …</c> drives), so running a job from
/// the UI is not a forked implementation — it is the console job, executed against the app's own container.
/// </summary>
/// <remarks>
/// The members are deliberately flat (one per operational mode, not one per console with flags): the Manage
/// page presents each as a single button with a fixed, sensible invocation, which keeps the UI free of
/// per-job option plumbing. The Import pipeline's modes are separate members because they are distinct
/// operations the user runs in sequence, not one job with toggles.
/// </remarks>
public enum JobKind
{
    // Operational jobs (run repeatedly as the library changes).
    EpisodeRefresh,
    StatusRecheck,
    RefreshUpdated,
    EnrichAkas,
    EnrichImdb,
    EnrichCountry,
    ImdbImport,

    // The one-time import pipeline (run in order, then the operational jobs above). Kept here so the
    // whole pipeline is drivable from the app; each body carries its own safety gates (lease, the
    // blocking-row review gate, idempotent targeting).
    ScrapeMyShows,
    ImportDryRun,
    ImportStage,
    ImportResolve,
    ImportReport,
    ImportPromote,
}

/// <summary>
/// One progress tick from a running job. <see cref="Total"/> is the item count when the job knows it
/// (per-show jobs report <c>Current</c> of <c>Total</c>); it is <c>0</c> for a job whose work is not
/// item-counted (a bulk row scan), where the caller shows an indeterminate bar and reads
/// <see cref="Detail"/> instead.
/// </summary>
public readonly record struct JobProgress(string JobName, int Current, int Total, string? Detail);

/// <summary>
/// The outcome of one in-process job run. <see cref="Succeeded"/> mirrors the console exit code
/// (<c>0</c> = success); a refused run (another writer holds the lease, a blocking row remains, etc.) is
/// <c>false</c> with a <see cref="Message"/> explaining why — the same signal a scheduled invocation
/// would see as a non-zero exit.
/// </summary>
public sealed record JobRunResult(bool Succeeded, int ExitCode, string Message);

/// <summary>
/// Runs a bulk job in-process, reporting progress as it goes. This is the seam that lets the UI drive the
/// jobs without naming Infrastructure (<c>ARCHITECTURE.md</c> §2): the ViewModels depend only on this
/// Application interface; Infrastructure implements it by invoking the real job bodies.
/// </summary>
/// <remarks>
/// <para>
/// <b>Concurrency is the job's own responsibility, not the caller's.</b> Every body is lease-guarded and
/// re-runnable (ADR-028), and the app takes no lease, so running a job while the app is open is exactly
/// the "console runs while the app is open" scenario the lease exists for. The caller is expected to run
/// this off the UI thread (the Manage page does) and to marshal <see cref="IProgress{T}"/> reports back to
/// the UI thread itself — the callback fires on the job's worker thread.
/// </para>
/// <para>
/// Only one job should run at a time from the UI: the bodies are heavy and lease-guarded, and two
/// concurrent in-process writers would simply have one refused by the lease. The Manage page enforces the
/// single-run rule in the UI rather than relying on the lease to arbitrate.
/// </para>
/// </remarks>
public interface IJobRunner
{
    Task<JobRunResult> RunAsync(
        JobKind kind,
        IProgress<JobProgress> progress,
        CancellationToken cancellationToken);
}
