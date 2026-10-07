using Shows.Infrastructure.Jobs;

// Step 7.1: a thin shell only. All behavior lives in ShowsJobRunner + EpisodeRefreshJob; this file just
// names the job by its own assembly and points at the handler (ARCHITECTURE.md §3 — no logic in Program.cs).
return await ShowsJobRunner.RunAsync(args, typeof(Program).Assembly, EpisodeRefreshJob.RunAsync);
