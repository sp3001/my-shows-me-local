using Shows.Infrastructure.Jobs;

// Step 2.2: the scraper does real work. The behavior lives in ScrapeJob (Infrastructure), not here —
// this file still just names the job by its own assembly and hands the args to the handler
// (ARCHITECTURE.md §3 — no business logic in Program.cs).
return await ShowsJobRunner.RunAsync(args, typeof(Program).Assembly, ScrapeJob.RunAsync);
