using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shows.App.Avalonia.Services;
using Shows.Application;

namespace Shows.App.Avalonia.ViewModels;

/// <summary>
/// The Manage tab: run the bulk jobs in-process, right inside the app, instead of from a console. It is a
/// thin driver over <see cref="IJobRunner"/> — the same job bodies the console jobs invoke, executed
/// against the app's own container. No new job logic lives here.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it does not freeze the UI.</b> The job runs on a thread-pool thread (<see cref="Task.Run"/>)
/// while the command awaits it, so the UI thread stays responsive. The job reports progress on its own
/// worker thread; that is marshalled back to the UI thread by <see cref="UiThreadProgress{T}"/> before
/// any bound property is touched — the recurring Avalonia "Call from invalid thread" trap (a bound write
/// off the UI thread crashes the process). The continuation after the <c>await</c> resumes on the UI
/// context (no <c>ConfigureAwait(false)</c>), so the completion writes are safe too.
/// </para>
/// <para>
/// <b>One job at a time.</b> Every button is disabled while a run is in flight (<see cref="CanRun"/>).
/// The bodies are lease-guarded and heavy, and two concurrent in-process writers would only have the
/// second refused by the lease — so the UI enforces the single-run rule rather than leaning on the lease
/// to arbitrate. A run can be cancelled; the body's per-unit-of-work transactions roll back cleanly, so
/// a cancelled run leaves no partial show and is safe to re-run.
/// </para>
/// <para>
/// <b>The tab persists across a tab switch</b> (the Shell holds its tab view models), so a job started
/// here keeps running and reporting while the user browses elsewhere — matching the request that the UI
/// may be inactive while a job runs.
/// </para>
/// </remarks>
public sealed partial class ManageViewModel : ObservableObject, IDisposable
{
    private readonly IJobRunner _jobRunner;
    private CancellationTokenSource? _runCts;

    public ManageViewModel(IJobRunner jobRunner)
    {
        _jobRunner = jobRunner;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunJobCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;

    /// <summary>The outcome of the last run (or a refusal/cancellation reason), shown when idle.</summary>
    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>The display name of the job currently running, shown in the progress panel.</summary>
    [ObservableProperty]
    private string? _runningJobName;

    /// <summary>The determinate bar value, 0..1. Ignored while <see cref="IsProgressIndeterminate"/>.</summary>
    [ObservableProperty]
    private double _progressValue;

    /// <summary>True for a job with no item total (a bulk row scan) — the bar animates instead of filling.</summary>
    [ObservableProperty]
    private bool _isProgressIndeterminate;

    /// <summary>The job's latest progress line, e.g. "12/547 — tvmaze 305".</summary>
    [ObservableProperty]
    private string? _progressDetail;

    /// <summary>True when no job is running — drives the idle status line vs the running progress panel.</summary>
    public bool IsIdle => !IsBusy;

    /// <summary>The job buttons, grouped into the operational set and the one-time import pipeline.</summary>
    public IReadOnlyList<JobGroup> Groups { get; } =
    [
        new("Maintenance",
        [
            new(JobKind.EpisodeRefresh, "Refresh episodes", "Re-fetch episode catalogs for airing shows (TVmaze)."),
            new(JobKind.StatusRecheck, "Recheck statuses", "Look for an Ended/Canceled show that has resumed."),
            new(JobKind.RefreshUpdated, "Refresh updated shows", "Ask TVmaze which shows changed and refresh only those."),
            new(JobKind.EnrichAkas, "Fetch localized titles", "Pull alternate (akas) titles from TVmaze."),
            // Improvement 21: no network — re-derives each show's country from the payload already stored,
            // so a show with no broadcaster country but a known language (a Korean Netflix drama) finally
            // shows one. Safe to re-run; a second pass reports zero changes.
            new(JobKind.EnrichCountry, "Re-derive countries", "Fill every show's country from the stored TVmaze payload. No network."),
        ]),
        new("IMDb",
        [
            new(JobKind.ImdbImport, "Import IMDb data", "Download / load the IMDb datasets for tracked shows."),
            new(JobKind.EnrichImdb, "Enrich from IMDb", "Fill episode gaps and flag mismatches (TVmaze stays authoritative)."),
        ]),
        new("Import pipeline (one-time)",
        [
            new(JobKind.ImportDryRun, "Dry run", "Read the JSON exports and report counts. Writes nothing."),
            new(JobKind.ImportStage, "Stage", "Fill the staging_* tables from the exports."),
            new(JobKind.ScrapeMyShows, "Scrape IMDb ids", "Resolve IMDb ids from the myshows pages."),
            new(JobKind.ImportResolve, "Resolve TVmaze ids", "Look up TVmaze ids for staged IMDb ids."),
            new(JobKind.ImportReport, "Export issues", "Write v_staging_issues to CSV for review."),
            new(JobKind.ImportPromote, "Promote", "Move reviewed staging rows into the live tables."),
        ]),
    ];

    private bool CanRun => !IsBusy;

    private bool CanCancel => IsBusy;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task RunJobAsync(JobOption job) => RunAsync(job.Kind, job.Label);

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _runCts?.Cancel();

    private async Task RunAsync(JobKind kind, string label)
    {
        IsBusy = true;
        RunningJobName = label;
        StatusMessage = null;
        ProgressDetail = null;
        ProgressValue = 0;
        IsProgressIndeterminate = false;

        _runCts = new CancellationTokenSource();
        var token = _runCts.Token;

        // The job reports on its worker thread; this reporter posts every tick back to the UI thread so
        // the bound properties below are only ever written on the dispatcher (the Avalonia thread trap).
        var progress = new UiThreadProgress<JobProgress>(OnProgress);

        try
        {
            // Offload the whole run so the UI thread never blocks on the job's synchronous start
            // (migrate + selection) or its awaits. The await resumes on the UI context.
            var result = await Task.Run(() => _jobRunner.RunAsync(kind, progress, token));
            StatusMessage = result.Message;
        }
        finally
        {
            // Detach the field first, then release — so a concurrent Dispose() (app shutdown) never sees
            // a half-torn-down token and the field is disposed exactly once. No Cancel here: the run has
            // already completed (we are past the await), so there is nothing left to abort.
            var cts = _runCts;
            _runCts = null;
            cts?.Dispose();
            IsBusy = false;
            RunningJobName = null;
            ProgressValue = 0;
            IsProgressIndeterminate = false;
            ProgressDetail = null;
        }
    }

    // Runs on the UI thread (UiThreadProgress marshalled it). A job with a known total drives a
    // determinate bar; one without (a bulk scan) drives an indeterminate bar and just shows its detail.
    private void OnProgress(JobProgress p)
    {
        if (p.Total > 0)
        {
            IsProgressIndeterminate = false;
            ProgressValue = Math.Clamp((double)p.Current / p.Total, 0d, 1d);
            ProgressDetail = p.Detail is null
                ? $"{p.Current}/{p.Total}"
                : $"{p.Current}/{p.Total} — {p.Detail}";
        }
        else
        {
            IsProgressIndeterminate = true;
            ProgressDetail = p.Detail;
        }
    }

    /// <summary>
    /// Cancels and releases any in-flight run. The Shell holds this tab view model for the process
    /// lifetime and never disposes it, so this is defensive — the per-run <c>finally</c> already releases
    /// the token between runs; this covers the app-shutdown path (CA1001/CA2213: the type owns a disposable
    /// field). Detaches the field before releasing so it is disposed exactly once.
    /// </summary>
    public void Dispose()
    {
        var cts = _runCts;
        _runCts = null;
        cts?.Cancel();
        cts?.Dispose();
    }
}

/// <summary>One job button: the <see cref="JobKind"/> to run plus the label and one-line description.</summary>
public sealed record JobOption(JobKind Kind, string Label, string Description);

/// <summary>A titled section of job buttons on the Manage page.</summary>
public sealed record JobGroup(string Title, IReadOnlyList<JobOption> Jobs);
