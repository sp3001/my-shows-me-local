using Shows.Infrastructure.Jobs;

// Step 7.2: a thin shell only. All behavior lives in ShowsJobRunner + StatusRecheckJob; this file just
// names the job by its own assembly and points at the handler (ARCHITECTURE.md §3 — no logic in Program.cs).
return await ShowsJobRunner.RunAsync(args, typeof(Program).Assembly, StatusRecheckJob.RunAsync);
