using Shows.Infrastructure.Jobs;

// Step 2.1: the Import job now does real work (--dry-run / --stage / --reset). The behavior lives in
// ImportJob (Infrastructure), not here — this file still just names the job by its own assembly and
// hands the args to the handler (ARCHITECTURE.md §3 — no business logic in Program.cs).
return await ShowsJobRunner.RunAsync(args, typeof(Program).Assembly, ImportJob.RunAsync);
