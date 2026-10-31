-- =====================================================================
-- FIX: watch marks fabricated by --promote for episodes that had not
--      aired yet at import time.
--
-- ROOT CAUSE — Promoter.BackfillWatchMarksAsync, branch 3.
-- Branch 3 fires when the myshows export says "fully watched"
-- (watched_episodes == total_episodes) and no explicit staging_episode
-- list exists, then marks EVERY counted episode in the TVmaze catalog.
-- Two ways that over-marks, and both stay invisible because
-- needs_review is left 0:
--
--   A. 0/0 shows. total == watched == 0 satisfies `watched == total`, so
--      a show myshows knows nothing about (not yet airing) takes branch 3
--      instead of branch 4 ("nothing watched"). DATA_ANALYSIS §5 says a
--      0/0 show must land in branch 4.
--
--   B. myshows total_episodes lags TVmaze. myshows counts only episodes
--      it knows; TVmaze's catalog already lists future-dated ones.
--      "Fully watched" on myshows' list != fully watched on TVmaze's
--      list, so the future episodes inherit a watched mark.
--
-- SCOPE — only marks created during the import. A mark made later in the
-- UI on a not-yet-aired episode is legitimate (early release, moved-up
-- date) and is left alone. The import instant is derived from the data
-- itself (max promoted_at), never hard-coded. All watched_at values share
-- one ISO-8601 format with a +00:00 offset, so TEXT comparison is
-- correct — the same property the writer_lease code relies on.
--
-- TWO TIERS. A mark on an episode that STILL has not aired is provably
-- wrong — it could not have been watched — so STEP 2 deletes those. A
-- mark on an episode that aired between the import and today is
-- ambiguous: the mark was fabricated at import time, but you may
-- genuinely have watched it since, and the UI would never have corrected
-- it because the row already read "watched". Those are listed in STEP 1b
-- and left alone by default — re-toggle them in the app if you did not.
--
-- Run inside a transaction: review STEP 1a/1b and STEP 4, then COMMIT.
-- =====================================================================

BEGIN IMMEDIATE;

-- ---------------------------------------------------------------------
-- STEP 0: collect candidate rows BEFORE deleting, so STEP 3 can still
--         tell which shows were touched.
-- ---------------------------------------------------------------------
CREATE TEMP TABLE _fabricated AS
SELECT
    w.id AS watch_id,
    e.show_id,
    -- 1 = still in the future: provably never watched, delete it.
    -- 0 = aired between the import and today: ambiguous, review by hand.
    CASE WHEN e.air_date > date('now') THEN 1 ELSE 0 END AS definite
FROM episode_watch w
JOIN episode e ON e.id = w.episode_id
WHERE w.watched_at <= (SELECT max(promoted_at) FROM staging_show)
  AND e.air_date IS NOT NULL
  -- date() strips the time+offset, so both sides become plain 'YYYY-MM-DD'
  -- and the comparison is date-vs-date rather than timestamp-vs-date.
  AND date(w.watched_at) < e.air_date
  -- The backfill only ever marks Regular(1) / significant Special(2), so
  -- this confines the delete to rows the buggy branch could have written.
  AND e.episode_type_id IN (1, 2);

-- ---------------------------------------------------------------------
-- STEP 1a: REVIEW — the rows STEP 2 deletes. Every one of these should
--          be an episode you could NOT have watched yet.
-- ---------------------------------------------------------------------
SELECT ts.title, e.season_number AS s, e.episode_number AS ep,
       e.air_date, w.watched_at, ts.status_id
FROM _fabricated f
JOIN episode_watch w ON w.id = f.watch_id
JOIN episode e       ON e.id = w.episode_id
JOIN tracked_show ts ON ts.id = e.show_id
WHERE f.definite = 1
ORDER BY ts.title, e.season_number, e.episode_number;

-- STEP 1b: REVIEW — aired after the import but on/before today. NOT deleted.
--          If you did not actually watch these, clear them in the app.
SELECT ts.title, e.season_number AS s, e.episode_number AS ep,
       e.air_date, w.watched_at
FROM _fabricated f
JOIN episode_watch w ON w.id = f.watch_id
JOIN episode e       ON e.id = w.episode_id
JOIN tracked_show ts ON ts.id = e.show_id
WHERE f.definite = 0
ORDER BY ts.title, e.season_number, e.episode_number;

-- STEP 1c: cached columns BEFORE, for the shows being touched. Compare STEP 4.
SELECT ts.title, ts.watched_regular AS before_watched, ts.total_regular AS before_total,
       ts.auto_finish_eligible AS before_afe, ts.has_unwatched_specials AS before_hus
FROM tracked_show ts
WHERE ts.id IN (SELECT DISTINCT show_id FROM _fabricated WHERE definite = 1)
ORDER BY ts.title;


-- ---------------------------------------------------------------------
-- STEP 2: delete the provably-fabricated marks.
-- ---------------------------------------------------------------------
DELETE FROM episode_watch
WHERE id IN (SELECT watch_id FROM _fabricated WHERE definite = 1);


-- ---------------------------------------------------------------------
-- STEP 3: recompute the cached columns for every show touched.
--   Mirrors ShowProgress.Compute (SPEC R4/R5/R7, ADR-034):
--     watched_regular         counted episodes that ARE watched
--     total_regular           counted episodes
--     has_unwatched_specials  EXISTS an unwatched insignificant special
--     auto_finish_eligible    total > 0 AND fully watched AND status IN (2,3)
--                             (ShowStatusRules.NoMoreEpisodesExpected =
--                              Ended|Canceled; Unknown/TBD/InDevelopment are
--                              deliberately excluded)
--   where "counted" = episode_type_id IN (1,2) = Regular + significant
--   Special. All four are recomputed so each row ends up self-consistent
--   with the Domain function regardless of what it held before.
-- ---------------------------------------------------------------------
UPDATE tracked_show
SET
    watched_regular = (
        SELECT count(*)
        FROM episode e
        JOIN episode_watch w ON w.episode_id = e.id
        WHERE e.show_id = tracked_show.id
          AND e.episode_type_id IN (1, 2)
    ),
    total_regular = (
        SELECT count(*)
        FROM episode e
        WHERE e.show_id = tracked_show.id
          AND e.episode_type_id IN (1, 2)
    ),
    has_unwatched_specials = (
        SELECT CASE WHEN EXISTS (
                   SELECT 1
                   FROM episode e
                   WHERE e.show_id = tracked_show.id
                     AND e.episode_type_id = 3
                     AND NOT EXISTS (SELECT 1 FROM episode_watch w
                                     WHERE w.episode_id = e.id)
               ) THEN 1 ELSE 0 END
    ),
    auto_finish_eligible = CASE
        WHEN (SELECT count(*) FROM episode e
              WHERE e.show_id = tracked_show.id
                AND e.episode_type_id IN (1, 2)) > 0
         AND (SELECT count(*) FROM episode e
              JOIN episode_watch w ON w.episode_id = e.id
              WHERE e.show_id = tracked_show.id
                AND e.episode_type_id IN (1, 2))
            = (SELECT count(*) FROM episode e
               WHERE e.show_id = tracked_show.id
                 AND e.episode_type_id IN (1, 2))
         AND status_id IN (2, 3)
        THEN 1 ELSE 0
    END
WHERE id IN (SELECT DISTINCT show_id FROM _fabricated WHERE definite = 1);

-- ---------------------------------------------------------------------
-- STEP 4: same shows, cached columns AFTER. Review, then COMMIT.
-- ---------------------------------------------------------------------
SELECT ts.title, ts.watched_regular AS after_watched, ts.total_regular AS after_total,
       ts.auto_finish_eligible AS after_afe, ts.has_unwatched_specials AS after_hus
FROM tracked_show ts
WHERE ts.id IN (SELECT DISTINCT show_id FROM _fabricated WHERE definite = 1)
ORDER BY ts.title;

-- ROLLBACK;  -- uncomment if STEP 1a or STEP 4 looks wrong
COMMIT;
