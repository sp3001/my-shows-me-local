---
status: current
note: router only - declares no authority and owns no facts
---
# AGENTS.md — TV Show Tracker

Entry point for any agent working in this repository. Read this first, then the docs listed below.

## What this project is

A personal TV-show tracker. Iteration 1 is a **.NET 8 Avalonia desktop app (XAML, MVVM)** over
**SQLite**. It imports the user's existing watch data from `myshows.me` JSON exports, reconciles it
against TVmaze and IMDb, and tracks shows across four categories. Iteration 2 will split it into a
Dockerized frontend + backend, so **layer separation is a hard requirement, not a nicety**.

## Who to ask, not what to read

Every substantive document declares, in its YAML front matter, which facts it owns (`authority`) and
which facts it must not carry (`not_authoritative_for`). **This file owns nothing** — it is a router.

| File | Owns | Must not carry |
|---|---|---|
| `docs/STATE.md` | current state, step status, open questions | everything else |
| `docs/SPEC.md` | requirements, business rules | id encoding, storage schema |
| `docs/SCHEMA.md` | storage schema, **id encoding (§1a)** | requirements |
| `docs/ARCHITECTURE.md` | layer graph, project graph, configuration | storage schema, id encoding |
| `docs/DATA_ANALYSIS.md` | verified external facts about the input data | schema, requirements |
| `docs/DECISIONS.md` | decision rationale (ADRs) | current state, encodings |
| `docs/IMPLEMENTATION_PLAN.md` | sequencing, acceptance criteria | requirements, current state |
| `docs/LOG.md` | append-only verification evidence | current state |
| `tasks/*.md` | one in-progress step: what to read, do, verify | canonical definitions |

**If two documents disagree, find the one that owns the fact** — do not resolve by recency or by
specificity. If neither declares it, that is the defect: declare an owner before writing.

## Start a step here

Every implementation step must leave the docs as accurate as it found them, or better.

1. **Read `docs/STATE.md`** — small, and rewritten constantly. Note your step and any assumption you
   are making.
2. **Promote the step into `tasks/`**: create `tasks/<step-id>.md` from its section in
   `IMPLEMENTATION_PLAN.md` — a `Read` list naming the owning documents, the operational consequences
   for *this* step, acceptance criteria, and where the evidence goes. That file is the working
   document; the reference docs are not meant to be read end to end.
3. **Work.** If you find a contradiction with `docs/DATA_ANALYSIS.md`, `docs/SCHEMA.md`, or
   `docs/ARCHITECTURE.md`, **stop and fix the owning doc in the same commit** as the code. Never let
   code and docs diverge.
4. **Finish**: append the evidence to the **Step Log** in `docs/LOG.md` (step id and date, files
   changed, the exact command run and its observed output, deviations and why, new facts learned, what
   is unblocked), update the step status in `docs/STATE.md`, and move the task file to `tasks/done/`.
5. **If a decision changes**: add a new ADR to `docs/DECISIONS.md` and mark the old one superseded.
   Do not silently edit the old one.
6. **If the schema changes**: update `docs/SCHEMA.md` and add the EF Core migration in the same step.
7. **Never mark a step complete** without a verification command and its observed output.
   "It builds" is not verification for a data-migration step — quote actual row counts.

Why the task file exists: an implementing agent should read the router, the state, its own task, and
the two or three owning sections — not 3,200 lines of reference. Why the evidence: other agents pick
this up cold, and a step logged without evidence forces them to re-derive everything or, worse, makes
them trust something that was never checked.

## Hard rules

- **Do not run the import against a database you have not backed up.** The one-time import consumes
  the user's only copy of their watch history.
- Import is **two-phase**: JSON → `staging_*` tables → human reviews in DB Browser for SQLite →
  `--promote` fills the main tables. Never write to main tables directly from a parser.
- `Shows.Application` must not reference `Microsoft.EntityFrameworkCore`, `System.Net.Http`,
  `Microsoft.Maui`, or `Newtonsoft.Json`. It defines interfaces; Infrastructure implements them.
- `Shows.Domain` has **zero** NuGet dependencies.
- `Shows.App.Avalonia` (UI) and `Shows.Infrastructure` must not reference each other except at the UI's
  composition root (`Program.cs` / `App.axaml.cs`) — see ADR-033 (Avalonia replaced the original MAUI head).
- **Source tables** (`tvmaze_*`, `imdb_*`, `staging_*`) are read by the enrichment mapper and nothing
  else. **Mapped tables** (`tracked_show`, `season`, `episode`, `genre`, `show_genre`) are derived
  from them and are fully rebuildable. **User tables** (`user_show`, `episode_watch`,
  `ui_season_state`) are the only irreplaceable data (SPEC P4). Data flows one way, through the
  mapper (SPEC P3/R11, ADR-022/ADR-023).
- The mapper **never writes** `user_show`, `episode_watch` or `ui_season_state`, and **never
  regenerates a surrogate id** — `episode_watch.episode_id` points at those ids, so delete-then-insert
  silently orphans the user's watch marks while every row count still looks correct.
- **Untracking is a logical delete.** `user_show.deleted_at` is set; nothing is deleted, so re-adding
  a show restores its watched state (ADR-024). Every read path and **every job's selection query**
  must filter `deleted_at IS NULL` — a soft-deleted show keeps its `tracked_show` row, so a job that
  forgets the filter burns external API calls on shows nobody tracks, silently.
- **Nothing derivable is stored** (SPEC P5). No TVmaze or IMDb URL column anywhere — build the URL
  from the id. `season` has no external id; it is a grouping entity (ADR-025).
- **No `AUTOINCREMENT`, no store-generated keys.** Ids are minted in-process through `IIdGenerator`
  (ADR-026/ADR-027) and **never re-minted** — `episode_watch.episode_id` points at them. The encoding
  is owned by `docs/SCHEMA.md` §1a and is not stated here: a global rule file is read by every agent,
  so anything technical in it gets copied everywhere. What belongs globally is the invariant.
- **Concurrent writers are supported** (ADR-028): the app may be open while a job runs. Bulk writes
  acquire a `writer_lease` row and renew on a heartbeat; **a renewal that updates zero rows means the
  lease was lost and the job must stop writing.** `--promote` is exclusive of every other lease.
  Never resume a failed transaction — roll back and re-run the idempotent unit of work with fresh ids.
- **No raw provider string may appear in a mapped column or a `WHERE` clause.** TVmaze's `"Running"`
  maps to the `ShowStatus` enum through exactly one table, and an unrecognised value fails closed to
  `Unknown` (SPEC R12). The original bug this prevents: comparing against `"Running Series"` — a
  myshows string TVmaze never returns — would have auto-finished every airing show.
- **Logging is configured once**, in `Shows.Infrastructure/Logging/logging.json` via
  `AddShowsLogging()`, writing to `log/<appname>.log`. No project gets its own sinks or levels
  (`ARCHITECTURE.md` §7).
- Categories are exactly four: `Started`, `Later`, `Cancelled`, `Finished`. The words
  "paused"/"ended"/"returning" in job descriptions refer to the **TVmaze `status` string**, never to
  user categories.
- Air-date day/month precision comes **only** from TVmaze. IMDb gives year granularity at best.

## Build & run

```powershell
dotnet build Shows.sln
dotnet test Shows.Tests
dotnet run --project Shows.Jobs.Import -- --dry-run
dotnet run --project Shows.Jobs.Import -- --stage
dotnet run --project Shows.Jobs.Import -- --report
dotnet run --project Shows.Jobs.Import -- --promote
dotnet run --project Shows.Jobs.ScrapeMyShows
dotnet run --project Shows.Jobs.EpisodeRefresh
dotnet run --project Shows.Jobs.StatusRecheck
dotnet run --project Shows.Jobs.RefreshUpdated
dotnet run --project Shows.Jobs.Enrichment
dotnet run --project Shows.Jobs.ImdbImport
```

Flags and expected outputs are specified in `docs/IMPLEMENTATION_PLAN.md`. Until Phase 0 lands,
these commands do not exist yet — check `docs/STATE.md` for what is actually implemented.

## Data files (input, read-only)

`watching.json`, `later.json`, `cancelled.json`, `finished.json`, `episodes.json`,
`title.basics.tsv`, `title.episode.tsv` — all in the repo root. **Never modify or delete them.**
The two `.tsv` files are >50 MB and cannot be opened by editor tooling; query them with
`grep`/`Select-String` or stream them in code.
