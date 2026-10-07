using Shows.Infrastructure.Jobs;

// Step 7.4: the ImdbImport job now does real work — download (or reuse) the IMDb datasets and load the
// rows reachable from active tracked shows. The behavior lives in ImdbImporter (Infrastructure), not
// here; this file still just names the job by its own assembly and hands the args to the handler
// (ARCHITECTURE.md §3 — no business logic in Program.cs).
return await ShowsJobRunner.RunAsync(args, typeof(Program).Assembly, ImdbImportJob.RunAsync);
