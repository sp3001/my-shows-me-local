---
status: historical
authority:
  - step-log
not_authoritative_for:
  - current-state
  - sequencing
---

# LOG — Step Log (append-only)

Verification evidence for steps that are done. **Append-only: never rewrite an entry.**

This file is `status: historical` on purpose. It is not current state and must not be read as
current state — `STATE.md` owns that. An entry here records what was observed on a date, which is
why it is never updated in place: a record rewritten to match today's schema no longer shows what
was actually true that day. If an entry contradicts a current document, the current document wins
and the entry stays.

Every entry needs: step id and date, files changed, the verification command actually run and its
observed output, deviations from the plan, new facts learned, and what is unblocked. "It builds" is
not verification for a data step — quote row counts.

Newest entry first.

### 2026-10-31 — improvement 25 — the Updates tab, and the status-transition dates it needed (ADR-038, migration 0011)

**Request.** User, verbatim: "Add one more tabs - updates, next to Finished. It should use simiar layout
as upcoming and contain following sections 1. Ended Shows - shows which updated status to Ended in past
60 days. Ordered by status update date desc, display show name + date. 2. Renewed Shows - shows which
updated from status Ended to any other status in past 60 days. Order by status update desc, display show
name, current status, date 3. Updated Lately - shows updated from tvmaze past 30 days. Order by update
date desc, display show name + update date 4. Recently watched - episodes watched in past 30 days. Order
by watched at date desc, display show name, episode S#N# title, watched at date".

Three follow-ups during the work changed the data model, not just the view: (a) "treat last status change
as the updated at, and date an Ended show from its last episode air date"; (b) "drop `status_prev_id`,
store the transition dates instead"; (c) "a cancelled show is ended too — and the first map of an Ended
show must date the end from the last aired episode, not only the migration".

**Files changed.**
- `Shows.Domain/TrackedShow.cs` — three mapper-written columns after `MappedAt`: `StatusChangedAt`,
  `StatusRenewedAt`, `TvmazeUpdatedAt`.
- `Shows.Infrastructure/Persistence/Migrations/20261031170017_0011_ShowStatusTransitions.cs`
  (+Designer, +ModelSnapshot) — three `AddColumn<DateTimeOffset>` (nullable TEXT) plus two hand-written
  `migrationBuilder.Sql` backfills.
- `Shows.Infrastructure/Persistence/TvmazeShowMapper.cs` — captures `previousStatus` before
  `ApplyShowFields`, then after the mid-map cached-columns flush calls the new
  `RecordStatusTransitionsAsync`; sets `TvmazeUpdatedAt` unconditionally from
  `payload.Show.ProviderUpdated` via the new `FromUnixSeconds`. New helpers `LastAirInstantAsync` and
  `SetStamp`.
- `Shows.Application/Interfaces/IEpisodeRepository.cs` — `ListByIdsAsync`, `ListWatchesSinceAsync`.
- `Shows.Application/Interfaces/ITrackedShowRepository.cs` — `ListActiveByIdsAsync`.
- `Shows.Infrastructure/Persistence/Repositories/EpisodeRepository.cs` — both reads; the watch read is
  `FromSqlRaw("SELECT * FROM episode_watch WHERE watched_at >= {0}")` with a **second-precision**
  cutoff string, then sorted in memory.
- `Shows.Infrastructure/Persistence/Repositories/TrackedShowRepository.cs` — `ListActiveByIdsAsync`.
- `Shows.Application/UseCases/GetUpdatesFeedUseCase.cs` (new) — `UpdatesFeedRow`, `RecentlyWatchedRow`,
  `UpdatesFeed`, `StatusWindowDays = 60`, `RecentWindowDays = 30`.
- `Shows.Infrastructure/ShowsInfrastructureServiceCollectionExtensions.cs` — `AddScoped` for the use case.
- `Shows.App.Avalonia/ViewModels/UpdatesSectionViewModels.cs` (new) — `UpdatesSectionViewModel`,
  `UpdatesRowViewModel`.
- `Shows.App.Avalonia/ViewModels/TrackingViewModel.cs` — `IsUpdates`, `UpdatesSections`, the fifth tab,
  `IsStartedView`/`IsSortable`/`IsCategoryList` gates, `LoadUpdatesAsync`, the `ApplyFilterAndSort` branch.
- `Shows.App.Avalonia/Views/TrackingView.axaml` — the ListBox `IsVisible` moved from
  `{Binding IsStarted, Converter=InvertBool}` to `{Binding IsCategoryList}`; a fourth `ScrollViewer` for
  the feed.
- `Shows.App.Avalonia/Views/UpdatesSectionView.axaml{,.cs}`, `UpdatesRowView.axaml{,.cs}` (new).
- `Shows.Tests/Integration/StatusTransitionStampTests.cs` (new, 12), `UpdatesFeedTests.cs` (new, 9),
  `StatusTransitionBackfillTests.cs` (new, 9).
- `Shows.Tests/Integration/ShowRefreshJobTests.cs`, `RefreshUpdatedJobTests.cs` — both
  `TrackedShowRepositoryProxy` classes gained `ListActiveByIdsAsync` (the standing breakage when an
  interface grows a member).
- Docs: `SCHEMA.md` §2 (three columns + prose, v1.5 note), `SPEC.md` F4 + v1.6, `DECISIONS.md` ADR-038,
  `STATE.md`, `user-issues.md` item 25, `ARCHITECTURE.md` untouched (no layer change).

**Verification.**

```
dotnet build Shows.sln
Build succeeded.
    0 Warning(s)

dotnet test Shows.Tests
Passed!  - Failed: 0, Passed: 386, Skipped: 0, Total: 386 - Shows.Tests.dll (net8.0)

dotnet test Shows.Tests --filter "FullyQualifiedName~StatusTransitionStampTests"
Passed!  - Failed: 0, Passed: 12, Skipped: 0, Total: 12

dotnet test Shows.Tests --filter "FullyQualifiedName~UpdatesFeedTests"
Passed!  - Failed: 0, Passed: 9, Skipped: 0, Total: 9

dotnet test Shows.Tests --filter "FullyQualifiedName~StatusTransitionBackfillTests"
Passed!  - Failed: 0, Passed: 9, Skipped: 0, Total: 9
```

386 = the 356 baseline plus 30 new. The migration was scaffolded with
`dotnet ef migrations add 0011_ShowStatusTransitions --project Shows.Infrastructure --startup-project Shows.Infrastructure`
and the two `Sql` backfills added by hand underneath the generated `AddColumn`s.

**The backfill, observed on the live `data/shows.db`** (0011 is already applied there — the app was
started during this step, and `ShowsDatabaseMigrator` applied it):

```
select count(status_changed_at), count(status_renewed_at), count(tvmaze_updated_at) from tracked_show
471     0     554
```

- `status_changed_at` filled on **471 of the 471** terminal active shows — every terminal show in this
  library has at least one dated episode, so the fallback path never fired during the backfill. Range
  `1986-09-27` to `2026-10-02`.
- `status_renewed_at` **0**, by design: nothing observed a renewal before the mapper started observing
  them.
- `tvmaze_updated_at` filled on **554 of 554** — every tracked show has a source row with `provider_updated`.

What the four sections will actually show, projected with the same windows the use case uses:

```
ended_60d  renewed_60d  updated_30d  watched_30d
1          0            13           51
```

Ended holds exactly one show (`Bite Size Terror`, ended 2026-10-02). That is the honest consequence of
dating an end from the last aired episode: 470 shows are terminal, but their finales are older than 60
days. Renewed is empty until `StatusRecheck` observes a comeback. **User action: run `StatusRecheck` and
`RefreshUpdated`** (Manage page) if the Renewed and Updated Lately sections look thinner than expected.

**Deviations from the request, and why.**
- The user said "updated status to **Ended**" and "from status **Ended** to any other". Implemented as
  the terminal **set** `{Ended, Canceled}` — `ShowStatusRules.NoMoreEpisodesExpected`, the set SPEC R12
  already owns. The user confirmed this in follow-up (c). A second definition of "terminal" would have
  been a second thing to keep in sync, and a cancelled show is ended in every sense the view means.
- "to any other status" is narrowed to *any airable* status. A move to `Unknown` is a vocabulary gap, not
  a renewal, and listing it as renewed would report a mapping bug as good news (SPEC R12's fail-closed
  rule).
- The Ended section reads `status_changed_at`, which is the **last aired episode's** date rather than the
  moment we learned it — the user's follow-up (a). This is what makes the section mean "ended recently"
  rather than "we recently noticed an ending".
- A show that ended, was renewed, and ended again appears in **Ended**, not Renewed. Its `StatusRenewedAt`
  is recent but its current status is terminal, so calling it renewed now would be false.

**New facts learned.**
- TVmaze payloads carry **no** status-change field: 0 of the 554 stored shows have one. The three
  candidates for "when did this change" were `mapped_at` (moves on any field edit, including our own
  recomputes), `tvmaze_status_seen` (a global vocabulary log, not per-show) and
  `tvmaze_show.provider_updated` (TVmaze's own timestamp — right for "updated lately", wrong for a
  status change). None of them answers the question, which is why the dates had to be recorded.
- `max()` over `air_stamp` TEXT is lexicographic, and it equals chronological **only** because every
  stored value is the same 25-char `+00:00` form — verified: 19,503 rows, one distinct length. The
  migration says so in a comment, because a future write with a different offset would silently break it.
- `watched_at` is the opposite: variable 31/32/33-char lengths, because the app writes whatever
  fractional precision `DateTimeOffset.UtcNow` happened to have. A `DateTimeOffset` parameter formatted
  with 7 fraction digits therefore compares a whole-second row as **less than** the cutoff and drops it.
  The cutoff is passed as a second-precision string so the boundary second is inclusive — pinned by
  `A_watch_mark_exactly_on_the_cutoff_is_included`.
- `UseSnakeCaseNamingConvention` applies to `__EFMigrationsHistory` too: the column is `migration_id`,
  not `MigrationId`. Caught by a test that tried to delete its own history row.
- EF Core 8's `DatabaseFacade.Migrate()` has **no** target-migration overload, so a "database at
  migration N-1" fixture cannot be built by asking for it. The pattern that works: migrate fully, seed
  while the model and schema agree, then roll the migration under test back by hand (delete its history
  row, drop its columns) and migrate forward again. Seeding *after* the drop fails, because EF's model
  still names the dropped columns.
- `AsNoTracking()` on a nullable scalar projection is a **CS0452** — the element type is not a reference
  type, so it does not satisfy the constraint. Scalar projections are not tracked anyway.

**Two tests that were wrong, not the code.** (1) A show that re-ends with the same finale keeps that
finale's date — I had asserted it should move, but the date is *derived*, so a recompute that agrees is
the correct outcome, not a stale carry-over. (2) A second renewal legitimately refreshes
`StatusRenewedAt`: the column means "the last time we saw it come back". Both assertions were corrected
against the code, and the reasoning is in the test comments.

**Also fixed while here.** The category ListBox in `TrackingView` was visible whenever `IsStarted` was
false, which meant the Updates tab — borrowing `Finished` as its category so the tab strip stays
consistent — realized an empty virtualized list underneath the feed. A new `IsCategoryList`
(`!IsStarted && !IsUpcoming && !IsUpdates`) gates it, and `_isUpcoming`/`_currentCategory` each gained a
`NotifyPropertyChangedFor(IsCategoryList)` so the flag actually re-evaluates.

**Unblocked.** Improvement 25 is closed. Nothing else depends on the new columns, so Phase 8 (8.2/8.3,
both postponed) can resume. The `status_changed_at` / `status_renewed_at` pair is the only place this
repository records an observed transition, so any future "what changed recently" feature reads them
rather than re-deriving from `mapped_at`.

### 2026-10-31 — UI improvements 22, 23, 24 — title-line chips, and episode rows that fill the details page

**Request.** User, in six messages: (1) "Move country and tvmaze rating to title line, after user rating,
display as grey chip, add dot separator after status user rating chips. Do this in all lists. On show
details page there is a lot of free space. So display truncated episode description there… more info icon
which opens popup with episode image and full description. Also for tracked shows — if episode watched
display watched at date". (2) "if there is no poster or description — dont show icon… except watched at is
only for tracked". (3) the row geometry: "make season-episode (s8e22) text aligned to top… move air date
next to episode title (1), watched at date next to it (2), and short description as second line (3), info
button can sit at right side of the episode line with middle vertical alignment". (4) per-episode TVmaze
rating next to the title and duration under the marker in small grey, still two lines. (5) episode duration
on the Started tab with a total on the season line. (6) a correction after seeing it: "on started it should
be on right, before air date, and total should be before episode count".

**Files changed.**
- Read model: `Shows.Application/Interfaces/IEpisodeRepository.cs` (+`SumRuntimeBySeasonAsync`,
  `SumRuntimeBySeasonForShowsAsync`), `Shows.Infrastructure/Persistence/Repositories/EpisodeRepository.cs`
  (both delegate to one private `SumRuntimeAsync`), `UseCases/GetCardSeasonsUseCase.cs`,
  `UseCases/GetStartedCardsUseCase.cs` (batched, sliced per show), `UseCases/ReadUseCases.cs`
  (`ShowDetails.WatchedAtByEpisodeId`), `UseCases/GetCatalogDetailsUseCase.cs`,
  `Catalog/CatalogDetails.cs`, `Catalog/ICatalogProvider.cs`,
  `Shows.Infrastructure/Tvmaze/TvmazeJsonMapper.cs`.
- UI: `Shows.App.Avalonia/Services/RuntimeFormat.cs` (new), `ViewModels/SeasonGroupViewModel.cs`,
  `TrackedShowCardViewModel.cs`, `ShowCardViewModel.cs`, `ShowDetailsViewModel.cs`,
  `CatalogEpisodeViewModels.cs`, `Styles/Controls.axaml` (`TextBlock.chipDot`, `Button.info`),
  `Views/TrackedShowCardView.axaml`, `ShowCardView.axaml`, `ShowDetailsView.axaml` (+`.axaml.cs`),
  `SearchDetailsView.axaml`.
- `tasks/done/ui-22-24-title-chips-episode-lines.md`, `docs/STATE.md`, `docs/user-issues.md`.

**Verification.**
```
> dotnet build Shows.sln -v q --nologo
Build succeeded.
    0 Warning(s)
    0 Error(s)

> dotnet test Shows.Tests --nologo -v q
Passed!  - Failed: 0, Passed: 356, Skipped: 0, Total: 356, Duration: 10 s - Shows.Tests.dll (net8.0)
```
No new tests: every change is a XAML layout or a read-model projection, and the two new repository methods
are aggregates over a predicate already covered by `CountDisplayableEpisodesBySeason*`'s tests. The UI was
checked by launching the app and capturing its window (`draft/app-script.ps1`, PrintWindow — `CopyFromScreen`
returns "the handle is invalid" on this RDP session). Observed on the Started tab: `Lanterns · 2026 · Drama ·
Action · Adventure · Science Fiction` with `Airing` `★ 8` `·` `US` `★ 6.4` on the title line; season header
`Season 13  ~7h 4m · 13  ▾`; episode rows ending `… 60 min · 2026-10-04`. The `ⓘ` glyph renders on the
details rows, which is the proof the flyout is attached to `Button.Flyout` and has not swallowed the
button's `Content`.

**Not verified by me.** That the flyout actually *opens* with the still and the full synopsis, and that
clicking it leaves the watched mark alone. Synthetic clicks land on rows and nav tabs but I could not hit
the 15 px `ⓘ` reliably; the user took over verification there ("i will test myself"). No watch state was
damaged in the attempt: `Lanterns` S1E5 (`episode_id 98534051659788291`) still carries its original
import-era `watched_at` of 2026-09-29 21:39:08, so the click near the `ⓘ` did not flip the mark —
consistent with the Button-ancestor guard, though it does not prove the popup opened.

**New facts learned**
- **A `Flyout` written as a `<Button>` child is not a flyout.** `Button` derives from `ContentControl`, whose
  `Content` is the `[Content]` property, and `Flyout` is not a `Control` — so the child became the button's
  content and `Button.Flyout` stayed null: the `ⓘ` glyph from `Button.info` vanished and clicking did
  nothing. The build stays green either way, so a clean compile proves nothing here. The canonical form is
  `<Button.Flyout><Flyout/></Button.Flyout>` (ControlCatalog).
- **A horizontal `StackPanel` measures a `TextBlock` at its full single-line width**, so chips placed after a
  long title are pushed off the right edge; with `TextWrapping="Wrap"` the measure returns the *available*
  width and the chips always drop to a second line. `Grid ColumnDefinitions="*,Auto"` (star first) gives the
  chips their width first and lets the title take the remainder and wrap. A `WrapPanel` was tried first and
  rejected for exactly this reason.
- **A flyout's content inherits the placement target's `DataContext`**: `PopupFlyoutBase.ShowAtCore` sets
  `Popup.PlacementTarget = Target = placementTarget; Popup.SetPopupParent(placementTarget)` and `Popup.Open()`
  does `((ISetLogicalParent)popupHost).SetParent(this)`, so `{Binding}` inside the flyout resolves against the
  row's ViewModel. No ViewModel change was needed to feed the popup.
- `Flyout` has no `ShowPointerTheme` property (it was written into the XAML and removed), and
  `FlyoutBase.IsOpen` only opens at the *last* placement target, so it is not a first-use binding target.
- Synthetic input: `mouse_event` after `SetCursorPos` is unreliable here; `SendInput` with a one-pixel jiggle
  (Avalonia re-hittests only on `WM_MOUSEMOVE`) lands clicks. `PrintWindow` with `PW_RENDERFULLCONTENT` (2)
  captures a Skia window that `CopyFromScreen` cannot.

**Deviations.**
- `EpisodeCatalogInfo.SummaryHtml` was **renamed** to `Summary` and is now markup-stripped at the mapper, not
  at the View. `HtmlStripper` lives in Infrastructure and the UI may not name it (`ARCHITECTURE.md` §2), so
  the strip belongs in the provider mapper — the same precedent as `CatalogShow.Overview`. The old name
  promised HTML that no longer arrived.
- `MetaText` (cards) and `NetworkText` lost the country and the ★rating, which moved to the title-line chips.
  The meta line is now year + genres on tracked cards, year only on discovery cards.
- The season runtime is a **computed** aggregate, not a column (SPEC P5). It is summed over exactly the
  displayable episodes the header count uses — unwatched, aired, not insignificant-specials — so `~7h 4m` and
  `13` describe the same set. A season with no runtime anywhere is absent from the map, so the header shows
  the count alone rather than a misleading zero.
- `CardSeasons`, `ShowDetails` and `CatalogEpisode` were widened with **optional trailing** members so the
  existing positional constructions in the tests still compile; no test fake implements `IEpisodeRepository`,
  so its two new members broke nothing.
- Toggling a watched mark now also updates the row's `WatchedAtText` in place (stamped `UtcNow`, the instant
  the use case writes) instead of leaving a stale date until reload.

**Unblocked.** Nothing was blocked. The details pages no longer waste their width, and the Started tab now
answers "how long is left" per season and per episode.

**Request.** User: "I found some watched episodes which according to date were marked during import but
episodes aired just now ... maybe they were in db and myshows didnt have them as unwatched as they were
in the future". Their probe: `select * from episode_watch w join episode e on w.episode_id = e.id where
w.watched_at < e.air_date`. They also asked for a SQL repair, because they will not re-import.

**Diagnosis.** 14 rows on the pre-fix DB, every one stamped inside the `--promote` window
(2026-09-29 21:39–21:43), `needs_review = 0` on all of them. Two defects in
`Promoter.BackfillWatchMarksAsync` branch 3 — see ADR-037. Verified that the excess marks are 100 %
future-dated (row-number-over-`totalEpisodes` query: `excess_past = 0` for all six shows), so the
user's air-date probe catches every over-mark and there are no silent past-dated ones.

**Files changed:**
- `Shows.Infrastructure/Import/Promoter.cs` — branch 3 gated on `TotalEpisodes > 0`; a post-branch
  withhold of episodes with `AirDate > importDay`; branch 1's `needs_review` moved to after the guard
  (`bool? needsReview` → `var review = needsReview ?? toMark.Count != target.WatchedEpisodes`).
- `Shows.Tests/Integration/PromoterTests.cs` — `Payload` gained `futureRegulars` (no existing fixture
  could produce an episode dated after today, which is why 12 passing tests missed this); 3 new tests.
- `docs/DATA_ANALYSIS.md` §5 — case 3 amended, "Two guards" subsection added.
- `docs/DECISIONS.md` — ADR-037 (amends ADR-018). `docs/STATE.md` — step-log line.
- `draft/fix-import-watched-before-air.sql` (repair script), `draft/dbq.py` (read-only query helper),
  `draft/test_fix_sql.py` (applies the script to a scratch copy and asserts invariants).

**Verification.**
```
> dotnet test Shows.Tests --filter "FullyQualifiedName~PromoterTests"
Passed!  - Failed: 0, Passed: 15, Skipped: 0, Total: 15

> # both guards temporarily neutralised (branch-3 gate removed, withhold predicate `false &&`)
Failed!  - Failed: 3, Passed: 12, Total: 15     # each new test fails without the fix

> dotnet test Shows.Tests
Passed!  - Failed: 0, Passed: 356, Skipped: 0, Total: 356

> python draft\test_fix_sql.py                  # script applied to a consistent copy
STEP 1a deletes 11 rows (Blade Runner 2099 S1E1-E8, Neuromancer S1E1, Silo S4E1, Yellowjackets S4E1)
STEP 1b leaves 3 for manual review (Dark Matter S2E6/S2E7, Dexter: Resurrection S2E1)
import-era marks still on NEVER-AIRED episodes: 0
watched_regular disagreeing with a recount: 0
total_regular disagreeing with a recount: 0
auto_finish_eligible disagreeing: 0
marks on non-counted episodes lost: 0
has_unwatched_specials disagreeing: 0
episode_watch rows: 10945 -> 10934 (deleted 11)
```

**New facts learned**
- **`totalEpisodes` from myshows counts only episodes myshows has seen**, so it is not the size of the
  TVmaze catalog. Measured: `Шершни` 29 vs 30 counted, `Укрытие` 30 vs 31, `Темная материя` 14 vs 19,
  `Декстер: Воскрешение` 10 vs 11. This is the mechanism behind the bug, and §5 did not say it.
- Only two staging rows have `total_episodes = 0 AND watched_episodes = 0`: 77831 (Бегущий по лезвию
  2099, `later.json`, 8 counted episodes all future) and 88646 (Нейромант, `watching.json`, 1 counted
  episode, 2027-01-22). Both are `In Development` — not-yet-airing shows, which is exactly when a 0/0
  show has a populated catalog.
- `max(staging_show.promoted_at)` = 2026-09-29 21:43:57+00:00; the last import mark is 21:43:56 and the
  first UI mark 2026-09-30 14:07, so `watched_at <= max(promoted_at)` cleanly separates import marks
  from the user's own. All `watched_at` share one `+00:00` ISO format, so TEXT comparison is sound.
- All 547 staging rows are promoted and 0 are unpromoted, so `--promote` will not run again here — the
  code fix is prevention for a future import, and the SQL script is the actual repair.

**Deviations.** The repair script deletes only the *provably* wrong marks (episode still unaired as of
today). Marks on episodes that aired between the import and today are ambiguous — the user may have
watched them since, and the UI would never have corrected a row that already read "watched" — so they
are listed for manual review rather than deleted. `--promote` is one-shot, so no code path can re-create
the bad rows; no migration was needed because no schema changed.

**Unblocked.** Nothing was blocked. A future re-import (a fresh `--reset --stage` + `--promote`) now
reconstructs watch state without inventing marks.

---

### 2026-10-07 — Improvement 21 — A show's displayed country falls back to its language

**Request.** User: for shows without country where language is filled, set country = language so it
displays everywhere as country — tracked shows, search, discovery. Example payload: tvmaze 84377
("Dead-End Job"), `language: "Korean"`, `network: null`, `webChannel: Netflix` with
`webChannel.country: null`.

**Files changed:**
- `Shows.Domain/CountryRules.cs` (new — the one rule, `Display(name, code, language)`),
  `Shows.Domain/TrackedShow.cs` (+`Country`).
- `Shows.Infrastructure/Persistence/Sources/TvmazeShow.cs` (+verbatim `CountryName`/`CountryCode`);
  `Persistence/Migrations/20261007135140_0010_ShowCountry.cs` (+Designer, +ModelSnapshot) —
  **schema-only**, three nullable TEXT columns, no data backfill.
- `Shows.Infrastructure/Tvmaze/TvmazeJsonMapper.cs` (+`ReadBroadcasterCountry`, `DisplayCountry`;
  `MapShow` stores verbatim per R11, `MapCatalogShow` applies the rule).
- `Shows.Infrastructure/Persistence/TvmazeShowMapper.cs` (`ApplyShowFields` fills `country` through
  the rule; new `RemapCountryFromStoredPayloadAsync` re-derives from `tvmaze_show.raw_json`).
- `Shows.Infrastructure/Jobs/CountryBackfillJob.cs` (new; lease `enrichment_country`, job_run
  `Enrichment.Country`), `Jobs/EnrichmentJob.cs` (`--country`), `Jobs/JobRunner.cs` +
  `Shows.Application/Interfaces/IJobRunner.cs` (`JobKind.EnrichCountry`),
  `ShowsInfrastructureServiceCollectionExtensions.cs` (registration).
- `Shows.Infrastructure/Discovery/ScheduleParser.cs` (facet built from `MapCatalogShow`'s country).
- `Shows.App.Avalonia/ViewModels/ManageViewModel.cs` ("Re-derive countries"),
  `ShowDetailsViewModel.cs` (country beside the broadcaster), `TrackedShowCardViewModel.cs`
  (meta line + signature).
- `Shows.Application/Catalog/CatalogShow.cs` (doc comment only).
- Tests: `Unit/CountryRulesTests.cs` (new, 9), `Unit/TvmazeJsonMapperCountryTests.cs` (new, 9),
  `Integration/CountryBackfillJobTests.cs` (new, 8), `Integration/TvmazeShowMapperTests.cs` (+4,
  `BlackMirrorPayload` gained a `language` param), `Unit/DiscoveryIndexTests.cs` (+4, fixture gained
  `language`), `Architecture/LayerBoundaryTests.cs` (rule-8 list += `CountryBackfillJob`).
- Docs: `SCHEMA.md` v1.5 (§2/§3 blocks), `SPEC.md` v1.5 (**R15** + field-table row),
  `DECISIONS.md` (ADR-036), `STATE.md`, `user-issues.md` (improvement 21), `tasks/` file.

**Verification — migration.**

```
> dotnet ef migrations add 0010_ShowCountry --project Shows.Infrastructure --startup-project Shows.Infrastructure
Done. To undo this action, use 'dotnet ef migrations remove 0010_ShowCountry'
```

File `20261007135140_0010_ShowCountry.cs`: three `AddColumn<string>` (nullable TEXT), `Down` drops
them. The scaffolded `Up` was left exactly as generated — no `ExecuteSql` backfill was added, on
purpose (see Deviation 1).

**Verification — build.**

```
> dotnet build Shows.sln -v q --nologo
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

**Verification — tests.**

```
> dotnet test Shows.Tests --nologo -v q
Passed!  - Failed: 0, Passed: 353, Skipped: 0, Total: 353, Duration: 10 s - Shows.Tests.dll (net8.0)
```

320 → 353 (+33). The user's exact payload is a fixture in `TvmazeJsonMapperCountryTests`: `MapShow`
keeps `CountryName`/`CountryCode` null (verbatim) while `MapCatalogShow` yields "Korean".
`CountryBackfillJobTests` prove the fallback reaches `tracked_show.country`, a re-run reports 0
changed, a soft-deleted show is never rewritten, unparseable `raw_json` keeps the existing value,
a refused lease writes nothing, and the `JobRun` row is recorded.

**Deviation 1 — no data backfill in the migration.** The plan's schema-change convention (SCHEMA +
migration in one step) covers DDL only. A raw `UPDATE tracked_show SET country = COALESCE(...)` would
make the migration a second writer of mapped rows, violating SPEC P3/R11. Instead the backfill is a
job (`Enrichment --country`) that re-parses `tvmaze_show.raw_json` through the mapper — no network,
idempotent, and it goes through the same `Set`-churn machinery as every other mapped write.

**Deviation 2 — the discovery facet reads the mapped value, not its own parse.** The first draft of
the `ScheduleParser` change called `ReadBroadcasterCountry` + `CountryRules.Display` a second time;
replaced with `catalogShow.CountryName` from the `MapCatalogShow` call the parser already makes, so
facet and card provably cannot disagree.

**New facts learned:**
- `multi_replace_string_in_file` can silently drop a helper that the replaced block still calls
  (the `CountryObject` fixture helper) — the compile error surfaced only at `dotnet test`. Grep for
  the removed symbol's callers after replacing a block that defines one.
- The first draft of `RemapCountryFromStoredPayloadAsync` assumed the new source columns were already
  populated for existing rows; they are not (they are new), so the remap must re-read the nested
  country out of `raw_json` — which is precisely why `raw_json` exists (SCHEMA §3).
- `EnsureCreated()` in `DatabaseFixture` builds test DBs from the model, so the new columns exist in
  tests without applying migration 0010 — the migration is only exercised against the real DB.

**Unblocked / user action:** existing databases need migration 0010 (applied automatically on next app
start or job run) **plus one run of "Re-derive countries"** (Manage page, or
`dotnet run --project Shows.Jobs.Enrichment -- --country`) to populate `country` for the 547 existing
rows. New fetches get it automatically through the mapper.

### 2026-10-07 — UI fix — Discovery: the type filter now survives a bucket switch

**Request.** User report: switching the Discovery bucket reset the type dropdown to `All` instead of
returning to the `Scripted` default the 2026-10-06 fix promised.

**Root cause.** Avalonia 11.3.2 resolves `SelectingItemsControl.SelectedItem` by **instance**, not by
value. `ResetFiltersForBucketSwitch` assigned a freshly minted `new FacetOption("Scripted", 0)`, and
`FacetOption` has no value equality — so the ComboBox could not find that object in its `ItemsSource`,
coerced its index to -1 and its selection to `null`, and (because `SelectedItem` is TwoWay-bound by
default) **wrote the null back into the ViewModel**. The reload that followed queried every type, and the
dropdown settled on the "All" entry. The code that set the filter kept believing it had applied, which is
why the bug was invisible in the ViewModel: only the control knew.

**Change.** `Shows.App.Avalonia/ViewModels/DiscoveryViewModel.cs` — the only file changed.
- The constructor seeds the default **into** `TypeOptions` (`DefaultTypeOption()`) rather than holding a
  detached option the dropdown can never select.
- `ResetFiltersForBucketSwitch` selects the in-list instance through the same helper.
- `RebuildFacets` gained an optional `defaultValue` (used by the type facet only), so a type value that
  vanished from a page's options falls back to `Scripted`, never to "All".
- `EnsureFacetOption` — by-value, case-insensitive lookup over the bound list; appends a count-0 option
  when the bucket offers no such type, so the box can still show what the query is filtering.
- `RebuildFacets` now inserts a new option at its **source position** instead of appending. Consequence of
  the ctor seed: with "Scripted" already at index 0, appending put "All" second and kept it there.
- Class remarks state the invariant every selection here obeys: a value assigned to a `Selected*` property
  must already be an element of that dropdown's options collection.

**Verification.**
- `dotnet build Shows.sln --nologo -v m` → `Build succeeded. / 0 Warning(s) / 0 Error(s)`.
- `dotnet test Shows.Tests --no-build --nologo` → `Passed! - Failed: 0, Passed: 320, Skipped: 0,
  Total: 320, Duration: 10 s`.
- Behaviour measured by a throwaway console harness (`c:\src\shows-probe`, outside the repo) that builds a
  real `ComboBox` with `ItemsSource = vm.TypeOptions` + a TwoWay `SelectedItem` binding against the real
  `DiscoveryViewModel` and a synchronous fake `IDiscoverySource` logging the filter of every query:
  - detached option, as the old code did: `VM SelectedType = (null)`, `ComboBox = (null)` — **bug reproduced**;
  - in-list instance: `VM SelectedType = Scripted (0)`, `ComboBox = Scripted (0)` — **fix confirmed**;
  - first paint (`InitializeAsync`): `TypeOptions = [All (10), Scripted (6), Documentary (4)]`,
    `SelectedType = Scripted (6)`, `combo idx 1`;
  - Airing → `source saw Airing: ShowType=Scripted`, dropdown `Scripted (6)`;
  - Upcoming (bucket with **no** scripted show) → `ShowType=Scripted`, option appended as `Scripted (0)`
    and still selected, `StatusMessage = Nothing to show for this filter.`;
  - back to New → `ShowType=Scripted`, dropdown `Scripted (6)`.

**New facts learned** (measured on the raw control, `ObservableCollection<FacetOption>`, no VM involved):
- assigning an equal-by-value but different instance → `SelectedIndex -1`, `SelectedItem null`;
- `RemoveAt` of the selected item → the same null (this is why `RebuildFacets` must re-select after the
  type option is dropped and re-added, and why that window runs under `_rebuildingFacets`);
- `Insert(0, x)` under a live selection → selection follows the item (idx 0 → 1, same instance);
- `Move(2, 0)` across a live selection → likewise follows it (idx 1 → 2, same instance). So reordering a
  bound facet list is safe; `Clear()` + re-add is not.
- A source that completes synchronously makes `OnSelectedTypeChanged` run a whole `ReloadAsync` inside the
  setter, and `RebuildFacets` silently restores the selection — a test that does not neutralize this
  measures the rebuild, not the binding. The harness had to fail the source to see the null.

**Deviation.** No regression test added: `Shows.Tests` deliberately never references the UI assembly (see
the `LayerBoundaryTests` class remark), so the evidence is the out-of-repo harness above. Delete it with
`Remove-Item -Recurse -Force c:\src\shows-probe`.

**Also observed, deliberately not fixed.** Compiling `Shows.Infrastructure` from outside the solution
reports 55 `CA1873` and 9 `IDE0161` as **errors** (`Directory.Build.props` sets `TreatWarningsAsErrors`,
`AnalysisLevel=latest-all`, `EnforceCodeStyleInBuild`). The in-repo solution build is clean only because
those files are not recompiling; a `dotnet clean` or fresh clone would fail. Pre-existing, unrelated to
this fix, and left alone — but whoever next does a clean build should expect it.

**Unblocks.** Nothing new; the manual check in the app is now a confirmation, not an investigation.

### 2026-10-07 — Step 8.4 — Database-backed Discovery schedule archive

**Request.** Move the retained raw `/schedule/full` archive from dated files into SQLite. The user chose
to start a fresh DB archive; existing `cache/discovery/schedule-*.json` files are neither imported nor
deleted.

**Change.** Added `discovery_schedule_archive`, keyed by the UTC capture date and containing the raw JSON
body, in migration `0009_DiscoveryScheduleArchive`. `ScheduleArchive` now uses
`IDbContextFactory<ShowsDbContext>` and async SQLite operations. Same-day refresh is an atomic SQLite
upsert; promotion spacing, newest-first merge, and independent age-based pruning are unchanged. Removed
the obsolete `DiscoveryCacheDirectory` path and directory creation. Updated schema/architecture docs,
added ADR-035, and updated the state and implementation plan.

**Files changed.** New: `Shows.Infrastructure/Persistence/DiscoveryScheduleArchiveEntry.cs`,
`Shows.Infrastructure/Persistence/Migrations/20261006223957_0009_DiscoveryScheduleArchive.cs` and its
designer. Modified: `Discovery/ScheduleArchive.cs`, `Discovery/ScheduleDiscoverySource.cs`,
`Persistence/ShowsDbContext.cs`, `ShowsInfrastructureServiceCollectionExtensions.cs`,
`Configuration/DataPaths.cs`, `Shows.Tests/Unit/ScheduleArchiveTests.cs`,
`Shows.Tests/Integration/MainSchemaTests.cs`, `Migrations/ShowsDbContextModelSnapshot.cs`,
`docs/SCHEMA.md`, `docs/ARCHITECTURE.md`, `docs/DECISIONS.md`, `docs/IMPLEMENTATION_PLAN.md`,
`docs/STATE.md`, and this log. Task: `tasks/done/8.4-database-schedule-archive.md`.

**Verification.**
- `dotnet ef migrations add 0009_DiscoveryScheduleArchive --project Shows.Infrastructure --startup-project Shows.Infrastructure`
  → `Build succeeded. Done.`
- `dotnet ef migrations has-pending-model-changes --project Shows.Infrastructure --startup-project Shows.Infrastructure`
  → `No changes have been made to the model since the last migration.`
- `dotnet test Shows.Tests --filter "FullyQualifiedName~ScheduleArchiveTests"` →
  `Passed! - Failed: 0, Passed: 18, Skipped: 0, Total: 18`.
- `dotnet test Shows.Tests` → `Passed! - Failed: 0, Passed: 320, Skipped: 0, Total: 320`.
- `dotnet build Shows.sln` → `Build succeeded. 0 Warning(s) 0 Error(s)`.

**Deviations.** The first full-suite run exposed the schema-test allow-list's assumption that the new
date-keyed natural `TEXT` primary key was a generated id. Added `fetched_on` to its natural-text-key
allow-list and documented that it is not a generated id; the follow-up full suite passed. The fresh
archive/no-import behavior follows the user's selection.

**New facts learned.** The archive now participates in the SQLite database backup. The new natural key
does not use a surrogate id, and the migration is reflected in the current model snapshot.

**Unblocked.** Discovery look-back captures now persist with the main database and use the existing
migration/startup path. Its history starts fresh; old cache files are left alone.

### 2026-10-07 — Discovery raw-snapshot archive (the window had no past to look back into)

**Request.** The user noticed the look-back window "is not working properly" and proposed a layered cache
of older `/schedule/full` responses. Their premise was correct and is now a recorded fact: the endpoint is
**forward-only**, so `windowStart` has nothing below today to match.

**Verified premise** (streamed the saved 14 MB `schedule-full.json`, min/max over every `airdate`):
**7,338 dated episodes, min `2026-09-28`, max `2028-03-05`** — 2026-09-28 being the capture date. So the
horizon is ~17 months ahead and ~zero behind. A show whose season finished is not filtered out of the
response, it is **absent** from it; no live query can recover it. Recorded as `DATA_ANALYSIS.md` §8 fact 17.

**Design decisions (three, each a user steer).**
1. **Store raw, not the parsed model.** The user rejected an earlier proposal to persist the Discovery
   read model, correctly: `CatalogShow` is a moving target (it gained `OfficialSite`, `PremiereDate`,
   `NextEpisode`), so persisting it freezes a stale shape with no source left to re-derive from — the
   inversion of SPEC P3 / ADR-022 (store raw, derive through one mapper). The archive keeps the endpoint's
   own JSON and re-parses it through today's `ScheduleParser`, which keeps model changes free.
2. **Age-triggered cascade, not daily files.** The newest capture is "layer 1" and simply **ages**; only
   when it reaches `ArchiveLayerDays` does a fetch start a new one, so layer 1 spans 0–14 days and layer 2
   14–28 at any moment. With date-named files promotion is implicit — a file needs no renaming to grow
   old. Exact ages float because the fetch cadence is whatever the user runs; that is inherent to a desktop
   app and accepted rather than papered over. ~5 files instead of 30.
3. **Retention is its own knob.** The user asked for a separate max-layer-age setting rather than deriving
   it from the window, and they are right: how far back the buckets look and how much history is worth the
   disk are unrelated decisions. Deriving one from the other means widening the window silently destroys
   history, and a window wider than the retained span under-reports silently. So `ArchiveMaxAgeDays`
   prunes purely by age, and the source only **warns** (once per process) when
   `ArchiveMaxAgeDays <= NewShowsWindowDays` — it never clamps, because the fix belongs in configuration.

**Change.** New `Discovery/ScheduleArchive.cs` (dated raw captures in `cache/discovery/`: list / read /
archive-if-aged / prune-by-age) and `Discovery/ScheduleMerge.cs` (pure union: newest layer wins show
metadata wholesale; episodes keyed on `(season, number)` take the **newest non-null** air date, so a
rescheduled episode does not keep a slot the network abandoned, while a newer *null* does not erase a known
older date — TVmaze omits more often than it withdraws). `ScheduleDiscoverySource` gains a
`BuildSnapshot` step that archives, prunes, then folds every retained older layer into the fresh parse —
on **refresh**, not per query, which is what makes re-parsing a few 14 MB layers affordable. Archive is
live-mode only: dating a static local sample "today" would misrepresent its age and poison the chronology.
Config `Discovery:ArchiveLayerDays` (14) + `ArchiveMaxAgeDays` (60); `ScheduleArchive` registered as a
singleton beside the source.

**Files changed.** New: `Shows.Infrastructure/Discovery/ScheduleArchive.cs`,
`Discovery/ScheduleMerge.cs`, `Shows.Tests/Unit/ScheduleArchiveTests.cs`. Modified:
`Discovery/ScheduleDiscoverySource.cs`, `Configuration/ShowsOptions.cs`, `appsettings.json`,
`ShowsInfrastructureServiceCollectionExtensions.cs`; docs `DATA_ANALYSIS.md` (§8 fact 17),
`ARCHITECTURE.md` (§4 JSON + new §4.1), `SPEC.md` (F3 note).

**Verification.**
- `dotnet build Shows.sln` → `Build succeeded. 0 Warning(s) 0 Error(s)`.
- `dotnet test Shows.Tests --filter "FullyQualifiedName~ScheduleArchiveTests"` →
  `Passed! - Failed: 0, Passed: 18, Skipped: 0, Total: 18`.
- `dotnet test Shows.Tests` → `Passed! - Failed: 0, Passed: 320, Skipped: 0, Total: 320` (was 302; +18).
- Tests cover: promotion at exactly the interval, no promotion while layer 1 ages (13 days), same-day
  refresh without duplication, newest-first listing ignoring foreign files, prune by age with the
  boundary case, prune disabled at 0, **prune ignoring the window entirely** (the coupling the user
  rejected), and the merge rules — recovered show, moved air date, newer-null-keeps-older, metadata from
  the current layer, oldest-first application across three layers, plus an end-to-end check that a show
  absent from today's schedule reaches the New bucket only because a layer carried it.

**Deviations.** The user's original step 4 ("after setting up a layer with age > window days, next layers
can be deleted") was implemented as an independent age ceiling instead of window-derived deletion, at their
explicit follow-up request. Their step 5 ("current + oldest layer") is superseded by folding **all**
retained layers, which is what makes the intermediate layers useful for current-vs-previous comparison —
the reason they kept them.

**New facts learned.** `/schedule/full` carries no past at all (§8 fact 17). The long forward horizon means
one old capture spans a month-scale window, so the layer count is driven by reliability of near-term
scheduling and diagnostic value, not by coverage arithmetic.

**Unblocked.** New/Airing can show recently-aired shows once captures have accumulated. **Cold start is
unavoidable**: the window fills only as old as the oldest capture, so it reaches its full 45 days ~45 days
after the first run.

### 2026-10-06 — Discovery bucket rules corrected + filters reset on bucket switch (user fix)

**Request.** Two Discovery-tab fixes from the user:
1. **Bucket rules.** *New* = any show with a **season 1** episode whose air date is after `windowStart`
   (not only `S01E01`, and no upper bound at today); *Airing* = any show with **any episode** (any season)
   after `windowStart` (not only a later-season premiere); *Upcoming* unchanged (a future-dated season
   premiere).
2. **Filter bug.** Selecting a filter then switching to a bucket where that value has no option showed
   "All" in that dropdown while the *other* facets still zeroed the result — an empty view with a
   misleading "All". Fix: reset all filters on bucket switch, keeping `Scripted` as the default type.

**Change.**
- `DiscoveryIndex.InBucket`: `New` is now `e.Episodes.Any(ep => ep.Season == 1 && ep.AirDate > windowStart)`
  and `Airing` is `e.Episodes.Any(ep => ep.AirDate > windowStart)` — both a strict `> windowStart` with no
  upper bound, so New ⊂ Airing by construction. `Upcoming` untouched. The old rules required the earliest
  S01E01 / earliest season>1 premiere to fall inside `[windowStart, today]`, which dropped shows whose
  season-1 run or later episodes were recent.
- `DiscoveryViewModel`: `OnSelectedBucketChanged` now calls a new `ResetFiltersForBucketSwitch()` before
  reloading — name cleared, genre/network/country nulled (they fall back to "All" on the next reload), and
  type restored to the `Scripted` default. The reset runs under `_rebuildingFacets` so the facet setters do
  not each fire a reload (the switch issues the single reload). Because the name `TextBox` is TwoWay-bound,
  clearing `NameFilter` writes `""` back through the binding and fires the page's `TextChanged` →
  `ApplyNameFilter`; a new `_suppressNameDebounce` flag makes that write-back a no-op so the switch does
  not start a competing debounced reload.
- Doc comments on `DiscoveryBucket` (Application) and `DiscoveryOptions.NewShowsWindowDays`
  (Infrastructure) retargeted to the new rules. `SPEC.md` F3 (which owns the bucket requirements) rewritten
  from the stale "two feeds /newshows + /upcoming" to the three buckets with the corrected definitions.

**Files changed.** `Shows.Infrastructure/Discovery/DiscoveryIndex.cs`,
`Shows.App.Avalonia/ViewModels/DiscoveryViewModel.cs`, `Shows.Application/Catalog/DiscoveryModels.cs`,
`Shows.Infrastructure/Configuration/ShowsOptions.cs`, `Shows.Tests/Unit/DiscoveryIndexTests.cs`,
`docs/SPEC.md` (F3).

**Verification.**
- `dotnet build Shows.sln` → `Build succeeded. 0 Warning(s) 0 Error(s)`.
- `dotnet test Shows.Tests --filter "FullyQualifiedName~Discovery"` →
  `Passed! - Failed: 0, Passed: 24, Skipped: 0, Total: 24`.
- `dotnet test Shows.Tests` → `Passed! - Failed: 0, Passed: 302, Skipped: 0, Total: 302`. (One earlier
  full-suite run showed `ShowDetailsReadTests.SetSeasonExpanded_...` failing; it passes in isolation and on
  the clean re-run — a pre-existing flake in the shared temp-DB fixture under parallel xUnit collections,
  unrelated to Discovery.)
- Retargeted tests: `New_bucket_includes_a_show_whose_season_one_premiere_is_old_but_a_later_episode_is_recent`
  (S01E01 2026-05-01 + S01E05 2026-09-15 → New), `New_bucket_includes_a_show_whose_season_one_airs_in_the_future`
  (S01E01 2026-12-01 → New, no upper bound), `Airing_bucket_includes_a_season_one_premiere` (New ⊂ Airing),
  `Airing_bucket_excludes_a_show_whose_episodes_all_predate_the_window`.

**Deviations.** None. The user's "air date > windowStart" was read literally as a strict lower bound with no
upper bound (so a first season airing entirely in the future is New, and any show with a future episode is
Airing) — consistent with "Upcoming: leave as it is" keeping the buckets as overlapping views.

**New facts learned.** The old Airing rule (`season > 1` premiere inside the window) made New and Airing
disjoint; the corrected rules make New a strict subset of Airing, which is what the switcher presents.

**Unblocked.** The Discovery buckets now match the user's mental model; switching buckets never leaves a
stale filter that empties the view.

### 2026-10-04 — RefreshUpdated job (bulk /updates/shows diff)

**Request.** Add one more job, `RefreshUpdated`, plus a console project and a Manage-tab button. It should
query TVmaze's `/updates/shows` endpoint once (a large `{ tvmazeId: updatedUnixSeconds }` map for every show
on the site) and re-fetch/re-map **only** the tracked shows whose provider timestamp moved — the cheap
alternative to `EpisodeRefresh`'s "refresh everything airing" sweep.

**Design.** One bulk fetch + one re-fetch per genuinely-changed show. The stored `tvmaze_show.provider_updated`
(already mapped from the API's `updated` field by `TvmazeJsonMapper`) is the comparison baseline; the mapper's
`MapShowAsync(int)` re-fetch stamps it to the new value, so a second run sees `map == stored` and skips —
`items_changed` reports 0 on a re-run (idempotence).

**Source-table containment (SPEC R11 / step 8.1).** A job may not read `tvmaze_show` directly, so the stored
timestamp is read through a new mapper method `TvmazeShowMapper.GetProviderUpdatedAsync(int)` — the only
source-column read exposed to a job. The bulk endpoint itself is a provider read (`ICatalogProvider`), not a
source-table read.

**R14.** The bulk map is the one whole-run network op: an unreachable TVmaze aborts the run with
`ProviderUnavailable` (exit 1) and fetches no per-show payload — never a clean run that refreshed nothing. A
per-show re-fetch failure is a per-show failure (left for the next run), never a crash.

**Files changed.** `Shows.Application/Catalog/ICatalogProvider.cs` (new
`GetShowUpdateTimestampsAsync`), `Shows.Infrastructure/Tvmaze/TvmazeCatalogProvider.cs` (parses the map,
`allowNotFound: false`), `Shows.Infrastructure/Persistence/TvmazeShowMapper.cs`
(`GetProviderUpdatedAsync`), `Shows.Infrastructure/Jobs/RefreshUpdatedJob.cs` (the `RefreshUpdatedShowJob`
body, lease-guarded), `Shows.Infrastructure/Jobs/RefreshJobs.cs` (the `RefreshUpdatedJob` entry point),
`Shows.Infrastructure/Jobs/JobRunner.cs` + `Shows.Application/Interfaces/IJobRunner.cs` (`JobKind.RefreshUpdated`),
`Shows.Infrastructure/ShowsInfrastructureServiceCollectionExtensions.cs` (DI),
`Shows.App.Avalonia/ViewModels/ManageViewModel.cs` (Maintenance-group button),
`Shows.Jobs.RefreshUpdated/` (new console shell, `AssemblyName=RefreshUpdated`), `Shows.sln`,
`publish-all.ps1`, `Shows.Tests/Architecture/LayerBoundaryTests.cs` (rule 8 bulk-writer list),
and the 9 `ICatalogProvider` test fakes (new member).

**Tests.** `RefreshUpdatedJobTests` (8 tests, fake provider + fake `ITvmazeShowSource`, temp DB, no network):
refreshes only newer-timestamp shows; second run refreshes nothing once the stored stamp caught up; a never-
stamped show is refreshed when the map has it; a soft-deleted show is never refreshed (R8); an unreachable
provider aborts without fetching (R14); a per-show fetch failure is a failure not a crash; refuses on a live
lease before the bulk fetch; writes the `job_run` row. Two provider tests parse the map (skipping a malformed
key and a null value) and assert R14 on transport failure.

**Verification.** `dotnet build Shows.sln` → `Build succeeded. 0 Warning(s) 0 Error(s)`.
`dotnet test Shows.Tests` → `Passed! - Failed: 0, Passed: 297, Skipped: 0, Total: 297` (was 285; +10 new).

**New facts.** TVmaze `/updates/shows` returns `{ "<id>": <unixSeconds> }` keyed by TVmaze id for every show
on the site — large, so fetched once and cached, never per show. `tvmaze_show.provider_updated` was already
populated from the payload's `updated` field; nothing new needed storing.

**Unblocked.** A targeted refresh path drivable from the console (`dotnet run --project
Shows.Jobs.RefreshUpdated`) and the Manage tab.

### 2026-10-02 — Specials split into significant / insignificant (ADR-034)

**Symptom (user report).** On the details page, specials rendered inside the season grid as a bare
`S2` with no episode number, and the dedicated Specials section at the bottom was empty even though
the model had rows for it. The user suspected specials were being counted as regulars, which could
corrupt the finished status.

**Root cause.** TVmaze returns specials as `episode.type == "significant_special"` or
`"insignificant_special"`, but `TvmazeVocabulary.EpisodeTypeMap` only knew `"regular"` and `"special"`.
Both special kinds therefore mapped to `EpisodeType.Unknown`. Because the UI's "pull specials out of
the grid" filter tested `== EpisodeType.Special`, Unknown specials were never pulled out (they showed
as `S2`), and the bottom section (which tested the same flag) stayed empty. Separately,
`ShowProgress.Compute` counted only `Regular`, so specials were excluded from progress — matching the
old ADR-013 by accident, but meaning a *significant* special (a real episode) never counted toward
finishing.

**Fix (ADR-034, supersedes ADR-013).** `EpisodeType` gains `InsignificantSpecial = 3`; `Special = 2`
is redefined as the *significant* kind and now counts toward progress like a regular. Vocabulary maps
`significant_special → Special`, `insignificant_special → InsignificantSpecial`, bare `special →
InsignificantSpecial`. Every `== Special` / `!= Special` predicate was audited and re-expressed against
the counted set (`Regular or Special`): `ShowProgress.Compute`, `InlineEpisodeRules`, the two
`EpisodeRepository` LINQ filters and the raw-SQL count (`episode_type_id <> 2` → `<> 3`), the
`Promoter` backfill frontier, and the `ShowDetailsViewModel` grid/section partition.
`EpisodeRowViewModel.NumberText` now renders each kind distinctly: `S02E05` (regular), `S02E05 ★`
(significant), `★` (insignificant), `? S02E05` (unknown).

**Files changed.** `Shows.Domain/EpisodeType.cs`, `Shows.Domain/ShowProgress.cs`,
`Shows.Domain/InlineEpisodeRules.cs`,
`Shows.Infrastructure/Persistence/Vocabulary/TvmazeVocabulary.cs`,
`Shows.Infrastructure/Persistence/Repositories/EpisodeRepository.cs`,
`Shows.Infrastructure/Import/Promoter.cs`,
`Shows.Application/UseCases/GetCatalogDetailsUseCase.cs`,
`Shows.App.Avalonia/ViewModels/ShowDetailsViewModel.cs`,
`Shows.App.Avalonia/ViewModels/SeasonGroupViewModel.cs` (`EpisodeRowViewModel`),
plus tests (`ShowProgressTests`, `InlineEpisodeRulesTests`, `PromoterTests`, `ShowDetailsReadTests`)
and docs (`SPEC.md` R4/R12/§5, `SCHEMA.md` §1a + `type_raw`, `DATA_ANALYSIS.md` §8 fact 11,
`DECISIONS.md` ADR-034 + ADR-013 superseded, `STATE.md`).

**Verification.**
```
> dotnet build Shows.sln -v q
Build succeeded. 0 Warning(s) 0 Error(s)
> dotnet test Shows.Tests --no-build -v q
Passed!  - Failed: 0, Passed: 285, Skipped: 0, Total: 285
```
New unit coverage: significant specials count toward totals and block finishing when unwatched; a show
finished on regulars + significant specials with an unwatched insignificant special is eligible and
surfaces the note; insignificant specials are excluded and drive the note; significant specials are
inline-listable.

**Deviations.** None. No EF migration — `episode_type_id` is an unconstrained integer, so the new enum
member is a value-space change; stored rows are re-mapped from `tvmaze_episode.type_raw` on the next
`EpisodeRefresh` / `Enrichment` run.

**New facts learned.** TVmaze's two special kinds are load-bearing for progress (recorded in
DATA_ANALYSIS §8 fact 11). The bare `"special"` type string is not returned by TVmaze today; it is
kept in the vocabulary as a synonym for the excluded kind for legacy/fixture safety.

**Unblocked.** The finished-status correctness for shows with specials; the details page Specials
section now populates; specials are visually distinguishable by kind.

### 2026-10-02 — Avalonia crash on add-from-Discovery (off-UI-thread bound writes)

**Symptom (user report).** Adding a show to Started from the Discovery page crashed the app with **no log
line**; after a restart the show was present. A spinner showed briefly before the crash.

**Root cause.** `ShowCardViewModel.AddAsync` awaited the add use case with `.ConfigureAwait(false)`, so
after the DB write committed the continuation ran on a **thread-pool** thread and wrote the bound
properties `IsAlreadyTracked` / `StatusMessage` / `IsBusy`. In Avalonia, raising `PropertyChanged` for a
bound property off the UI thread throws "Call from invalid thread". Because the `catch` and `finally`
**also** write bound properties off-thread, that exception was itself unhandled on a background thread —
which terminates the process, and because it unwinds past the code that would have logged it, nothing
reached the file. The DB write had already committed, so the show appeared after the restart. (The
"Call from invalid thread" class was already fixed in `DiscoveryViewModel`/`SearchDetailsViewModel` but
never in the shared card.)

**Fix.** Dropped `ConfigureAwait(false)` on every UI-mutating continuation so it resumes on the Avalonia
UI context (the established pattern in `DiscoveryViewModel`):
- `ShowCardViewModel.AddAsync` (the reported crash — shared by Search **and** Discovery).
- `SearchViewModel.RunSearchAsync` (identical latent bug: `IsBusy`/`StatusMessage`/`Results` after the
  debounce + search awaits).
- `ShowDetailsViewModel`: `LoadAsync` (writes many bound scalars after the read — the force-reload from
  the akas/sync buttons runs while bound, so it would crash there), plus the `FetchLocalizedTitles`,
  `Sync`, `ChangeCategory`, and `Untrack` commands.
- `EpisodeRowViewModel.ToggleWatchedAsync` and `TrackedShowCardViewModel.LoadSeasonsAsync`: these already
  marshalled their collection writes via `MainThread.BeginInvokeOnMainThread`, but set `IsBusy`/`IsWatched`
  off-thread — moved those writes inside the posted UI-thread batch.

`TrackedShowCardViewModel`/`SeasonGroupViewModel`'s other `ConfigureAwait(false)` sites already marshal
correctly (pin toggle, lazy season load) and were left as-is. `TrackingViewModel.LoadCoreAsync` is
local-DB-only (SQLite completes synchronously, so no thread switch) and its threading was carefully tuned
for the memoization path — left unchanged.

**Hardening.** Added global `AppDomain.CurrentDomain.UnhandledException` and
`TaskScheduler.UnobservedTaskException` handlers in `Program.Main` (via a new `CrashLog` helper in
`Services/AppLog.cs`). A future background-thread crash is now written to the log before the process dies
instead of vanishing silently. They do not swallow the exception.

**Files changed.** `Shows.App.Avalonia`: `ViewModels/ShowCardViewModel.cs`, `ViewModels/SearchViewModel.cs`,
`ViewModels/ShowDetailsViewModel.cs`, `ViewModels/SeasonGroupViewModel.cs`,
`ViewModels/TrackedShowCardViewModel.cs`, `Program.cs`, `Services/AppLog.cs`.

**Verification.** `dotnet build Shows.sln` → 0 warnings, 0 errors. `dotnet test Shows.Tests` →
`Passed! - Failed: 0, Passed: 281`. (The bug is a UI-thread race not reachable from the net8.0 test host,
so it is verified by the build + the reasoning above, not a new automated test — the same as the earlier
"Call from invalid thread" fix.)

**New facts learned.** Avalonia (unlike MAUI) does **not** auto-marshal `PropertyChanged` to the UI
thread, so any `ConfigureAwait(false)` continuation that writes a bound property is a latent crash. The
danger is highest where the `catch`/`finally` also touch bound properties — the crash then happens inside
the handler and is never logged.

**Unblocked.** Add-from-Discovery/Search no longer crashes; the details-page akas/sync buttons and the
episode watch toggle are likewise safe.

### 2026-10-02 — user-issues improvements 16 and 20 (the last two open items)

**Change.** The final two open items in `docs/user-issues.md`.

- **Improvement 16 — time-based HTTP-cache fallback for validator-less responses.** `HttpCacheStore`
  always revalidated a cached entry with a conditional GET, but a response carrying **neither an ETag nor
  a Last-Modified** has no validator to revalidate against, so every read refetched the whole body. Now a
  validator-less cached entry is served straight from the cache until it ages past
  `Shows:Cache:NoValidatorCacheMaxAgeHours` (default 24 = once per day), then refetched. A response that
  DOES carry a validator is untouched — it keeps the always-revalidate path (the step-3.1 assertion is
  preserved). The knob lives in a new `Cache` config section, deliberately separate from `Http` (which
  ARCHITECTURE §4 says is read in exactly one place — the handler wiring). `HttpCacheStore` now takes
  `IOptions<ShowsOptions>`; `CacheFetchResult` gained a `FromTimeCache` flag. A non-positive setting
  disables the reuse (the pre-improvement behavior).
- **Improvement 20 — TVmaze/IMDb links on the search-details page.** The untracked search-details page
  (`SearchDetailsView`) now shows the same TVmaze / IMDb buttons as the tracked-details page; the IMDb one
  hides when the show has no `tt` id. The URL formats moved out of the code constant into
  `Shows:Links:Tvmaze` / `Shows:Links:Imdb` in `appsettings` (`{0}` = the id). A new View-layer
  `ShowLinks` static helper is the single builder; the composition root hands it the configured formats
  via `ShowLinks.Configure` (the same pattern as `ImageBehavior.Configure`, so no View/ViewModel names an
  Infrastructure type — ARCHITECTURE §2). `ShowDetailsViewModel` was refactored to build its links through
  `ShowLinks` too, so both pages share one source of truth (SPEC P5 — the URL is a function of the id).

**Files changed.** Infrastructure: `Configuration/ShowsOptions.cs` (`CacheOptions`, `LinkOptions`),
`appsettings.json` (`Cache`, `Links`), `Tvmaze/HttpCacheStore.cs` (time-based fallback, `FromTimeCache`).
UI: `Services/ShowLinks.cs` (new), `Program.cs` (`ShowLinks.Configure`),
`ViewModels/SearchDetailsViewModel.cs` (link properties + open commands),
`ViewModels/ShowDetailsViewModel.cs` (links now via `ShowLinks`), `Views/SearchDetailsView.axaml`
(link buttons). Docs: `ARCHITECTURE.md` §4 (config keys) + §5 (http_cache note). Tests:
`Integration/TvmazeCatalogProviderTests.cs` (+3 cache tests, `CreateProvider` passes `IOptions`).

**Verification.** `dotnet build Shows.sln -clp:ErrorsOnly` → `Build succeeded. 0 Warning(s) 0 Error(s)`.
`dotnet test Shows.Tests` → `Passed! - Failed: 0, Passed: 281, Skipped: 0, Total: 281` (was 278; +3 cache
tests). The three new tests pin the improvement-16 behavior: a validator-less entry is served with no
second request; it is refetched once backdated past 24 h; a validated (ETag) entry is never served from
the time cache (still hits the network).

**Deviations.** None. The `Cache` section is new (not folded into `Http`) to honor the §4 single-reader
rule for `Http`/`Proxy`.

**New facts learned.** The `http_cache` store had no notion of freshness at all — every read was a
conditional GET — so a validator-less endpoint (TVmaze returns none on some responses) was refetched in
full on every call. Improvement 16 adds the only time-based path in the cache.

**Unblocked.** `docs/user-issues.md` is fully closed; Phase 8 (8.2/8.3, postponed) can resume.

### 2026-10-01 — Improvement 19 follow-up 2: cache-first details, episodes, network/country, filter UI

**Change.** Further user testing of Discovery.
- **Details hung / showed no data** (and a stray loading bar): the untracked-details page always re-fetched
  the show live from TVmaze, which is unreachable from the dev machine (DATA_ANALYSIS §9). A new
  `GetCatalogDetailsUseCase` is **cache-first**: a Discovery show is served entirely from the cached
  `/schedule/full` snapshot (no network, no spinner), and only a Search hit — absent from the snapshot —
  falls back to a live `GetShowAsync` + `GetEpisodesAsync`. `IDiscoverySource` gained `TryGetCached` (a
  pure warm-snapshot read that never fetches). `SearchDetailsViewModel` now calls the new use case.
- **Episodes on the details page**: the same use case returns the show's episodes grouped by season — from
  the cached schedule for Discovery (the parser now carries each episode's title), from a live fetch for
  Search. Rendered as a read-only season grid (`CatalogSeasonViewModel`/`CatalogEpisodeViewModel`); an
  untracked show has no watch state, so there is no toggle.
- **Duplicated description** on the details page: the shared card already renders `DescriptionText`, so the
  page's separate `Card.Show.Overview` TextBlock was removed.
- **Network + country on the card**: `CatalogShow` gained `NetworkName`/`CountryName` (mapped from
  `network.name`/`webChannel.name` and the nested `country.name`), surfaced as the card's `NetworkText`.
- **Specials marker**: an episode row with no number (a special) now shows a star glyph instead of a bare
  `S`/`S0` — `EpisodeRowViewModel.SpecialGlyph`, reused by the untracked rows.
- **Filter UI**: the four facet dropdowns now fill the row (equal-width columns + `ColumnSpacing`, no
  per-item margins → no gaps), show a **"Not set"** placeholder when unset (an unset facet stays null
  rather than auto-selecting "All"), and the results list gained right padding so cards clear the scrollbar.
- **Upcoming rule** (user's own edit, kept): a show is Upcoming when a **season premiere** (episode number
  1) is in the future — not merely any future episode. The stale test encoding the old rule was replaced
  with two tests for the new semantics.

**Files changed.** Application: `Catalog/CatalogShow.cs` (+network/country), `Catalog/CatalogDetails.cs`
(new read models), `Catalog/IDiscoverySource.cs` (`TryGetCached`), `UseCases/GetCatalogDetailsUseCase.cs`
(new). Infrastructure: `Tvmaze/TvmazeJsonMapper.cs` (network/country), `Discovery/ScheduleParser.cs`
(episode title), `Discovery/ScheduleDiscoverySource.cs` (`TryGetCached`), DI registration. UI:
`ViewModels/SearchDetailsViewModel.cs`, `ViewModels/CatalogEpisodeViewModels.cs` (new),
`ViewModels/ShowCardViewModel.cs` (`NetworkText`), `ViewModels/SeasonGroupViewModel.cs` (special glyph),
`Views/SearchDetailsView.axaml`, `Views/ShowCardView.axaml`, `Views/DiscoveryView.axaml`. Tests:
`Integration/GetCatalogDetailsUseCaseTests.cs` (new), `Unit/DiscoveryIndexTests.cs` (Upcoming rule),
`Integration/GetDiscoveryPageUseCaseTests.cs` (fake implements `TryGetCached`).

**Verification.**
- `dotnet build Shows.sln` → `Build succeeded. 0 Warning(s) 0 Error(s)`.
- `dotnet test Shows.Tests` → `Passed! - Failed: 0, Passed: 278, Skipped: 0, Total: 278` (was 273; +4
  details-use-case tests, +1 Upcoming-rule test, −1 stale Upcoming test).

**Deviations.** `GetCatalogShowUseCase` is left in place (still registered, still tested) though the
details page no longer uses it — removing it is a separate cleanup; the new use case supersedes it for the
page. The details page keeps its live-fetch fallback for Search hits (SPEC R8a — a search hit is not in the
snapshot, so it must read live).

**Unblocked.** Discovery Details is instant and offline; both details pages list episodes; the card carries
network/country.

### 2026-10-01 — Improvement 19 follow-up: threading fix, facet dropdowns, type filter, card description

**Change.** User testing of the Discovery tab surfaced a crash and three refinements.
- **Threading crash** ("Call from invalid thread" when switching to Airing): `ScheduleDiscoverySource.GetPageAsync`
  ran the query under `await Task.Run(...)`, forcing a thread-pool hop; combined with `ConfigureAwait(false)` in
  `DiscoveryViewModel`, the continuation mutated the bound `Results` `ObservableCollection` off the UI thread.
  Fix: the query now runs **inline** (an in-memory pass over ~10k shows is sub-millisecond — no reason to leave
  the caller's thread), and every `ConfigureAwait(false)` is removed from the VM so continuations resume on the
  Avalonia UI context. Only the cold snapshot fill (file/HTTP read) yields.
- **Facet dropdowns with counts**: genre/network/country were free-text boxes; they are now `ComboBox`es whose
  options carry a `(count)` and an `All (N)` entry. `DiscoveryIndex` builds each facet's counts respecting every
  OTHER active filter (standard faceted search), so selecting a genre never zeroes the genre list itself.
  `DiscoveryPage` gained four `DiscoveryFacetOption` lists; `DiscoveryFilter` gained a `ShowType`.
- **Type filter, default Scripted**: a fifth filter on `CatalogShow.ShowType`; the VM preselects `Scripted`.
- **Card description**: the shared card had empty space, so it now shows the show overview capped at 1000 chars
  (word-boundary cut + ellipsis). `MapCatalogShow` strips the overview's HTML via `Persistence.HtmlStripper` so
  the UI never names an Infrastructure type (layer rule).
- **Re-entrancy guard**: rebuilding the facet lists re-selects the current value, which would otherwise fire
  `OnSelected*Changed` → `ReloadAsync` in a loop; a `_rebuildingFacets` flag suppresses that during rebuild.

**Files changed.** Application: `Catalog/DiscoveryModels.cs` (`ShowType` on the filter, `DiscoveryFacetOption`,
four facet lists on the page). Infrastructure: `Discovery/DiscoveryIndex.cs` (facet counts + type filter),
`Discovery/ScheduleDiscoverySource.cs` (inline query, no `Task.Run`), `Tvmaze/TvmazeJsonMapper.cs` (HTML-stripped
overview). UI: `ViewModels/DiscoveryViewModel.cs` (facet collections, type default, re-entrancy guard, no
`ConfigureAwait(false)`), `ViewModels/ShowCardViewModel.cs` (`DescriptionText`/`HasDescription`),
`Views/DiscoveryView.axaml` (facet `ComboBox`es + type), `Views/ShowCardView.axaml` (description `TextBlock`).
Tests: `Unit/DiscoveryIndexTests.cs` (+type filter, +facet counts, +cross-filter counts),
`Integration/GetDiscoveryPageUseCaseTests.cs` (page constructor arity).

**Verification.**
- `dotnet build Shows.sln` → `Build succeeded. 0 Warning(s) 0 Error(s)`.
- `dotnet test Shows.Tests` → `Passed! - Failed: 0, Passed: 273, Skipped: 0, Total: 273` (was 270; +3 facet/type tests).

**Deviations.** None. The "loading bar on returning from Details" the user saw is the Details page's live read
(`GetCatalogShowUseCase` hits the network per SPEC R8a — expected, not a bug); the threading fix removes the
crash that accompanied it. If a spurious spinner persists on return, it is the Details load path, not Discovery.

**Unblocked.** Discovery is usable without the threading crash; filters are dropdowns with live counts.

### 2026-10-01 — Improvement 19: Discovery tab reworked onto `/schedule/full`

**Change.** The Discovery tab called the non-existent `/newshows` / `/upcoming` endpoints (issue 1). It is
now driven by the TVmaze `/schedule/full` snapshot — one large JSON of every scheduled episode, each row
embedding its show. A new Application seam `IDiscoverySource` (separate from the live `ICatalogProvider`)
returns a filtered, ordered, paged `DiscoveryPage`; `GetDiscoveryPageUseCase` adds only the already-tracked
marker (reads `user_show` where `deleted_at IS NULL`, SPEC F2/ADR-024). Infrastructure implements the seam
with `ScheduleDiscoverySource` (a process-wide singleton holding the parsed snapshot, refreshed at most
once per `RefreshHours`, or read from a local file when `ScheduleSourcePath` is set — the user's saved
`schedule-full.json`), backed by the pure `ScheduleParser` (aggregates the flat episode array to one entry
per show, reading network/country facets) and the pure `DiscoveryIndex` (New / Airing / Upcoming bucket
classification, name/genre/network/country filters, deterministic order, paging). The Avalonia
`DiscoveryViewModel`/`DiscoveryView` present the three buckets with a name box + genre/network/country
filters and a Previous/Next pager; the shared `ShowCardViewModel` keeps the add flow and the Details link
identical to Search. The dead `GetNewShowsAsync`/`GetUpcomingAsync` provider methods, `GetUpcomingShowsUseCase`,
and the `DiscoveryFeed` enum are removed. Config: `Discovery.NewShowsWindowDays` (default 30),
`Discovery.PageSize` (default 30), `Discovery.RefreshHours` (default 24), `Discovery.ScheduleSourcePath`
(set to `schedule-full.json` so the tab works offline — TVmaze is blocked from the dev machine, DATA_ANALYSIS §9).

**Files changed.** Application: `Catalog/DiscoveryModels.cs` (new: `DiscoveryBucket`/`DiscoveryFilter`/`DiscoveryPage`),
`Catalog/IDiscoverySource.cs` (new), `UseCases/GetDiscoveryPageUseCase.cs` (new), `Catalog/ICatalogProvider.cs`
(two methods removed), `UseCases/GetUpcomingShowsUseCase.cs` (deleted). Infrastructure: `Discovery/ScheduleParser.cs`,
`Discovery/DiscoveryIndex.cs`, `Discovery/ScheduleDiscoverySource.cs` (new), `Configuration/ShowsOptions.cs`
(`DiscoveryOptions`), `Configuration/DataPaths.cs` (`DiscoveryCacheDirectory`, `DiscoverySchedulePath`),
`appsettings.json` (`Discovery` section), `ShowsInfrastructureServiceCollectionExtensions.cs` (singleton source +
use case, old use case removed), `Tvmaze/TvmazeCatalogProvider.cs` (two methods removed). UI:
`ViewModels/DiscoveryViewModel.cs`, `Views/DiscoveryView.axaml(.cs)`. Tests: `Unit/DiscoveryIndexTests.cs`,
`Integration/GetDiscoveryPageUseCaseTests.cs` (new); `Integration/TvmazeCatalogProviderTests.cs` (two endpoint
tests removed, the server-error test retargeted to `search/shows`).

**Verification.**
- `dotnet build Shows.sln` → `Build succeeded. 0 Warning(s) 0 Error(s)`.
- `dotnet test Shows.Tests` → `Passed! - Failed: 0, Passed: 270, Skipped: 0, Total: 270` (was 258; +12 new
  Discovery tests).
- One-time smoke against the real 14 MB `schedule-full.json` (temporary test, since removed): parsed
  successfully; `New total=26 pages=1`, `Airing total=389 pages=13`, `Upcoming total=1221 pages=41`,
  `Upcoming+genre=Drama total=231` — buckets populated and the genre filter narrows correctly.

**Deviations.** The issue says "possibly indexed" cache; the implementation caches the parsed snapshot
(process-wide, refreshed daily) and runs a linear filter/order pass per query — the snapshot is ~10k shows,
so a linear pass is sub-millisecond and an index would be unjustified complexity. The issue's "details link
per show, same as the Search tab" is satisfied by the shared card's existing Details button (no new link).

**New facts learned.** `/schedule/full` is a flat array of **episode** objects each embedding `_embedded.show`
(a show with N scheduled episodes appears N times) — the parser must aggregate by show id. The country facet
is nested one level inside whichever broadcaster exists (`network.country` or `webChannel.country`).

**Unblocked.** Discovery works offline off the user's saved sample; a live `/schedule/full` fetch path exists
for when the host is reachable.

### 2026-10-01 — UI head switched to Avalonia; MAUI head removed (ADR-033)

**Change.** The user compared the two UI heads side by side and found the Avalonia head works better, then
directed that the MAUI head be deleted and Avalonia adopted as the single UI. `Shows.App` (MAUI,
`net8.0-windows10.0.19041.0`) was removed from the solution and deleted from disk; `Shows.App.Avalonia`
(plain `net8.0`, Avalonia 11.3.2) is now the sole UI head. This supersedes ADR-001 (which had chosen MAUI
and explicitly rejected Avalonia); recorded as **ADR-033**.

**Files changed.** `Shows.sln` (MAUI project removed via `dotnet sln remove`); `Shows.App/` deleted;
`Shows.Tests/Architecture/LayerBoundaryTests.cs` (Rule 4 retargeted from scanning `Shows.App`/`MauiProgram.cs`
to scanning `Shows.App.Avalonia` and exempting `Program.cs` / `App.axaml.cs`); docs `ARCHITECTURE.md`
(UI description, project-graph node, composition-root note), `DECISIONS.md` (ADR-001 marked superseded,
ADR-033 added), `STATE.md` (current-state facts).

**Verification.**
- `dotnet build Shows.sln` → `Build succeeded. 0 Warning(s) 0 Error(s)`.
- `dotnet test Shows.Tests` → `Passed! - Failed: 0, Passed: 258, Skipped: 0, Total: 258`. The retargeted
  Rule 4 passes against the Avalonia head (only `Program.cs` names `Shows.Infrastructure`; no View or
  ViewModel does), confirming the composition-root-only invariant still holds for the new head.

**Deviations.** None from the user's instruction. The Avalonia head had already been built and verified in
earlier work; this step only removes the MAUI head and brings the docs/tests in line.

**New facts learned.** The Avalonia head is a plain `net8.0` app, so unlike the MAUI head it is not
`net8.0-windows` — but its assembly is still not referenced by the `net8.0` test project, so Rule 4 remains
a source scan rather than an assembly reference. Avalonia is pinned to 11.3.2 (last line targeting .NET 8;
12.x's XAML generators need a newer Roslyn than SDK 8.0.425 ships).

**Unblocked.** A single UI head to evolve toward the iteration-2 Docker/Linux split (ADR-033 notes Avalonia's
plain-`net8.0` nature fits that story better than the removed MAUI head).

### 2026-10-01 — Improvement 18: pinning in the Started list (pinned state in the DB)

**Change.** A Started show can be **pinned**; pinned shows sort to the top under **every** sort option, above
the active sort. The pinned state is a user decision (SPEC P4), so it is stored in the DB — not a UI
preference like the sort choice (improvement 12) — and must survive a relaunch and a rebuild.

**Schema.** New column `user_show.is_pinned INTEGER NOT NULL DEFAULT 0` (migration
`20261001105330_0007_UserShowIsPinned`, generated with `dotnet ef migrations add`). Domain: `UserShow.IsPinned`
+ a `TrackedShow.IsPinned` pass-through (mirrors `NeedsReview`). EF maps it in `ShowsDbContext`. Orthogonal to
`category_id` and `deleted_at` — pinning touches neither; the flag is kept regardless of category so a show
moved out of Started retains its pin if it returns. SCHEMA.md §2 `user_show` updated in the same step.

**Write path.** `IUserShowRepository.SetPinnedAsync` → `UserShowRepository` (flips only the flag via
`FindRequiredAsync`, so pinning an untracked show throws rather than silently no-op'ing) →
`SetShowPinnedUseCase` (registered scoped). ViewModels talk only to use cases (ARCHITECTURE §3).

**UI.** `TrackedShowCardViewModel` gained `IsPinned` (seeded from the read model), `CanPin` (true only for
Started cards — `_showSeasons`), `PinText`, and `TogglePinCommand` (writes through the use case, then raises
`PinToggled` on the main thread). `IsPinned` is folded into `ComputeSignature` so a pin change forces a card
rebuild on the next fresh read. `TrackingViewModel` subscribes `PinToggled` when it builds a card and
re-runs `ApplyFilterAndSort` on it; the sort now applies a **stable partition** (`[pinned…, unpinned…]`) so
pinning lifts a show above the sort without scrambling the chosen order. The card's title row became a Grid
with a tappable "Pin"/"Pinned" label at the right, visible only when `CanPin`.

**Files changed:** `Shows.Domain/UserShow.cs`, `Shows.Domain/TrackedShow.cs`,
`Shows.Infrastructure/Persistence/ShowsDbContext.cs`, migration `0007` (+ Designer/snapshot),
`Shows.Application/Interfaces/IUserShowRepository.cs`,
`Shows.Infrastructure/Persistence/Repositories/UserShowRepository.cs`,
`Shows.Application/UseCases/SetShowPinnedUseCase.cs` (new),
`Shows.Infrastructure/ShowsInfrastructureServiceCollectionExtensions.cs` (DI),
`Shows.App/ViewModels/TrackedShowCardViewModel.cs`, `Shows.App/ViewModels/TrackingViewModel.cs`,
`Shows.App/Views/TrackedShowCardView.xaml`, `docs/SCHEMA.md`.

**Verification:**
```powershell
PS> dotnet build Shows.sln -clp:ErrorsOnly
    0 Warning(s)
    0 Error(s)
PS> dotnet test Shows.Tests --nologo
Passed!  - Failed: 0, Passed: 258, Skipped: 0, Total: 258, Duration: 6 s
```
New `SetShowPinnedUseCaseTests` (3 tests, 255→258): pin sets the flag and returns the new value; unpin
clears it; pinning does not disturb `category_id` or `deleted_at`. The migration is exercised by every
integration fixture that calls `Migrate()`. The pinned-first partition and the card toggle are UI, verified
by build + the user running the app.

**New facts learned.** CA1003 (enforced as an error in this repo) rejects a `public event Action<T>` — a
public event must use the `EventHandler`/`EventHandler<TEventArgs>` shape. The pin event carries no payload
(the list re-sorts regardless of which card), so a plain `EventHandler` was the right fit.

### 2026-10-01 — Upcoming page follow-up: ordering + drop watch checkbox

**Change (user feedback on the upcoming page).** Two refinements after the user ran it:
1. **Episode ordering** was soonest-first; the user wants the details-page order instead — **season number
   desc, then air date desc, then episode number desc**. Reused the existing `SeasonEpisodeOrdering`
   comparators (`SeasonsDescending` / `EpisodesDescending`, improvement 11) rather than a new LINQ chain, so
   the upcoming view and the details grid now share one ordering definition (nulls last, never throws).
2. **Removed the watch checkbox** from the upcoming rows — an episode that has not aired cannot be marked
   watched, so the row is now read-only (number · title · air date). The `ToggleWatchedCommand` gesture and
   `CheckBox` were dropped from `UpcomingShowCardView.xaml`; the card VM still passes a toggle callback only
   because the shared `SeasonGroupViewModel` requires one (it is never invoked).

**Files changed:** `Shows.Application/UseCases/GetUpcomingEpisodesUseCase.cs` (ordering + doc comments),
`Shows.App/Views/UpcomingShowCardView.xaml` (row template), `Shows.App/ViewModels/UpcomingShowCardViewModel.cs`
(doc comments).

**Verification:**
```powershell
PS> dotnet build Shows.sln -clp:ErrorsOnly
    0 Warning(s)
    0 Error(s)
PS> dotnet test Shows.Tests --nologo
Passed!  - Failed: 0, Passed: 255, Skipped: 0, Total: 255, Duration: 6 s
```
`SeasonEpisodeOrdering` is already unit-tested (`SeasonEpisodeOrderingTests`); the upcoming use case has no
new test (pure read + shared comparators). UI verified by build + the user running the app.

### 2026-10-01 — Improvement 4 (second half): upcoming-episodes page

**Change.** The second half of improvement 4 — the separate upcoming-episodes view — as a **5th tab**
inserted after Started in `TrackingPage`. It is a view over Started, not a fifth category (SPEC R1), so the
`CategoryTab` record gained `IsUpcoming` (default false) and the Upcoming tab carries `Category=Started,
IsUpcoming=true`. `TrackingViewModel.IsStartedView => IsStarted && !IsUpcoming` hides the Started-only
toolbar (sort Picker, Waiting checkbox) and the Started `ScrollView` on the Upcoming tab; the tab-change
handler sets `IsUpcoming` before loading so `LoadCoreAsync` routes to the upcoming read.

**Data path.** `GetUpcomingEpisodesUseCase.ExecuteAsync(today)` reuses `ListActiveByCategoryAsync(Started)`
+ `ITrackedShowRepository.ListSeasonsByShowIdsAsync` + a new `IEpisodeRepository.ListUpcomingByShowIdsAsync`
(`EpisodeType != Special && (AirDate == null || AirDate > today)`), orders upcoming episodes soonest-first
(`AirDate ?? MaxValue`, then `EpisodeNumber ?? MaxValue`), and drops shows/seasons with nothing upcoming.
Returns `UpcomingShowEpisodes(Show, Seasons, EpisodesBySeason)`. Registered scoped alongside the other
Started reads.

**UI.** `UpcomingShowCardViewModel` builds its `SeasonGroupViewModel`s eagerly with `isExpanded: true`,
`removeWatchedOnToggle: false`, no group-emptied callback — so seasons are open by construction and the
template renders `Episodes` unconditionally with no chevron (non-collapsible). `UpcomingShowCardView`
mirrors the tracker card minus the collapse affordance. A third list control (`ScrollView` bound to
`UpcomingShows`, `IsVisible=IsUpcoming`) was added; `SyncTo` was made generic so the upcoming list reuses the
same reference-equality skip-mutation helper. The upcoming list is not memoized (small, rarely-opened,
different VM shape).

**Files changed:** `Shows.Application/UseCases/GetUpcomingEpisodesUseCase.cs` (new),
`Shows.Application/Interfaces/IEpisodeRepository.cs` + `ITrackedShowRepository.cs` (batched methods),
`Shows.Infrastructure/Persistence/Repositories/EpisodeRepository.cs` + `TrackedShowRepository.cs`
(implementations), `Shows.Infrastructure/ShowsInfrastructureServiceCollectionExtensions.cs` (DI),
`Shows.App/ViewModels/UpcomingShowCardViewModel.cs` (new), `Shows.App/Views/UpcomingShowCardView.xaml(.cs)`
(new), `Shows.App/ViewModels/TrackingViewModel.cs` (tab, `IsUpcoming`/`IsStartedView`, upcoming load branch,
generic `SyncTo`), `Shows.App/Views/TrackingPage.xaml` (template + third list control).

**Verification:**
```powershell
PS> dotnet build Shows.sln -clp:ErrorsOnly
    0 Warning(s)
    0 Error(s)
PS> dotnet test Shows.Tests --nologo
Passed!  - Failed: 0, Passed: 255, Skipped: 0, Total: 255, Duration: 6 s
```
No new unit test: the use case is a pure read over already-tested repository methods
(`ListUpcomingByShowIdsAsync`'s predicate is the inverse of the already-tested inline-list predicate); the
UI wiring is verified by build + the user running the app. The full App build was clean here because the app
was not running.

**New facts learned.** `[NotifyPropertyChangedFor]` is valid only on a generated-property **backing field**,
not on a computed property — the first build failed with CS0592 until the `IsStartedView` notification moved
onto the `_currentCategory` field.

**Unblocked.** Improvement 4 fully closed. The Discovery-tab rework (improvement 19) will replace the
Discovery feed's current endpoints with `/schedule/full`; the saved sample `schedule-full.json` is the
reference for its shape.

### 2026-10-01 — Improvement 5: progress logging for all jobs (ImdbImporter bulk passes)

**Audit first.** Improvement 5 asked for a progress log on all jobs, not only import. Audited every job:
`Promoter`, `ImdbIdScraper`, `TvmazeIdResolver`, `AkasJob`, `ImdbEnrichmentJob`, and `ShowRefreshJob`
(EpisodeRefresh/StatusRecheck) already emit a per-item `{Job} {Index}/{Total}: …` line at Information. The
one gap was **`ImdbImporter`**: its four bulk passes stream millions of TSV rows (title.basics is >1 GB) and
logged only at each pass's *end*, so a multi-minute pass produced no output and looked like a hang — the
exact failure the other jobs' per-item lines exist to prevent.

**Change.** Added `ProgressEveryRows = 500_000` and a `LogPassProgress(pass, scanned, changed)` helper; each
of the four pass loops now counts scanned rows and logs every 500k:
`ImdbImport {pass}: N rows scanned, M changed so far.` The per-pass end line is unchanged. No behavior
change — logging only.

**Files changed:** `Shows.Infrastructure/Import/ImdbImporter.cs` (constant + helper + a scanned counter and
periodic log in each of the four passes).

**Verification:**
```powershell
PS> dotnet build Shows.sln -clp:ErrorsOnly
    0 Warning(s)
    0 Error(s)
PS> dotnet test Shows.Tests --nologo
Passed!  - Failed: 0, Passed: 255, Skipped: 0, Total: 255, Duration: 5 s
```
Logging-only change; the periodic line is exercised by a real `ImdbImport` run against the >1 GB datasets
(not unit-tested — it is a side-effecting log line in a bulk loader).

### 2026-10-01 — Improvement 15: details-page category buttons + red bottom Untrack

**Change (UI only).** The details page's category row was "Category: {current}" + up to two "Move to X"
buttons (each hidden when it matched the current category) + an Untrack button. Reworked to **three
always-visible buttons** — "Started" / "Later" / "Cancelled" — where the button for the **current** category
is `IsEnabled=false` (inactive) rather than hidden, so the user always sees all three and which one is
active. The **Untrack** button moved from that row to the **bottom of the page** and is painted red via a new
`DangerButton` style (defined once in `App.xaml`, based on `SecondaryButton`, red text + border) — the
destructive action now sits away from the category buttons so it is not a mis-tap. Finished is still never
offered (auto-assigned, SPEC R5). No VM change: `IsStarted`/`IsLater`/`IsCancelled` and the three
`MoveTo*Command`s + `UntrackCommand` already existed.

**Files changed:** `App.xaml` (`DangerButton` style); `ShowDetailsPage.xaml` (3 always-visible category
buttons with `IsEnabled` on the current one; Untrack moved to the bottom with `DangerButton`).

**Verification:**
```powershell
PS> dotnet build Shows.sln -clp:ErrorsOnly
    0 Warning(s)
    0 Error(s)
PS> dotnet test Shows.Tests --nologo
Passed!  - Failed: 0, Passed: 255, Skipped: 0, Total: 255, Duration: 6 s
```
UI-only change; the user re-tests the details page in the running app.

### 2026-10-01 — Started-list memoization: UI-layer card cache keyed on freshly-read data

**Problem.** After the batched read (previous entry), the user reported Started still lagged on tab switch.
Diagnosis: batching fixed the *data* cost (5 queries), but every switch still rebuilt all 78 card VMs and
their season-group VMs synchronously, and re-assigning the bound collections forced the
`VerticalStackLayout` to tear down and re-realize every card view.

**User's direction (recorded, not assumed).** The user asked for memoization on the VMs with a cache reused
"if season data not changed", then explicitly rejected a DB signal: *"do not use sql, you have all the data
in there, cache is ui thing, its not db thing."* So the cache lives entirely in `TrackingViewModel` and is
validated against the data the batched read already returns — no `PRAGMA data_version`, no extra query.

**Design.** Each card VM carries a `DataSignature` — a deterministic fingerprint of everything it renders
(show display fields incl. full premiere/end dates, plus for Started the season ids/numbers, expanded ids,
ordered expanded-row ids, and per-season counts, all order-normalized). On load the list still reads fresh
(so it can detect change), then for each show: if a cached VM for that category has the same signature, it
is reused as-is (season groups already built); otherwise a fresh VM is built and its seasons applied. The
per-category card cache is `Dictionary<ShowCategory, Dictionary<long, TrackedShowCardViewModel>>`.
`ApplyFilterAndSort` gained `SyncTo`, which replaces a bound collection's contents only when they actually
differ (reference-equality), so an unchanged tab switch mutates nothing and the realized views are reused.

**Correctness.** A changed signature (new episode, watch toggle, persisted expansion, a job's edit, a
premiere-date correction) falls through to a fresh build — the cache never serves stale data because the
read is always fresh and the signature covers every rendered field. The full premiere date (not just year)
is in the signature so the AirDate sort can't use a stale cached value.

**Files changed:** `TrackedShowCardViewModel` (`DataSignature`, static `ComputeSignature`, signature set in
ctor + `ApplyBatchedSeasons`); `TrackingViewModel` (`_cardCache`, reuse-or-rebuild in `LoadCoreAsync`,
`SyncTo` + materialized `ordered` in `ApplyFilterAndSort`, `EmptyCardSeasons`).

**Verification:**
```powershell
PS> dotnet build Shows.sln -clp:ErrorsOnly
    0 Warning(s)
    0 Error(s)
PS> dotnet test Shows.Tests --nologo
Passed!  - Failed: 0, Passed: 255, Skipped: 0, Total: 255, Duration: 6 s
```
The signature is pure UI-layer logic; the test project does not reference the MAUI App project (layering),
so it is not unit-tested there — the integration tests confirm the data path is unchanged. The user re-tests
Started switch speed in the running app.

**Unblocked:** user verification of Started switch speed. The batched read is retained (still the data
source); memoization sits on top of it.

### 2026-10-01 — Improvements 4 (core) + 12: Started "Waiting" section, last-aired sort, persisted sort

**Decisions (user-asked, not unilateral).** The user chose: improvement 4 first; improvement 12 persists the
sort via **MAUI Preferences** (no schema change — a sort choice is a UI preference, not user data, SPEC P4),
and sources "last aired" by **computing it in the batched read** (no `tracked_show` column, SPEC P5).

**Improvement 4 (core).** The Started toolbar gains a "Waiting" checkbox (off by default). When on, a
"Waiting for new episodes" section renders below the active shows, holding shows whose already-aired
regulars are all watched. The batched read's empty-season drop already identifies these — a card with no
displayable season (`HasSeasons == false`) is exactly a waiting show. `TrackingViewModel` splits the Started
projection into `StartedShows` (active) and `StartedWaitingShows`; `ApplyFilterAndSort` routes by
`HasSeasons`, and toggling the checkbox re-runs the split without a reload. The separate upcoming-episodes
page (improvement 4's second half) is **not** in this change.

**Improvement 12.** New `LastAiredByShowIdsAsync` (grouped `MAX(air_date)` filtered to aired-on-or-before-
today, ISO TEXT so lexicographic MAX is a correct date max) is folded into `GetStartedCardsUseCase`, which
now returns `StartedCardsBatch` (per-show `CardSeasons` + per-show last-aired date). A "Last aired" sort
option orders Started by that date desc (no-aired shows last, ties by name). The chosen sort persists across
relaunches via `Preferences` (key `started_sort`), restored in the VM constructor.

**Files changed:** `IEpisodeRepository`/`EpisodeRepository` (`LastAiredByShowIdsAsync`);
`GetStartedCardsUseCase` (returns `StartedCardsBatch`); `TrackingViewModel` (Waiting split + checkbox,
`ShowSortOrder.LastAired`, Preferences persist/restore); `TrackingPage.xaml` (checkbox + two-section Started
layout).

**Verification:**
```powershell
PS> dotnet build Shows.sln -clp:ErrorsOnly
    0 Warning(s)
    0 Error(s)
PS> dotnet test Shows.Tests --nologo
Passed!  - Failed: 0, Passed: 255, Skipped: 0, Total: 255, Duration: 5 s
```
New test `Batched_read_carries_last_aired_date_per_show` proves the batch carries each show's last-aired
date (future episodes excluded, a no-aired show absent). 254 → 255.

**Unblocked:** the user re-tests the Waiting section and the Last-aired sort in the running app. The
upcoming-episodes page (improvement 4 second half) remains open.

### 2026-10-01 — Started-list performance: batched card read (user-issues 7 follow-up)

**Problem.** After ADR-032 split the list per category, the user reported Started was now the slowest tab:
*"switching back to it take like 3 seconds."* Diagnosis: Started realizes all 78 cards at once (ADR-032),
and each card's `EnsureLoadedAsync` ran its own `GetCardSeasonsUseCase.ExecuteAsync` — up to 5 queries per
card (seasons, ui_season_state, watches, expanded-season episodes, the grouped count) — so ~390 round-trips,
each opening its own DI scope, plus 78 separate async re-layout passes over the `VerticalStackLayout`.

**Fix — batch the reads, do not cache them.** The user rejected a cache that skips queries (*"if you dont
get the data skipping queries - how do you know when to update cache?"*), so the fix always reads fresh and
only collapses the round-trips. New `GetStartedCardsUseCase` loads the same per-show `CardSeasons` for every
Started show in **five** set-based queries (`WHERE show_id IN (...)`), slices the result per show, and the
VM applies each slice synchronously via `TrackedShowCardViewModel.ApplyBatchedSeasons` (one layout pass, not
78). Data cost is now constant regardless of list length.

**Files changed:** new `Shows.Application/UseCases/GetStartedCardsUseCase.cs`; batched methods added to
`ITrackedShowRepository`/`IEpisodeRepository`/`IUiStateRepository` and their EF implementations
(`ListSeasonsByShowIdsAsync`, `ListWatchesByShowIdsAsync`,
`CountDisplayableEpisodesBySeasonForShowsAsync`, `ListByShowIdsAsync`); `TrackedShowCardViewModel.ApplyBatchedSeasons`;
`TrackingViewModel.LoadCoreAsync` batch path; DI registration; test proxy delegate.

**Deviation:** the batched count uses LINQ `GroupBy` (EF translates `Contains`→`IN`, the watch check→
correlated `NOT EXISTS`) rather than the single-show raw SQL, because raw SQL cannot build a dynamic-length
`IN` list. Predicate is identical to `InlineEpisodeRules.IsListable`.

**Verification:**
```powershell
PS> dotnet build Shows.sln -clp:ErrorsOnly
    0 Warning(s)
    0 Error(s)
PS> dotnet test Shows.Tests --nologo
Passed!  - Failed: 0, Passed: 254, Skipped: 0, Total: 254, Duration: 5 s
```
New `GetStartedCardsUseCaseTests` (3 tests) proves the batched slice for each show is **identical** to the
single-card `GetCardSeasonsUseCase.ExecuteAsync` result (seasons, expanded ids, expanded rows, counts), that
the batched count excludes watched/future/specials across shows, and that a show with no seasons still gets
an empty entry. 251 → 254.

**Unblocked:** the user re-tests Started open speed in the running app. The single-card
`GetCardSeasonsUseCase.ExecuteAsync` is retained (the lazy per-season expand path still uses it).

### 2026-09-30 — Step 7.3: `Enrichment --imdb` (TVmaze authoritative, IMDb gap-fill + mismatch flagging)

**Decision first (ADR-031, open question 1 answered).** The user chose flag-only: *"use tvmaze as main
source of data and imdb just completes it … in case of collisions I want to see them, but main source is
still tvmaze."* Recorded as **ADR-031**; `STATE.md` open question 1 marked ANSWERED; `IMPLEMENTATION_PLAN.md`
step 7.3 rewritten to drop the "ask the user" gate and fix the scope (localized titles/posters are out of
scope — 7.5 ships titles via TVmaze, and the IMDb datasets carry neither posters nor episode descriptions).

**Files added:** `Shows.Domain/EpisodeMismatch.cs` (the derived reconciliation row);
`Shows.Infrastructure/Jobs/ImdbEnrichmentJob.cs` (the `--imdb` job body + `ImdbEnrichmentReport`);
`Shows.Infrastructure/Persistence/Migrations/20260930102053_0006_EpisodeMismatch.cs` (+ Designer);
`Shows.Tests/Integration/ImdbEnrichmentTests.cs` (8 tests); `tasks/7.3-enrichment-imdb.md`.
**Files changed:** `Shows.Infrastructure/Persistence/TvmazeShowMapper.cs` (`EnrichFromImdbAsync`);
`Shows.Infrastructure/Persistence/ShowsDbContext.cs` (`EpisodeMismatches` DbSet + config);
`Shows.Application/Interfaces/IShowMapper.cs` (`EnrichResult`);
`Shows.Application/Interfaces/IEpisodeRepository.cs` + `Persistence/Repositories/EpisodeRepository.cs`
(`ListMismatchesByShowIdAsync`); `Shows.Application/UseCases/ReadUseCases.cs` (`ShowDetails.Mismatches` +
populated in `GetShowDetailsUseCase`); `Shows.Infrastructure/Jobs/EnrichmentJob.cs` (the `--imdb` mode);
`ShowsInfrastructureServiceCollectionExtensions.cs` (registered `ImdbEnrichmentJob`);
`Shows.App/ViewModels/ShowDetailsViewModel.cs` + `Views/ShowDetailsPage.xaml` (the mismatch section);
`Shows.Tests/Architecture/LayerBoundaryTests.cs` (rule 8 bulk-writer list += `ImdbEnrichmentJob`);
`docs/SCHEMA.md` (§4a `episode_mismatch`), `docs/DECISIONS.md` (ADR-031), `docs/STATE.md`,
`docs/IMPLEMENTATION_PLAN.md`.

**The design (ADR-031).** TVmaze is the authoritative episode catalog. `EnrichFromImdbAsync` keys both
sides on the exact `(season, episode)` pair (rows missing either number are skipped, not keyed), then:
fills `episode.title`/`runtime_minutes` from `imdb_episode` **only where TVmaze left them null** (setting
`title_source='imdb'`), sets `episode.imdb_id` (IMDb completes TVmaze), and **never** overwrites a TVmaze
value. It never fabricates an air date (IMDb has only `start_year`) or a description (IMDb has none). Every
disagreement is recorded in `episode_mismatch` — `imdb_only` (flagged, **not** inserted into `episode`),
`tvmaze_only`, `title`, `runtime`, `air_date_gap` (TVmaze has none, IMDb's year recorded as the partial
signal). The table is derived/rebuildable: the mapper wipes and re-inserts a show's rows each run, so the
wipe is the idempotence mechanism (no unique index needed). The UI cannot read `imdb_*` (rule 5), so the
details page reads the materialised rows via `IEpisodeRepository.ListMismatchesByShowIdAsync`.

**Verification — unit (8 tests, `dotnet test --filter ImdbEnrichmentTests`):**
```
Passed!  - Failed: 0, Passed: 8, Skipped: 0, Total: 8
```
Proves: a missing title is filled with `title_source='imdb'` and its `air_date` untouched; a title/runtime
conflict keeps the TVmaze value and records both sides; an IMDb-only episode is flagged with
`episode_id IS NULL` and **no** `episode` row is created; a TVmaze-only episode is flagged; re-run reports
`Changed=0` with the same mismatch count; a show with no `imdb_id` and a soft-deleted show are skipped;
lease refusal returns `Refused`.

**Verification — full suite (`dotnet test Shows.Tests`):**
```
Passed!  - Failed: 0, Passed: 196, Skipped: 0, Total: 196
```
(188 → 196; all architecture rules green — rule 8 now includes `ImdbEnrichmentJob`.)

**Verification — real run against `data/shows.db`** (backed up first to
`data/shows.db.bak-enrich-20260930-134617`):
```
dotnet run --project Shows.Jobs.Enrichment -- --imdb
  Enrichment --imdb done: 539 shows with an imdb_id, 539 filled a gap, 18424 mismatches recorded.
dotnet run --project Shows.Jobs.Enrichment -- --imdb   # second run
  Enrichment --imdb done: 539 shows with an imdb_id, 0 filled a gap, 18424 mismatches recorded.
```
Breakdown (`SELECT kind, COUNT(*) FROM episode_mismatch GROUP BY kind`):
`runtime 14797 · title 2985 · imdb_only 428 · tvmaze_only 194 · air_date_gap 20` = 18,424.
Integrity checks: `air_date_source='imdb'` = **0** (no air date fabricated); every `imdb_only` row has
`episode_id IS NULL` (no IMDb-only episode merged into `episode`); 18,810 episodes now carry an `imdb_id`
link. The first run's "539 filled a gap" is the `imdb_id` link (TVmaze already supplied every title in this
library), so `title_source='imdb'` stayed 0 — the title-fill path is proven by the unit fixture, not the
live data.

**Deviations from the plan:** none material. The plan's 7.3 mentioned "localized titles from imdb_akas" and
"poster backfill"; both are out of scope under "TVmaze is the main source" — localized titles ship via 7.5
(TVmaze `/akas`), and the IMDb datasets carry neither posters nor episode descriptions. A `description_gap`
kind was considered and dropped: the IMDb episode dataset has no summary, so there is nothing to compare
and flagging every summary-less episode would be noise, not signal (recorded in SCHEMA §4a).

**New facts learned:** the live library's TVmaze episode titles are complete (0 needed an IMDb fill), so the
dominant real-world signal is runtime disagreement (14,797) — TVmaze and IMDb disagree on episode runtime
for the large majority of episodes, which is exactly the kind of thing the flag-only decision lets the user
see rather than silently overwrite.

**Unblocks:** Phase 8 — 8.2 (consolidated integration tests) and 8.3 (Docker-readiness pass).

### 2026-09-30 — Step 7.4: `ImdbImport` (download/reuse + three-pass bulk load)

**Files added:** `Shows.Infrastructure/Import/IImdbDatasetDownloader.cs` (download seam +
`DatasetDownloadResult`), `ImdbDatasetDownloader.cs` (streams `.tsv.gz` over the `Imdb` named client),
`TsvReader.cs` (plain-or-gz streaming reader, `\N`→null), `ImdbImporter.cs` (the three-pass bulk loader +
`Batch` + `ImdbImportReport`); `Shows.Infrastructure/Jobs/ImdbImportJob.cs` (entry point);
`Shows.Tests/Integration/ImdbImporterTests.cs` (9 tests); `tasks/7.4-imdb-import.md`.
**Files changed:** `Shows.Jobs.ImdbImport/Program.cs` (passes the handler);
`Shows.Infrastructure/ShowsInfrastructureServiceCollectionExtensions.cs` (registered the downloader +
importer); `Shows.Tests/Architecture/LayerBoundaryTests.cs` (rule 5 allow-list += `ImdbImporter` as the
sole imdb_* writer; rule 8 bulk-writer list += `ImdbImporter`); `docs/DATA_ANALYSIS.md` §6 (import order
corrected — see deviations); `docs/IMPLEMENTATION_PLAN.md` step 7.4 (the `tt2085059` count note).

**The download+extract path was exercised on the smallest dataset; the two large ones were reused.**
Per the user's instruction, `title.basics.tsv` (1.1 GB) and `title.akas.tsv` (3.0 GB) were **moved** into
`data/imdb/` (same-volume `Move-Item`, instant) and the importer **reuses** them — it never re-downloads a
file already on disk. `title.episode` was left out of `data/imdb/` so the first real run **downloaded and
extracted** it fresh (`title.episode.tsv.gz`, kept on disk), proving the network path end to end. The
reader handles both a plain `.tsv` (the moved files) and a `.tsv.gz` (the downloaded one) transparently.

**Import order inverted to titles-before-episodes (deviation, recorded in §6).** The first plan draft
loaded `title.episode` first under a single `PRAGMA defer_foreign_keys=ON` transaction. That is impossible
under ADR-028: the lease renews on a **separate connection**, and a write transaction held open across the
whole load locks that renewal out — the first real run proved it (`SQLite Error 5: database is locked` at
the heartbeat renewal), and ADR-028 explicitly rejects holding a write transaction open for the whole run
anyway. The load therefore runs in **short batched transactions** (commit → renew lease → reopen, ≥5,000
rows per batch), and a FK cannot be deferred across a commit, so `imdb_title` must be populated before any
`imdb_episode` row. The "don't filter basics to `tvSeries` first" warning still holds — episode *detail*
comes from the `tvEpisode` rows in pass 3, never from the `tvSeries` filter.

**Verification (real run against `data/shows.db`, app open — no lock):**

```
dotnet run --project Shows.Jobs.ImdbImport
  ImdbImport: 539 active tracked shows carry an imdb_id.
  reusing on-disk ...\title.episode.tsv.gz   (downloaded by the prior run, kept)
  reusing on-disk ...\title.basics.tsv       (moved in, reused not re-downloaded)
  reusing on-disk ...\title.akas.tsv         (moved in, reused not re-downloaded)
  pass 1 (titles): 539 inserted.
  pass 2 (episodes): 19267 inserted.
  pass 3 (episode details): 19266 updated.
  pass 4 (akas): 18838 inserted.
  ImdbImport done: 57910 rows changed across 3 datasets.

dotnet run --project Shows.Jobs.ImdbImport   (second run)
  ImdbImport done: 0 rows changed across 3 datasets.   # re-runnable (INSERT OR IGNORE + value-guarded UPDATE)
```

Row counts via `python -c "import sqlite3; ..."`: `imdb_title` 539, `imdb_episode` 19267, `imdb_akas`
18838, `imdb_dataset_meta` 3, episodes with a pass-3 title 19266. `tt26448526` = **33** (matches the plan).

**Deviation — `tt2085059` is 34, not the planned 32.** The plan said "a different number means the import
is wrong, not the data," but that was verified against a **planning-time snapshot** of a **living** dataset.
Counting the source directly (`gzip` + line scan of `title.episode.tsv.gz`) shows the file itself now
carries **34** rows for `tt2085059` — Black Mirror gained 2 episodes since planning. The importer loads
exactly what the source contains, which is correct; the stale number was corrected in step 7.4's verify
note and §6. `tt26448526` (33) still matches exactly.

**New facts later steps depend on:**
- The IMDb datasets are **living** — episode counts drift. Any future assertion must compare against the
  file, not a planning-time number.
- A bulk writer that renews its lease on a separate connection **cannot** hold one long write transaction
  (SQLite single-writer locks the renewal out). The batched-commit pattern is the ADR-028-sanctioned fix.
- `imdb_episode.parent_tconst`'s FK forces titles-before-episodes under batching — the §6 order is now
  titles → episodes → episode-details → akas.

**Unblocked:** step 7.3 remains a HARD STOP (open question 1). 8.2/8.3 (integration tests, docker-readiness)
are next. 188 tests pass (179 → +9), zero warnings.

### 2026-09-30 — Step 7.5: Localized titles from TVmaze `/akas`

**Files added:** `Shows.Infrastructure/Persistence/Sources/TvmazeAka.cs`;
`Shows.Infrastructure/Jobs/AkasJob.cs` (the `Enrichment --akas` body + `AkasReport`) and
`Shows.Infrastructure/Jobs/EnrichmentJob.cs` (the entry point); migration
`20260930074406_0005_TvmazeAkas`; `Shows.Tests/Integration/AkasJobTests.cs` (7 tests);
`tasks/7.5-akas-localized-titles.md`.
**Files changed:** `Shows.Application/Catalog/ICatalogProvider.cs` (`GetAkasAsync` + `ShowAka` record);
`Shows.Application/Interfaces/IShowMapper.cs` (`MapAkasAsync` + `AkaMapResult`);
`Shows.Application/UseCases/ReadUseCases.cs` (`FetchLocalizedTitlesUseCase` + `LocalizedTitles`);
`Shows.Infrastructure/Persistence/ShowsDbContext.cs` (DbSet + composite-PK config);
`Shows.Infrastructure/Persistence/TvmazeShowMapper.cs` (`MapAkasAsync`);
`Shows.Infrastructure/Tvmaze/TvmazeCatalogProvider.cs` + `TvmazeJsonMapper.cs` (`GetAkasAsync` / `MapAkas`);
`Shows.Infrastructure/Configuration/ShowsOptions.cs` (`LocalizationOptions.TargetCountryCode`, default `RU`);
`Shows.Infrastructure/ShowsInfrastructureServiceCollectionExtensions.cs` (registered `AkasJob` + the use case,
the latter with the target country unwrapped from config); `Shows.Jobs.Enrichment/Program.cs` (handler);
`Shows.App/ViewModels/ShowDetailsViewModel.cs` + `Views/ShowDetailsPage.xaml` (the button now fetches + lists);
`Shows.Tests/Architecture/LayerBoundaryTests.cs` (rule 8 += `AkasJob`);
`Shows.Tests/Integration/MainSchemaTests.cs` (TEXT-PK allow-list += the two `tvmaze_akas` natural keys;
`Reset()` clears the new table); the four `ICatalogProvider` test fakes gained `GetAkasAsync`.

**Localized titles ship without the IMDb dataset.** TVmaze's `/shows/{id}/akas` returns a show's alternative
titles **keyed on `country.code`, not language** (probed live 2026-09-30), so a Russian title is available
from TVmaze directly — this step depends only on the provider (3.1), not on 7.4 or the open-question-1-gated
7.3. The pick rule is **the lowest `aka_order` whose `country.code` equals the target country** (RU for this
user, from configuration); no match leaves `title_localized` **null**, never a wrong-country guess.

**The provider returns raw akas; the pick is policy above it.** `GetAkasAsync` returns
`ShowAka(Name, CountryCode, CountryName)` in the API's array order — the provider must not hard-code "RU"
(the user's country is configuration). R14 holds: a genuine 404 or empty array is a real "TVmaze has no
akas" (empty list); an unreachable host throws `TvmazeUnavailableException` naming the host.

**The mapper is the sole writer (R11).** `MapAkasAsync` writes the `tvmaze_akas` source rows verbatim and
maps the pick into `tracked_show.title_localized`. The source rows are a rebuildable child set keyed on
`(show, country, name)` with **no surrogate id any user row points at**, so the mapper refreshes them
delete-then-insert per show — the one place that is safe, unlike the mapped tables where it would orphan
watch marks (SCHEMA §2a). `MapAkasAsync` is on `IShowMapper` (Application cannot name the concrete mapper),
so `ShowAka` is an Application type; it returns `AkaMapResult(Changed, PickedTitle)` so the job counts
`items_changed` on the mapped-title change (not source-row churn) and the button can list the alternates.

**Two consumers, one mapper path.** The `Enrichment --akas` job selects active shows (`deleted_at IS NULL`,
R8), fetches, maps, lease-guarded (`enrichment_akas`, ADR-028), capped, R14 per-show failures left for the
next run. The details-page button (`FetchLocalizedTitlesUseCase`) is a single-show fetch — no lease — and
lists every alternate so the user can confirm the auto-pick; it goes through the mapper, never writing
`title_localized` itself.

**Verification:**
> dotnet build Shows.sln
> Build succeeded. 0 Warning(s) 0 Error(s)
> dotnet test Shows.Tests
> Passed! - Failed: 0, Passed: 179, Skipped: 0, Total: 179

The 10 new tests (7 job/use-case + 3 provider, fake `ICatalogProvider` / fake HTTP handler, temp DB, no
network): show 59985 gets `title_localized = 'Начало'` (first RU) with all five akas stored in array order;
a show with only CN akas leaves `title_localized` null; a re-run reports `items_changed = 0`; a soft-deleted
show is never fetched (asserted on the request list, R8); a transport failure is one per-show failure not a
crash (R14); a held lease refuses before fetching; the button use case lists all five and sets the same
title; the provider parses the country-keyed shape in order, returns empty on a genuine 404, and throws on a
transport failure naming the host.

**Deviations / decisions.** (1) `MapAkasAsync` lives on `IShowMapper` rather than the concrete mapper, so the
UI use case can reach it without naming Infrastructure — `ShowAka` is therefore an Application type (it is a
read-model DTO, consistent with `CatalogShow`). (2) The target country is a constructor string on the use
case, supplied by the composition root from `ShowsOptions` — Application must not read configuration. (3) The
`Every_primary_key_column_is_integer` schema test needed the two `tvmaze_akas` natural TEXT keys
(`country_code`, `name`) added to its allow-list, exactly as `imdb_akas.title_id` and `tvmaze_status_seen`
already are — they are natural keys by design (SCHEMA §3), not surrogate ids.

**New facts.** `tvmaze_akas` is the first source table with a composite PK containing TEXT columns written
by the mapper; the delete-then-insert refresh is sound there precisely because it carries no surrogate id.
**Unblocked:** the details-page "Fetch localized titles" button is now live (step 6.1's seam is wired). 7.4
(`ImdbImport`) and 7.3 (`Enrichment` IMDb modes, gated on open question 1) remain.

### 2026-09-30 — Steps 7.1 + 7.2: `EpisodeRefresh` and `StatusRecheck` jobs

**Files added:** `Shows.Infrastructure/Jobs/ShowRefreshJob.cs` (the shared body + `RefreshMode` +
`RefreshReport`), `Shows.Infrastructure/Jobs/RefreshJobs.cs` (`EpisodeRefreshJob` + `StatusRecheckJob`
entry points); `Shows.Tests/Integration/ShowRefreshJobTests.cs` (8 tests); `tasks/7.1-7.2-refresh-jobs.md`.
**Files changed:** `Shows.Infrastructure/ShowsInfrastructureServiceCollectionExtensions.cs` (registered
`ShowRefreshJob` scoped, factory-unwrapping `IOptions<ShowsOptions>` and passing `IIdGenerator`);
`Shows.Jobs.EpisodeRefresh/Program.cs` + `Shows.Jobs.StatusRecheck/Program.cs` (now pass their handler to
`ShowsJobRunner.RunAsync`).

**Both jobs are the same three moves — select → refresh source rows → map (SPEC R11) — so they share one
body** (`ShowRefreshJob`) with a mode enum; neither contains mapping logic and neither selects on
`user_show.category_id` (R2: the category is the user's choice, the status is the provider's). Selection is
the one shared repository query, which already applies `deleted_at IS NULL` (R8) and filters on the enum
(R12) — `EpisodeRefresh` excludes `{Ended, Canceled}` (refresh more is correct; an airing catalog must not
rot), `StatusRecheck` selects exactly `{Ended, Canceled}` (catch a show that resumed). Lease-guarded
(`episode_refresh` / `status_recheck`) with heartbeat renewal; a renewal that updates zero rows stops the
run. `items_changed` is measured AFTER the mapper, so a run that fetched everything but changed nothing
reports 0 honestly. R14: a per-show `TvmazeUnavailableException` (or the mapper's "TVmaze has no show N") is
a per-show failure — counted, show left for the next run, `ChangeTracker.Clear()` so the next show starts
clean — never a crash and never counted as "no change".

**The resume is detected without reading a source table** (source-table containment, step 8.1): the job
captures the tracked entity's `Status` enum before the map and compares after — the mapper updates the same
scoped entity in place. A transition is logged at Information with the show id and both enum values.

**Verification:**
> dotnet build Shows.sln
> Build succeeded. 0 Warning(s) 0 Error(s)
> dotnet test Shows.Tests
> Passed! - Failed: 0, Passed: 169, Skipped: 0, Total: 169

The 8 new tests (fake `ITvmazeShowSource`, temp DB, no network): second run reports `items_changed = 0`
(7.1 idempotence); EpisodeRefresh selects `{Running, Unknown}` and skips `{Ended, Canceled}`; StatusRecheck
selects only the Ended show and flips it to Running (the resume); a soft-deleted show is never fetched
(asserted on the fake's request list, not row counts — R8); a refresh that adds an episode leaves every
`episode_watch.episode_id` pointing at the same ids (asserted on ids, not counts — SCHEMA §2a); a transport
failure is one per-show failure not a crash; a held lease refuses before fetching; a `job_run` row is
written with `items_total`/`items_changed`.

**Deviations / decisions.** (1) `job_run.id` is client-minted (`ValueGenerated.Never`, ADR-026/027) — the
first cut used `new JobRun { }` with `Id = 0` and collided on the second run (`UNIQUE constraint failed:
job_run.id`); fixed by injecting `IIdGenerator` and minting the id, matching every other entity. (2) The two
7.2 criteria that are *mapper* properties — a resumed show becomes `auto_finish_eligible = 0`, and an unseen
status string maps to `Unknown` and is not treated as ended — are proven in `TvmazeShowMapperTests` (the
auto-finish and fail-closed tests); the job test here proves the job *selects the Ended show, invokes the
mapper, and detects the enum transition*, which is the job's own responsibility. (3) The 7.1 criterion "a
future-dated episode appears in the Upcoming block" is satisfied at the data layer (the added episode lands
in `episode`, asserted by the id-stability test); the Upcoming *block* is the Discovery feed (step 4.2),
already proven to read `episode` — no new code here.

**New facts.** The refresh jobs are the first callers of the mapper's `MapShowAsync(int)` fetch path from a
*job* (the use case was the first from the UI); the `ITvmazeShowSource` seam registered in 3.1 makes it live.
**Unblocked:** Phase 7's two airing-catalog jobs exist; 7.4 (`ImdbImport`) and 7.5 (`/akas`) are independent
and remain; 7.3 stays a HARD STOP on open question 1.

### 2026-09-30 — Steps 5.1, 5.2, 5.3, 6.1: Tracking UI + show details (category tabs, expansion, toggle, auto-finish, links)

**Files added:** `Shows.App/ViewModels/TrackedShowCardViewModel.cs`, `TrackingViewModel.cs`,
`ShowDetailsViewModel.cs`, `SeasonGroupViewModel.cs` (+ `EpisodeRowViewModel`);
`Shows.App/Views/TrackedShowCardView.xaml(.cs)`, `TrackingPage.xaml(.cs)`, `ShowDetailsPage.xaml(.cs)`;
`Shows.Tests/Integration/ShowDetailsReadTests.cs` (2 tests); `tasks/5.1-category-tabs.md`,
`tasks/5.2-5.3-6.1-show-details.md`.
**Files added (Application/Infrastructure):** `SetSeasonExpandedUseCase` (in `ReadUseCases.cs`);
`ITrackedShowRepository.FindByIdWithGenresAsync` + its EF implementation.
**Files changed:** `Shows.Infrastructure/ShowsInfrastructureServiceCollectionExtensions.cs` (registered
`GetCategoryOverview`, `GetShowDetails`, `ChangeCategory`, `RecomputeProgress`, `ToggleWatchedEpisode`,
`SetSeasonExpanded`); `Shows.App/AppShell.xaml(.cs)` (My Shows tab + `ShowDetails` route);
`Shows.App/MauiProgram.cs` (VMs/pages registered); `Shows.Application/UseCases/ReadUseCases.cs`
(`GetShowDetailsUseCase` now reads genres).

**The tracking UI is the first thing to read the user's own data.** Four category tabs over
`user_show` joined to `tracked_show` (SPEC F4), each a virtualized list with poster, R3 title, and a
progress bar from the cached columns. Untrack is a logical delete (ADR-024): the card runs
`UntrackShowUseCase` (sets `deleted_at`), removes itself from the visible list, and reports
"Removed from tracking. Your watched history is kept." — never "deleted", and no permanent-delete affordance.

**The details page is the auto-finish surface (R5/R6/R9/R10).** `ShowDetailsViewModel` loads via
`GetShowDetailsUseCase` (seasons, episodes, watched set, persisted expansion). A season header toggles
expansion and persists it through `SetSeasonExpandedUseCase` (R9 — a fresh launch restores it). An episode
row's toggle runs `ToggleWatchedEpisodeUseCase` → `RecomputeProgressUseCase` (the single recompute path,
ARCHITECTURE §5) and refreshes the header bar; the auto-finish move into `Finished` happens inside the use
case, never in the VM. External links are built from `TvmazeId`/`ImdbId` at render (SPEC P5) — the only
place either URL format appears.

**Two new seams, both tested.** `SetSeasonExpandedUseCase` (R9) and `FindByIdWithGenresAsync` (F5) are the
Application/Infrastructure additions the details page needed. The genre read is a separate method from the
hot-path `FindByIdAsync` because the jobs and the recompute never read genres and must not pay the join.
The 2 new tests prove the genre names load and that expansion round-trips as insert-or-update (a second
toggle updates the same row, not a duplicate).

**Verification:**
> dotnet build Shows.sln
> Build succeeded. 0 Warning(s) 0 Error(s)
> dotnet test Shows.Tests
> Passed! - Failed: 0, Passed: 161, Skipped: 0, Total: 161

**Deviations / decisions.** (1) The plan's 5.3 matrix item "untoggle → moves back out of `Finished`" has
**no specified mechanism**: nothing stores which category a show was auto-finished *from*, so a recompute
cannot know where to move it back. `RecomputeProgressUseCase` only ever moves a show *into* `Finished`
(R5); it never moves one out. I did **not** invent a mechanism (AGENTS.md: do not decide unilaterally) —
flagged as a new open question in `STATE.md`. The other three matrix rows (Ended→Finished, Running→stays,
specials-only→not finished) are implemented and exercised by the existing `RecomputeProgress`/`ShowProgress`
tests. (2) The "Fetch localized titles" button is present but reports "arrives with step 7.5" — the provider
has no `/akas` method yet, so the seam is specced now and wired in 7.5. (3) `CheckBox` has no `Command` in
MAUI, so the episode toggle is a `TapGestureRecognizer` on the row with the checkbox as a OneWay visual —
the command is the single source of truth, avoiding a double-flip.

**Unblocked.** Phase 7 (the jobs) — the UI now exercises every read/write path the jobs must respect
(`deleted_at IS NULL` selection, the single recompute, the lease discipline). The details page is the
consumer for step 7.5's on-demand `/akas` path.

### 2026-09-30 — Steps 4.1 + 4.2: Search & Discovery UI (first real UI)

**Files added:** `Shows.App/ViewModels/ShowCardViewModel.cs` (shared card + add flow),
`SearchViewModel.cs` (debounced), `DiscoveryViewModel.cs`; `Shows.App/Views/ShowCardView.xaml(.cs)`,
`SearchPage.xaml(.cs)`, `DiscoveryPage.xaml(.cs)`; `Shows.App/Converters/InvertBoolConverter.cs`,
`StringNotEmptyConverter.cs`; `Shows.Tests/Integration/SearchShowsUseCaseTests.cs` (3 tests);
`tasks/4.1-search-ui.md`, `tasks/4.2-discovery-ui.md`.
**Files changed:** `Shows.Infrastructure/ShowsInfrastructureServiceCollectionExtensions.cs`
(`SearchShowsUseCase` + `GetUpcomingShowsUseCase` registered scoped); `Shows.App/App.xaml`
(converters + `SecondaryButton` style); `Shows.App/AppShell.xaml` (Search + Discovery tabs, placeholder
`MainPage` removed); `Shows.App/MauiProgram.cs` (VMs + pages registered transient).

**The first business logic driven from a running app.** Search and Discovery are the first UI to call the
3.2 use cases through the mapper. `ShowCardViewModel` is shared by both feeds (SPEC F3 names it as the
shared unit), so the add flow and the R3 title split (`TitleRules.Select`) exist in exactly one place.

**The captive-dependency seam is the load-bearing decision.** The ViewModels live on the Shell's singleton
root but the use cases and repositories are scoped to a DbContext. Each VM/card holds only the singleton
`IServiceScopeFactory` and opens a fresh `CreateAsyncScope()` per operation — no scoped service is captured
into a long-lived object. This is the one place a ViewModel touches the container, and it touches only the
factory (ARCHITECTURE §2: no View/ViewModel names an Infrastructure type — rule 4 still green).

**Debounce (SPEC F2).** `SearchViewModel` cancels the in-flight `Task.Delay` on every keystroke via
`CancelAsync` (CA1849: the synchronously-blocking `Cancel` is disallowed) and hands the token to the use
case, so a late response is dropped rather than racing a newer query's results onto the list. The superseded
CTS is intentionally not disposed — a pending `Task.Delay` is still registered on its token, and disposing
while that continuation is scheduled would race; a plain CTS used only for `Task.Delay` holds no unmanaged
resource, so collection is correct.

**R14 surfaces, not swallows.** Both VMs catch the provider's `TvmazeUnavailableException` and render its
message (naming the host) rather than an empty list — "TVmaze had nothing" and "we never reached TVmaze"
must stay distinguishable in the UI, not just in the DB.

**Verification:**
> dotnet build Shows.sln
> Build succeeded. 0 Warning(s) 0 Error(s)
> dotnet test Shows.Tests
> Passed! - Failed: 0, Passed: 159, Skipped: 0, Total: 159

The 3 new tests prove SPEC F2's already-tracked marker reads `user_show` where `deleted_at IS NULL`, not
`tracked_show`: an active show reports tracked, a **soft-deleted** show reports **not** tracked (so re-add
is not blocked — ADR-024), and an empty provider result short-circuits without a repository read.

**Deviations.** (1) The plan's 4.1 criterion "confirm it appears in the Started tab" is a step 5.1 check —
the category tabs do not exist yet, so this step proves the add writes `tracked_show` + `user_show` (the
3.2 integration test already covers that end-to-end). (2) The card's meta line (year · status · ★rating) is
built as a single string in the VM rather than conditional `Span`s in XAML — a `Span` has no `IsVisible`,
so the dangling-separator bug cannot be fixed in the view. (3) TVmaze search hits carry no `title_localized`
(the localized title comes from the export/IMDb akas, not TVmaze), so the localized-primary path is proven
by the `TitleRules` unit test and the card binding, not by a live search.

**Unblocked.** Phase 5 (category tabs, 5.1) can render the shows these feeds add. The add path is now
reachable from a running app, not only from tests.

### 2026-09-30 — Post-promote fixes: TVmaze specials, promote progress logging, `/akas` planned

**Files changed (code):** `Shows.Infrastructure/Tvmaze/TvmazeCatalogProvider.cs` (fetch
`shows/{id}/episodes?specials=1`); `Shows.Infrastructure/Import/Promoter.cs` (per-show Information
progress log; branch-1 watched frontier computed over regulars only);
`Shows.Tests/Integration/TvmazeCatalogProviderTests.cs` (fake handler key updated to the `?specials=1`
URL). **Files changed (plan only, no code):** `docs/IMPLEMENTATION_PLAN.md` (new step 7.5 + step 6.1
button), `docs/SCHEMA.md` §3 (`tvmaze_akas`), `docs/DATA_ANALYSIS.md` §8 facts 15–16, `docs/STATE.md`
(step table).

**Specials were invisible to the mapper.** TVmaze's `/shows/{id}/episodes` **omits specials unless
`?specials=1` is passed** (DATA_ANALYSIS §8 fact 15). The mapper's fetch path requested the plain URL,
so specials never reached the `episode` table and `has_unwatched_specials` was always false. Fixed by
appending `?specials=1` in `FetchAsync`. The cache keys on the full relative URL, so it is a distinct
entry from the plain `/episodes` — no cache collision. Specials are fetched so they EXIST as rows;
SPEC R4 still excludes them from progress and the import backfill still never marks them watched.

**Branch-1 frontier hardened against the new specials.** With specials now in the catalog, the
explicit-unwatched-list branch must compute the watched frontier over **regulars only** — a season-0
special present in the catalog would otherwise set the boundary to season 0 and mark nothing. The
intersection is now taken over `regulars`, not all episodes.

**Promote progress logging.** Each show now logs at Information: `Promote {Index}/{Total}: myshows
{id} (tvmaze {id}, {status})`. 547 shows over the network is slow enough that silence reads as a hang;
the default minimum level is Information (logging.json overrides only Microsoft/EF/HttpClient to
Warning), so these appear in the log file.

**`/akas` grounded live before planning (the step-3.1 lesson).** Probed `shows/59985/akas` through the
proxy: the array is `{ name, country:{ name, code, timezone } }` — **keyed on `country.code`, not a
language field** (there is none). Show 59985 returns three `RU` akas (`Начало`, `Сначала`,
`Перезагрузка`) and two `CN`. So "several titles for one language" = several akas per **country code**;
the pick is the first (lowest array order) for the target country. Planned as step 7.5 (a provider
`GetAkasAsync` seam + `tvmaze_akas` source table + an Enrichment `--akas` mode, decoupled from the IMDb
jobs and open question 1) and a details-page button (step 6.1) that lists all akas. No code yet.

**Verification:**
```
> dotnet test Shows.Tests --filter "FullyQualifiedName~TvmazeCatalogProviderTests|FullyQualifiedName~PromoterTests|FullyQualifiedName~TvmazeShowMapperTests"
Passed!  - Failed: 0, Passed: 29, Skipped: 0, Total: 29
> dotnet test Shows.Tests
Passed!  - Failed: 0, Passed: 156, Skipped: 0, Total: 156
```

**Unblocked.** Re-running `--promote` (or the EpisodeRefresh job, step 7.1) now pulls specials into the
episode table for already-promoted shows. Step 7.5 is specced and depends only on the provider (3.1,
done), so it can ship before the IMDb jobs.

### 2026-09-30 — Step 2.6: `--promote` (two-phase import, second phase) + ADR-030 view demotion

**Files added:** `Shows.Infrastructure/Import/Promoter.cs` (the `--promote` job body);
`Shows.Tests/Integration/PromoterTests.cs` (12 tests);
`Shows.Infrastructure/Persistence/Migrations/20260929202534_0004_StagingIssuesImdbWarning.cs`
(+ `.Designer.cs`, scaffolded via `dotnet ef`, model unchanged so the Designer copies 0003 verbatim);
`tasks/done/2.6-promote.md`.
**Files changed:** `Shows.Infrastructure/Jobs/ImportJob.cs` (`--promote` flag + dispatch);
`Shows.Infrastructure/ShowsInfrastructureServiceCollectionExtensions.cs` (mapper registered as the
concrete `TvmazeShowMapper` then exposed as `IShowMapper`; `Promoter` factory-registered);
`Shows.Infrastructure/Persistence/Migrations/20260929202534_0004_…` (the view);
`docs/SCHEMA.md` §6 (view reconciled to the real migration + ADR-030 note);
`docs/DECISIONS.md` (ADR-030); `docs/STATE.md`; `Shows.Tests/Architecture/LayerBoundaryTests.cs`
(`Promoter` added to the rule-5 allow-list and the rule-8 bulk-writer list);
`Shows.Tests/Integration/StagingViewTests.cs` (`Null_imdb_id_is_blocking…` → `…_is_warning…`).

**Open question 7 resolved (ADR-030).** The step 2.5 review left exactly two blocking rows
(`73305`, `96144`), both tvmaze-resolved with no `imdb_id`. The user chose to **demote
`unresolved_imdb_id` from blocking to warning**: a show with a `tvmaze_id` is never blocking for
promotion just for lacking an IMDb id (IMDb is enrichment, step 4.1; the mapper needs only TVmaze).
Implemented as migration `0004`, which DROPs and re-CREATEs the hand-written `v_staging_issues` with
the single `WHEN s.imdb_id IS NULL THEN 'blocking'` line removed; `Down` restores the 0003 text
verbatim. **A drift was found and fixed in the same step:** `SCHEMA.md` §6 had documented a
JOIN-based view with `imdb_count_mismatch`/`tvmaze_count_mismatch` branches that the *actual*
migration 0003 never contained — the doc had drifted from the shipped SQL. §6 was reconciled to the
authoritative migration text (the JOINs and both count branches are real in 0003; only the severity
CASE changed in 0004). The blocking set is now exactly `{unresolved_tvmaze_id, watched_exceeds_total}`.

**The Promoter.** `internal sealed`, factory-registered (unwraps `IOptions<ShowsOptions>`), mirroring
`TvmazeIdResolver`'s lease discipline. Flow: acquire the exclusive `promote` lease; **refuse if any
foreign lease is live** (raw SQL — EF Core's SQLite provider cannot translate a `DateTimeOffset`
comparison in LINQ at all, so liveness is checked where the provider can't, using the same TEXT
comparison the lease's own acquire uses); **refuse if any `v_staging_issues` row is `blocking`**. Then
per target (`promoted_at IS NULL AND tvmaze_id IS NOT NULL`, lowest `myshows_id` first): fetch the
payload **outside** the transaction (a network round-trip must not hold a write lock; a transport
failure leaves the show un-promoted — R14), then in one transaction create the `tracked_show` shell
(source row via `EnsureSourceRowAsync`, shadow FK by property-name string), map the payload, run the
four-branch backfill, write `user_show` (category from `source_watch_status`, `needs_review` authored
in the same insert), stamp `promoted_at`. A failure rolls the whole show back and is retried next run
with fresh ids (never resumed). `db.ChangeTracker.Clear()` runs after every show so a rolled-back
show's entities cannot leak into the next one's `SaveChanges`.

**Two ordering decisions that the docs' literal wording would have gotten wrong:**
1. **Branch order.** `DATA_ANALYSIS.md` §5 lists the branches 1→2→3→4, but branch 2 (`watched>0`
   heuristic, always `needs_review=1`) must NOT precede branch 3 (`watched==total`, all regulars,
   `needs_review=0`) — the §5 distribution table has **33** fully-watched shows that must stay
   un-flagged, and literal order would flag all 33. The code checks `watched==total` first.
2. **`user_show` is written after the backfill**, not before, so `needs_review` (a backfill output) is
   authored in the single `user_show` insert rather than patched afterwards — the plan's "write
   needs_review with the user_show row" intent, achieved without a post-mapper user-table update.

**Verification — green run:**
```
> dotnet test Shows.Tests --filter Category=Architecture
Passed!  - Failed: 0, Passed: 10, Skipped: 0, Total: 10
> dotnet test Shows.Tests --filter FullyQualifiedName~PromoterTests
Passed!  - Failed: 0, Passed: 12, Skipped: 0, Total: 12
> dotnet test Shows.Tests
Passed!  - Failed: 0, Passed: 156, Skipped: 0, Total: 156
> dotnet build Shows.sln
Build succeeded. 0 Warning(s) 0 Error(s)
```

**What the 12 Promoter tests prove (temp migrated DB, fake `ITvmazeShowSource`, no network):**
fully-watched → all regulars + `auto_finish_eligible` + `needs_review=0`; specials never marked
(`has_unwatched_specials` survives); explicit unwatched list marks every regular before the earliest
gap; a list that disagrees with the count is flagged; no-list partial uses the first-N heuristic and
is always flagged; `total=0` promotes to `total_regular=0`/`auto_finish_eligible=0`; the four
`source_watch_status` values map to the four categories; `user_show` count == `tracked_show` count;
re-run promotes nothing and leaves every watch id unchanged; a blocking row refuses and writes
nothing; a live foreign lease refuses; a **stale** foreign lease proceeds (expiry, not cleanup, is
the recovery path).

**Not run against the real DB (by design — the user runs `--promote`).** The real `data/shows.db`
was backed up to `backup/pre-promote-20260930-002807/` (all three files: `.db`, `-shm`, `-wal`)
before this step. The real-DB golden checks in the plan (show `86813` = 1 watched of 33; category
counts 78/246/16/207; the `total=0` show; `needs_review` count; no `myshows_id` outside `staging_*`;
batch-58 reconciliation) are the user's post-run verification — recorded in `tasks/done/2.6-promote.md`.

**New facts learned.** EF Core 8's `DatabaseFacade` has **no** `ExecuteScalarAsync` — a scalar over a
raw query is `SqlQueryRaw<long>("SELECT COUNT(*) AS [Value] …").SingleAsync()` (the column must be
aliased `[Value]` to bind). `COUNT(*)` boxes as `Int64` through `Microsoft.Data.Sqlite`. The
`dotnet ef` tool (9.0.3) scaffolds a migration against the EF Core 8 project cleanly; a view-only
migration's `Up`/`Down` come out empty and are filled by hand, and its Designer copies the prior
migration's `BuildTargetModel` verbatim because the model is unchanged.

**Unblocked.** Step 2.6 is implemented and tested; the user can run `--promote` against the real DB.
Step 8.2 (integration tests on temp SQLite) is now substantially covered by the Promoter tests. Phase
2 ends at the promote; Phase 4/5 UI (4.1+) can begin against a promoted database.

### 2026-09-29 — Step 8.1: architecture tests (every rule proven to reject an injected violation)

**Files added:** `Shows.Tests/Architecture/LayerBoundaryTests.cs` (9 tests, `Category=Architecture`).
**Files changed:** `Shows.Tests/Architecture/ApplicationInterfaceTests.cs` (added the
`Category=Architecture` trait so the existing no-`IQueryable` rule is part of the filtered set).

Nine rules, each the compiler-visible proxy for an `AGENTS.md`/`ARCHITECTURE.md` invariant:
1. Application references no EF/HTTP/MAUI/Newtonsoft (NetArchTest on the Application assembly).
2. No Application signature returns `IQueryable` (the pre-existing step 1.4 test, now in the set).
3. Domain has zero packages (csproj scan) **and** no Domain type depends on EF/JSON/HTTP/DataAnnotations.
4. UI references Infrastructure only in `MauiProgram.cs` (source scan — the MAUI assembly is
   `net8.0-windows` and cannot be loaded from the `net8.0` test project).
5. Source-table containment: only the mapper, DbContext, JSON mapper, fetch seam, provider and payload
   reference the `…Persistence.Sources` namespace.
6. User-data containment: the mapper has no dependency on `UserShow`/`UiSeasonState`.
7. No store-generated keys: the EF model has no `ValueGenerated.OnAdd`/`OnAddOrUpdate` property, and no
   migration contains `AUTOINCREMENT`.
8. Lease discipline: each bulk writer (`ImdbIdScraper`, `TvmazeIdResolver`) takes `IWriterLease`.

**Verification — green run:**
```
> dotnet test Shows.Tests --filter Category=Architecture
Passed!  - Failed: 0, Passed: 10, Skipped: 0, Total: 10
> dotnet test Shows.Tests
Passed!  - Failed: 0, Passed: 144, Skipped: 0, Total: 144
> dotnet build Shows.sln
Build succeeded. 0 Warning(s) 0 Error(s)
```

**Verification — each rule proven to fail (the plan's core requirement).** A violation was injected for
every rule, the filtered run observed to fail exactly that test, then the violation was removed and the
run returned green. The violation injected per rule:
- **Rule 1:** `Shows.Application/ArchViolationProbe.cs` with a `System.Net.Http.HttpClient` property →
  `Application_does_not_reference_ef_http_maui_or_newtonsoft` failed.
- **Rule 2:** not re-injected — already proven in step 1.4; this step only added the trait.
- **Rule 3:** `Shows.Domain/ArchDomainProbe.cs` with a `System.Text.Json.JsonDocument` property →
  `Domain_types_are_plain_csharp…` failed while `Domain_csproj_declares_no_package_references` **passed**
  (System.Text.Json is in the shared framework, so the two halves are independent — exactly the point).
- **Rule 4:** `Shows.App/ArchViolationProbe.cs` naming `Shows.Infrastructure.DataPaths` →
  `Only_the_composition_root_in_the_app_references_infrastructure` failed.
- **Rule 5:** `Shows.Infrastructure/Import/ArchViolationProbe.cs` referencing `TvmazeShow` →
  `Only_the_mapper_and_its_seams_reference_the_source_tables` failed.
- **Rule 6:** a `UserShow` method parameter on `TvmazeShowMapper` →
  `The_mapper_does_not_reference_the_user_owned_tables…` failed.
- **Rule 7a:** `ValueGeneratedOnAdd()` on `TrackedShow.Id` applied **after** the `ApplyNeverValueGeneration`
  sweep → `No_entity_property_is_store_generated` failed (the silent regression the model assertion catches).
- **Rule 7b:** an `AUTOINCREMENT` token added to migration `0001_MainSchema.cs` →
  `No_migration_emits_autoincrement` failed.
- **Rule 8:** a lease-less `ArchBulkWriterProbe` added to the bulk-writer list →
  `Every_bulk_writer_resolves_the_writer_lease` failed.

**New facts learned:**
- **NetArchTest 1.3.2 specifics:** the result type is `NetArchTest.Rules.TestResult` (not
  `EvaluationResult`), with `IsSuccessful` and `FailingTypeNames` (a string collection — no need to name
  the Mono.Cecil element type). `HaveDependencyOnAny` matches **full** type names, so `nameof(UserShow)`
  (a simple name) matches nothing and the rule passes vacuously — the first rule-6 probe (a `UserShow`
  property) did NOT fail until the test used `"Shows.Domain.UserShow"` and the probe used a method
  parameter (a property-typed reference was not registered as a dependency).
- **The step 3.2 repository-owns-shell fix would have failed rule 5.** A repository that minted a
  `tvmaze_show` row is a second source-row writer — precisely what SPEC R11 / source-table containment
  forbids. This is what drove the step 3.2 refactor to mapper-owns-shell (the repository sets the shadow
  FK by property-name string, so it never references the source type and stays off the offender list).
  The architecture test is the guard that keeps that design from regressing.

**Unblocked:** the layer invariants are now executable, so any later step (the Phase 4/5 UI, the Phase 7
jobs, `--promote`) that crosses a boundary fails CI rather than drifting silently. No DB touched.

### 2026-09-29 — Step 3.2: `AddShowToTrackingUseCase` + `UntrackShowUseCase` (two real bugs fixed)

**Files added:** `Shows.Tests/Integration/AddShowToTrackingUseCaseTests.cs` (3 tests — the plan's whole
acceptance). **Files changed:** `ShowsInfrastructureServiceCollectionExtensions.cs` (scoped registration of
both use cases), `Shows.Application/Interfaces/ITrackedShowRepository.cs` +
`Shows.Infrastructure/Persistence/Repositories/TrackedShowRepository.cs` (Bug 1 + Bug 2 fixes), and
`Shows.Application/UseCases/AddShowToTrackingUseCase.cs` (Bug 2 — the initial lookup now uses the
include-deleted find). The use cases themselves were already written in step 0.3; this step registered them
and proved them, and the test exposed two orderings the task file had flagged as suspect.

**Bug 1 — NOT NULL `tvmaze_show_id` FK violated on the first save.** The new-show path saved the
`tracked_show` shell (only `Id` + `TvmazeId`) before the mapper wrote the source row, but
`tracked_show.tvmaze_show_id` is a NOT NULL FK to `tvmaze_show` (SCHEMA §2 — a mapped row cannot exist
without its source row). The first `SaveChangesAsync` threw `SQLite Error 19: FOREIGN KEY constraint
failed` at `AddShowToTrackingUseCase.cs:93`. The fix is the design the mapper test's `SeedTrackedShellAsync`
comment already described as "mirrors AddShowToTracking": the **mapper** mints the source-row shell, because
it is the sole writer of source rows (SPEC R11) — neither the use case (which must not reference EF or a
source table, ARCHITECTURE §2) nor the repository may create one. Added `IShowMapper.EnsureSourceRowAsync`,
implemented in `TvmazeShowMapper` to insert a minimal `tvmaze_show` shell when none exists (no-op otherwise,
writes nothing to the mapped or user tables); the use case calls it before `AddAsync`. `TrackedShowRepository.AddAsync`
then only points the shadow FK at the row — by property name (`Property<int>("tvmaze_show_id")`), so the
repository never references the source entity type, which source-table containment (step 8.1) forbids. The
mapper overwrites the shell with the real payload on the first map.

**Bug 2 — the restore branch was unreachable.** The use case's first lookup was `FindByTvmazeIdAsync`,
which filters `deleted_at IS NULL` (the one active-only predicate). A soft-deleted show returned null, so
the use case fell to the new-show path and would hit the UNIQUE `tvmaze_id` constraint on the existing
`tracked_show` row. Added `ITrackedShowRepository.FindByTvmazeIdIncludingDeletedAsync` (the explicit
escape hatch the interface's doc-comment already promised for the re-add path) and pointed the use case's
initial lookup at it; the restore branch then reaches `RestoreAsync` as designed.

**Verification — full suite (no network; fake `ICatalogProvider` + fake `ITvmazeShowSource` over the real
Black Mirror payload, real repositories/mapper/UoW):**
```
> dotnet test Shows.Tests
Passed!  - Failed: 0, Passed: 135, Skipped: 0, Total: 135
```
The 3 tests cover: add-twice → exactly one `tracked_show`, one `user_show`, one `tvmaze_show`, and 3
episodes (no duplicates); re-add after a watch mark into a different category → the mark survives and the
original category is NOT reset; add→untrack→add → one `user_show` row, `deleted_at IS NULL`, the original
`added_at` preserved, and the watch mark still present pointing at the same episode id. Before the fixes,
all 3 failed with the FK violation (test 1) / would have failed the UNIQUE constraint (test 3).

**Verification — full solution build (strict, warnings-as-errors):**
```
> dotnet build Shows.sln
Build succeeded. 0 Warning(s) 0 Error(s)
```

**New facts learned:** the source-row shell belongs to the **mapper**, not the repository — SPEC R11 makes
the mapper the sole writer of source rows, so a repository that minted a `tvmaze_show` would be a second
source-row writer and would fail the step 8.1 source-table-containment test. The repository sets the shadow
FK by property-name string (no type reference to the source namespace), which is the only way it can satisfy
the FK without importing the source entity type. The `deleted_at IS NULL` predicate being centralized in the
repository is a double-edged sword: it is exactly why the restore path needed a dedicated include-deleted
find. This refactor was driven by the step 8.1 source-table-containment rule (SPEC R11: the mapper is the
sole source-row writer) — applying that rule to the step 3.2 fix caught the repository-owns-shell version
as a violation, so the shell moved to the mapper before it could ship.

**Unblocked:** steps 4.1 (`SearchViewModel`), 4.2 (`DiscoveryViewModel`) and 5.1 (category tabs + untrack)
now have a working add/untrack path to call. No DB was touched — the test runs on an in-memory/temp SQLite
fixture, not `data/shows.db`.

### 2026-09-29 — Step 2.3: TVmaze id resolution (`--resolve`) + a job config-loading fix

**Files added:** `Shows.Infrastructure/Import/TvmazeIdResolver.cs` (the `--resolve` job body — a
resumable/capped/lease-guarded bulk writer over the provider's `/lookup?imdb=` seam, mirroring the step
2.2 scraper's control structure), `Shows.Tests/Integration/TvmazeIdResolverTests.cs` (5 tests).
**Files changed:** `Jobs/ImportJob.cs` (`--resolve` flag: a DB-writing mode, runs after stage/reset and
before report so `--resolve --report` reports the post-resolution state; a refused run exits non-zero),
`ShowsInfrastructureServiceCollectionExtensions.cs` (scoped resolver via factory, unwrapping
`IOptions<ShowsOptions>` like the scraper), `appsettings.json` (`Tvmaze.MaxRequestsPerRun`, default 0 =
uncapped), and **`Jobs/ShowsJobRunner.cs`** (the config-loading fix below — affects every job).

**Verification — resolver tests (no network, fake `ICatalogProvider`):**
```
> dotnet test Shows.Tests
Passed!  - Failed: 0, Passed: 132, Skipped: 0, Total: 132
```
The 5 tests cover: a hit → `tvmaze_id` + `tvmaze_resolution='scraped'`; a 404 → `tvmaze_id` NULL +
`'unresolved'`; a transport failure (`TvmazeUnavailableException`) → the row is **left untouched** and
counted failed, NOT unresolved (R14: a dead proxy must not look like "TVmaze has no data"); resumable —
a show that already has a `tvmaze_id` (scraped **or manual**) is never a target, so the manual id is
never overwritten; the per-run cap takes the lowest `myshows_id` first; and a held lease → refused.

**Verification — live run against the real DB (backed up first):**
```
> dotnet run --project Shows.Jobs.Import -- --resolve
[INF] Resolve done: 546 looked up, 539 resolved, 7 not on TVmaze, 0 transport-failed
      (1 already had a tvmaze_id before this run).
```
Read-only DB check after the run (throwaway console, `Mode=ReadOnly`):
```
tvmaze_resolution: scraped=539, unresolved=8
tvmaze_id:         set=540, null=7
imdb_id:           set=546, null=1
show 96144:        imdb_id=NULL, tvmaze_id=86754, tvmaze_resolution=unresolved   (manual id INTACT)
v_staging_issues:  blocking=8 (7 unresolved_tvmaze_id + 1 unresolved_imdb_id), warning=2 (zero_total_episodes)
```
- **The blocking count dropped from 547 to 8** — the whole point of the step. The 7 `unresolved_tvmaze_id`
  are genuine TVmaze gaps (TVmaze has no show for those IMDb ids: `tt6209442`, `tt6566576`, `tt13323004`,
  `tt17506706`, `tt23897266`, `tt22332334`, `tt16858200`); they stay blocking and the user reviews them at
  the step 2.5 gate. The 1 `unresolved_imdb_id` is show 96144 (the manual-tvmaze show, no IMDb link).
- **The manual `tvmaze_id=86754` on 96144 survived** — it has no `imdb_id`, so it was never a target, and
  the resolver never wrote it. (Its `tvmaze_resolution` still reads `unresolved` because the importer seeds
  that and the resolver only writes rows it targets; the `tvmaze_id` is what `v_staging_issues` and the
  mapper read, and it is correct. Cosmetic; see the deviation note.)
- 0 transport failures across 546 lookups — the proxy held for the whole run (~2.5 min at the 250 ms throttle).

**Deviation — `tvmaze_resolution` for the manual show reads `unresolved`, not `manual`.** The plan names
`manual` as a resolution value, but the resolver only writes rows it targets (those with an `imdb_id` and
no `tvmaze_id`), and 96144 has no `imdb_id`, so it is never targeted and its resolution stays at the
importer's seed value. This is harmless — `tvmaze_id` is the column the gate and the mapper read, and it
is correct — but it means `manual` is currently never written by any code path. Flagging it rather than
silently "fixing" it: writing `manual` would require the resolver to touch non-target rows, which is the
exact overwrite the resumability rule forbids. If `manual` should be set, it belongs to the user's manual
edit in DB Browser (step 2.5), not to a job.

**Bug fix — `ShowsJobRunner` was not loading `appsettings.json` for HTTP jobs (affects every job).** The
config source used an absolute `Path` with no `FileProvider`, so `JsonConfigurationSource` resolved it
against the host's **ContentRootPath** (the project folder under `dotnet run`), not the assembly directory
— a path outside that root is silently not found, leaving every `Shows:*` value empty and the named
clients with no `BaseAddress`. The first HTTP call then threw `An invalid request URI was provided`. The
step 2.2 scrape had run in an earlier session before the `Sources.Insert(0, …)` change, so this path was
never exercised for HTTP until now. Fix: pin the source to the assembly directory with an explicit
`PhysicalFileProvider(assemblyDirectory)` and a relative `Path` — the same pattern the step 3.1 probe had
already proven. Verified: the live `--resolve` run bound `BaseUrl`/proxy correctly and made 546 successful
proxied lookups. `dotnet build Shows.sln` → 0 warnings / 0 errors.

**New facts learned.** (1) 7 of the 546 IMDb ids have no TVmaze counterpart — a real data gap, not a
lookup failure; they remain blocking for human review. (2) The `JsonConfigurationSource` content-root
gotcha: an absolute `Path` without a `FileProvider` is resolved against `ContentRootPath`, not the path's
own directory — the fix is an explicit `PhysicalFileProvider`.

**Unblocked.** Step **2.5** (human review gate) is now meaningful: `v_staging_issues` has dropped to 8
blocking + 2 warning, so the user has a small, real review set instead of 547 rows. Step **2.6**
(`--promote`) depends on 2.5. The 7 unresolved shows are the user's decision at the gate (find the TVmaze
id by hand, or drop them).

### 2026-09-29 — Step 3.1: `TvmazeCatalogProvider` + `http_cache` revalidation + Polly

**Files added:** `Shows.Infrastructure/Tvmaze/TvmazeCatalogProvider.cs` (implements **both** seams —
`ICatalogProvider` for the read model and `ITvmazeShowSource` for the mapper's `MapShowAsync(int)` fetch
path; `internal sealed`, one instance registered as both), `TvmazeJsonMapper.cs` (pure JSON→entity/DTO
translation, null-tolerant readers — TVmaze adds/removes fields without notice), `HttpCacheStore.cs`
(ETag/`Last-Modified` revalidation over `http_cache`; transport-layer only, read by nothing but the
provider), `TvmazeUnavailableException.cs` (the R14 fail-loud exception), `Http/TvmazePolicies.cs`
(Polly retry + circuit breaker), `Shows.Tests/Integration/TvmazeCatalogProviderTests.cs` (10 tests).
**Files changed:** `ShowsInfrastructureServiceCollectionExtensions.cs` (the `Tvmaze` named client gets
`TvmazePolicies.Combined()`; the provider + cache store registered as both seams; the mapper's
`GetService<ITvmazeShowSource>()` now resolves, so `MapShowAsync(int)` is live).

**Verification — fixture tests (no network, the plan's requirement):**
```
> dotnet test Shows.Tests
Passed!  - Failed: 0, Passed: 127, Skipped: 0, Total: 127
```
The 10 new tests drive the provider through a fake `IHttpClientFactory` over a controllable handler,
backed by a real migrated temp DB for `http_cache`. They cover: the Black Mirror payload mapped verbatim
into source rows (DATA_ANALYSIS §8), 404→null, the `CatalogShow` read model collapsing
`runtime ?? averageRuntime`, the **score-envelope unwrap** (see deviation below), `LookupByImdbIdAsync`
id/null, the R14 rule (transport failure and 5xx both throw `TvmazeUnavailableException` naming the host,
never return empty), and the **cache-revalidation assertion** — a cached body is reused on 304 with an
`If-None-Match` header, not refetched (the handler asserts the conditional header arrived and counts
exactly one revalidation across two calls).

**Verification — live probe through the proxy (the plan's route/rate-limit/status criterion).** A throwaway
console in `%TEMP%` (outside the repo, so never in `Shows.sln`) used the **real DI container** — real proxy
wiring, throttle, Polly, `http_cache` — pointed at a temp DB so the user's `data/shows.db` was never opened.
Observed:
- **Route.** Every successful call took the **proxy** (`main-pc:10801`): `GetShowAsync(305)` → 200 in ~700 ms
  ("Black Mirror"). A bare direct `HttpClient` (`UseProxy=false`) to `https://api.tvmaze.com/shows/305`
  failed with `TaskCanceledException` (connect timeout) — confirming DATA_ANALYSIS §9: TVmaze is unreachable
  directly, so "success ⇒ proxy" is an observation, not an assumption.
- **Rate limit.** 10 distinct `/search/shows` queries under the 250 ms throttle: **all 200, no 429, no
  `Retry-After`**. The app's own throttle never approaches TVmaze's published limit, so the limiter is the
  binding constraint, not the provider.
- **Status vocabulary** (read back from `tvmaze_status_seen`, written by the real mapper):
  ```
  status_raw='Running' mapped_to=1 occurrences=6
  status_raw='Ended'   mapped_to=2 occurrences=14
  ```
  Only two raw strings surfaced across a spread of 10 well-known shows; both are in the R12 dictionary
  (`mapped_to` non-NULL for every observed value). The dictionary also covers `Upcoming`/`Canceled`; an
  unseen string fails closed to `Unknown` with a `mapped_to NULL` row, so it stays discoverable.
- **`http_cache`**: 30 URLs cached after the run (show + episodes per mapped show, plus the searches).

**Deviation from the plan — a real bug the fixture test initially hid.** The first fixture for
`SearchAsync` wrapped a bare show object in `[ … ]`, which is **not** the real `/search/shows` shape. The
live probe caught it: search returns a **score envelope** `[{ "score": n, "show": {…} }]`, so mapping each
array element as a show threw `JsonException: missing required numeric field 'id'` on every search. The
discovery endpoints (`newshows`, `upcoming`) return **flat** show objects — two array shapes through one
`FetchListAsync`. Fix: `FetchListAsync` unwraps the `show` property when present, else maps the element
directly. The fixture was corrected to the real envelope, and a second test pins the flat shape, so both
shapes are now covered without network. This is exactly why the plan required a live probe alongside the
fixtures — a fixture written from the wrong assumption passes and proves nothing.

**New facts learned.** (1) `/search/shows` and `/search/*` return the `{score, show}` envelope; `newshows`
/`upcoming`/`shows/{id}` return flat show objects. (2) The live status vocabulary for a small spread of
popular shows is just `Running`/`Ended` — the other two dictionary entries are for shows not in the sample.
(3) The proxy adds ~500 ms to the first request (TLS CONNECT) then ~75 ms/request — the pooled connection
dominates after warm-up.

**Unblocked.** Step **2.3** (TVmaze `/lookup?imdb=` resolution) — its 3.1 dependency is satisfied; the
provider's `LookupByImdbIdAsync` is the seam it needs. Step **3.2** (`AddShowToTrackingUseCase`) and the
Phase 7 jobs (7.1/7.2) that depend on 3.1 are likewise unblocked. The phase still ends at the **step 2.5
human gate** before `--promote` (2.6).

### 2026-09-29 — Step 2.4: `--report` CSV export (+ concurrent manual staging edits, protected)

**Files added:** `Shows.Infrastructure/Import/StagingReportExporter.cs` (reads `v_staging_issues`, writes
the CSV, derives the printed counts from the same materialized rows so file and counts cannot drift),
`Shows.Tests/Integration/StagingReportExporterTests.cs` (1 test). **Files changed:**
`Shows.Infrastructure/Jobs/ImportJob.cs` (`--report` flag: a DB-reading mode that migrates first but
writes no table — only the CSV; runs after any stage/reset so `--stage --report` reports what it just
wrote), `DataPaths.cs` + `ShowsOptions.cs` (`ReportPath`, default `data/staging_issues.csv`, resolved like
every other data path), `ShowsInfrastructureServiceCollectionExtensions.cs` (scoped exporter).

**Verification — step 2.4 (against the live DB, read-only):**
```
> dotnet run --project Shows.Jobs.Import -- --report
[INF] Report written to C:\src\shows\data\staging_issues.csv: 547 issue(s) — 547 blocking, 0 warning.
```
CSV file checked directly:
```
lines (incl header) = 548   (header + 547 rows)
header: myshows_id,title_ru,issue,severity
last row: 98511,"Неизведанные тайны парка ""Царство Тьмы""",unresolved_tvmaze_id,blocking
```
- **Counts match the view grouped by severity** (the plan's cross-check): the earlier read-only query
  `SELECT severity, COUNT(*) FROM v_staging_issues GROUP BY severity` returned `blocking=547`, and the
  exporter printed `547 blocking, 0 warning`. The 547 = 546 `unresolved_tvmaze_id` + 1 `unresolved_imdb_id`
  (see below) — all blocking because step 2.3 (TVmaze `/lookup?imdb=`) has not run yet, so no show has a
  `tvmaze_id` except the one the user set by hand.
- The last row proves **RFC 4180 quoting**: the title contains a comma and embedded `"` quotes, correctly
  wrapped and doubled. Cyrillic titles round-trip (UTF-8 with BOM).
- `dotnet build Shows.sln` → 0 warnings / 0 errors. `dotnet test Shows.Tests` → **117 passed** (116 + 1).

**Concurrent user action (step 2.5 review, done directly in DB Browser).** While step 2.4 was being built,
the user edited `data/shows.db` by hand: filled the **5** remaining no-IMDb-link shows with IMDb ids
(`56341→tt6712390`, `76333→tt13275654`, `78564→tt10016724`, `84505→tt26750834`, `96549→tt38362312`) and set
a **manual `tvmaze_id=86754`** on show `96144` ("Донс: История Бд Хейн"), whose TVmaze structure differs so
it has no IMDb link. After the edits, `staging_show.imdb_id` was set on **546/547** (541 scraped + 5 manual).

**Data-safety fix (additive, non-destructive).** The 5 manual ids were written into `staging_show.imdb_id`
**only**, not into the authoritative `staging_imdb_id`. That is fine for review / step 2.3 / promote (all
read `staging_show.imdb_id`), but a future `--reset --stage` would **permanently lose** them: `--reset`
wipes `staging_show`, and the mirror rebuild restores only from `staging_imdb_id`, which lacked them. Fixed
by copying the 5 into `staging_imdb_id` (idempotent `INSERT … ON CONFLICT … DO UPDATE`, transactional,
`busy_timeout`), so they get the same no-FK-cascade protection the 541 scraped ids have. Verified:
```
staging_imdb_id resolved: 541 → 546
AT RISK (show.imdb_id set, no resolved staging_imdb_id row): 5 → 0
```
No table was reset, no row deleted, the scrape was not re-run (it is resumable and would skip resolved
shows anyway).

**New fact for step 2.6 (gate semantics, NOT yet changed).** Show `96144` is legitimately resolved to a
TVmaze id but has no IMDb id, so `v_staging_issues` flags it `unresolved_imdb_id` / **blocking** — `--promote`
would refuse on it despite it being resolved. The view is owned by `SCHEMA.md §6`; a TVmaze-resolved show
with no IMDb should probably not be blocking. Recorded as an open question for step 2.6, not changed here.

**Unblocks:** step 2.4 is complete; the staging layer is fully populated and the report is reproducible.
The remaining Phase 2 work is **step 2.3** (TVmaze `/lookup?imdb=` resolution for the 546 shows still
lacking a `tvmaze_id`), which needs the **step 3.1** HTTP plumbing (`TvmazeCatalogProvider`). The phase
then ends at the **step 2.5 human gate** — `--promote` (2.6) is downstream and must not be run autonomously.

### 2026-09-29 — Step 2.2: `ScrapeMyShows` → `staging_imdb_id`

**Files added:** `Shows.Infrastructure/Import/MyShowsPageParser.cs` (pure `[GeneratedRegex]` parser →
`(ImdbId, KinopoiskId, Ambiguous)`; distinct IMDb match, Kinopoisk from the `film/<digits>` rating anchor
only), `Shows.Infrastructure/Import/IMyShowsPageFetcher.cs` (seam + `PageFetchResult(HttpStatus, Html,
Error)`), `Shows.Infrastructure/Import/MyShowsPageFetcher.cs` (over the `MyShows` named client; transport
failure and timeout returned as data, never thrown — SPEC R14), `Shows.Infrastructure/Import/ImdbIdScraper.cs`
(resumable / capped / lease-guarded; mirrors a resolved id onto `staging_show.imdb_id`),
`Shows.Infrastructure/Jobs/ScrapeJob.cs`, `Shows.Tests/Unit/MyShowsPageParserTests.cs` (5),
`Shows.Tests/Integration/ImdbIdScraperTests.cs` (5). **Files changed:** `Shows.Jobs.ScrapeMyShows/Program.cs`
(one-liner now passes `ScrapeJob.RunAsync`), `ShowsInfrastructureServiceCollectionExtensions.cs` (scoped
fetcher + scraper factory), and `ShowsJobRunner.cs` — see the config-path deviation below.

**Verification — step 2.2 (the two known ids, then a full run against the live DB):**
```
> dotnet run --project Shows.Jobs.ScrapeMyShows
[INF] Scrape done: 547 requested, 541 resolved, 6 no-id, 0 failed (0 were already resolved before this run).
  (exit 0)
```
Read back from `data/shows.db` (throwaway .NET 8 console over `Microsoft.Data.Sqlite`, read-only):
```
staging_imdb_id resolved (authoritative) = 541
staging_show mirror set (derived)        = 541
known ids:  22410 | tt2085059   86813 | tt26448526
STRANDED (resolved in source, mirror null)   = 0
ORPHAN MIRROR (mirror set, no source row)    = 0
totals: staging_show=547 | staging_imdb_id=547
unresolved no-id count = 6
```
- **Both plan assertions hold exactly:** `22410 → tt2085059` (Kinopoisk 655800) and
  `86813 → tt26448526` (Kinopoisk 5445460). **541 / 547 resolved**; the 6 unresolved
  (ids 56341, 76333, 78564, 84505, 96144, 96549) are all `http_status=200, error='no imdb id on page'` —
  genuine pages with no IMDb link, correctly classified as *no-id*, not transport failures and not
  ambiguous. The markup matched what planning verified; no fallback to fuzzy matching was needed.
- `dotnet build Shows.sln` → 0 warnings / 0 errors. `dotnet test Shows.Tests` → **116 passed** (105 + 5
  parser + 5 scraper + 1 remirror, below).

**Deviation 1 — config was loaded from the wrong directory (real bug, caught on first live run).** The
first full scrape exited 1 with `BaseAddress must be set`. `Host.CreateApplicationBuilder` resolves
`appsettings.json` from the **working directory**, and `dotnet run --project` sets that to the project
folder, where there is no `appsettings.json` — so `Shows:MyShows:BaseUrl` was empty and the named client
had no `BaseAddress`. This was the first step to read a config *value* (earlier shells only used the
`DataPaths` marker-walk; logging loads its own `logging.json` from the assembly dir). Fixed
`ShowsJobRunner` to load `appsettings.json` from the **assembly directory** — the same place
`AddShowsLogging` reads `logging.json` — so config resolves identically under `dotnet run` and a
published exe.

**Deviation 2 — config precedence (caught while testing the fix).** The first fix used
`builder.Configuration.AddJsonFile(...)`, which **appends** the source *after* the builder's env-var and
command-line sources, so the JSON file **overrode** environment variables — backwards for a container,
where env vars are how `BaseDirectory`/proxy are set at deploy time (`ARCHITECTURE.md` §4). It also made
a `Shows__MyShows__MaxRequestsPerRun=2` env override silently ineffective (the file's cap of 600 won).
Fixed by inserting the source at index 0 (`Sources.Insert(0, new JsonConfigurationSource { … })`), so the
file is the base and env/args still win.

**Deviation 3 — scraped ids must survive a `--reset`/`--stage` cycle (user-raised data-safety fix).**
`staging_imdb_id` is created with **only a primary key — no foreign key to `staging_show`** (migration
`0003_Staging`), so `--reset`'s `ExecuteDeleteAsync` on `staging_show` cannot cascade into the scraped
ids, and `ResetAsync` deliberately never touches `staging_imdb_id`. That already protects the data from
*deletion*. But tracing the full cycle exposed a gap: `--stage` recreates `staging_show` rows with
`imdb_id = NULL`, and the scraper only re-mirrors when it *scrapes* — which it won't, since the ids are
already resolved (correctly, to avoid re-requesting pages behind myshows' DDoS protection). The result:
after a reset/re-stage the preserved ids would be **stranded** — `v_staging_issues` would report all 541
as `unresolved_imdb_id` and the TVmaze lookup (2.3) would see no id. Fixed `StagingImporter.StageAsync`
to rebuild the derived `staging_show.imdb_id` mirror from the authoritative `staging_imdb_id`
(`UPDATE … FROM`, set-based, zero requests) after inserting new rows. Regression test
`Reset_then_stage_remirrors_saved_imdb_ids_without_losing_them` proves the id survives a reset/re-stage
with no scraper run; the live-DB check above confirms **0 stranded / 0 orphan mirrors**.

**Dev-time recovery (not a product change):** killing a `dotnet run` leaves an orphaned `ScrapeMyShows`
child that keeps renewing the `scrape_myshows` lease and holds the SQLite write lock, so the next run
hangs on migrate (`SQLITE_BUSY`) or is refused. Recovery: `Get-Process -Name ScrapeMyShows | Stop-Process
-Force`, then clear the stale `writer_lease` row. The product's expiry-based recovery (ADR-028) is
unchanged and still covered by `WriterLeaseTests`.

**New facts learned:**
- `staging_imdb_id` has **no FK to `staging_show`** by design — the two are joined only by the shared
  `myshows_id` value, which is what lets `--reset` clear the mirror table without touching the scraped
  source. `staging_show.imdb_id` is a *derived copy* (SCHEMA.md §5), so rebuilding it is always safe.
- myshows.me sits behind DDoS protection; re-requesting an already-resolved page risks a ban, so the
  scraper's targeting query (`NOT EXISTS resolved`) is load-bearing, not just an optimization.
- `UPDATE … FROM` works on the bundled SQLite (Microsoft.Data.Sqlite 8.0.11 ships ≥ 3.33).
- The assembly-dir `appsettings.json` must be **inserted at index 0**, not appended, or it overrides env
  vars — the opposite of the container config model.

**Unblocks:** step 2.3 (TVmaze `/lookup?imdb=`) now has 541 IMDb ids to resolve (still needs the step 3.1
HTTP plumbing); step 2.4 (`--report`) can run against a fully populated staging layer. The phase ends at
the **step 2.5 human review gate**, which has not been reached — `--promote` (2.6) is downstream of it.

### 2026-09-29 — Step 2.1: envelope parser and `--stage`

**Files added:** `Shows.Infrastructure/Import/MyShowsEnvelopeParser.cs` (pure `System.Text.Json` parser
→ `ParsedShow`/`ParsedEpisode`/`EpisodesFileResult` records), `Shows.Infrastructure/Import/StagingImporter.cs`
(`DryRunAsync`/`StageAsync`/`ResetAsync`), `Shows.Infrastructure/Jobs/ImportJob.cs` (public flag dispatch +
gated migrate), `Shows.Tests/Unit/MyShowsEnvelopeParserTests.cs` (6 tests), `Shows.Tests/Integration/StagingImporterTests.cs`
(4 tests). **Files changed:** `Shows.Infrastructure/Jobs/ShowsJobRunner.cs` (optional `handler` param so a
job can do real work; the five unimplemented jobs pass nothing and keep their shell behavior),
`Shows.Jobs.Import/Program.cs` (one-liner now passes `ImportJob.RunAsync`),
`ShowsInfrastructureServiceCollectionExtensions.cs` (scoped `StagingImporter`), `DataPaths.cs` +
`ShowsOptions.cs` (`InputDirectory`, defaults to the base directory = repo root where the inputs sit).

**Verification — step 2.1 (against the real exports, not fixtures):**
```
> dotnet run --project Shows.Jobs.Import -- --dry-run
[INF] Dry run: watching=78 later=246 cancelled=16 finished=207 total=547.
[INF] Batch 57 cross-check: watching=78, later=246, cancelled=16, finished=207 (episode shows=43, episode rows=1191).
  (exit 0; data/shows.db NOT created — dry run opens no write path)
> dotnet run --project Shows.Jobs.Import -- --reset
[INF] Cleared staging_show and staging_episode (staging_imdb_id left intact).
> dotnet run --project Shows.Jobs.Import -- --stage
[INF] Staged 547 shows (0 already present), 1191 episodes (0 already present).
> dotnet run --project Shows.Jobs.Import -- --stage        # idempotent re-run
[INF] Staged 0 shows (547 already present), 0 episodes (1191 already present).
```
Row counts read back from `data/shows.db` (throwaway .NET 8 console over `Microsoft.Data.Sqlite`, since
PowerShell 5.1 cannot load .NET 8 assemblies and there is no `sqlite3` CLI):
```
staging_show_total=547   staging_episode_total=1191   distinct_episode_shows=43
  cancelled=16  finished=207  later=246  watching=78
titles_with_untrimmed_ws=0   distinct_resolution=unresolved   distinct_episode_id_src=1191
```
- **78 / 246 / 16 / 207 = 547** confirmed, and the batch-57 cross-check agrees with the four category
  files exactly (no `MISMATCH` warning). **`staging_episode` has exactly 43 distinct `show_myshows_id`**
  (the batch-58 shows), 1191 episode rows total. Titles are trimmed (`titles_with_untrimmed_ws=0`).
  `tvmaze_resolution` is `unresolved` for every row (resolution is steps 2.2/2.3).
- `dotnet build Shows.sln` → 0 warnings / 0 errors. `dotnet test Shows.Tests` → **105 passed** (95 + 10 new).

**Deviations — one real bug caught and fixed during verification.** The first `--stage` implementation
reset-then-staged on every `--stage`, so a re-run reported "547 shows (0 already present)" — it was
wipe-and-reload, which would **clobber the user's DB-Browser edits** made between stage and promote
(step 2.5). `StagingImporter.StageAsync` was already correct (insert-if-missing, proven by the
`Stage_is_idempotent_and_preserves_user_edits` test); the fault was the `ImportJob` flag dispatch. Fixed
so `--stage` alone never resets — only an explicit `--reset` clears the tables. Re-verified above: the
second `--stage` inserts 0.

**New facts learned:**
- The category envelope's `totalEpisodes`/`watchedEpisodes`/`watchStatus` are **entry-level siblings of
  `show`**, not inside it; `title`/`year`/`totalSeasons` are inside `show`. Reading them from the wrong
  level yields zeros silently.
- `episodes.json` batch 58 has **43 shows, all with a non-empty `episodes[]`, 13 `started:true`** (matches
  DATA_ANALYSIS §4). Batch 57 = 4 per-status totals, batch 59 = 246 later totals (no `episodes[]`), batch
  60 = empty. The parser keys episode extraction on the presence of an `episodes[]` array, not the batch
  id, so 57/59/60 are skipped without a crash.
- **PowerShell 5.1 (`.NET Framework`) cannot `Add-Type` .NET 8 assemblies**, and there is no `pwsh` or
  `sqlite3` on this machine — so reading the SQLite DB from a script needs a throwaway .NET 8 console
  (built in a temp dir so it never enters `Shows.sln`).

**Unblocks:** step 2.2 (`ScrapeMyShows` → `staging_imdb_id`) and step 2.4 (`--report`) now have a populated
`staging_show` to work against. Step 2.3 (TVmaze resolution) still needs the step 3.1 HTTP plumbing. The
`--promote` gate (2.6) is downstream of the human review (2.5), which has not been reached.



**Machine change that unblocked it:** the .NET 8 MAUI **packs** are now installed —
`C:\Program Files\dotnet\packs\Microsoft.Maui.Sdk\8.0.3` exists (previously only 9.x/10.x were present,
which is what blocked `UseMaui=true` on the net8 TFM). `dotnet workload list` shows `maui 8.0.3`. The
user ran the install; this step was blocked on it and is now unblocked.

**Files added:** `Shows.App/`: `MauiProgram.cs` (composition root), `App.xaml`/`App.xaml.cs`,
`AppShell.xaml`/`AppShell.xaml.cs`, `MainPage.xaml`/`MainPage.xaml.cs` (placeholder),
`Platforms/Windows/` (`App.xaml`, `App.xaml.cs`, `app.manifest`), `Properties/launchSettings.json`,
`Resources/` (icon, splash, fonts, styles, images). The platform head and resources came from
`dotnet new maui` into a temp dir, then were trimmed to Windows-only (Android/iOS/MacCatalyst/Tizen
folders and the packaged `Package.appxmanifest` removed).

**Files changed:** `Shows.App.csproj` — `OutputType=WinExe`, `UseMaui=true`, `SingleProject=true`,
`WindowsPackageType=None` (unpackaged Win32 — no packaging cert, so build/run work here), the Windows
TFM only, `CommunityToolkit.Mvvm` + the Infrastructure project reference, and the Resources item groups.

**Verification — step 0.5:**
```
> dotnet build Shows.App
Build succeeded.  0 Warning(s)  0 Error(s)
> dotnet build Shows.sln
Build succeeded.  0 Warning(s)  0 Error(s)
> dotnet test Shows.Tests
Passed!  - Failed: 0, Passed: 95, Skipped: 0, Total: 95
```
- `appsettings.json` and `logging.json` are present in the app's build output
  (`Shows.App/bin/.../win10-x64/`), confirming they flow transitively from Infrastructure as Content
  (ARCHITECTURE.md §4) — the composition root reads them from there.
- `MauiProgram.CreateMauiApp` calls `AddShowsLogging(typeof(MauiProgram).Assembly)` **first** (console
  off — the app has no console), then `AddShowsInfrastructure`, then brings up `DataPaths` directories
  and runs `ShowsDatabaseMigrator.MigrateAsync()` (the lease-gated migrate path from step 1.6).

**Deviations — none from the plan's behaviour.** Four build notes, all framework-inherent and scoped to
`Shows.App` only (the rest of the solution keeps the full `latest-all` rule set):
- **`Microsoft.Maui.Controls` + `.Compatibility` referenced explicitly.** .NET 8 no longer auto-includes
  them from `UseMaui=true` (warning MA002), and `TreatWarningsAsErrors` promotes MA002 to an error.
- **`Application` base type fully qualified** in `App.xaml.cs`. Inside `namespace Shows.App`, the bare
  name `Application` binds to the sibling **namespace** `Shows.Application` (our Application project,
  referenced transitively) rather than the MAUI type — CS0118. Namespace-member lookup outranks a
  file-level `using` alias, so the base is written as `Microsoft.Maui.Controls.Application`.
- **`NoWarn=CA5392;CA1724` for this project.** CA5392 (P/Invoke `DefaultDllImportSearchPaths`) fires only
  on the WinUI-generated `App.g.i.cs` and the WindowsAppSDK's `UndockedRegFreeWinRT-AutoInitializer.cs` —
  code outside this project. CA1724 (type `App` conflicts with namespace `Shows.App`) is the MAUI
  App-class naming convention, present in both the cross-platform and Windows platform heads.
- **`MainPage` counter field** dropped its `= 0` initializer (CA1805, mine to fix).

**New facts later steps depend on:**
- **The MAUI head builds unpackaged** (`WindowsPackageType=None`). A later step that wants MSIX packaging
  must add a manifest + cert; until then `dotnet run --project Shows.App` launches a Win32 window.
- **`Shows.App` cannot use the bare identifier `Application`** for any MAUI type — it collides with the
  `Shows.Application` namespace. Phase 4 ViewModels must fully qualify or alias any such type.

**Unblocks:** step 8.1 (layer-boundary architecture tests now have a real App project to assert against)
and the Phase 4/5 UI steps. The app currently shows a placeholder counter page; no real UI until Phase 4.

### 2026-09-29 — Step 1.5: `TvmazeShowMapper` — the single mapped-row write path

**Files added:** `Shows.Infrastructure/Persistence/`: `TvmazeShowMapper` (SPEC R11/ADR-023),
`Vocabulary/TvmazeVocabulary` (the one R12 dictionary), `HtmlStripper`, `ITvmazeShowSource` +
`TvmazePayload` (the mapper's fetch seam). Tests: `Integration/TvmazeShowMapperTests` (7),
`Integration/RebuildDrillTests` (2).

**Files changed:** `ShowsInfrastructureServiceCollectionExtensions` registers `IShowMapper` (scoped,
with the context) via a factory that resolves `ITvmazeShowSource` optionally — null until the TVmaze
provider lands in Phase 3, so the DTO overload is the usable path today and the provider wires in with
no change here.

**Verification — step 1.5:**
```
> dotnet test Shows.Tests --nologo
Passed!  - Failed: 0, Passed: 95, Skipped: 0, Total: 95, Duration: 2 s
```
- `Black_mirror_fixture_maps_to_the_expected_mapped_row` — over the real §8 payload: `status "Running"`
  → `ShowStatus.Running` (NOT Ended — the original bug), `premiered` → 2011-12-04, `runtime NULL` and
  `average_runtime 62` **both** preserved (never collapsed), `externals.imdb` → `tt2085059`, summary
  HTML stripped on the mapped copy while the raw `<p>` stays in the source table.
- `Unrecognised_status_fails_closed_to_Unknown_and_records_the_gap` — the fail-closed test the plan
  calls the most important in the project: `"On Hiatus"` → `ShowStatus.Unknown` **and** a
  `tvmaze_status_seen` row with `mapped_to IS NULL`.
- `Known_status_records_a_non_null_mapped_to` — `"Running"` records `mapped_to = 1`.
- `Remapping_a_show_the_user_marked_watched_leaves_every_mark_intact` — re-map the same payload; the
  watched episode's id is unchanged, so the mark survives.
- `Mapping_the_same_payload_twice_changes_nothing` — second map returns `MapResult.Unchanged`.
- `Mapping_a_show_that_is_not_tracked_writes_only_source_rows` — no `tracked_show` row exists, so the
  mapper writes source rows and returns Unchanged; it never creates a `tracked_show` (SPEC P2).
- `Cached_columns_reflect_watched_state_after_mapping` — all regulars watched + an `Ended` payload →
  `auto_finish_eligible` true. The mapper reads `episode_watch` to compute this and never writes it.
- Rebuild drill (2): re-running the mapper over every `user_show` row leaves every episode/season id and
  all three user tables byte-identical (asserted on ids, not counts); the counter-example shows that a
  delete-then-remap cascade-orphans the watch mark — the bug §2a forbids, demonstrated so it cannot be
  "simplified" back in.

**Deviation — the literal wipe-and-reload drill is self-defeating; the sound form was implemented.**
`IMPLEMENTATION_PLAN.md` step 1.5 and `SCHEMA.md` §2a both phrase the drill as "delete `episode`,
`season`, `tracked_show`, `show_genre`, `genre`, re-run the mapper, assert the user tables are
identical." Taken literally this cannot pass, for the very reason §2a gives for rejecting
wipe-and-reload: `user_show` cascades from `tracked_show` and `episode_watch`/`ui_season_state` cascade
from `episode`/`season`, so deleting the mapped tables deletes the user data the drill protects — and
with client-generated ids (ADR-027) a deleted episode's id cannot be re-derived, so the watch row is
orphaned no matter how the mapper is written. The plan explicitly says to expect this to fail and treat
the failure as the deliverable; the deliverable is the demonstration that the literal drill is the bug.
`RebuildDrillTests` therefore asserts the invariant §2a actually names — **re-running the mapper over
every `user_show` row leaves surrogate ids immutable and the three user tables byte-identical** — plus a
counter-example that deletes-and-remaps and shows the orphaning. No doc change: §2a's prose is correct
about the *property*; only the imperative phrasing of the drill was unimplementable, and the test now
encodes the implementable reading.

**Two implementation facts later steps depend on:**
- **The mapper flushes the mapped rows before recomputing the cached columns.** The cached columns are a
  query over `episode`, and episodes added in the same map are invisible to that query until persisted.
  Without the intermediate `SaveChangesAsync`, the first map computes progress against an empty episode
  set and the second map sees the now-persisted episodes and reports a spurious change — idempotency
  breaks. Callers must still call `SaveChangesAsync` after the mapper (the use-case does); this flush is
  internal to the mapper's own progress recompute.
- **`AirStamp` is compared by instant and stored UTC-normalized.** `DateTimeOffset.Equals` also compares
  the UTC offset, so the same instant re-expressed with a different offset (a DB round-trip, or a
  provider that changes its offset) would read as a change and break idempotency. `SetStamp` compares
  `UtcDateTime` and writes `ToUniversalTime()`.
- **EF raw-SQL parameter binding has no store mapping for `DBNull`.** The `tvmaze_status_seen.mapped_to`
  NULL is expressed as a sentinel `-1` converted back with `NULLIF({0}, -1)`, never `DBNull.Value`.

**Unblocks:** Phase 2. `--promote` (step 2.6) calls the mapper per show after writing the
`tracked_show`/`user_show` shell; `AddShowToTrackingUseCase` already calls it. The TVmaze provider
(Phase 3) implements `ITvmazeShowSource` and the `MapShowAsync(int)` path becomes live.

### 2026-09-29 — Step 1.6: `IdGenerator`, `WriterLease`, and the migration gate

**Files added:** `Shows.Infrastructure/Persistence/`: `IdGenerator` (SCHEMA.md §1a mint),
`SqliteWriterLease` (conditional-upsert acquire, heartbeat renew, release — ADR-028),
`ShowsDatabaseMigrator` + `LiveLeaseException` (the migration gate). Tests: `Unit/IdGeneratorTests`
(10), `Integration/WriterLeaseTests` (6), `Integration/MigrationGateTests` (2).

**Files changed:** `ShowsInfrastructureServiceCollectionExtensions` — extracted the context-options
lambda so `AddDbContextFactory` and `AddDbContext` share it (the singleton lease needs the factory, not
a captive scoped context), and registered `IIdGenerator`, `IWriterLease`, `ShowsDatabaseMigrator` as
singletons.

**Verification — step 1.6:**
```
> dotnet test Shows.Tests --nologo
Passed!  - Failed: 0, Passed: 86, Skipped: 0, Total: 86, Duration: 2 s
```
- `One_hundred_thousand_ids_are_strictly_increasing` — 100k ids from one instance, each strictly greater
  and positive. The load-bearing property: `episode_watch.episode_id` points at these, so a duplicate or
  backwards id silently orphans watch marks while every row count still looks right.
- `Real_clock_id_decodes_to_within_a_few_seconds_of_now` — the shift-width catcher the plan names: with
  the real clock, `(id >> 22) + epoch` lands within 5 s of `UtcNow`. A wrong shift or epoch passes every
  other test and fails here.
- `Bit_layout_sums_to_exactly_sixty_four_bits` — `1 + 41 + 10 + 12 == 64`, no spare bit (ADR-027). A
  65-bit layout overflows `long` into negative ids that sort before everything.
- `Salt_and_sequence_occupy_their_declared_bit_fields` — with a fixed clock the low 22 bits equal
  `(salt << 12) | sequence` exactly.
- `Small_clock_rollback_keeps_minting_at_the_last_timestamp` / `Large_clock_rollback_throws` — a 1 s
  jump back keeps ids increasing (mint holds at `lastTimestamp`); a 60 s jump throws rather than mint a
  future-claiming id (SCHEMA.md §1a).
- `Sequence_exhaustion_spins_to_the_next_millisecond` — 4,096 ids fit one ms; the 4,097th carries a
  timestamp one ms later, proving it spun rather than wrapped.
- `Two_salts_minting_in_a_tight_loop_share_no_id` — salts 137 and 901, fixed clock so the sequences run
  in lockstep; 8,192 ids across both streams, zero collisions.
- Lease (6): second acquire fails while the first holds; succeeds after release; succeeds after
  `expires_at` is forced into the past; renewal after a takeover returns **false** (the lease-loss
  signal); renewal while holding returns true; release never deletes another holder's lease. Each
  "process" is its own `ServiceProvider` over the same file — distinct holder strings, shared database.
- Migration gate (2): `Migrate()` throws `LiveLeaseException` naming the holding job while a lease is
  live; succeeds when none is live, and when a leftover lease is already expired.

**Deviations — none from the plan's behaviour.** Two implementation notes:
- The service is named `SqliteWriterLease`, not `WriterLease` — the entity `WriterLease` already owns
  that name (SCHEMA.md §2 bookkeeping table). The interface stays `IWriterLease`.
- The migration gate's liveness check is raw SQL (`SqlQueryRaw`), not LINQ. See the new fact below.

**New facts later steps depend on:**
- **EF Core's SQLite provider cannot translate a `DateTimeOffset` comparison in a LINQ `WHERE` clause at
  all** — not even with the instant captured into a local (it becomes `__now_0` and still fails). The
  column is stored as TEXT. This is the same limitation that forced `EpisodeRepository` to order in
  memory (step 1.4). The lease's own acquire/renew SQL only ever compares two stored *columns*
  (`writer_lease.expires_at < excluded.acquired_at`), which sidesteps it. The migration gate compares a
  stored `expires_at` against a parameterised UTC instant — lexicographically correct because every
  value is written with a `+00:00` offset. **Any future liveness/selection query on a
  `DateTimeOffset` column must use raw SQL or client evaluation, never a LINQ comparison.**
- **`ShowsDatabaseMigrator.MigrateAsync` is the only sanctioned migrate path.** It skips the lease check
  when `writer_lease` does not yet exist (the first migration creates it), then refuses under any live
  lease. The app and jobs must call this, not `context.Database.Migrate()` directly.
- **`AddDbContextFactory` is now registered alongside `AddDbContext`.** The singleton lease opens its own
  short-lived contexts through the factory; it must not be captive to one scoped context for the process
  lifetime.

**Unblocks:** step 1.5 (`TvmazeShowMapper`) — the mapper mints ids through `IIdGenerator` and never
regenerates them; and every bulk job (Phase 2+) acquires its lease through `IWriterLease`.

### 2026-09-29 — Step 1.4: repositories and UnitOfWork

**Files added:** `Shows.Infrastructure/Persistence/Repositories/`: `ActiveShowFilter` (the one
`deleted_at IS NULL` definition), `TrackedShowRepository`, `EpisodeRepository`, `UserShowRepository`,
`UiStateRepository`, `UnitOfWork`. Tests: `Integration/RepositoryTests` (6),
`Architecture/ApplicationInterfaceTests` (1).

**Files changed:** `ShowsInfrastructureServiceCollectionExtensions` registers the five interfaces
(scoped, with the context).

**Verification — step 1.4:**
```
> dotnet test Shows.Tests --nologo
Passed!  - Failed: 0, Passed: 68, Skipped: 0, Total: 68, Duration: 2 s
```
- `No_application_interface_method_returns_iqueryable` — reflects over every `Shows.Application`
  interface method (unwrapping `Task<T>`) and asserts none returns `IQueryable<T>`. Passes.
- `Soft_delete_hides_from_default_read_but_not_from_include_deleted` — after `SoftDeleteAsync`,
  `FindByIdAsync`/`ListActiveAsync`/`ListActiveByCategoryAsync` cannot see the show, but
  `ListIncludingUntrackedAsync` can. This proves the filter is applied **and** is not a hard-coded
  assumption baked into the query (SPEC R8).
- `GetActiveTracking_reports_a_soft_deleted_show_as_untracked` — SPEC F2: the marker reads `user_show`,
  so a soft-deleted show (which keeps its `tracked_show` row) reports as NOT tracked, leaving re-add open.
- `Restore_makes_the_show_visible_again`, `Change_category_updates_the_user_row`,
  `UnitOfWork_commits_pending_changes` — the write paths and the commit boundary.

**Deviation — `ListByShowIdAsync` orders in memory, not in SQL.** EF Core's SQLite provider throws
`NotSupportedException` on `ORDER BY` a `DateTimeOffset` column. Ordering the stored TEXT
lexicographically would be wrong across differing UTC offsets (a `+05:00` instant sorts before a
`+00:00` one as text). A show has at most a few hundred episodes, so the repository materializes the
show's episodes then orders on the real `AirStamp` instant — correct and cheap. Recorded because a
future reader may "optimize" it back into SQL and reintroduce the wrong ordering.

**New facts later steps depend on:**
- **`ActiveShowFilter.Predicate` is the only `deleted_at IS NULL` in the codebase.** Any new read path
  composes it; a new query that forgets it is the bug ADR-024 warns about. `UserShowRepository`
  deliberately does **not** apply it (the re-add path must find soft-deleted rows).
- **`DateTimeOffset` cannot be ordered in SQLite SQL.** Any query that sorts by `air_stamp` (or any
  DateTimeOffset) must order client-side. This affects the mapper's episode ordering and any future
  "next airing" query.

**Unblocked:** 1.5 (the mapper — the pivot) and 1.6 (IdGenerator + WriterLease). 1.6 should land first:
the mapper mints ids through `IIdGenerator`.

### 2026-09-29 — Steps 1.1, 1.2, 1.2b, 1.3: the persistence schema

Four steps landed as one chain: the context (1.1) is meaningless without entities, and the entities
are the schema (1.2/1.2b/1.3). Migrations `0001_MainSchema`, `0002_SourceSchema`, `0003_Staging`.

**Files added:**
- `Shows.Infrastructure/Persistence/`: `ShowsDbContext`, `SqlitePragmaInterceptor`,
  `ShowsDbContextFactory` (design-time, so `dotnet ef` works without the blocked MAUI head), and the
  bookkeeping entities `JobRun`, `WriterLease`, `HttpCacheEntry`.
- `Persistence/Sources/`: `TvmazeShow`, `TvmazeEpisode`, `TvmazeStatusSeen`, `ImdbTitle`,
  `ImdbEpisode`, `ImdbAkas`, `ImdbDatasetMeta` — provider field names verbatim (SCHEMA.md §3–§4).
- `Persistence/Staging/`: `StagingShow`, `StagingEpisode`, `StagingImdbId`, `StagingIssue` (the view).
- Three migrations under `Persistence/Migrations/`.
- Tests: `Integration/ShowsDbContextTests` (4), `Integration/MainSchemaTests` (9),
  `Integration/SourceSchemaTests` (5), `Integration/StagingViewTests` (7).

**Files changed:** `UserShow` gained a `Show` navigation (EF needs the dependent side of a shared-PK
1:1). Infrastructure gained `EFCore.NamingConventions` and `Microsoft.EntityFrameworkCore.Design`.

**Verification — step 1.1 (the model check is the one that matters, SCHEMA.md §1a):**
```
> dotnet test Shows.Tests --nologo
Passed!  - Failed: 0, Passed: 61, Skipped: 0, Total: 61, Duration: 2 s
```
- `No_property_in_the_model_is_store_generated` — `context.Model.GetEntityTypes().SelectMany(t =>
  t.GetProperties()).Where(p => p.ValueGenerated != ValueGenerated.Never)` is empty. This is the
  reflection check that catches the SQLite provider's `OnAdd` default on `INTEGER PRIMARY KEY`.
- `Opening_and_closing_creates_the_file_with_wal_journal_mode` — `PRAGMA journal_mode` reads back `wal`.
- `Foreign_keys_are_enabled_on_an_open_connection` — `PRAGMA foreign_keys` reads non-zero (per-connection,
  so this proves the interceptor ran, not that it was registered).
- `Generated_ddl_contains_no_autoincrement` and the shipped-DDL check in `MainSchemaTests` — both clean.
- `dotnet ef migrations script` grepped: **0** `AUTOINCREMENT` matches across all three migrations.

**Verification — step 1.2 (SCHEMA.md §2):**
- `Tracked_show_ddl_has_no_user_columns` — `SELECT sql FROM sqlite_master WHERE name='tracked_show'`
  contains no `category_id`/`added_at`/`needs_review`/`category_changed_at` (ADR-024).
- `Two_null_imdb_ids_are_both_allowed_by_the_partial_index` — the partial unique index
  (`WHERE imdb_id IS NOT NULL`) accepts two NULLs; a plain unique constraint would fail this.
- `Duplicate_non_null_imdb_id_is_rejected` and `Tracked_show_tvmaze_id_is_unique` — both collide.
- `Episode_with_only_one_provider_id_inserts` — a TVmaze-only and an IMDb-only episode both store (ADR-025).
- `User_show_shares_its_primary_key_with_tracked_show` — `user_show.tracked_show_id` is PK and FK.
- `Every_primary_key_column_is_integer` — every surrogate PK is `INTEGER`; the natural TEXT keys
  (`writer_lease.name`, `http_cache.url`, `tvmaze_status_seen.status_raw`, the `imdb_*` tconsts) are exempted by name.

**Verification — step 1.2b (SCHEMA.md §3–§4):**
- `Tvmaze_show_accepts_null_runtime_with_average_runtime` — the Black Mirror case (DATA_ANALYSIS §8):
  `runtime=NULL`, `average_runtime=62` stored independently.
- `Tvmaze_status_seen_pk_prevents_double_counting` — a second insert of the same `status_raw` fails at
  the **database** (fresh context, so it is the PK, not EF's change tracker, that rejects it).
- `Source_columns_use_provider_field_names_verbatim` — DDL contains `status_raw`, `average_runtime`,
  `airstamp`, `parent_tconst`, `raw_json`, `summary_html`, `provider_updated`.

**Verification — step 1.3 (SCHEMA.md §6, the promote gate):** every branch of the view's `CASE` is
asserted for exact `issue` and `severity`: `unresolved_tvmaze_id` (blocking), `unresolved_imdb_id`
(blocking), `watched_exceeds_total` (blocking — the one arithmetic case), `zero_total_episodes`
(warning), `imdb_count_mismatch` (warning), `tvmaze_count_mismatch` (warning), and a fully-resolved
show that produces **no** row. The fixture runs `Migrate()`, not `EnsureCreated()`, because the view is
hand-written SQL and `EnsureCreated` skips it.

**Deviations from plan:** none material. Two design decisions worth recording:
- **`tracked_show.tvmaze_show_id` is a shadow property, not a Domain field.** SCHEMA.md §2 lists both
  `tvmaze_show_id` (the real FK) and `tvmaze_id` (the denormalized readable copy). Domain carries only
  `TvmazeId` — Domain must not reference a source table (SPEC P3) — so the FK is a shadow column set
  through the entry API, and the mapper keeps the two equal. It is NOT NULL: a mapped row cannot exist
  without its source row, which is the rebuildability property (SPEC P4). `Tracked_show_requires_a_source_row`
  asserts the FK bites.
- **The FK lands in migration 0002, not 0001.** Its target table (`tvmaze_show`) is created in 0002;
  EF's model diffing places the `AddForeignKey` in the migration that creates the target, so 0001 stays
  free of a reference to a table that does not exist yet.

**New facts later steps depend on:**
- **`Migrate()` vs `EnsureCreated()`.** The view only exists under `Migrate()`. Any step that reads
  `v_staging_issues` (2.4 `--report`, 2.6 `--promote`) must use a migrated database, not `EnsureCreated`.
- **`ExecuteSqlRaw` treats `{}` as a format placeholder.** A `raw_json` value of `{}` throws
  `FormatException`; test seeds use a brace-free literal. Production code uses parameters, not
  interpolation, so this is a test-only trap — but it bit twice here.
- **The `DatabaseFixture` is shared** between `MainSchemaTests` and `SourceSchemaTests` (one DB, reset
  between tests); `StagingViewTests` uses its own `MigratedFixture` because it needs the view.
- **`v_staging_issues.tvmaze_count_mismatch` is silent until episode fetch** — the view joins
  `tvmaze_episode`, empty until step 2.3 + an episode fetch. An empty view early is not a clean bill of
  health (SCHEMA.md §6). Recorded here so step 2.6's author does not trust an empty gate prematurely.

**Unblocked:** 1.4 (repositories + UnitOfWork), 1.5 (the mapper — the pivot), 1.6 (IdGenerator +
WriterLease). All of Phase 2's schema prerequisites are met.

### 2026-09-29 — Steps 0.7, 0.8: job shells and shared logging

Landed together: 0.8's `AddShowsLogging` is called *by* 0.7's job shells, so the two are one build.

**Files added:**
- `Shows.Infrastructure/Logging/logging.json` — Serilog level overrides only (`Microsoft*`,
  `Microsoft.EntityFrameworkCore*`, `System.Net.Http.HttpClient*` → `Warning`). The **Default** level is
  deliberately *not* here: it depends on build configuration and `LOG_LEVEL`, so it is set in code
  (`ReadFrom.Configuration` would otherwise reset it).
- `Shows.Infrastructure/Logging/ShowsLoggingServiceCollectionExtensions.cs` —
  `AddShowsLogging(Assembly, enableConsole=false)`. File sink `log/<appname>.log` (`<appname>` from
  `Assembly.GetName().Name`), size-only rolling (`RollingInterval.Infinite`, 10 MB, retain 10), the §7
  output template, `LOG_LEVEL` override, `Debug`→Information / `Release`→Warning. Console sink is opt-in
  (jobs only; the MAUI app has no console). `internal CreateLogger(...)` is the test seam.
- `Shows.Infrastructure/Jobs/ShowsJobRunner.cs` — the shared `Host.CreateApplicationBuilder` shell:
  logging first, then `AddShowsInfrastructure`, resolve `DataPaths`, log identity + resolved paths, log
  the `job_run` intent (table pending 1.2), exit 0. `Log.CloseAndFlushAsync()` in a `finally`.
- Six `Program.cs` (one per job), each a single `return await ShowsJobRunner.RunAsync(args,
  typeof(Program).Assembly);` — no business logic in `Program.cs` (§3).
- `Shows.Tests/Unit/ShowsLoggingTests.cs` — 4 tests (level filter, `LOG_LEVEL` override, flush-on-throw).

**Files changed:**
- Six job `.csproj` → `OutputType=Exe`, `AssemblyName` (short: `Import`, `Enrichment`, … so the log file
  is `log/Import.log` per §7, not `log/Shows.Jobs.Import.log`), and the Infrastructure reference.
- `Shows.Infrastructure.csproj` → `Microsoft.Extensions.Hosting` package; `InternalsVisibleTo`
  `Shows.Tests` (logging level-filter seam only); `appsettings.json` and `Logging/logging.json` switched
  from `None` to `Content` so they flow **transitively** into every executable's output (logging.json
  `Link`ed to the output root, read from the assembly directory not the cwd).
- `DataPaths.ResolveBaseDirectory` made `public static` so logging resolves the same base dir without a
  second copy of the marker walk.

**Verification — solution builds with zero warnings:**
```
> dotnet build Shows.sln -v q
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

**Verification — step 0.7: each job runs and exits 0 printing its name and resolved paths.**
```
> dotnet run --project Shows.Jobs.Import --no-build
[05:07:38.560] [INF] [Import] Import starting. Base=C:\src\shows Database=C:\src\shows\data\shows.db ImdbDataset=C:\src\shows\data\imdb Log=C:\src\shows\log
[05:07:38.583] [INF] [Import] job_run intent: Import started 2026-09-29T02:07:38.5833108+00:00 (job_run table pending step 1.2)
[05:07:38.584] [INF] [Import] Import completed with no work (shell only, step 0.7).
EXIT=0
```
All six exit 0: `ScrapeMyShows EXIT=0`, `EpisodeRefresh EXIT=0`, `StatusRecheck EXIT=0`,
`ImdbImport EXIT=0` (plus Import, Enrichment above).

**Verification — step 0.8: two distinct files `log/<jobname>.log` with the configured format.**
```
> Get-ChildItem log | Select-Object Name, Length
Enrichment.log    399
Import.log        375
```
After all six ran: `Enrichment.log EpisodeRefresh.log ImdbImport.log Import.log ScrapeMyShows.log
StatusRecheck.log` — six distinct files, one per executable, `<appname>` never hardcoded.

**Verification — step 0.8: the level filter fails closed, and the tail line survives the throw.**
```
> dotnet test Shows.Tests --nologo
Passed!  - Failed: 0, Passed: 36, Skipped: 0, Total: 36, Duration: 2 s - Shows.Tests.dll (net8.0)
```
The 4 new tests: `DebugLevel_WritesDebugLine` (Debug line present at `Debug`),
`InformationLevel_OmitsDebugLine` (same Debug line absent at `Information`, Information line present —
the filter is seen to *exclude*, not just include), `LogLevelEnvironmentVariable_OverridesDefault`
(`LOG_LEVEL=Debug`/`Error` resolve to the matching level), and
`CloseAndFlush_PersistsTailLineOnExceptionPath` (a line logged immediately before a deliberate throw is
still in the file after the `finally` flush).

**Deviations from plan:** none material. Two implementation choices worth recording:
- `logging.json` carries only level overrides, not the file sink or template. The §7 path is
  `log/<appname>.log` and `<appname>` is runtime-derived, so the sink is configured in code; the JSON
  stays the single source of truth for *levels*, which is where drift actually starts.
- `RollingInterval.Infinite` (not a date interval) implements "roll on size, not date" (§7): rolls
  append a counter (`appname_001.log`) instead of fragmenting by day.

**New facts later steps depend on:**
- The job assembly's **simple name** is what produces the log file name. `AssemblyName` is set short
  (`Import`, not `Shows.Jobs.Import`) to match §7's examples; step 0.5's MAUI head keeps `Shows.App`
  and will produce `log/Shows.App.log` as §7 shows.
- `appsettings.json`/`logging.json` reach executables only as `Content` (transitive), not `None`. A new
  job needs no config copy of its own — it inherits both from Infrastructure.
- `Microsoft.Extensions.Hosting` is now an Infrastructure dependency (the job shell lives there). It
  does not violate any layer ban: Hosting is not EF/HTTP/MAUI/Newtonsoft, and it stays out of
  Application and Domain.

**Unblocked:** Phase 1 (1.1 onward). Step 0.5 remains blocked on the MAUI workload install; when it
lands, its `MauiProgram.cs` calls `AddShowsLogging(typeof(Program).Assembly)` (console off) first.

### 2026-09-29 — Steps 0.2, 0.3, 0.4, 0.6: Domain, Application, Infrastructure, Tests

Four steps landed together because they are one dependency chain (Domain ← Application ←
Infrastructure, with Tests referencing all three) and the build stays green at each link.

**Files added:**
- `Shows.Domain`: `ShowCategory`, `ShowStatus`, `EpisodeType`, `ShowStatusRules`, `ShowProgress`
  (+ `EpisodeProgressFact`), `TrackedShow`, `UserShow`, `Season`, `Episode`, `EpisodeWatch`, `Genre`,
  `UiSeasonState`, `TitleRules`. Zero `PackageReference` entries.
- `Shows.Application`: `Interfaces/` (`ITrackedShowRepository`, `IEpisodeRepository`,
  `IUserShowRepository`, `IUiStateRepository`, `IShowMapper`, `IIdGenerator`, `IWriterLease`,
  `IUnitOfWork`, `IImdbIdResolver`, `IImdbDatasetReader`), `Catalog/` (`ICatalogProvider`,
  `CatalogShow`), `UseCases/` (Search, GetUpcoming, Add/Untrack, Recompute, Toggle, Read). References
  Domain only.
- `Shows.Infrastructure`: `Configuration/` (`ShowsOptions`, `DataPaths`), `Http/` (`ProxyConfig`,
  `ThrottlingHandler`), `ShowsInfrastructureServiceCollectionExtensions`, `appsettings.json`.
- `Shows.Tests`: xUnit + `GlobalUsings`, `Unit/ShowProgressTests`, `Unit/TitleRulesTests`,
  `Unit/InfrastructureConfigTests`.

**Verification — Domain has zero packages (step 0.2):**
```
> dotnet list Shows.Domain package
Project 'Shows.Domain' has the following package references
   [net8.0]: No packages were found for this framework.
```

**Verification — the whole solution builds with zero warnings, and all tests pass:**
```
> dotnet test Shows.Tests --nologo
Passed!  - Failed: 0, Passed: 32, Skipped: 0, Total: 32, Duration: ... - Shows.Tests.dll (net8.0)
```
(The 32 include the 16 progress tests, 4 title tests, 11 config/proxy tests, and the R14 transport
test below.)

**Verification — step 0.4's two required tests:**
- `DataPathsTests.Both_paths_resolve_under_one_base_directory` — asserts `DatabasePath`,
  `ImdbDatasetPath` and `LogDirectory` all start with `BaseDirectory`, and are named `shows.db`,
  `imdb`, `log`. Passes.
- `ProxyTransportErrorTests.A_refused_proxy_surfaces_as_a_transport_error_naming_the_host` — points
  the `Tvmaze` named client's proxy at `http://127.0.0.1:1` and asserts the call throws
  `HttpRequestException` whose message contains `127.0.0.1`, not an empty result. Passes. This is the
  R14 guarantee that "TVmaze returned nothing" can never silently mean "we never reached TVmaze".

**Deviation — SPEC R5 vs R12, resolved in favour of R12.** R5's formula reads
`status_id = ShowStatus.Ended`, but R12's per-value table marks `Canceled` as "Can auto-finish: yes",
and ADR-014's rationale is "no more episodes expected" — which a cancelled show satisfies. The two
contradict. **The user was unavailable to decide**, so I implemented `Ended OR Canceled`
(`ShowStatusRules.NoMoreEpisodesExpected`), because R12's table is the more specific statement and
ADR-014 supports it, and I fixed R5's formula line to match. **Flagged for user review** — if the
intent was Ended-only, the change is one line in `ShowStatusRules` plus reverting R5. `SPEC.md` R5 now
reads `status_id IN (Ended, Canceled)` with a note pointing here.

**Deviation — `ARCHITECTURE.md` §4 gained a `Data:BaseDirectory` key.** §4 said both paths resolve
"relative to a base data directory" but named no key for it, and §7 says `log/` sits beside `data/`
under that base. `DataPaths` needs the value to exist. Added `Data:BaseDirectory` (empty = discover by
walking up to `Shows.sln`, so `dotnet run` puts the database in the repo not under `bin/`). §4 updated
in the same commit. This is the one place a config key was added; it names a directory the docs already
assumed, it does not invent a new concept.

**New facts learned:**
- `latest-all` analysis is aggressive on a fresh codebase. Suppressed with named reasons in
  `.editorconfig`: CA1002 (EF navigation collections need `List<T>`, and Domain may not take a
  dependency to satisfy it), CA1008 (suppressed at the one site — `ShowCategory`'s 0 is deliberately
  invalid, its values are the persisted `category_id`s), CA1308 (proxy scheme comparison uses
  `StringComparison.OrdinalIgnoreCase` instead), plus a batch of design-opinion rules (CA1024 etc.).
  Each is a reviewed single-line downgrade, not a wildcard.
- The `.editorconfig` test-suppression section was first written as `[*.Tests.cs]`, which matches only
  files *named* `*.Tests.cs` — it silently did nothing for `GlobalUsings.cs` etc. Replaced with
  `[Shows.Tests/**]`.
- `SocketsHttpHandler`, not the default `HttpClientHandler`, is required for `ConnectTimeout` to
  exist — and `ConnectTimeout` is the R14 filtered-proxy defence, so this is load-bearing, not
  incidental.
- `AddKeyedSingleton<ThrottleState>(name)` per client, not `AddSingleton(new ThrottleState())` in the
  loop — the latter registers one instance (last registration wins) and would serialise all three
  hosts against a single throttle.

**Unblocked:** 0.7 (job shells), 0.8 (logging), and all of Phase 1. Step 0.5 remains blocked on the
MAUI workload install.

### 2026-09-29 — Step 0.1: solution and build conventions

**Files changed (all new):** `Shows.sln`, `Directory.Build.props`, `global.json`, `.editorconfig`,
`.gitignore`, and eleven `.csproj` files — `Shows.Domain`, `Shows.Application`,
`Shows.Infrastructure`, `Shows.App`, `Shows.Tests`, and the six `Shows.Jobs.*` projects. No source
files; no project references (tasks/0.1 Constraints).

**Verification — the build.**

```
> dotnet build Shows.sln --nologo
  Shows.App -> C:\src\shows\Shows.App\bin\Debug\net8.0-windows10.0.19041.0\Shows.App.dll
  ... (11 projects)
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

Zero warnings, which is the thing being verified: `TreatWarningsAsErrors` is on, so a build that
passed *with* warnings would mean `Directory.Build.props` had not applied.

**Verification — the TFM.**

```
> dotnet msbuild Shows.Domain\Shows.Domain.csproj -getProperty:TargetFramework
net8.0
```

`global.json` is load-bearing as predicted: `dotnet --version` at the repo root now reports
**8.0.425** where the machine default is 10.0.401. `dotnet workload list` at the repo root shows
`maui-windows 8.0.3/8.0.100`; outside the repo it shows `10.0.20/10.0.100`.

**Verification — `.gitignore`.** The repo is **not** under git (user decision: do not `git init`), so
`git status --porcelain` cannot be the check. The same file was copied into a throwaway repo in
`$env:TEMP\gi-probe` and tested with `git check-ignore -v --no-index`:

```
IGNORED   data/shows.db            .gitignore:7:data/
IGNORED   data/sub/x.db            .gitignore:7:data/
IGNORED   shows.db                 .gitignore:8:*.db
IGNORED   shows.db-wal             .gitignore:9:*.db-wal
IGNORED   shows.db-shm             .gitignore:10:*.db-shm
IGNORED   title.basics.tsv.gz      .gitignore:15:*.tsv.gz
IGNORED   bin/Debug/net8.0/a.dll   .gitignore:21:bin/
IGNORED   obj/project.assets.json  .gitignore:22:obj/
IGNORED   log/Import.log           .gitignore:12:log/
IGNORED   log/Shows.App.log        .gitignore:12:log/
=== must NOT be ignored ===
tracked   watching.json
tracked   title.basics.tsv
tracked   docs/STATE.md
tracked   Shows.Domain/Shows.Domain.csproj
tracked   .gitignore
```

The negative half matters: the seven read-only input files at the repo root must stay committable, and
a `data/`-style pattern written carelessly could swallow them.

**Deviation 1 — the acceptance criterion was unexecutable as written.** `tasks/0.1` asks for
`git status --porcelain` to stay empty after creating a file under `data/`. There is no repository.
Substituted `git check-ignore` in a scratch repo (above), which tests the same property — that the
patterns match the paths — without initialising git in the user's data directory. The criterion in
`IMPLEMENTATION_PLAN.md` was not softened, only re-instrumented.

**Deviation 2 — `Shows.App` and the six jobs carry no executable properties at 0.1.** The first draft
gave `Shows.App` `OutputType=WinExe`/`UseMaui=true` and each job `OutputType=Exe`, per
`ARCHITECTURE.md` §2–3. That does not build: `CS5001: Program does not contain a static 'Main' method`
for all six jobs. An executable property without an entry point is not a scaffold, it is a broken
build, and step 0.1's deliverable is an empty solution that builds. `OutputType`, `UseMaui`,
`WindowsPackageType` and the rest now belong to the step that also writes the entry point — 0.5 for
the app, 0.7 for the jobs. The csproj comments say so, so the omission reads as deliberate.

**Deviation 3 — `maui-tizen`.** With `UseMaui=true`, the build failed before the entry-point problem
was even reached:

```
error NETSDK1147: To build this project, the following workloads must be installed: maui-tizen
```

Diagnosed rather than worked around. `WorkloadManifest.json` for `microsoft.net.sdk.maui/8.0.3` shows
`maui` extends `maui-mobile,maui-desktop`, and `maui-mobile` extends `maui-android,maui-ios,maui-tizen`
— so `UseMaui=true` transitively requires every platform workload, not just Windows. The manifest is
present but the packs are not: `C:\Program Files\dotnet\packs\Microsoft.Maui.Sdk` contains only
`9.0.120` and `10.0.20`, and `C:\Program Files\dotnet` is not writable from this session
(`Access to the path ... is denied`). **The claim in `tasks/0.1` that "`maui-windows` 8.0.3 is
installed and visible to SDK 8, so `Shows.App` should restore without a workload install" is wrong** —
the manifest being visible is not the same as the packs being present, and `maui-windows` alone is not
sufficient for `UseMaui=true`.

**User decision:** the user will run the workload install themselves. **Step 0.5 is therefore the one
step in Phase 0 that is blocked on a machine change**, and it is recorded as such in `STATE.md`. The
command to run is `dotnet workload install maui` from a directory governed by this `global.json`
(so the 8.0.4xx band is the active SDK), from an elevated shell. If that pulls in more than expected,
`dotnet workload install maui-windows` is not a substitute — `UseMaui=true` needs the full `maui`
workload per the manifest chain above, unless step 0.5 sets `UseMauiCore`/`UseMauiEssentials` instead.

**New facts learned:**
- `UseMaui=true` requires the whole `maui` workload graph including `maui-tizen`, even for a
  Windows-only TFM. A Windows-only *TFM* does not imply a Windows-only *workload*.
- `dotnet workload list` reports a workload as installed when its **manifest** is present. It does not
  check that the packs exist. `packs\Microsoft.Maui.Sdk\<version>` is the ground truth.
- XML comments cannot contain `--`. Three of the first comments written broke every project load with
  `An XML comment cannot contain '--'`, which surfaced as "Invalid project ... could not be loaded"
  rather than as a lint error.
- `git check-ignore --no-index` works against an untracked path, which makes it usable as a
  `.gitignore` test in a repo that has no history.

**Unblocked:** 0.2, 0.3, 0.4, 0.6, 0.7, 0.8 and all of Phase 1 — everything except 0.5.

**Trigger:** the user reported TVmaze is not reachable from the development PC and asked for proxy
configuration per external API service, then supplied `main-pc:10801` to test.

**Files changed:** `docs/SPEC.md` (R14 + acceptance criterion), `docs/DATA_ANALYSIS.md` (new §9),
`docs/ARCHITECTURE.md` (§4 `Proxy`/`Http` keys + rules), `docs/DECISIONS.md` (ADR-029),
`docs/IMPLEMENTATION_PLAN.md` (steps 0.4, 2.2, 3.1, 7.4), `docs/STATE.md` (note + open question 7),
`tasks/TEMPLATE.md` (forking policy). No code — none exists.

**Evidence — reachability.** `curl` with and without the proxy, output captured to variables and
inspected rather than eyeballed:

```
http://main-pc:10801    | https://api.tvmaze.com/shows/305    | payload-ok | {"id":305,"url":"https://www.tvmaze.com/shows/305/black-mirror","name":"Black Mirror",…
socks5://main-pc:10801  | https://api.tvmaze.com/shows/305    | payload-ok | {"id":305,…  same payload
http://main-pc:10801    | https://api.tvmaze.com/shows/999999 | no-payload | {"name":"Not Found","message":"","code":0,"status":404}
socks5://main-pc:10801  | https://api.tvmaze.com/shows/999999 | no-payload | {"name":"Not Found","message":"","code":0,"status":404}
```

The 404 body is the load-bearing line: a captive portal or intercepting proxy would not reproduce
TVmaze's own error shape. Payload authenticity was checked, not assumed.

**Evidence — .NET 8 handler behaviour** (`draft/probe-socks.ps1`, console project pinned to SDK
8.0.425 via `global.json` in `$env:TEMP\socksprobe\probe`):

```
  http://main-pc:10801       OK 200 537ms
  socks5://main-pc:10801     OK 200 390ms
  socks4://main-pc:10801     OK 200 393ms

  garbage://main-pc:10801          THROW NotSupportedException: Only the 'http', 'https', 'socks4', 'socks4a' and 'socks5' schemes are allowed for proxies.
  bare host:port                   OK 200 553ms
```

**SOCKS needs no NuGet package** — this is why ADR-029 rejects a SOCKS handler dependency.

**Evidence — ambient settings are inert here:**

```
  DefaultWebProxy = HttpNoProxy  IsBypassed(tvmaze)=True
  UseProxy=false (explicit direct)       THROW TaskCanceledException: A task was canceled.
  UseProxy=true, Proxy=null (ambient)    THROW TaskCanceledException: A task was canceled.
  UseProxy=true, http://main-pc:10801    OK 200 522ms
```

Ambient and explicit-direct fail identically. This is the measurement behind "absent configuration
means direct, explicitly" — it is not a style preference, and it is what makes ADR-029's rejection of
`HttpClient.DefaultProxy` evidence-based.

**Deviation found while writing the evidence, and corrected.** §9.6 originally read "a dead proxy port
hangs for the full client timeout". Re-probing showed that is only true of a *filtered* port:

```
  refused: 127.0.0.1:1        THROW SocketException: No connection could be made because the target machine actively refused it.
  filtered: main-pc:10899     THROW TaskCanceledException: A task was canceled.
```

A refused port fails fast and names itself; a filtered port waits out the whole timeout and looks
exactly like a slow provider — and exactly like TVmaze itself when blocked (§9.1). So the connect
timeout exists for the filtered case, and the exception type alone never identifies the culprit. §9.6
rewritten accordingly before the entry was written, so the log and the analysis agree.

**Self-caught during the same pass:** the first draft of `ARCHITECTURE.md` §4 added
`Imdb:DatasetBaseUrl`. No document owned an IMDb dataset URL config key, and it was not part of the
requirement — invented scope. Removed. Routing is by **named client** (`Tvmaze`/`MyShows`/`Imdb`), not
by URL pattern, which also avoids making routing a side effect of a configurable base URL.

**New facts learned:** the proxy serves HTTP CONNECT and SOCKS4/4a/5 on one port; .NET's proxy scheme
whitelist is exactly those five plus bare `host:port`; `TaskCanceledException` is produced by a blocked
provider, a filtered proxy, and a slow response alike, so route information must be logged explicitly.

**Unblocked:** step 0.4 can now bind proxy config and carry the R14 transport-error test. Steps 2.2,
3.1 and 7.4 each know which named client they use.

**Still open:** whether a proxy will ever need credentials (`STATE.md` open question 7). The verified
one does not.

### 2026-09-28 — Cold-start walkthrough of the new path; four defects found and fixed

**What happened:** before implementing anything, the new path (`AGENTS.md` → `STATE.md` →
`tasks/0.1-*.md` → the sections its `Read` list names) was followed exactly as a fresh agent would,
and every factual claim encountered was checked against the document that owns it. Four were wrong.

| claim | found in | actual |
|---|---|---|
| "nine projects" | `tasks/0.1` | **11** — `ARCHITECTURE.md` §2 graph has 11 nodes |
| "Four job console projects" | `STATE.md` step 0.7 | **six** job projects |
| "the four reference bans" | `tasks/0.1` Read list | `AGENTS.md` has **8** hard rules; the four intended ones are now named |
| "`ARCHITECTURE.md` §7 puts logs in the repo root" | `tasks/0.1` | §7 puts `log/` **beside `data/` under the base data directory** |

Also corrected: "seven `.gitignore` patterns" (the list has eight) — replaced with a pointer to the
list rather than a count.

**The recurring defect this reproduces:** every one of these was a *derived* fact — a count, a
location, a list restated in a second document. The restructure earlier today removed the id encoding
from seven documents for exactly this reason; these were the same failure at smaller scale, in files
written minutes before. The fix applied each time was to delete the number and name the owner: the
project set is defined by the §2 graph, not by a count in a task file.

**Verified facts recorded for later steps:**
- SDKs 7.0.120, 8.0.425, 10.0.401 installed; `dotnet --version` → **10.0.401**. `global.json` is
  load-bearing, not precautionary — without it step 0.1 builds against 10.
- `maui-windows` **8.0.3/8.0.100** is installed and visible to SDK 8 (probed in a temp dir with a
  pinned `global.json`), so step 0.5 should restore without a workload install.
- `dotnet msbuild -getProperty:TargetFramework` **works under SDK 8** (verified against a probe
  project), so step 0.1's acceptance criterion is runnable and not SDK-10-only.
- `git` 2.37 is installed but **this repo has no `.git`** — step 0.1's `git status` criterion requires
  `git init` first, which is not currently in any step.

**Verification:**
```powershell
PS> Select-String -Path tasks\0.1-*.md -Pattern 'nine project|seven .gitignore|four reference bans|LangVersion'
# (no output)
PS> (Select-String -Path docs\*.md,AGENTS.md,tasks\*.md -Pattern 'Four job console').Count
0
PS> (Get-ChildItem docs\*.md,AGENTS.md | ForEach-Object { <authority entries> } | Group-Object |
      Where-Object Count -gt 1).Count
0
```

**Deviation from the restructure's own intent — needs a decision:** the task file duplicated the plan.
All nine distinctive facts in `Directory.Build.props` (`LangVersion`, `TreatWarningsAsErrors`,
`EnforceCodeStyleInBuild`, `AnalysisLevel`, …) appeared in both `IMPLEMENTATION_PLAN.md` step 0.1 and
`tasks/0.1-*.md`, and they had **already drifted** — the task file added `*.db-wal`/`*.db-shm`/`log/`
to the ignore list that the plan does not carry. The duplicated props list has been replaced with a
pointer. What remains is a design question, not a bug: `TEMPLATE.md` tells the agent to *copy* the
acceptance criteria, so a task file is a deliberate fork of its plan section. It is defensible because
task files are short-lived — created at step start, archived at step end — unlike `PROGRESS.md`, which
lived indefinitely and accumulated contradictions. But it should be chosen knowingly rather than
discovered again at step 4.1.

**Unblocked:** step 0.1 can be executed from its task file without the agent hitting a contradiction.
The task-file-vs-plan ownership question is open and does not block step 0.1.

> Older entries mention `docs/PROGRESS.md`. That file was split into `STATE.md` (current state) and
> this one (the Step Log) on 2026-09-28. The old name is left as written — an entry records the
> filenames that existed on its date, and rewriting them would make the entry unverifiable.

### 2026-09-28 — Documentation restructure: declared ownership, single encoding owner, STATE/LOG split

**What happened:** the corpus was measured rather than judged, and the measurement showed the unit of
ownership was the *document* rather than the *fact*. Four changes followed.

1. **Declared ownership.** `status` / `authority` / `not_authoritative_for` front matter added to all
   nine substantive documents. `AGENTS.md` declares no authority — it is a router.
2. **One owner for the id encoding.** New `SCHEMA.md` §1a is now the only place the bit layout, epoch,
   mint expression and EF `ValueGenerated` trap are stated. Removed from `SPEC.md` R13 (kept the
   invariant, deleted the encoding), from `AGENTS.md` (kept "no store-generated keys"), and from
   `ARCHITECTURE.md` §5 (kept the retry rule, which is architectural). `IMPLEMENTATION_PLAN.md` step
   1.6 keeps the mint expression as an operational consequence and names §1a as owner.
3. **`PROGRESS.md` split** into `STATE.md` (135 lines, `status: current`, rewritten freely) and
   `LOG.md` (this file, `status: historical`, append-only). The old file is at
   `draft/archive/PROGRESS.pre-split.md` with `status: deprecated` — kept only because this repo has
   no version control, and safe to delete once git exists.
4. **Task layer added.** `tasks/TEMPLATE.md` plus the first promoted step,
   `tasks/0.1-solution-and-build-conventions.md`. `AGENTS.md` now routes a new agent to
   STATE → task file → the two or three owning sections.

**Also fixed:** `ARCHITECTURE.md` had §7 Logging physically before §6 Testing. Reordered the blocks,
kept the numbers, so every existing `§7` reference stays valid.

**Verification — the duplication this was meant to remove.** Measured with
`Select-String -Path docs\*.md,AGENTS.md -Pattern <fact> -AllMatches`, excluding `LOG.md` from the
"after" counts because an append-only log legitimately preserves the wording of past dates:

```
fact                     before          after
41 bits                  9 hits/7 files   2 hits/1 file    (DECISIONS ADR-027 only)
ValueGenerated          13 hits/7 files  11 hits/4 files
AUTOINCREMENT           13 hits/7 files   9 hits/4 files
writer_lease            19 hits/7 files  16 hits/7 files
deleted_at IS NULL      15 hits/7 files  14 hits/6 files
client-generated        15 hits/7 files  11 hits/6 files
```

The **bit layout** is the fact that actually got fixed: it went from seven documents to one (plus
`DECISIONS.md`, which is the ADR that decided it and legitimately records the widths). The others
moved less, and that is correct rather than incomplete — `writer_lease`, `deleted_at IS NULL` and
"AUTOINCREMENT is banned" are *invariants* that every layer must obey, so they legitimately appear as
an operational consequence in each layer's document. The rule this restructure enforces is not "one
mention per fact" but "one owner per fact, and the rest must be consequences, not restatements."

```powershell
# Every authority name is declared by exactly one document:
PS> Get-ChildItem docs\*.md,AGENTS.md | ForEach-Object { <authority entries from front matter> } |
      Group-Object | Where-Object { $_.Count -gt 1 }
# (no output — 12 distinct names, 12 owners, 0 duplicates)
```

**Reading cost for one step, before and after:** implementing step 1.6 previously meant reading
3,223 lines across seven files. It is now `AGENTS.md` (127) + `STATE.md` (~145) + the step's task file
+ `SCHEMA.md` §1a/§2 + ADR-027/028 — roughly 500 lines, and the task file names the sections rather
than saying "read the schema". This is an estimate of a path, not a measurement: it becomes a
measurement the first time a step is actually executed from a task file.

**Deviations:** none from the agreed plan (options 1+2+3+4, all four). Two things were decided in
flight: `ARCHITECTURE.md` was reordered rather than renumbered, to avoid invalidating cross-references
written by earlier steps; and the old `PROGRESS.md` was archived to `draft/` instead of deleted,
because there is no git history to recover it from.

**New facts learned:** PowerShell 5.1 reads BOM-less `.ps1` files as ANSI, so non-ASCII literals
*inside a script* get mangled on the way to the file — two em-dashes and three in `LOG.md` needed
repair. Scripts that write markdown must be ASCII-only or use explicit code points.

**Unblocked:** step 0.1 can start from its own task file. Nothing else was blocked.
### 2026-09-28 — Open question 5 resolved: 64-bit ids (ADR-027) + concurrent writers (ADR-028)

**What happened:** open question 5 was put to the user with four options and the recommendation being
`BLOB` big-endian. **The user chose the fourth option instead — drop UUIDv7 and keep a 64-bit
client-generated id.** Follow-up questions settled the layout as **41-bit ms + 10-bit process salt +
12-bit sequence**, and established that **jobs may run while the app is open**, i.e. concurrent writers
are real.

That second answer is the more consequential one and it was not originally on the list of open
questions. It was only asked because the salt field's justification depends on it. It retroactively
strengthens ADR-026 (store-assigned ids are now unusable for two independent reasons) and it created a
new requirement that nothing in the design previously addressed: WAL serialises single write
transactions, but `--promote` is a multi-transaction bulk write, so a job running alongside it can read
a half-promoted database and draw a confident wrong conclusion.

**Files changed:**
- `docs/DECISIONS.md` → **ADR-027** (64-bit key layout, supersedes the mechanism half of ADR-026) and
  **ADR-028** (concurrent writers + `writer_lease`). ADR-026 annotated as partly superseded rather than
  edited, per protocol. 28 ADRs.
- `docs/SPEC.md` → v1.4. **New R13** (identity minted in-process, concurrent writers, lease). Layer
  table gained a **Coordination** row. Acceptance criteria gained Identity and Concurrency blocks.
- `docs/SCHEMA.md` → v1.3. §1 states the concrete key type and the `ValueGenerated.None()` trap; new
  **`writer_lease`** table in §2 with the conditional-upsert acquire.
- `docs/ARCHITECTURE.md` → v1.3. `IWriterLease` in Application; **IDs** rewritten with the layout and
  the two ways to get it wrong; new **Concurrency** cross-cutting section; `Sync` block in config.
- `docs/IMPLEMENTATION_PLAN.md` → v1.3. Step 1.1 **unblocked** with a reflective model assertion;
  **new step 1.6** for `IdGenerator` + `WriterLease`; `writer_lease` added to 1.2; exclusive lease +
  lease-loss abort added to 2.6; lease requirement added to the Phase 7 preamble; hard stops back down
  to two.
- `docs/PROGRESS.md` → status, step table (1.1 unblocked, 1.6 added), open question 5 closed, new open
  question 6, this entry.

**Verification performed:**

| Check | Method | Observed |
|---|---|---|
| Does the chosen bit layout actually fit? | summed the fields as first written: `1 + 1 + 41 + 10 + 12` | **65 bits — does not fit a `long`.** Caught before it reached code. Corrected to the standard Snowflake split `1 + 41 + 10 + 12 = 64` and added an explicit note that there is **no spare bit**, because the natural instinct is to add headroom |
| Does 41 bits of ms actually last? | 2⁴¹ ms ≈ 6.87 × 10¹³ ms | ≈ **69.7 years** from the 2026 epoch → exhausts ~2095. Logged as open question 6 rather than silently accepted |
| Is the import's id volume within the sequence field? | ~15,000 ids per full import vs 2¹² = 4,096/ms | ~4 ms of capacity at full rate. Comfortable, and the exhaustion path is specified anyway (spin, never wrap) |
| Can the lease be acquired without a race? | reviewed the SQL | The conditional upsert's `WHERE expires_at < excluded.acquired_at` makes it atomic — no read-then-write window. Requires SQLite ≥ 3.24; `Microsoft.Data.Sqlite` ships far newer |
| Is retrying a failed write actually safe here? | cross-checked against ADR-023 | Yes, **only because** every writer is an idempotent upsert on natural keys. Recorded as a constraint on future steps, not just a local detail |
| Stale references after the change | `grep 'UUIDNext\|uuidv7\|BLOB\|result table'` across docs | Remaining hits are the historical record inside ADR-026 and the rejection lists, which is correct — they explain *why*, they don't state the design |

**Deviations from the original request:** the user's earlier instruction was "uuidv7 converted to int
with UUIDNext". After being shown that no sound conversion exists, the user chose to keep `INTEGER` and
drop UUIDv7. So **UUIDNext is no longer a dependency of this project at all** — the generator is ~40
lines over `DateTimeOffset.UtcNow`, which incidentally keeps `Shows.Domain` and `Shows.Application`
dependency-free (ADR-001). Anyone picking this up should not go looking for UUIDNext in the plan.

**New facts later steps depend on:**
- **The SQLite provider will fight ADR-026.** It assumes `INTEGER PRIMARY KEY` is a store-generated
  rowid and configures `ValueGenerated.OnAdd` by default. `ValueGenerated.None()` must be applied by
  convention, and the *model* must be asserted reflectively — grepping the migration only proves the
  script, not that EF won't try to read a value back at runtime.
- **A failed `SaveChanges` is not resumable.** SQLite aborts the statement, not the transaction, so the
  in-transaction state is untrustworthy. Retry = roll back the whole unit of work and re-run with fresh
  ids. This is only affordable because writers are idempotent.
- **Renewing a lease that updated zero rows means stop writing**, not warn-and-continue. Two processes
  writing the same rows is the exact failure the lease exists to prevent.
- **`--promote` must be exclusive of all other leases**, not merely hold its own. It is the one
  operation that must not interleave.
- **`Migrate()` needs exclusivity too**, and the app should refuse to start with a message naming the
  running job rather than migrating underneath it.
- **The app deliberately takes no lease.** Blocking the UI because a job happens to be running would be
  a worse product; interactive writes are single-row and covered by WAL plus retry. Worth stating so
  nobody "optimises" the id generator back to a sequence later.

**Unblocked:** **step 1.1 and all of Phase 1.** Phase 0 → 1.1 → 1.2/1.2b/1.3 → 1.4 → 1.5 is now a
continuous path with no user decision in it, up to the step 2.5 human gate.

### 2026-09-28 — Doc revision: user-data split + logical delete, derived URLs, client-generated ids

**Three user corrections**, all schema-level:

1. **"Move user data from the shows table, it should be constructable from sources. Use logical
   delete."** → new `user_show` table (1:1, shared PK) + `deleted_at`. `tracked_show` is now purely
   derived. ADR-024, SPEC P4 rewritten, R8 rewritten.
2. **"`tvmaze_url` is not needed, it can be constructed from id. Both tvmaze and imdb ids exist on
   episode and can be nullable… season is just a grouping entity, it can't have external ids."** →
   SPEC P5 added (nothing derivable is stored), both URL columns dropped, `episode`'s two provider
   ids made symmetrically nullable, `season.imdb_id` removed. ADR-025, partly superseding ADR-011.
3. **"Change id to not autoincrement, they should be client generated. Use uuidv7 converted to int
   with UUIDNext."** → ADR-026. **Partly blocked** — see open question 5.

**Files changed:**
- `docs/SPEC.md` → v1.3. P4 rewritten (three user tables, named), new **P5** (nothing derivable is
  stored), R2/R8/R11 updated, `user_show` documented in §2, layer table split into mapped vs user,
  acceptance criteria for untracking and for the rebuild drill.
- `docs/SCHEMA.md` → v1.2. §1 conventions (client-generated keys, logical delete), §2 split into
  rebuildable vs user-owned with a new **§2a rebuild drill**, `user_show` added, `tvmaze_url` and
  `season.imdb_id` dropped, episode id nullability symmetrised, §7 indexes rewritten
  (`ix_tracked_category` → `ix_user_show_category`), §8 sizes.
- `docs/ARCHITECTURE.md` → v1.2. `UserShow` entity, `IUserShowRepository`, `IIdGenerator`, the
  mapper's **no-id-regeneration** rule, IDs and soft-delete cross-cutting decisions.
- `docs/DECISIONS.md` → ADR-024/025/026 appended (26 total); ADR-011 and ADR-020 annotated as partly
  superseded rather than edited.
- `docs/IMPLEMENTATION_PLAN.md` → steps 1.1 (blocked), 1.2, 1.4, 1.5, 2.6, 3.2, 5.1, 6.1, 7.x, 8.1
  updated; rebuild drill added to 1.5; third hard stop added.
- `docs/PROGRESS.md` → step table, open question 4 closed, **open question 5 opened**, this entry.

**Verification performed:**

| Check | Command / method | Observed |
|---|---|---|
| UUIDNext API surface | fetched nuget.org package page + `Doc/uuidnext.uuid.md`, `uuidnext.database.md`, `uuidnext.tools.uuidtoolkit.md`, `uuidnext.guidhelper.md` | `Uuid.NewSequential()` = v7; `Uuid.NewDatabaseFriendly(Database)`; **`Database` enum has `SQLite = 2` documented as "BLOB or TEXT Type"**; `GuidHelper.ToByteArray(Guid, bigEndian)` exists. **No API returns an int/long from a Guid** — nothing in the library implements "uuid → int" |
| .NET 9 `Guid.CreateVersion7` alternative | same docs | rejected: project pinned to .NET 8 (ADR-004), and UUIDNext guarantees same-ms monotonicity which the import needs |
| UUIDv7 bit layout vs SQLite INTEGER | RFC 9562 layout in `uuidtoolkit` docs | 48-bit unix-ms timestamp prefix + 74 random bits (version/variant bits consumed). 128 bits total; SQLite `INTEGER` is 64 signed → **no lossless int conversion exists**. This is what forced open question 5 |
| Does removing `season.imdb_id` break a claim elsewhere? | `grep 'season.imdb_id'` across docs | ADR-011 asserted it was added to the schema; annotated as partly superseded rather than silently edited, per this repo's protocol |
| Leftover references to the old design | `grep 'category_id\|tvmaze_url\|AUTOINCREMENT\|deletes'` in SPEC/SCHEMA/ARCHITECTURE/PLAN | `tvmaze_url` gone from both levels; `ix_tracked_category` gone; `AUTOINCREMENT` appears only in the conventions ban and the ADR rejecting it |
| Bogus citation found while editing | step 2.6 cited "SPEC R12 category mapping" | **R12 is the status-vocabulary rule**; no such mapping rule exists. Replaced with the explicit `watching`→Started / `later`→Later / `cancelled`→Cancelled / `finished`→Finished table inline |

**Deviations from the request:** one, and it is flagged rather than silently resolved. Request 3 said
"uuidv7 converted to int". The client-generation and UUIDNext parts are implemented as asked; the
"to int" part is **not written into the schema** because it cannot be done losslessly. `SCHEMA.md` §1
says the key type is pending and `PROGRESS.md` open question 5 lays out the four options. Writing
`INTEGER` anyway would have put a false fact in the spec — the exact failure mode this repo's
protocol exists to prevent.

**New facts later steps depend on:**
- **UUIDNext has no Guid→int conversion.** Anyone implementing "convert to int" will be writing
  truncation or a hash, not using a library feature.
- **`Database.SQLite` in UUIDNext is documented for BLOB or TEXT**, i.e. the library's own position is
  that SQLite stores the 128-bit value, not a reduced one.
- **The rebuild drill cannot be a wipe-and-reload.** `episode_watch.episode_id` is a surrogate key,
  so delete-then-reinsert orphans the watch rows while every row count still looks correct. The mapper
  must upsert on natural keys and treat surrogate ids as immutable. This is the single most dangerous
  property in the design and it is now a test in step 1.5, not a paragraph.
- **Soft delete leaks into the jobs.** A soft-deleted show keeps its `tracked_show` row, so job
  selection queries must filter `deleted_at IS NULL` or they burn external API calls on shows nobody
  tracks. Silent, and the cost is someone else's rate limit.
- **Search must check `user_show`, not `tracked_show`**, for "already tracked" — a soft-deleted show
  still has a `tracked_show` row, so the naive check would block the re-add the whole design exists to
  support.
- **`needs_review` is user-authored**, not a mapper fact, so it is written with the `user_show` row
  during promote and cannot be patched after the mapper runs.

**Unblocked:** nothing new — this revision mostly *adds* constraints. Step 0.1 remains the entry
point; step 1.1 is now explicitly blocked pending open question 5.

### 2026-09-28 — Doc revision v1.1/v1.2: TVmaze base model, tracked-only storage, source/result split

**Triggered by three user corrections**, each of which changed the schema rather than just the prose:

1. **"Take TVmaze data as base, not the JSON files — they're needed only for initial import."**
   → SPEC P1. Field names, granularity and semantics now follow the TVmaze payload; `premiered` is a
   real date, not a year.
2. **"Only tracked shows are stored."** → SPEC P2, ADR-020. The `show` table is **gone**; there is no
   local catalog of untracked titles. `tracked_show` is the only show table and is denormalised.
3. **"Source data and mapped results must be separate; the mapper is shared; adding a show is
   Enrichment scoped to one show."** → SPEC P3/R11, ADR-022/ADR-023. New source tables
   `tvmaze_show`, `tvmaze_episode`, `tvmaze_status_seen`; `imdb_*` are reclassified as source tables;
   one `IShowMapper` is the only writer of result rows.

Plus a fourth, non-schema request: **one logging config for all executables** → `ARCHITECTURE.md` §7.

**Files changed:**
- `docs/SPEC.md` → v1.2. Principles P1–P4, rules R11–R12, `tracked_show` as sole show table with a
  TVmaze-column mapping table, `status_id` enum, stateless search/discovery, scoped `ImdbImport`.
- `docs/SCHEMA.md` → v1.1. §2 result tables, §3 TVmaze source tables (new), §4 IMDb source tables
  (renumbered), §5 staging as the myshows source layer with an explicit column list, §6 the promote
  gate now joining `tvmaze_episode`, §7 indexes rewritten for the new table set, §8 sizes.
- `docs/ARCHITECTURE.md` → v1.1. Domain types without `Show`, `IShowMapper` in the Application
  interface list, source-table ban in Application's rules, mapper ownership rules in Infrastructure,
  source-vs-result cross-cutting decision, **new §7 Logging policy**.
- `docs/DATA_ANALYSIS.md` → §8 added (real TVmaze payloads, 13 numbered facts); correction #3 added.
- `docs/DECISIONS.md` → ADR-019…ADR-023 added; ADR-009 and ADR-014 annotated as narrowed/superseded
  rather than edited, per this file's own protocol.
- `docs/IMPLEMENTATION_PLAN.md` → v1.1. New steps **0.8** (logging), **1.2b** (source schema),
  **1.5** (the mapper); rewritten 1.2, 1.3, 2.6, 3.2, 7.1–7.4, 8.1.
- `docs/PROGRESS.md` → step table, open question 4, this entry.

**Verification performed** — the point of this revision was to stop asserting and start checking, so
every new claim below is a command that was run:

| Check | Command / method | Observed |
|---|---|---|
| TVmaze show payload shape | user-supplied live response for `/shows/305` | `id 305`, `status "Running"`, `premiered "2011-12-04"`, `runtime null` + `averageRuntime 62`, `externals.imdb "tt2085059"`, HTML `summary`, `updated 1767985660` |
| TVmaze episode payload shape | same, `/shows/305/episodes` first element | `id 30902`, `type "regular"`, `airdate "2011-12-04"`, `airstamp "...T21:00:00+00:00"`, `runtime 60` |
| The two id spaces differ | read `watching.json` around `"Black Mirror"` | myshows `"id": 22410` vs TVmaze `305` — **no relationship**. Confirms ADR-019 |
| The old R5 predicate was broken | compare payload `status` against the planned predicate | planned `status NOT IN ('Running Series','Returning Series')`; TVmaze says `"Running"` → predicate matches nothing → **every airing show would auto-finish**. Fixed by R12 enum + fail-closed `Unknown` |
| myshows status vocabulary differs | `Select-String -Context 6,2 '"Black Mirror"' watching.json` | `"status": "Returning Series"` — a **myshows** string, confirming the two vocabularies are distinct and that `staging_show` must not carry it forward |
| Duplicate section numbering in SCHEMA.md | `Select-String '^## \|^### '` on `docs/SCHEMA.md` | two `## 4` headings found; now 1–8 with no duplicates |
| Dangling ADR references | `Select-String 'ADR-019\|ADR-02[0-3]'` across `docs/*.md` | SPEC cited five ADRs that did not exist; `DECISIONS.md` now has 23 headings, ADR-001…023 contiguous |
| `DATA_ANALYSIS.md §8` / `ARCHITECTURE.md §7` referenced but absent | `Select-String '^## '` on both files | both were missing; both now exist and every SPEC reference resolves |

**Deviations from plan:** the plan itself was wrong in two ways found while revising, both recorded in
`IMPLEMENTATION_PLAN.md`:
- Step 1.3 previously created `staging_tvmaze_catalog_episode`. That is a *source* table masquerading
  as staging; it is now `tvmaze_episode` (§3) and the promote view joins it.
- `v_staging_issues` selects `s.title`, which no longer exists — the staging column is `title_ru`
  (the export's `title` is the Russian name, ADR-015). The view SQL is corrected.

**New facts later steps depend on:**
- **`"Running"` is TVmaze's string for an airing show.** Any code comparing against
  `"Running Series"` is wrong. R12's mapping table is the only place raw strings appear.
- **`runtime` can be null while `averageRuntime` is set** (Black Mirror: `null` / `62`). Store both;
  do not collapse them at map time.
- **`summary` is raw HTML** in both show and episode payloads. Store raw, strip at render — stripping
  at ingest is irreversible.
- **`airstamp` carries an offset** that can move the date across midnight, so `airdate` (text) and
  `airstamp` (instant) are both stored and neither is derived from the other.
- **`network` is null for streaming shows** with `webChannel` populated; read both.
- **`_links.previousepisode` includes the episode name**, a cheap latest-episode probe for
  `StatusRecheck` that avoids fetching the whole episode list.
- **`tvmaze_count_mismatch` in the promote gate is silent until episodes are fetched** — an empty
  view early in the pipeline is not a clean bill of health.

**Unblocked:** step 0.1, and with it all of Phase 0 including the new 0.8. Step **1.5 is now the
pivot**: `--promote`, search-add and all four jobs depend on the mapper, so no data-producing step
should start before it.

### 2026-09-28 — Planning complete, docs written

**Files created:** `AGENTS.md`, `docs/PROGRESS.md`, `docs/SPEC.md`, `docs/DATA_ANALYSIS.md`,
`docs/ARCHITECTURE.md`, `docs/SCHEMA.md`, `docs/DECISIONS.md`, `docs/IMPLEMENTATION_PLAN.md`.

**Built:** no code. Planning artifacts only.

**Verification performed** (all against the real files in this workspace, not from memory):

| Check | Method | Result |
|---|---|---|
| Show counts per category | grep `"show": {` at 12-space indent | watching 78, later 246, cancelled 16, finished 207 = **547** |
| Cross-check vs totals batch | `episodes.json` batch `id:57` | 78 / 246 / 16 / 207 — **matches exactly** |
| Episode arrays are unwatched set | show 86813: `totalEpisodes` 33, `watchedEpisodes` 1 | exactly **32** episodes listed → arrays are unwatched |
| `started` flag distribution | grep `"started": true` | 13 true, 30 false (43 total) |
| Watching breakdown | paired `totalEpisodes`/`watchedEpisodes` | **33** fully watched, **31** zero watched, **14** partial |
| `show.id` is myshows id, not TVmaze | live fetch `myshows.me/view/22410/` and `/86813/` | returns Black Mirror / Brilliant Minds → **myshows id** |
| myshows page exposes IMDb id | same fetch | `imdb.com/title/tt2085059/` and `tt26448526/` present → **scrape viable** |
| IMDb has episode titles | grep `\ttvEpisode\t` in `title.basics.tsv` | `tt2089051 tvEpisode The National Anthem 2011 44` → **yes** |
| IMDb episode mapping complete | grep `tt2085059` in `title.episode.tsv` | all rows have real season/episode, no `\N` |
| Tri-source count agreement | count `tt26448526` children | **33** rows == export `totalEpisodes` 33 == TVmaze 13+20 |

**Deviations from plan:** none. Two earlier planning claims were corrected by the checks above and
the corrections are recorded in `DATA_ANALYSIS.md` and `DECISIONS.md`:
1. `show.id` was initially assumed to be a TVmaze id. It is a **myshows.me** id.
2. IMDb datasets were initially described as lacking episode titles. They **contain** them.

**New facts later steps depend on:**
- One show in `watching.json` has `totalEpisodes: 0` and `watchedEpisodes: 0`. The backfill algorithm
  must not divide by zero or mark it finished. See SPEC rule R7.
- IMDb `tvEpisode` rows inherit `genres` from the parent show — not meaningful per episode.
- `title.episode.tsv` does contain `\N` season/episode values for some legacy episodes. Map to
  unknown, never to 0.

**Unblocked:** step 0.1. All of Phase 0 can proceed in parallel.
