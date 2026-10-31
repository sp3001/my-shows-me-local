---
status: current
---

# Step improvement 25 — the Updates tab

## Read
- `docs/STATE.md` — current state, and the fact that no status-change timestamp existed before this step
- `docs/SPEC.md` F4 — the tracking view, and (as of v1.6) the two tabs that are views rather than categories
- `docs/SCHEMA.md` §2 `tracked_show` — the three transition columns and what each one means
- `docs/DECISIONS.md` ADR-038 — why dated transitions and not a previous-status column
- `docs/ARCHITECTURE.md` §2 — source-table containment: why `tvmaze_updated_at` has to be a mapped copy
- `docs/SPEC.md` R12 — the single definition of "no more episodes expected" this step reuses

## Do
1. `TrackedShow`: `StatusChangedAt`, `StatusRenewedAt`, `TvmazeUpdatedAt` (all nullable).
2. Migration `0011_ShowStatusTransitions`, scaffolded then hand-edited: three `AddColumn<DateTimeOffset>`
   plus two `migrationBuilder.Sql` backfills — `status_changed_at` from `max(episode.air_stamp)` for
   terminal shows, `tvmaze_updated_at` from `strftime(..., provider_updated, 'unixepoch')`. No renewal
   backfill.
3. `TvmazeShowMapper.MapShowAsync(TvmazePayload)`: capture `previousStatus` before `ApplyShowFields`;
   after the cached-columns flush (so this map's episodes are visible) call `RecordStatusTransitionsAsync`;
   then set `TvmazeUpdatedAt` unconditionally, not counted as a change.
4. Reads: `IEpisodeRepository.ListWatchesSinceAsync` (raw SQL, second-precision cutoff — a
   provider-formatted parameter drops the boundary second), `ListByIdsAsync`,
   `ITrackedShowRepository.ListActiveByIdsAsync`.
5. `GetUpdatesFeedUseCase` (Application) + `AddScoped`. Windows are constants on the use case, not config.
6. UI: `UpdatesSectionViewModels.cs`, `UpdatesSectionView`/`UpdatesRowView` (ViewLocator by name), the
   fifth `CategoryTab`, `IsUpdates`, `IsCategoryList`, `LoadUpdatesAsync`, the `ApplyFilterAndSort` branch
   placed **before** the `IsUpcoming` branch.

## Acceptance criteria
Forked from the user's request (this is a `user-issues.md` improvement, not a numbered plan step):

- [x] A tab labelled "Updates" sits immediately after Finished and reuses the Upcoming layout.
- [x] **Ended Shows** — terminal shows whose end date falls in the last 60 days, ordered by that date
      desc, showing title + date. `Bite Size Terror` (ended 2026-10-02) is the one row on the live DB.
- [x] **Renewed Shows** — shows that left terminal for an airable status in the last 60 days, ordered by
      that date desc, showing title + current status + date.
- [x] **Updated Lately** — shows TVmaze updated in the last 30 days, ordered by that date desc, title +
      date. 13 rows on the live DB.
- [x] **Recently Watched** — episodes watched in the last 30 days, ordered by `watched_at` desc, showing
      show title + `S#E#` episode title + date. 51 rows on the live DB.
- [x] A show that ended, was renewed and ended again is in **Ended**, not Renewed.
- [x] A cancelled show appears in Ended.
- [x] An untracked (soft-deleted) show's watch marks do not appear. `deleted_at IS NULL` comes from
      `ActiveShowFilter`, never re-typed here.
- [x] An empty section is hidden; if all four are empty the status line says so.
- [x] The title filter narrows all four sections without re-reading the database.
- [x] `dotnet build Shows.sln` clean with warnings as errors; every new test named for the failure it
      prevents.

## Constraints
- The mapper is the **only** writer of these columns. A job or the UI writing them would create a second
  notion of when a show ended.
- The mapper never re-mints a surrogate id, and `RecordStatusTransitionsAsync` must not count the
  `tvmaze_updated_at` mirror as a change — otherwise every refresh reports every show as changed.
- `Shows.Application` may not name `tvmaze_show.provider_updated` (SPEC P3/R11), which is the whole reason
  the column exists.
- EF Core on SQLite cannot translate a `DateTimeOffset` comparison in LINQ at all — the watch read is raw
  SQL and the three show sections filter in memory over one active-show load.
- `max()` over `air_stamp` TEXT is chronological only because every stored value is the same 25-char
  `+00:00` form. `watched_at` is **not** uniform, so its cutoff is passed at second precision.
- A first map of a non-terminal show writes **no** renewal. The guard keys on the show *being* terminal,
  not on `previous != Unknown` — a new row starts at `Unknown`, so a previous-status guard would skip the
  first-map case entirely.

## Evidence
In the Step Log: `docs/LOG.md`, entry "2026-10-31 — improvement 25 — the Updates tab…". Summary:
`dotnet build Shows.sln` → `Build succeeded. 0 Warning(s)`; `dotnet test Shows.Tests` →
`Failed: 0, Passed: 386` (356 baseline + 30 new); the three new fixtures pass 12 / 9 / 9 filtered;
live-DB backfill observed at `471 / 0 / 554` and projected sections `1 / 0 / 13 / 51`.
Moved to `tasks/done/` with `docs/STATE.md` updated in the same step.
