using Shows.Infrastructure.Jobs;

// A thin shell only. All behavior lives in ShowsJobRunner + RefreshUpdatedJob; this file just names the
// job by its own assembly and points at the handler (ARCHITECTURE.md §3 — no logic in Program.cs).
return await ShowsJobRunner.RunAsync(args, typeof(Program).Assembly, RefreshUpdatedJob.RunAsync);
