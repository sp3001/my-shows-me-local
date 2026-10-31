---
status: current
authority:
  - current-state
not_authoritative_for:
  - requirements
  - storage-schema
  - id-encoding
  - decision-rationale
  - step-log
---

# STATE — What Is Actually Built

> **Read this first, and update it as you work.** This is the single source of truth for what is
> built versus what is planned. It is small on purpose: it gets rewritten constantly, so anything
> that belongs elsewhere makes this file stale. Verification evidence for finished steps lives in
> `LOG.md` (`status: historical`, append-only) — do not append log entries here.

> Protocol: `../AGENTS.md`. Ownership of every fact in this repo: the `authority` front matter of
> each document. If two documents disagree, read the one that declares the fact.

**Last updated:** 2026-10-31
**Overall status:** **Phases 0–6 are complete; steps 7.1–7.5, 8.1 and 8.4 are done** — steps 0.1–0.8,
1.1–1.6, 2.1–2.6, 3.1–3.2, 4.1–4.2, 5.1–5.3, 6.1, 7.1–7.5, 8.1 and 8.4 are done. Solution
builds with zero warnings, 386 tests pass, all seven job shells run and exit 0, and the Avalonia UI head
builds. The UI head is **Avalonia** (`Shows.App.Avalonia`); the MAUI head (`Shows.App`) was removed
(ADR-033, supersedes ADR-001).
**Discovery buckets corrected 2026-10-06** (user fix): *New* = any season-1 episode after `windowStart`,
*Airing* = any episode (any season) after `windowStart` (New ⊂ Airing), *Upcoming* unchanged; switching a
bucket now resets all filters (type back to the `Scripted` default) so a stale facet can no longer empty the
view. See `LOG.md` and `SPEC.md` F3.
**That type reset never actually took effect until 2026-10-07**: Avalonia binds `SelectedItem` TwoWay and
resolves it by instance, so the freshly created option was coerced to `null` and the null written back into
the ViewModel — the switch filtered by no type at all and the dropdown displayed `All`. The rule now stated
in `DiscoveryViewModel`'s class remarks: a value assigned to a `Selected*` property must already be an
element of that dropdown's bound option list.
**Discovery look-back works 2026-10-07**: `/schedule/full` is **forward-only** (DATA_ANALYSIS §8 fact 17 —
min airdate equals the capture date, max ~17 months ahead), so the look-back buckets had nothing to look
back at. The source retains dated **raw** responses in SQLite and folds them into the query on refresh
(`ScheduleArchive` + `ScheduleMerge`, newest non-null air date wins). Retention is its own knob
(`ArchiveMaxAgeDays`), deliberately not derived from the window; the source warns if it is set at or below
`NewShowsWindowDays`. Step 8.4 moves the archive to SQLite (`discovery_schedule_archive`); existing files
are intentionally not imported or deleted, so the DB archive starts fresh. **Cold start is unavoidable**
— the window fills only as far back as the oldest capture, so it reaches the full 45 days ~45 days after
the first run. See `ARCHITECTURE.md` §4.1 and ADR-035.
**Specials are split into significant and insignificant** (ADR-034, supersedes ADR-013): TVmaze's
`significant_special` maps to `EpisodeType.Special` (counts toward progress/finished, shown in the
season grid with a `★` marker), `insignificant_special` maps to `EpisodeType.InsignificantSpecial`
(excluded from progress, shown only in the details page's Specials section). `Unknown` renders with a
`?` marker so a vocabulary gap is visible. No migration — re-run `EpisodeRefresh`/`Enrichment` to
re-map stored rows from `type_raw`.
Step **8.1** (architecture tests) is done: the layer invariants are now executable and each is proven to
reject an injected violation.
Step **8.4** is done: Discovery's raw schedule archive is now stored in SQLite (`discovery_schedule_archive`)
with its existing age-triggered layers and retention; old `cache/discovery` files remain untouched and
are not imported (ADR-035).
**The Updates tab landed 2026-10-31** (improvement 25, SPEC F4 v1.6, ADR-038, migration `0011`): a fifth
tab next to Finished with four date-ordered sections — Ended Shows (60 days), Renewed Shows (60 days),
Updated Lately (30 days), Recently Watched (30 days). It is a **view over the tracked shows, not a fifth
category**: it borrows `Finished` as its tab-strip category and never touches `user_show.category_id`.
The three dates it reads did not exist, because **no provider serves a status-change timestamp** (0 of the
554 stored TVmaze payloads carry one), so the mapper now records the transitions itself:
`tracked_show.status_changed_at` when a show becomes terminal (`{Ended, Canceled}` —
`ShowStatusRules.NoMoreEpisodesExpected`, not Ended alone), `status_renewed_at` when it leaves terminal
for an airable status, and `tvmaze_updated_at` as the mapped copy of `tvmaze_show.provider_updated` (the
UI may not read a source table, SPEC P3/R11 — same relationship `status_id` has to `status_raw`). An end
is dated from the **last aired episode**, not from the moment we noticed, so a show added today whose
finale aired in 2019 does not appear as having ended this week; with no dated episode the mapper writes a
placeholder once and replaces it when `EpisodeRefresh` brings the finale. `status_renewed_at` is
deliberately **not** backfilled — nothing observed a renewal before this, and inventing dates would put
the whole library in Renewed.
**Expect the sections to look thin until the jobs run**: on the live DB the backfill filled 471 of 471
terminal shows' end dates and all 554 `tvmaze_updated_at`, but the projected section counts are
**Ended 1 / Renewed 0 / Updated 13 / Watched 51** — 470 shows are terminal but their finales are older
than 60 days, and no renewal has been observed since the mapper started observing them. **User action: run
`StatusRecheck` and `RefreshUpdated`** (Manage page) to populate Renewed and refresh Updated Lately.
`0011` is already applied to `data/shows.db` (the app applied it on start during this step).
**Per-tab sort, tighter season rows and inline chips landed 2026-10-31** (improvement 26, `LOG.md`): the sort
combo is now remembered **per tab** — `started_sort` / `later_sort` / `cancelled_sort` / `finished_sort` — and
restored when the tab is selected, so switching Started → Later swaps the combo to Later's saved order without
re-persisting it under Started's key (the change hook is suppressed while a tab switch restores). This required
fixing a defect in `Services/Preferences.cs`: `Default` was declared **above** `StorePath`, and static
initializers run in declaration order, so `Default`'s constructor called `Load()` while `StorePath` was still
null — `File.Exists(null)` is false, the store always started empty, and **every** saved preference silently
read its default on every launch while writes still landed in the right file. Improvement 12's persisted sort
had therefore never worked. `StorePath` is now a property, evaluated at call time. The Started card also
tightened (season/episode row padding and spacing), and the chip run no longer sits in a
`Grid ColumnDefinitions="*,Auto"` pinned to the card's right edge: it is a `WrapPanel ItemSpacing="6"
LineSpacing="2"` holding the title TextBlock, a dot, then the chips — so the chips follow the title text
immediately and wrap to the next line when they do not fit. A `WrapPanel` measures a `TextWrapping="Wrap"`
TextBlock at the constraint width (its longest line), which is what makes the following children land after the
text; a `StackPanel` measures it at infinite width and pushes them off-screen. Each card exposes a
`ShowTitleChipSeparator` so the dot before the first chip is hidden when no chip renders.
**Title-line chips and fuller episode rows landed 2026-10-31** (improvements 22–24, `LOG.md`): every card and
details header now carries `status · user rating · country · TVmaze rating` on the **title** line — the
country and TVmaze rating moved off the meta line, which a long genre list used to push them off a trimmed
row. Details episode rows are two lines
(marker top-aligned, `S8E22` with its runtime beneath; title · air date · watched date · rating; description
beneath) with a `ⓘ` flyout carrying the still and the full synopsis — the flyout is attached through
`<Button.Flyout>`, because a `<Flyout>` written as a Button **child** becomes `Button.Content` and the popup
never opens. The Started tab shows each episode's runtime at the right **before** the air date and the
season's total **before** the count (`~7h 4m · 13`). The runtime totals are **computed**, not stored (P5):
`IEpisodeRepository.SumRuntimeBySeason{,ForShows}Async` sums over exactly the displayable set the header's
count uses, so the two numbers describe the same episodes. `ShowDetails.WatchedAtByEpisodeId` reuses watch
rows already loaded — no extra query.
**Country fallback works 2026-10-07** (improvement 21, SPEC R15 / ADR-036): a show's displayed country is
`broadcaster country name ?? country code ?? language`, applied at the mapped layer only
(`CountryRules.Display` — one function, used by the mapper, the catalog read model and the Discovery
country facet, so cards and facet always agree). `tvmaze_show.country_name`/`country_code` stay verbatim
(SPEC R11); `tracked_show.country` carries the rule-applied value (migration `0010`, schema-only).
Existing rows are filled **without network** by `Enrichment --country` (Manage page: "Re-derive
countries"), which re-parses `tvmaze_show.raw_json` through the mapper. **User action: run it once**
after the migration applies (app start or any job applies it automatically).
**The import backfill no longer invents watch marks** (2026-10-31, ADR-037, amends ADR-018): branch 3
of `Promoter.BackfillWatchMarksAsync` required `totalEpisodes > 0` (a 0/0 show is case 4, not "fully
watched"), and no episode dated after the import day is ever marked — **`totalEpisodes` counts only
episodes myshows has seen**, so it is not the size of the TVmaze catalog and "fully watched" on myshows'
list silently under-counts future episodes. `DATA_ANALYSIS.md` §5 carries both guards. `--promote` is
one-shot (`promoted_at` is set for all 547 rows), so this is prevention for a future import, not the
repair. **User action: run `draft/fix-import-watched-before-air.sql`** against `data/shows.db` — it
deletes only marks made during the import on episodes that still have not aired, recomputes the four
cached columns, and lists the ambiguous ones (aired since the import) for manual review in the app.
The full SQLite schema is in place across eleven migrations (`0001_MainSchema`, `0002_SourceSchema`,
`0003_Staging`, `0004_StagingIssuesImdbWarning`, `0005_TvmazeAkas`, `0006_EpisodeMismatch`,
`0007_UserShowIsPinned`, `0008_UserShowUserRating`, `0009_DiscoveryScheduleArchive`, `0010_ShowCountry`,
`0011_ShowStatusTransitions`);
`IdGenerator` / `WriterLease` / the migration gate exist;
and the `TvmazeShowMapper` — the single mapped-row write path and the pivot of the plan — is built and
tested, including the rebuild drill. Step 0.5 was unblocked when the .NET 8 MAUI packs were installed.
**Phase 2 is complete through the promote step**: steps 2.1 (envelope parser + `--stage`), **2.2**
(`ScrapeMyShows` → `staging_imdb_id`), **2.3** (TVmaze `/lookup?imdb=` → `tvmaze_id`), **2.4** (`--report`
CSV), **2.5** (the user's human review — done: all 547 staged shows carry a `tvmaze_id`, the two
no-IMDb shows resolved by hand) and **2.6** (`--promote`) are done. **Phase 3 has begun**: step **3.1**
(`TvmazeCatalogProvider` + `http_cache` revalidation + Polly) is done, which unblocks the mapper's
`MapShowAsync(int)` fetch path and the search/discovery read model. Step **3.2**
(`AddShowToTrackingUseCase` + `UntrackShowUseCase`) is done: the two use cases are registered and proven
by an integration test that adds a show twice, re-adds after a watch mark, and round-trips add→untrack→add
— exposing and fixing two real bugs (the `tracked_show` shell now satisfies its NOT NULL `tvmaze_show_id`
FK via a source row the mapper mints, and the restore path now finds a soft-deleted show instead of falling
into a UNIQUE `tvmaze_id` violation). This is the first business logic driven end-to-end through the mapper.
**Phase 4 is complete**: steps **4.1** (`SearchViewModel` + `SearchPage`) and **4.2** (`DiscoveryViewModel`
+ `DiscoveryPage`) are done — the first real UI, and the first thing to drive the 3.2 add path from a running
app. Both feeds share one `ShowCardViewModel` (SPEC F3), which holds only the singleton `IServiceScopeFactory`
and opens a per-operation scope so no scoped service is captive to the Shell's singleton root. The search
read model is proven by 3 integration tests (the already-tracked marker reads `user_show` where
`deleted_at IS NULL`, so a soft-deleted show reports untracked and re-add is not blocked — SPEC F2/ADR-024).
159 tests pass; the solution builds with zero warnings.

**Step 2.6 (`--promote`) is implemented and tested — the user runs it against the real DB.** The
`Promoter` takes the exclusive `promote` lease, refuses on a live foreign lease or any `blocking`
`v_staging_issues` row, then per show (in one transaction) fetches the payload, creates the
`tracked_show` shell, maps it, runs the four-branch watch backfill, writes `user_show`, and stamps
`promoted_at`. 12 integration tests on a temp migrated DB prove the backfill branches, the category map,
idempotence, and the lease/gate refusals. **ADR-030** demoted `unresolved_imdb_id` from blocking to
warning (migration `0004`), which is what let the two remaining review rows (`73305`, `96144`, both
tvmaze-resolved with no IMDb id) become promotable — the review gate is now clear. The real
`data/shows.db` was backed up to `backup/pre-promote-20260930-002807/` before this step. **The next
action is the user running `dotnet run --project Shows.Jobs.Import -- --promote`** and checking the
golden numbers in `tasks/done/2.6-promote.md`; nothing reaches the mapped/user tables until they do.

> **One decision needs user review:** SPEC R5 vs R12 on whether a `Canceled` show can auto-finish.
> Resolved as **yes** (Ended OR Canceled) while the user was unavailable; see the note in SPEC R5 and
> the 2026-09-29 entry in `LOG.md`. One-line revert if wrong.

---

## Current state

Phase 0 and Phase 1 are built, including the UI head (0.5 — now Avalonia, ADR-033) and the **full SQLite schema**
(created in 1.1–1.3 and extended through migration 0009).
`Shows.sln` has twelve projects with their references wired: Domain (zero packages) ←
Application ← Infrastructure; the seven job consoles and the test project reference Infrastructure. The
`ShowsDbContext` maps the mapped tables (`tracked_show`, `season`, `episode`, `genre`, `show_genre`),
the user tables (`user_show`, `episode_watch`, `ui_season_state`), the bookkeeping/cache tables
(`job_run`, `writer_lease`, `http_cache`, `discovery_schedule_archive`), the source tables
(`tvmaze_*`, `imdb_*`), and the staging tables plus the `v_staging_issues` view — across eleven migrations.
The repositories and `UnitOfWork` (1.4) assemble
the `TrackedShow`/`UserShow` aggregate with the one `deleted_at IS NULL` filter. `IdGenerator` mints the
client-generated keys (SCHEMA.md §1a), `SqliteWriterLease` coordinates concurrent bulk writers
(ADR-028), and `ShowsDatabaseMigrator` refuses to migrate under a live lease. The `TvmazeShowMapper`
(1.5) is the single mapped-row write path: it writes source rows verbatim, translates vocabulary through
the one `TvmazeVocabulary` dictionary (SPEC R12, fail-closed to `Unknown`), upserts the mapped rows on
natural keys with immutable surrogate ids, and recomputes the cached columns via the Domain function —
never touching a user table. 156 tests pass (146 functional + 10 architecture); the solution builds with
zero warnings.

The **import's staging half** (step 2.1) is built: `MyShowsEnvelopeParser` (pure) + `StagingImporter`
(`--dry-run`/`--stage`/`--reset`) fill `staging_show`/`staging_episode` from the exports, insert-if-missing
so a re-run never clobbers the user's DB-Browser edits. The **scraper** (step 2.2) is built: `ImdbIdScraper`
fetches each staged show's myshows page over the `MyShows` named client and records the IMDb id in
`staging_imdb_id` (resumable, capped, lease-guarded), mirroring it onto `staging_show.imdb_id`; `--stage`
rebuilds that mirror from `staging_imdb_id` so a `--reset`/`--stage` cycle never strands a scraped id
(`staging_imdb_id` has no FK to `staging_show`, so `--reset` cannot cascade into it). The **resolver**
(step 2.3) is built: `--resolve` calls TVmaze `/lookup?imdb=` for each staged show that has an `imdb_id`
but no `tvmaze_id` and records `tvmaze_id`/`tvmaze_resolution` (resumable — a show with a `tvmaze_id`,
scraped or manual, is never a target, so the manual id is never overwritten; capped; lease-guarded under
`resolve_tvmaze`; a transport failure is left for the next run, never marked unresolved — R14). The **report** (step 2.4)
is built: `--report` exports `v_staging_issues` to `data/staging_issues.csv` (blocking-first, RFC 4180 quoting)
and prints the per-severity counts, read-only against the DB. The user has begun the **step 2.5 review** by
hand — filling 5 IMDb ids and one TVmaze id — and those manual IMDb ids were copied into `staging_imdb_id` so
a future `--reset --stage` cannot lose them. **`--promote` (2.6) is now built**: the `Promoter` moves the
reviewed staging rows into the mapped + user tables (exclusive lease, blocking-row gate, per-show
transaction, four-branch watch backfill), proven by 12 integration tests on a temp migrated DB. It has
**not** been run against the real `data/shows.db` — that is the user's action; the DB was backed up to
`backup/pre-promote-20260930-002807/` first.

What is **not** built yet: the docker-readiness pass (8.3) and the consolidated integration-test pass (8.2) — **both postponed 2026-09-30** at the user's direction. Priority shifted to fixing the defects and UX gaps recorded in `docs/user-issues.md`; **every issue and improvement there is now done** (the last two — improvement 16, a time-based HTTP-cache fallback for validator-less responses, and improvement 20, TVmaze/IMDb links on the search-details page built from the id with the URL formats in `appsettings` — landed 2026-10-02; improvement 21, the country-from-language display fallback, landed 2026-10-07; improvements 22–24, the title-line chips and the fuller details/Started episode rows, landed 2026-10-31; improvement 25, the Updates tab and the status-transition dates it required, landed 2026-10-31; improvement 26, per-tab persisted sort plus the `Preferences` static-init defect that had silently defeated it, landed 2026-10-31; see `LOG.md`). Phase 8 can resume.
The `Enrichment` IMDb-driven mode (7.3) is now built (ADR-031 — see below).
The two airing-catalog jobs —
**7.1 `EpisodeRefresh` and 7.2 `StatusRecheck`** — are now built: one shared `ShowRefreshJob` body (select →
refresh source rows → map), differing only in selection (EpisodeRefresh excludes `Ended`/`Canceled`,
StatusRecheck selects exactly those) and logging (a resume is logged at Information with both enum values).
Both are lease-guarded, measure `items_changed` after the mapper, treat a transport failure as a per-show
failure (R14), and detect a resume by comparing the tracked entity's enum before/after the map — never by
reading a source table (step 8.1). 8 integration tests prove idempotence, the selection sets, the soft-delete
skip (asserted on the request list), and watch-mark id stability across a refresh.
A third refresh job, **`RefreshUpdated`** (added 2026-10-04, not a numbered plan step), is now built: it
fetches TVmaze's `/updates/shows` map once (`{ tvmazeId: updatedUnixSeconds }`, large, so fetched once and
cached — `ICatalogProvider.GetShowUpdateTimestampsAsync`) and re-fetches/re-maps **only** the tracked shows
whose stored `tvmaze_show.provider_updated` is older than the map's value. The stored timestamp is read
through the mapper (`GetProviderUpdatedAsync`) — source-table containment (step 8.1) forbids the job reading
`tvmaze_show` itself. Lease-guarded; an unreachable provider aborts with `ProviderUnavailable` (R14) rather
than reporting a clean no-op; a per-show fetch failure is a per-show failure. 8 integration tests + 2 provider
tests prove the diff selection, idempotence (a re-run refreshes nothing once the stamp catches up), the
soft-delete skip, the R14 abort, and the lease refusal. Drivable from the console and the Manage tab. **Step 7.4 is now built**:
the `ImdbImport` job. `ImdbDatasetDownloader` streams a dataset's `.tsv.gz` over the `Imdb` named client
(streaming so the request timeout covers only headers, not the whole body); the importer **reuses a dataset
already on disk** (a plain `.tsv` or a prior `.tsv.gz`) and only downloads what is missing, then loads only
rows reachable from active tracked shows (ADR-021) in the order `DATA_ANALYSIS.md` §6 now fixes —
**titles → episodes → episode-details → akas** (titles first because the `imdb_episode.parent_tconst` FK is
checked at each batch commit and cannot be deferred across one). The load runs in **short batched
transactions** with the lease renewed at each boundary — ADR-028 forbids holding one write transaction open
for the whole run (it locks out the lease's separate-connection renewal and the UI's writes). Raw
parameterized `Microsoft.Data.Sqlite` batches, `INSERT OR IGNORE` + a value-guarded `UPDATE` make it
re-runnable, `\N`→NULL throughout, and `imdb_dataset_meta` records provenance. 9 integration tests prove the
documented counts, the `\N`→null rule, re-run=0, file-kept-on-disk, reuse-without-download, the soft-delete
skip, and the lease refusal. **The real run loaded 539 titles / 19,267 episodes / 18,838 akas and a second
run reported 0 changes.** (Note: `tt2085059` now carries 34 episode rows in the living dataset, not the 32
recorded at planning — the importer loads exactly what the source contains; see the §6 / step-7.4 notes.)
**Step 7.5 is now built**:
localized titles from TVmaze `/akas` (decoupled from IMDb). `ICatalogProvider.GetAkasAsync` returns raw
`ShowAka(Name, CountryCode, CountryName)` in array order (the provider never hard-codes a country — the
target is configuration, default `RU`); the new `tvmaze_akas` source table (migration `0005`, composite PK
`(show, country, name)`) holds them verbatim; `IShowMapper.MapAkasAsync` picks the lowest-order aka for the
target country into `title_localized` (null on no match, never a wrong-country guess). The `Enrichment --akas`
job and the details-page button both go through that one mapper path. 10 tests prove the RU pick, the
null-on-no-match, idempotence, the soft-delete skip, and R14. The
full UI is now in place — the Avalonia head (`Shows.App.Avalonia`) has My Shows (four category tabs with
untrack), Search, Discovery,
and a show-details page (season expansion, episode toggles, auto-finish, id-built external links). The
"Fetch localized titles" button is live (step 7.5): it fetches the show's `/akas`, maps the target-country
pick through the mapper, and lists every alternate. **Step 7.3 is now built** (ADR-031, open question 1
answered — TVmaze authoritative, IMDb completes): the `Enrichment --imdb` mode. `TvmazeShowMapper.EnrichFromImdbAsync`
fills `episode.title`/`runtime_minutes` from `imdb_episode` **only where TVmaze left them null** (setting
`title_source='imdb'`), links `episode.imdb_id`, and **never** overwrites a TVmaze value or fabricates an
air date (IMDb has only a year) or a description (IMDb has none). Every disagreement — the `(season, episode)`
set diff both directions, title/runtime conflicts, and air-date gaps — is recorded in the new derived,
rebuildable `episode_mismatch` table (migration `0006`), which the enrichment mapper wipes/recomputes per
show and the details page reads (the UI cannot read `imdb_*`). IMDb-only episodes are **flagged, not merged**.
8 tests prove the gap-fill, the never-overwrite rule, the flag-not-merge behaviour, and re-run=0. **The real
run enriched 539 shows and recorded 18,424 mismatches (14,797 runtime / 2,985 title / 428 imdb_only / 194
tvmaze_only / 20 air_date_gap); a second run reported 0 changes, and `air_date_source='imdb'` stayed 0.**
The composition root is wired (logging → infrastructure → directories → gated migrate →
VMs/pages). The `MapShowAsync(int)` path is exercised by the use case through the `ITvmazeShowSource` seam
(the provider implements it, 3.1); the DTO overload remains the path the jobs use.

The repo is **not under git** (user decision, 2026-09-29). `.gitignore` is verified with
`git check-ignore` in a scratch repo instead of `git status`. See `LOG.md` for the evidence.

**Step 0.5 unblocked 2026-09-29.** The .NET 8 MAUI **packs** are now installed
(`C:\Program Files\dotnet\packs\Microsoft.Maui.Sdk\8.0.3` exists — previously only 9.x/10.x were, which
is what blocked `UseMaui=true` on the net8 TFM). `Shows.App` builds as an unpackaged Win32 MAUI app
(`WindowsPackageType=None`). Evidence in `LOG.md`. No machine blocker remains in Phase 0 or Phase 1.

```
c:\src\shows\
  cancelled.json      16 shows   input, read-only
  episodes.json       4 RPC batches (see DATA_ANALYSIS.md)
  finished.json      207 shows   input, read-only
  later.json         246 shows   input, read-only
  watching.json       78 shows   input, read-only
  title.episode.tsv  >50 MB      IMDb dataset, read-only (input copy)
  data\imdb\         IMDb datasets the importer reads/keeps: title.basics.tsv,
                     title.akas.tsv (moved here, reused not re-downloaded),
                     title.episode.tsv.gz (downloaded + kept by step 7.4)
  AGENTS.md          router: who owns which fact, and how to start a step
  docs\              SPEC, SCHEMA, ARCHITECTURE, DATA_ANALYSIS, DECISIONS,
                     IMPLEMENTATION_PLAN, STATE (this file), LOG
  tasks\             one file per in-progress step; tasks\done\ holds finished ones
  Shows.sln          eleven projects, no project references yet (step 0.1)
  Directory.Build.props  global.json  .editorconfig  .gitignore
  Shows.Domain\ Shows.Application\ Shows.Infrastructure\ Shows.App\ Shows.Tests\
  Shows.Jobs.Import\ Shows.Jobs.ScrapeMyShows\ Shows.Jobs.EpisodeRefresh\
  Shows.Jobs.StatusRecheck\ Shows.Jobs.Enrichment\ Shows.Jobs.ImdbImport\
```

The seven data files may not be modified or deleted. All code goes into new project directories.

**Docs were restructured 2026-09-28** (evidence in `LOG.md`): every document now declares its
`authority` in front matter, the id encoding lives only in `SCHEMA.md` §1a, and `PROGRESS.md` was
split into this file (current state, rewritten freely) and `LOG.md` (append-only evidence). Start a
step by promoting it into `tasks/` per `AGENTS.md`. Step 0.1 is finished and its task file is in
`tasks\done\`.

**New requirement 2026-09-29: per-service HTTP proxy (SPEC R14, ADR-029).** TVmaze is unreachable
directly from the development machine; myshows.me and the IMDb dataset host are not. Verified via a
proxy at `main-pc:10801` — observations in `DATA_ANALYSIS.md` §9, config keys in `ARCHITECTURE.md` §4.
**This affects Phase 0 work immediately**: step 0.4 configures the three named clients, and any
provider that builds its own `HttpClient` will work on CI and fail here. No new step — R14 lands in
0.4, 2.2, 3.1 and 7.4.

---

## Step status

| Step | Title | Status | Depends on |
|---|---|---|---|
| 0.1 | Solution + `Directory.Build.props` + `.editorconfig` | **done** | — |
| 0.2 | `Shows.Domain` with zero dependencies | **done** | 0.1 |
| 0.3 | `Shows.Application` interfaces + use-case skeletons | **done** | 0.2 |
| 0.4 | `Shows.Infrastructure` project + `DataPaths` + config | **done** | 0.1 |
| 0.5 | `Shows.App` MAUI head (net8.0-windows) + DI wiring | **done** | 0.3, 0.4 |
| 0.6 | `Shows.Tests` project | **done** | 0.1 |
| 0.7 | Job console projects (empty, DI host only) — see `ARCHITECTURE.md` §2 for the set | **done** | 0.4 |
| 0.8 | Shared logging policy (`logging.json` + `AddShowsLogging`) | **done** | 0.4, 0.7 |
| 1.1 | `ShowsDbContext` + SQLite PRAGMA setup + id convention | **done** | 0.4 |
| 1.2 | **Mapped** tables + `user_show` + indexes + migration 1 | **done** | 1.1 |
| 1.2b | **Source** tables (`tvmaze_*`, `imdb_*`) + migration 2 | **done** | 1.1 |
| 1.3 | Staging entities + `v_staging_issues` view | **done** | 1.1 |
| 1.4 | Repositories + `UnitOfWork` (assembles the TrackedShow/UserShow aggregate) | **done** | 1.2, 1.2b |
| 1.5 | `TvmazeShowMapper` — the single mapped-row write path + rebuild drill | **done** | 1.4, 1.6 |
| 1.6 | `IdGenerator` + `WriterLease` **new** | **done** | 1.1 |
| 2.1 | JSON-RPC envelope parser + `--stage` | **done** | 1.3 |
| 2.2 | `ScrapeMyShows` job → `staging_imdb_id` | **done** | 1.3 |
| 2.3 | TVmaze `/lookup?imdb=` resolution | **done** | 2.1, 2.2, 3.1 |
| 2.4 | `--report` CSV export | **done** | 1.3 |
| 2.5 | **Human gate:** user reviews staging in DB Browser | **done** | 2.4 |
| 2.6 | `--promote` with backfill algorithm | **done** (user runs it on the real DB) | 2.5, 1.5 |
| 3.1 | `TvmazeCatalogProvider` + HTTP caching + Polly | **done** | 0.3 |
| 3.2 | `AddShowToTrackingUseCase` (calls the mapper) | **done** | 1.5, 3.1 |
| 4.1 | `SearchViewModel` + search UI | **done** | 3.2 |
| 4.2 | `DiscoveryViewModel` (new + upcoming) | **done** | 3.2 |
| 5.1 | Category tabs + show list + **untrack (soft delete)** | **done** | 3.2 |
| 5.2 | Season expansion + persisted state | **done** | 5.1 |
| 5.3 | Episode toggle + auto-finish recompute | **done** (see open question 9) | 5.2 |
| 6.1 | Show details page (+ "Fetch localized titles" button, step 7.5 seam) | **done** | 5.3, 7.5 |
| 7.1 | `EpisodeRefresh` job | **done** | 1.5, 3.1 |
| 7.2 | `StatusRecheck` job | **done** | 1.5, 3.1 |
| 7.3 | `Enrichment --imdb` job (gap-fill + `episode_mismatch` flagging, ADR-031) | **done** | 1.5, 7.4 |
| 7.4 | `ImdbImport` job | **done** | 1.2b |
| 7.5 | Localized titles from TVmaze `/akas` (decoupled from IMDb) | **done** | 3.1 |
| 8.1 | Layer-boundary architecture tests | **done** | 0.5, 1.5 |
| 8.2 | Integration tests on temp SQLite | **postponed** (2026-09-30) | 2.6 |
| 8.3 | Docker-readiness pass | **postponed** (2026-09-30) | 8.1, 8.2 |
| 8.4 | Database-backed Discovery schedule archive | **done** | 4.2, 8.1 |

Parallelizable: 0.1–0.7 mostly in parallel; after 1.5, Phase 2 and Phase 3 proceed independently
until step 2.3 needs 3.1. Phase 4 and Phase 5 are independent of each other. All of Phase 7 is
mutually parallel.

**Step 1.5 was the pivot of the plan, and it has landed.** Every writer of mapped rows — `--promote`,
search-add, and all four jobs — depends on the mapper, so nothing that produces data could start before
it. With it done, Phase 2 (the import) and Phase 3 (the provider) can proceed independently until step
2.3 needs 3.1.

---

## Known open questions

These need a user decision before the step that consumes them. Do not decide unilaterally.

1. ~~**Step 7.3** — when TVmaze and IMDb disagree on a show's episode set, should `Enrichment`
   **union** them (adding IMDb-only episodes with a null air date) or only **flag**?~~ **ANSWERED
   2026-09-30 — flag only, TVmaze authoritative.** IMDb fills only gaps TVmaze left null (`title`,
   `runtime_minutes`, with `title_source='imdb'`); it never overwrites a TVmaze value and never
   fabricates an air date or description (the datasets carry neither a full date nor a summary).
   Every disagreement — set diff both directions, field conflicts, air-date/description gaps — is
   recorded in a new rebuildable `episode_mismatch` table and shown on the details page. No IMDb-only
   episode is inserted. ADR-031.
2. **Deferred** — a "shows with unwatched specials" filter. Requested, explicitly deferred past v1.
3. ~~**Partly answered, confirm at step 3.1** — the exact set of TVmaze `status` strings.~~ **ANSWERED
   at step 3.1 (2026-09-29).** A live probe through the proxy mapped a spread of 10 shows and observed
   exactly two raw strings — `"Running"` (→ `Airing`) and `"Ended"` (→ `Ended`) — both already in the R12
   dictionary, so `tvmaze_status_seen.mapped_to` is non-NULL for every observed value. The dictionary also
   covers `Upcoming` and `Canceled`; any string it does not know fails closed to `Unknown` with a
   `mapped_to NULL` row, so an unseen value stays discoverable rather than silently wrong. **TVmaze rate
   limits are now observed too**: 10 distinct searches under the 250 ms throttle all returned 200 with no
   429 and no `Retry-After` (evidence in `LOG.md`). The completeness check is satisfied — the live sample
   surfaced only two of the four known statuses, which is expected for a small spread of well-known shows.
4. ~~**Untracking a show: hard delete or soft delete?**~~ **ANSWERED 2026-09-28 — logical delete.**
   `user_show.deleted_at`; nothing is deleted and re-adding restores the watched state. ADR-024.
   The same answer also moved user data out of `tracked_show` entirely, which is what makes SPEC P4
   a testable property rather than a claim.
5. ~~**BLOCKING — step 1.1. What storage type for the client-generated key?**~~ **ANSWERED
   2026-09-28 — 64-bit time-ordered integer, UUIDv7 dropped.** The user chose to keep `INTEGER` and
   abandon UUIDv7 rather than store a 128-bit value in a BLOB/TEXT. The layout is specified once, in
   `SCHEMA.md` §1a, and is not repeated here. Recorded as **ADR-027**, which supersedes the mechanism
   half of ADR-026 while keeping client-generation.
   The same conversation also settled a question the id choice depended on: **jobs may run while the
   app is open**, so there are genuinely concurrent writers — that is **ADR-028** (`writer_lease`), and
   it is why store-assigned ids are now doubly ruled out.
6. **New, low urgency — confirm at step 1.6.** The id epoch is a fixed constant with a long but finite
   horizon. Nothing to decide now; it is recorded so that whoever finds negative ids far in the future
   knows which constant to change and understands that the timestamp field cannot simply be widened
   (`SCHEMA.md` §1a: the layout has no spare bits, ADR-027).
7. ~~**New — decide at step 2.6 (promote gate semantics).** `v_staging_issues` (`SCHEMA.md §6`) flags a show
   as `unresolved_imdb_id`/blocking whenever `imdb_id IS NULL`, even if it already has a `tvmaze_id`. Show
   `96144` was resolved **straight to a TVmaze id** by the user (its TVmaze structure differs, so it has no
   IMDb link), yet the view marks it blocking — so `--promote` would refuse on a legitimately-resolved show.
   A show with a `tvmaze_id` should probably not be blocking for lacking an `imdb_id` (the TVmaze id is what
   the mapper actually needs). Fixing this means editing the view's CASE in `SCHEMA.md §6` **and** migration
   `0003_Staging` (or a new migration) — do not decide unilaterally; the view is the promote contract.~~
   **ANSWERED 2026-09-30 — demoted to warning (ADR-030).** The user chose to demote `unresolved_imdb_id`
   from blocking to warning: a show with a `tvmaze_id` is never blocking for promotion just for lacking an
   `imdb_id` (IMDb is enrichment, step 4.1; the mapper needs only TVmaze). Implemented as migration
   `0004_StagingIssuesImdbWarning` (DROP + re-CREATE the hand-written view with the one blocking line
   removed; `Down` restores 0003 verbatim). The blocking set is now exactly `{unresolved_tvmaze_id,
   watched_exceeds_total}`. This cleared the two review rows (`73305`, `96144`) so the gate is empty and
   `--promote` can run.
8. **New, low urgency — confirm at step 0.4.** Proxy credentials. The verified proxy at `main-pc:10801`
   needs none, so `Proxy:<service>` is a bare URL. If a future one requires auth, the value becomes
   `user:pass@host:port` and the password must never reach the log — see the consequence in ADR-029
   before adding a separate `Password` key, which would make it worse.
9. **New — surfaced at step 5.3. "Untoggle moves a show back out of `Finished`" has no mechanism.** The
   plan's 5.3 matrix says untoggling the last watched episode on an auto-finished show should move it back
   out of `Finished`. But nothing stores *which* category the show was in before the auto-finish moved it
   in, so a recompute cannot know where to move it back — and `RecomputeProgressUseCase` only ever moves a
   show *into* `Finished` (R5), never out. Options: (a) drop the criterion (a user who wants it back in
   `Started`/`Later` moves it manually — R6 already says manual moves win); (b) store a
   `pre_finish_category` column and restore it; (c) move it to a fixed category. Recommendation: (a) — it
   matches R6's "manual changes win" and avoids a column for a rare round-trip. **Do not decide
   unilaterally; the UI ships with the into-Finished direction only.**

---
