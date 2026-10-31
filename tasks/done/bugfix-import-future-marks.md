---
status: done
---

# Step bugfix-import-future-marks — The backfill invented watch marks for not-yet-aired episodes

## Read
- `docs/DATA_ANALYSIS.md §5` — the four-branch backfill algorithm this step amends
- `docs/DECISIONS.md ADR-018` — the original backfill heuristic and the user's "tell me when the count
  disagrees" rule
- `docs/DECISIONS.md ADR-034` / `Shows.Domain/ShowProgress.cs` — the counted set (`Regular` +
  significant `Special`) and the four cached columns the repair must recompute
- `docs/SCHEMA.md §5` — `staging_episode` is the UNWATCHED set; `docs/SCHEMA.md §2` — `episode_watch`
- `Shows.Infrastructure/Import/Promoter.cs` — `BackfillWatchMarksAsync`, the defect site

## Symptom
User: watched episodes whose air date is after the import. Their probe:
`select * from episode_watch w join episode e on w.episode_id = e.id where w.watched_at < e.air_date`.
14 rows on the pre-fix DB, all stamped inside the `--promote` window, `needs_review = 0` on every one.

## Do
- `Promoter.cs` — branch 3 gated on `TotalEpisodes > 0` (a 0/0 show is branch 4, per §5); a
  post-branch withhold of any episode with `AirDate > importDay`; branch 1's `needs_review` moved to
  after the withhold so the count comparison sees the final mark set.
- `PromoterTests.cs` — `Payload` gained `futureRegulars`; 3 new tests.
- `docs/DATA_ANALYSIS.md §5`, `docs/DECISIONS.md` (ADR-037), `docs/LOG.md`, `docs/STATE.md`.
- `draft/fix-import-watched-before-air.sql` — the repair the user runs (no re-import).
- `draft/dbq.py`, `draft/test_fix_sql.py` — read-only query helper + script verifier.

## Verify
```
> dotnet test Shows.Tests --filter "FullyQualifiedName~PromoterTests"
Passed!  - Failed: 0, Passed: 15, Skipped: 0, Total: 15

> # guards temporarily neutralised (branch-3 gate dropped, withhold `false &&`)
Failed!  - Failed: 3, Passed: 12, Total: 15

> dotnet test Shows.Tests
Passed!  - Failed: 0, Passed: 356, Skipped: 0, Total: 356

> python draft\test_fix_sql.py        # applied to a consistent copy via the sqlite3 backup API
STEP 1a deletes 11 (Blade Runner 2099 S1E1-E8, Neuromancer S1E1, Silo S4E1, Yellowjackets S4E1)
STEP 1b leaves 3 for manual review (Dark Matter S2E6/S2E7, Dexter: Resurrection S2E1)
import-era marks still on NEVER-AIRED episodes: 0
watched_regular / total_regular / auto_finish_eligible / has_unwatched_specials disagreeing: 0/0/0/0
marks on non-counted episodes lost: 0
episode_watch rows: 10945 -> 10934 (deleted 11)
```

## New facts
- **`totalEpisodes` from myshows counts only episodes myshows has seen**, so it is NOT the size of the
  TVmaze catalog. Measured: Шершни 29 vs 30 counted, Укрытие 30 vs 31, Темная материя 14 vs 19,
  Декстер: Воскрешение 10 vs 11. This is the mechanism, and §5 did not state it.
- Excess marks are 100 % future-dated (`excess_past = 0` for all six shows), so the user's air-date
  probe catches every over-mark — no silent past-dated ones.
- Only two staging rows are 0/0: 77831 (Бегущий по лезвию 2099, 8 counted episodes, all future) and
  88646 (Нейромант, 1 counted, 2027-01-22). Both `In Development` — which is exactly when a 0/0 show
  has a populated catalog.
- `max(staging_show.promoted_at)` = 2026-09-29 21:43:57+00:00; last import mark 21:43:56, first UI mark
  2026-09-30 14:07. So `watched_at <= max(promoted_at)` cleanly separates import marks from the user's.
- All 547 staging rows are promoted, 0 unpromoted — `--promote` will not run again on this DB.

## Deviations
- The script deletes only the *provably* wrong marks (episode still unaired today). Marks on episodes
  that aired between the import and today are ambiguous — the user may have watched them since, and the
  UI would never have corrected a row that already read "watched" — so they are listed, not deleted.
- No migration: no schema changed. The repair is data-only, run by the user.
- The code fix is prevention for a future import, not the repair, because `--promote` is one-shot.

## Unblocked
Nothing was blocked. A future `--reset --stage` + `--promote` now reconstructs watch state without
inventing marks.
