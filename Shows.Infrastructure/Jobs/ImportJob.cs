using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shows.Application;
using Shows.Infrastructure.Import;
using Shows.Infrastructure.Persistence;

namespace Shows.Infrastructure.Jobs;

/// <summary>
/// The <c>Shows.Jobs.Import</c> entry point (step 2.1). Lives in Infrastructure — not in the job
/// project — so the flag handling, the gated migrate, and the staging write are all testable through
/// the Infrastructure reference the test project already has, while <c>Program.cs</c> stays a one-liner
/// (ARCHITECTURE.md §3: no business logic in <c>Program.cs</c>).
/// </summary>
/// <remarks>
/// Flags: <c>--dry-run</c> (also the default) reads the input files and reports only; <c>--stage</c>
/// fills the staging tables; <c>--reset</c> clears them first; <c>--report</c> exports
/// <c>v_staging_issues</c> to CSV. A write mode migrates through <see cref="ShowsDatabaseMigrator"/>
/// (which refuses under a live lease, ADR-028) before writing; <c>--dry-run</c> never touches the
/// database, so it is safe to run before the schema exists. <c>--report</c> reads the database (so it
/// migrates first) but writes no table — only the CSV.
/// </remarks>
public static class ImportJob
{
    // The console path: no progress sink, no cancellation. Delegates to the progress-aware overload so the
    // flag handling, gated migrate, and mode dispatch live in one place.
    public static Task<int> RunAsync(IServiceProvider services, string[] args) =>
        RunAsync(services, args, null, CancellationToken.None);

    /// <summary>
    /// The progress-aware form. The console calls the two-argument overload; the app's Manage page calls
    /// this one so the DB-writing modes (--resolve, --promote) report per-show progress and can be
    /// cancelled. Behavior is otherwise identical.
    /// </summary>
    public static async Task<int> RunAsync(
        IServiceProvider services, string[] args, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        var flags = ParseFlags(args);
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Import");

        if (flags.DryRun)
        {
            // No migrate, no write — the database need not exist for a dry run.
            using var scope = services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<StagingImporter>().DryRunAsync(cancellationToken);
            return 0;
        }

        await services.GetRequiredService<ShowsDatabaseMigrator>().MigrateAsync(cancellationToken);

        using var writeScope = services.CreateScope();
        var importer = writeScope.ServiceProvider.GetRequiredService<StagingImporter>();

        // --reset clears the staging tables this job owns; --stage then fills them. They compose in
        // either order, but --stage ALONE must NOT reset — between stage and promote the user edits
        // these rows in DB Browser (step 2.5), and a plain re-stage is insert-if-missing so it never
        // clobbers those edits. Only an explicit --reset discards them.
        if (flags.Reset)
        {
            await importer.ResetAsync(cancellationToken);
        }

        if (flags.Stage)
        {
            await importer.StageAsync(cancellationToken);
        }
        else if (flags.Reset)
        {
            logger.LogInformation("Reset complete; nothing staged (pass --stage to populate).");
        }

        // --resolve fills staging_show.tvmaze_id from TVmaze /lookup?imdb= for every staged show that
        // has an imdb_id but no tvmaze_id yet (step 2.3). It is a DB-writing mode (the resolver takes its
        // own lease and writes staging_show), so it sits after the migrate and after any stage/reset. It
        // runs before --report so `--resolve --report` reports the post-resolution state.
        if (flags.Resolve)
        {
            var report = await writeScope.ServiceProvider
                .GetRequiredService<TvmazeIdResolver>()
                .RunAsync(cancellationToken, progress);

            // A refused run (another writer holds the lease) is a non-success for a scheduled invocation.
            if (report.Refused)
            {
                return 1;
            }
        }

        // --promote is the second phase of the two-phase import (step 2.6): it moves the reviewed
        // staging rows into the mapped + user tables. It is a DB-writing mode (its own exclusive lease,
        // writes tracked_show/user_show/episode_watch), so it sits after the migrate and after any
        // resolve. It normally runs as its OWN invocation, after the human review in DB Browser (step
        // 2.5) — composing --stage --promote would promote un-reviewed data, which the blocking-row gate
        // is the last line of defence against, not a substitute for the review. Runs before --report so
        // `--promote --report` reports the post-promotion state.
        if (flags.Promote)
        {
            var report = await writeScope.ServiceProvider
                .GetRequiredService<Promoter>()
                .RunAsync(cancellationToken, progress);

            // Refused (lease held, a foreign lease live, or blocking rows remain) is a non-success: the
            // operator must see it, and a scheduled invocation must not report success on a no-op.
            if (report.Refused)
            {
                return 1;
            }
        }

        // --report reads v_staging_issues (so the schema must exist — hence it sits after the migrate)
        // but writes nothing to the database: only the CSV. It runs after any stage/reset so a combined
        // `--stage --report` reports the state it just produced.
        if (flags.Report)
        {
            await writeScope.ServiceProvider
                .GetRequiredService<StagingReportExporter>()
                .ExportAsync(cancellationToken);
        }

        return 0;
    }

    private static ImportFlags ParseFlags(string[] args)
    {
        var stage = false;
        var reset = false;
        var report = false;
        var resolve = false;
        var promote = false;
        var dryRun = false;
        foreach (var arg in args)
        {
            var flag = arg.Trim();
            if (flag.Equals("--stage", StringComparison.OrdinalIgnoreCase))
            {
                stage = true;
            }
            else if (flag.Equals("--reset", StringComparison.OrdinalIgnoreCase))
            {
                reset = true;
            }
            else if (flag.Equals("--report", StringComparison.OrdinalIgnoreCase))
            {
                report = true;
            }
            else if (flag.Equals("--resolve", StringComparison.OrdinalIgnoreCase))
            {
                resolve = true;
            }
            else if (flag.Equals("--promote", StringComparison.OrdinalIgnoreCase))
            {
                promote = true;
            }
            else if (flag.Equals("--dry-run", StringComparison.OrdinalIgnoreCase))
            {
                dryRun = true;
            }
        }

        // The default (no mode flag) is the safe read-only dry run. --report, --resolve and --promote are
        // DB modes, not dry runs, so none may fall through to the file-only path.
        if (!stage && !reset && !report && !resolve && !promote)
        {
            dryRun = true;
        }

        return new ImportFlags(dryRun, stage, reset, report, resolve, promote);
    }

    private readonly record struct ImportFlags(
        bool DryRun, bool Stage, bool Reset, bool Report, bool Resolve, bool Promote);
}
