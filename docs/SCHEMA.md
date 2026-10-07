---
status: current
authority:
  - storage-schema
  - id-encoding
not_authoritative_for:
  - requirements
  - layer-graph
  - external-facts
---
# SCHEMA — SQLite Database Design

Version 1.5, 2026-10-07. v1.5: **a show's display country falls back to its language** —
`tracked_show.country` (ADR-036), with `tvmaze_show.country_name`/`country_code` holding the
broadcaster's country verbatim (SPEC R11).
v1.4: the raw Discovery schedule archive is stored in the
date-keyed `discovery_schedule_archive` cache table (ADR-035).
v1.3: **the surrogate key is a 64-bit time-ordered `INTEGER`** (ADR-027, resolving the open question
that blocked step 1.1), and **`writer_lease`** is added because the app and the jobs write concurrently
(ADR-028).
v1.2: **user data no longer lives in `tracked_show`** — it is split into
`user_show`, and untracking is a **logical delete** (ADR-024). Provider URLs are no longer stored
(derivable from ids), `season` carries no external id (ADR-025), and surrogate keys are
**client-generated** (ADR-026, storage type pending). Update this file in the same commit as any
migration.

---

## 1. Conventions

- snake_case tables and columns; EF Core conventions overridden with a naming convention.
- Surrogate PKs are **client-generated**; no `AUTOINCREMENT`, no database-assigned ids (ADR-026).
  Every `id` / `PK` below is that generated key: `INTEGER` in storage, `long` in C#. The encoding is
  specified in **§1a**, which is its only home in this repository.
- **Concurrent writers are expected** — the app and the jobs may write at once (ADR-028). WAL and
  `busy_timeout` below are therefore mandatory, not tuning. Bulk operations take a row in
  `writer_lease` (§2); `--promote` takes the exclusive lease.
- **Logical delete only for user data.** Nothing the user created is ever `DELETE`d. Untracking sets
  `user_show.deleted_at`; the show's seasons, episodes and watch rows all survive (ADR-024).
- Booleans as `INTEGER` 0/1. Dates as ISO-8601 TEXT in UTC (EF Core default for SQLite).
- `\N` from IMDb TSVs maps to SQL `NULL`, never to empty string or `0`.
- `PRAGMA journal_mode=WAL;` and `PRAGMA foreign_keys=ON;` on every connection open.
- Staging tables are prefixed `staging_` and are **truncate-and-reload**. Application layer never
  references them.

### 1a. Surrogate key encoding — canonical owner

**This section is the authority for `id-encoding` (ADR-027).** `SPEC.md` R13 owns the *invariant* —
ids are minted in-process and never re-minted; this section owns the *encoding*. If the two disagree,
this section wins on anything about bit widths, epochs or shifts, and `SPEC.md` wins on whether the
rule exists at all.

Layout — a positive 64-bit `INTEGER`, summing to exactly 64 with **no spare bit**:

| Field | Bits | Notes |
|---|---|---|
| sign | 1 | always 0, so every id is positive and sorts correctly as `long` |
| ms since epoch | 41 | epoch fixed at `2026-01-01T00:00:00Z`; ~69-year horizon |
| process salt | 10 | drawn once per process launch |
| sequence | 12 | 4,096 per millisecond per process |

`1 + 41 + 10 + 12 = 64`. **Check this sum before changing any width** — a 65-bit layout silently
overflows `long` into negative ids, which sort before everything and read like corruption. The field
cannot simply be widened; a different layout needs a new ADR.

Mint expression: `(ms - epoch) << 22 | salt << 12 | sequence`. The epoch is a **named constant with a
comment**, never a literal buried in a shift — a wrong epoch is invisible until 2095.

Behaviour the encoding depends on:
- **Never mint backwards.** Track `lastTimestamp`. Rollback ≤ 2 s → keep minting at `lastTimestamp`
  and increment the sequence. Rollback > 2 s → **throw**; do not mint ids claiming a time the clock
  says is future.
- **Sequence exhausted** → spin to the next millisecond. Never wrap the sequence, which would
  duplicate an id minted earlier in the same millisecond.
- The salt only *reduces* the chance of a same-millisecond clash. Correctness comes from `UNIQUE`
  constraints plus whole-unit-of-work retry, never from the salt.

⚠️ **The EF trap this section exists to prevent.** The SQLite provider assumes `INTEGER PRIMARY KEY`
means a store-generated rowid and will configure `ValueGenerated.OnAdd` unless told otherwise.
`ValueGenerated.None()` must be applied **by convention to every key**, in one place, so no entity can
opt out by forgetting. Assert it two ways: grep the generated migration for `AUTOINCREMENT`, and
assert **reflectively** that no property in `context.Model` has `ValueGenerated != Never`. The grep
catches the DDL; the reflection check catches the model, which is what actually decides whether EF
tries to read a generated value back. The model check is the one that matters.

Tests that pin the encoding — built in step 1.6, listed here so the owner of the encoding owns its
evidence:
- 100,000 ids from one instance: all distinct and **strictly** increasing. Non-decreasing is what a
  broken sequence check still returns.
- Decode `(id >> 22) + epoch` and assert it lands within seconds of now. This is the only test that
  catches a wrong shift width; every other test passes with one.
- Two instances with different salts in a tight loop, zero observed collisions — report the salts it
  ran with, since the test is probabilistic by nature.
- Fake clock: back 1 s → still unique and increasing; back 60 s → throws.
- Clock frozen, mint 4,097 → the 4,097th carries a later timestamp than the first, proving it spun.

### Layering rules (SPEC P3, R11, R12)

- **Source tables** (§3 TVmaze, §4 IMDb, §5 myshows staging) hold provider payloads **verbatim** —
  raw strings, raw HTML, provider timestamps. Written by ingestors, read by **the mapper and nothing
  else**.
- `discovery_schedule_archive` (§2) is a separate raw-response cache read by the Discovery schedule
  parser; it is not a mapper source table.
- **Mapped tables** (§2: `tracked_show`, `season`, `episode`, `genre`, `show_genre`) hold mapped,
  queryable data and are **fully rebuildable** from the sources. Written by the mapper only.
- **User tables** (§2: `user_show`, `episode_watch`, `ui_season_state`) hold what the user did.
  Written by user actions only — never by the mapper — and they are the only irreplaceable rows
  (SPEC P4).
- **No query outside the mapper may reference a source table.** Enforced by an architecture test at
  step 8.1.
- **No raw provider string may appear in a mapped column or a `WHERE` clause.** `"Running"` becomes
  `status_id`; the raw text is kept only for re-mapping and forensics.
- Source rows are **never deleted to reflect a provider change** — a show removed upstream keeps its
  source row so the mapping can be re-derived and the change is visible in `job_run`.

---

## 2. Mapped and user tables

The Application layer and the UI read only from here. These tables split into two groups with
opposite lifetimes, and v1.2 makes that split **physical** rather than a convention:

| Group | Tables | Lifetime |
|---|---|---|
| **Rebuildable** | `tracked_show`, `season`, `episode`, `genre`, `show_genre` | a pure function of the source tables. The mapper owns every column; any row can be re-derived |
| **User-owned** | `user_show`, `episode_watch`, `ui_season_state` | irreplaceable (SPEC P4). The mapper never writes them and nothing deletes them |

There is **no `show` table** (ADR-020): untracked shows are never persisted. And there is now **no
user data in `tracked_show`** — the tracking decision is its own row in `user_show` (ADR-024).

**Why the split matters:** while `category_id` and `needs_review` lived on `tracked_show`, that table
was *not* rebuildable, so "the mapper is a pure function of the sources" was a claim about columns
rather than a property you could test. It now is one — see the rebuild drill in §2a.

### 2a. The rebuild drill

Re-mapping after a mapper bug must not require restoring user data. The property to preserve:

> Delete `episode`, `season`, `tracked_show`, `show_genre`, `genre`, re-run the mapper over every
> `user_show` row, and the database is equivalent — same content, same watch marks, same UI state.

**This does not work as a wipe-and-reload**, and the reason is the one thing to get right here:
`episode_watch.episode_id` and `ui_season_state.season_id` are surrogate keys, so deleting and
re-inserting an episode mints a new id and silently orphans the watch row that pointed at it. So
"rebuild" means **idempotent upsert keyed on natural keys, with surrogate ids immutable once
minted** — never delete-then-insert. The mapper adds, updates and logically retires rows; it does not
recreate them. Assert this with the drill in `IMPLEMENTATION_PLAN.md` step 1.5.

### genre / show_genre
```
genre.id   INTEGER PK
genre.name TEXT NOT NULL UNIQUE                -- canonical, from TVmaze genres[]
show_genre.show_id   INTEGER NOT NULL -> tracked_show.id
show_genre.genre_id  INTEGER NOT NULL -> genre.id
PRIMARY KEY (show_id, genre_id)
```

### tracked_show
The **rebuildable** catalog row for a tracked show. Every column is mapper-owned or derived; the
user's tracking decision lives in `user_show`, not here (ADR-024).
```
id                       INTEGER PK
tvmaze_show_id           INTEGER NOT NULL UNIQUE -> tvmaze_show.tvmaze_id   -- natural key, NOT NULL
tvmaze_id                INTEGER NOT NULL UNIQUE     -- denormalized for readable queries
title                    TEXT    NOT NULL            -- from tvmaze_show.name
title_localized          TEXT    NULL                -- export / imdb_akas
show_type                TEXT    NULL                -- 'Scripted' | 'Animated' | ... (no behaviour)
language                 TEXT    NULL
status_id                INTEGER NOT NULL DEFAULT 0  -- ShowStatus enum, SPEC R12
runtime_minutes          INTEGER NULL                -- tvmaze_show.runtime (often null)
average_runtime_minutes  INTEGER NULL                -- tvmaze_show.average_runtime
premiere_date            TEXT    NULL                -- full date from 'premiered'
end_date                 TEXT    NULL                -- full date from 'ended'
official_site            TEXT    NULL
schedule_time            TEXT    NULL
schedule_days            TEXT    NULL                -- comma list
description              TEXT    NULL                -- HTML stripped by the mapper
rating_average           REAL    NULL                -- TVmaze's rating, not the user's
network_name             TEXT    NULL
webchannel_name          TEXT    NULL
country                  TEXT    NULL                -- broadcaster country, else language (ADR-036)
imdb_id                  TEXT    NULL                -- from tvmaze_show.imdb_id
thetvdb_id               INTEGER NULL
poster_url               TEXT    NULL                -- image.original
poster_medium_url        TEXT    NULL                -- image.medium
mapped_at                TEXT    NULL                -- last mapper write
-- derived caches: recomputed from episode + episode_watch, never authored
watched_regular          INTEGER NOT NULL DEFAULT 0  -- cached, excludes specials
total_regular            INTEGER NOT NULL DEFAULT 0  -- cached, excludes specials
has_unwatched_specials   INTEGER NOT NULL DEFAULT 0  -- cached
auto_finish_eligible     INTEGER NOT NULL DEFAULT 0  -- cached, SPEC R5
```
`tvmaze_show_id` is the real FK; `tvmaze_id` is duplicated deliberately so ad-hoc SQL in DB Browser
stays readable during import review, which is a first-class workflow here. The mapper keeps them
consistent; nothing else writes either.

**No URL columns.** `tvmaze_url` is dropped: `https://www.tvmaze.com/shows/{tvmaze_id}` is derivable
from the id, and storing a second copy of a derivable value is how the two ever disagree. Same for
any IMDb URL. `official_site` stays — it is authored data that is *not* derivable.

**No user data.** `category_id`, `added_at`, `category_changed_at` and `needs_review` moved to
`user_show`. The four cached columns stay because they are derived from `episode` × `episode_watch`,
both of which survive, and recomputing them is exactly what a rebuild does. `needs_review` moved
because it records a *human* judgement about the import, not a mapper fact.

**No `myshows_id` column** (ADR-019) — it exists only in `staging_show`.

**`country` is derived, not a TVmaze field.** TVmaze has no country on a show; the country belongs to
the broadcaster (`network.country` or `webChannel.country`), and streaming broadcasters frequently
carry `country: null` while `language` is filled. `country` therefore holds
`country_name ?? country_code ?? language` — the one rule in `CountryRules.Display` (ADR-036), applied
by the mapper so every read path agrees. It is a **mapped** column: the verbatim broadcaster country
lives in `tvmaze_show.country_name`/`country_code`, and the fallback is never written there (R11). A
show with neither is NULL, which is what the Discovery facet files as "Not set".

### user_show
The one row per tracked show that holds everything the user decided. **This table plus
`episode_watch` and `ui_season_state` is the entire irreplaceable content of the database.**
```
tracked_show_id     INTEGER PRIMARY KEY -> tracked_show.id   -- 1:1, shares the key (no own id)
category_id         INTEGER NOT NULL     -- 1 Started 2 Later 3 Cancelled 4 Finished
added_at            TEXT    NOT NULL
category_changed_at TEXT    NOT NULL
needs_review        INTEGER NOT NULL DEFAULT 0   -- set by import backfill, cleared by the user
is_pinned           INTEGER NOT NULL DEFAULT 0   -- improvement 18: pinned to the top of the Started list
user_rating         INTEGER NULL                 -- the user's own 1-10 rating; NULL = not rated
deleted_at          TEXT    NULL                 -- logical delete; NULL = actively tracked
```
`tracked_show_id` is both PK and FK: the relationship is 1:1 and a second surrogate key here would
only create a second thing to keep in sync.

**User rating:** `user_rating` is the user's own opinion of a show, from 1 to 10, or NULL when unrated. It
is deliberately distinct from `tracked_show.rating_average`, which is TVmaze's catalog rating and is never
the user's. It is user data (SPEC P4), so it lives here and survives a rebuild. The 1-10 range is enforced
by the write use case (`SetShowUserRatingUseCase`), not a CHECK constraint — the only writer is that seam.
It is orthogonal to `category_id` and `deleted_at`, so setting it never touches either.

**Pinning (improvement 18):** `is_pinned` lifts a show above the active sort in the Started list. It is a
user decision (SPEC P4), so it is stored here rather than in a UI preference like the sort choice — it must
survive a relaunch and a rebuild. It is orthogonal to `category_id` and `deleted_at`: pinning never touches
either, and the flag is kept regardless of category so a show moved out of Started retains its pin if it
returns. Only the Started list offers pinning through the UI.

**Logical delete semantics (ADR-024):**
- `deleted_at IS NOT NULL` means untracked. The `tracked_show`, `season`, `episode`, `genre` rows and
  every `episode_watch` row **stay in the database**.
- Every read path filters `deleted_at IS NULL`. That is one predicate, and it belongs in the
  repository, not in each caller — a forgotten filter shows an untracked show in the tabs.
- Re-adding a show clears `deleted_at` and restores the previous `category_id`. Because episode ids
  were never regenerated, the watch marks are still exactly where they were. This is the whole point
  of the logical delete.
- `category_id` is kept while deleted so the restore has something to restore to.
- Physical deletion is not a feature of v1. If the table ever needs one, it is a maintenance script
  with a backup, not a UI action.

**Cost, stated honestly:** the category-tab query now joins `user_show` to `tracked_show` instead of
reading one denormalised table. That is the price of making `tracked_show` rebuildable. The join is
on a primary key over ~547 rows and `ix_user_show_category` is partial on `deleted_at IS NULL`, so
the indexed set is exactly the active shows.

### season
A **grouping entity** holding the metadata that belongs to a season rather than to a show or an
episode. It has **no external identifier** (ADR-025): neither TVmaze nor IMDb gives a season a stable
id that survives a re-fetch, so a column claiming one would be a promise nothing can keep.
```
id            INTEGER PK
show_id       INTEGER NOT NULL -> tracked_show.id
season_number INTEGER NOT NULL                   -- 0 = specials
name          TEXT    NULL
episode_count INTEGER NULL
poster_url    TEXT    NULL
UNIQUE (show_id, season_number)
```
Everything here is derived by grouping episodes (or read off the show payload), which is why `season`
is in the rebuildable group. `episode_count` is a cache of a count over `episode`, not an authority.

### episode
```
id                 INTEGER PK
show_id            INTEGER NOT NULL -> tracked_show.id
season_id          INTEGER NOT NULL -> season.id
tvmaze_episode_id  INTEGER NULL UNIQUE           -- NULL when this episode has no TVmaze side
season_number      INTEGER NOT NULL              -- denormalized for cheap ordering
episode_number     INTEGER NOT NULL              -- NULL if genuinely unknown (IMDb legacy)
title              TEXT    NULL
title_localized    TEXT    NULL
episode_type_id    INTEGER NOT NULL DEFAULT 0    -- EpisodeType enum: 0 Unknown 1 Regular 2 Special(significant) 3 InsignificantSpecial
air_date           TEXT    NULL                  -- date from 'airdate'
air_stamp          TEXT    NULL                  -- offset-aware instant; canonical ordering key
runtime_minutes    INTEGER NULL
rating_average     REAL    NULL
image_url          TEXT    NULL
image_medium_url   TEXT    NULL
description        TEXT    NULL                  -- HTML stripped
imdb_id            TEXT    NULL                  -- NULL when this episode has no IMDb side
title_source       TEXT    NULL                  -- 'tvmaze' | 'imdb' | 'manual'
air_date_source    TEXT    NULL
UNIQUE (show_id, season_number, episode_number)
```
**Both provider ids are nullable, symmetrically.** An episode may be known to TVmaze only, to IMDb
only, or to both, and the row is the union of whichever sides exist (ADR-025). This is the same
rule as `tracked_show.imdb_id`, applied one level down — the previous draft made
`tvmaze_episode_id` the assumed-present key and `imdb_id` the optional extra, which quietly encodes
"TVmaze is always right" and would have blocked the IMDb-only episodes that open question 1 is about.

No URL column here either — both episode pages are derivable from the respective id.

`season_number`/`episode_number` denormalized onto `episode` deliberately: episode ordering and the
backfill algorithm both sort by it, and joining through `season` for every row is pure waste.

`episode_type_id` replaces the old `is_special` boolean. TVmaze's `type` field distinguishes
`regular` from special independently of season number, which is strictly better information — some
shows number specials in-season. `season_number = 0` is only the fallback when IMDb data is the sole
source (SPEC R4).

TVmaze splits specials into two kinds and the split is load-bearing for progress (ADR-034):
`significant_special` → `Special` (2) counts toward `watched_regular`/`total_regular` and auto-finish
exactly like a regular, and stays in the season grid with a marker; `insignificant_special` →
`InsignificantSpecial` (3) is filler, excluded from every progress figure, and shown only in the
details page's separate Specials section. The bare `special` value (not returned by TVmaze today) is
mapped to `InsignificantSpecial` so a legacy row still means "not counted". `Unknown` (0) is
fail-closed: excluded from progress and rendered with a distinct marker so a vocabulary gap is visible
rather than masquerading as a regular.

The `*_source` columns exist because the `Enrichment` job reconciles two providers with different
strengths. Without provenance you cannot tell a TVmaze gap from an IMDb guess after three job runs.

⚠️ `UNIQUE (show_id, season_number, episode_number)` does **not** constrain rows where
`episode_number IS NULL` — SQLite treats nulls as distinct in a unique index, so two IMDb-legacy
episodes with unknown numbers can both insert for the same season. Deliberate (they are genuinely
distinct episodes), but it means the mapper cannot rely on that index to deduplicate them.

### episode_watch
```
id          INTEGER PK
show_id     INTEGER NOT NULL -> tracked_show.id
episode_id  INTEGER NOT NULL UNIQUE -> episode.id
watched_at  TEXT    NOT NULL
```
**Absence of a row means unwatched.** Rows rather than a boolean column, to preserve history for
future statistics. `show_id` is denormalized so per-show progress is a single-table count.

**This is the most important table in the database.** Together with `user_show` and `ui_season_state`
it is the only data that cannot be rebuilt from source tables (SPEC P4). It is the reason the import
has a staging phase at all.

Untracking a show does **not** touch this table (ADR-024) — the rows survive so that re-adding the
show restores the exact watched state. Deleting a row here is what "unwatched an episode" means; it
is the one user action that is a real delete, because the absence is the meaning.

### ui_season_state
```
id          INTEGER PK
show_id     INTEGER NOT NULL -> tracked_show.id
season_id   INTEGER NOT NULL -> season.id
is_expanded INTEGER NOT NULL DEFAULT 0
UNIQUE (show_id, season_id)
```
UI state in the domain schema is deliberate (ADR-012): it is per-show and per-season, not a global
setting, and it survives the UI rewrite. User-owned (P4) and untouched by untracking, for the same
reason as `episode_watch`.

### job_run
```
id            INTEGER PK
job_name      TEXT    NOT NULL
started_at    TEXT    NOT NULL
finished_at   TEXT    NULL
succeeded     INTEGER NOT NULL DEFAULT 0
items_total   INTEGER NULL
items_changed INTEGER NULL
detail        TEXT    NULL                        -- JSON: errors, skips, counts
```
Every job writes one row. This is the only way to answer "did `StatusRecheck` actually run, and what
did it change?" without reading logs. `items_changed` is measured **after the mapper runs**, not
after the fetch — a job that fetched 547 shows and changed nothing should report 0.

### writer_lease
Not a source table and not user data — coordination state, safe to delete when nothing is running.
```
name        TEXT PRIMARY KEY   -- 'promote' | 'episode_refresh' | 'status_recheck' | 'enrichment'
holder      TEXT NOT NULL      -- 'pid-31337@2026-09-28T10:00:00Z/7f3a'
acquired_at TEXT NOT NULL
expires_at  TEXT NOT NULL      -- now + 90s, heartbeat-renewed every 30s
```
Acquired by a conditional upsert (`WHERE expires_at < now`), so a live lease is never overwritten and
there is no read-then-write race. **Expiry is the cleanup mechanism** — a killed process leaves a row
that goes stale rather than a lock that must be reaped. `--promote` additionally requires that no
other lease is live (ADR-028).

### http_cache
Transport-layer only. Holds raw response bodies for ETag revalidation. **Not a source table** — the
mapper must never read it; if data is only reachable through `http_cache`, the ingest step failed.
```
url           TEXT PRIMARY KEY
etag          TEXT NULL
last_modified TEXT NULL
body          TEXT NOT NULL
fetched_at    TEXT NOT NULL
```

### discovery_schedule_archive
Discovery's retained raw `/schedule/full` captures. This is an Infrastructure-owned cache, not a
mapper source table or user data. The date is the natural key; the JSON body is reparsed by the
Discovery schedule parser on refresh. This table is included in the SQLite database backup. Existing
`cache/discovery/schedule-*.json` files are not imported or deleted when upgrading.
```
fetched_on    TEXT PRIMARY KEY   -- ISO date, the UTC capture day; natural key, not a generated id
raw_json      TEXT NOT NULL      -- untouched /schedule/full response body
```

---

## 3. Source tables — TVmaze

Written verbatim from the API payload by the ingest step, read **only** by the mapper (SPEC R11).
Column names mirror the JSON keys so the mapping is obvious and a payload change is visible in a diff
of this file. Populated for **tracked shows only** (SPEC P2).

### tvmaze_show
```
tvmaze_id            INTEGER PRIMARY KEY          -- the API 'id'
url                  TEXT    NULL
name                 TEXT    NULL                 -- original title, verbatim
type_raw             TEXT    NULL                 -- 'Scripted' | 'Animated' | ...
language             TEXT    NULL
status_raw           TEXT    NOT NULL             -- VERBATIM: 'Running' | 'Ended' | ...
runtime_minutes      INTEGER NULL                 -- 'runtime', frequently NULL
average_runtime      INTEGER NULL                 -- 'averageRuntime'
premiered            TEXT    NULL                 -- full date '2011-12-04'
ended                TEXT    NULL                 -- full date
official_site        TEXT    NULL
schedule_time        TEXT    NULL
schedule_days        TEXT    NULL                 -- comma list from days[]
rating_average       REAL    NULL
network_name         TEXT    NULL
webchannel_name      TEXT    NULL
country_name         TEXT    NULL                 -- network/webChannel .country.name, VERBATIM
country_code         TEXT    NULL                 -- .country.code, VERBATIM (null for streaming)
imdb_id              TEXT    NULL                 -- externals.imdb
thetvdb_id           INTEGER NULL                 -- externals.thetvdb
tvrage_id            INTEGER NULL                 -- defunct, kept verbatim
genres_raw           TEXT    NULL                 -- JSON array, verbatim
image_original       TEXT    NULL
image_medium         TEXT    NULL
summary_html         TEXT    NULL                 -- RAW HTML, not stripped
provider_updated     INTEGER NULL                 -- API 'updated', unix seconds
raw_json             TEXT    NOT NULL             -- full payload as fetched
fetched_at           TEXT    NOT NULL
```
`raw_json` is kept because TVmaze adds fields without notice and a re-fetch may not reproduce an
earlier state. It is the difference between "we can re-map after a bug" and "we must re-crawl 547
shows and hope the data is the same". At ~2 KB per show this is trivial next to `imdb_episode`.

`summary_html` is stored **unstripped**. Stripping is a mapper concern, and stripping on write makes
the original unrecoverable.

### tvmaze_episode
```
tvmaze_episode_id    INTEGER PRIMARY KEY          -- the API 'id'
tvmaze_show_id       INTEGER NOT NULL -> tvmaze_show.tvmaze_id
url                  TEXT    NULL
name                 TEXT    NULL
season_number        INTEGER NOT NULL
number               INTEGER NULL                 -- API 'number'
type_raw             TEXT    NOT NULL             -- 'regular' | 'significant_special' | 'insignificant_special'
airdate              TEXT    NULL                 -- date
airstamp             TEXT    NULL                 -- offset-aware instant
runtime_minutes      INTEGER NULL
rating_average       REAL    NULL
image_original       TEXT    NULL
image_medium         TEXT    NULL
summary_html         TEXT    NULL                 -- RAW HTML
raw_json             TEXT    NOT NULL
fetched_at           TEXT    NOT NULL
```
Indexed on `(tvmaze_show_id, season_number, number)`.

### tvmaze_status_seen
A small but genuinely useful table: every raw status string ever observed, with a counter.
```
status_raw     TEXT PRIMARY KEY
first_seen_at  TEXT NOT NULL
last_seen_at   TEXT NOT NULL
occurrences    INTEGER NOT NULL DEFAULT 0
mapped_to      INTEGER NULL                       -- ShowStatus it resolved to; NULL = unmapped
```
This is how an unrecognised TVmaze status becomes **visible** rather than silently landing in
`Unknown`. `SELECT * FROM tvmaze_status_seen WHERE mapped_to IS NULL` should be empty at all times;
if it is not, the R12 dictionary needs an edit. Written by the ingest step, read by the mapper and by
the user.

### tvmaze_akas
A show's alternative titles from `/shows/{id}/akas`, stored verbatim (step 7.5). The endpoint keys on
**country**, not language — there is no `language` field (probed live 2026-09-30). One row per aka.
```
tvmaze_show_id   INTEGER NOT NULL -> tvmaze_show.tvmaze_id   -- composite PK with country_code+name
country_code     TEXT    NOT NULL                            -- 'RU', 'CN', ... (country.code)
country_name     TEXT    NULL                                -- country.name, verbatim
name             TEXT    NOT NULL                            -- the aka title, verbatim
aka_order        INTEGER NOT NULL                            -- index in the API array; the pick is the lowest for the target country
fetched_at       TEXT    NOT NULL
```
Composite PK `(tvmaze_show_id, country_code, name)` — TVmaze returns several titles per country, so
`country_code` alone is not unique per show. `aka_order` preserves the API's order, which is the order
to trust: the mapper picks the **lowest `aka_order` for the target country** as
`tracked_show.title_localized`. Read only by the mapper (SPEC R11/P3); the details-page button reads
the mapped result, not this table.

---

## 4. Source tables — IMDb

Scoped to tracked shows: only titles whose `tconst` matches a `tracked_show.imdb_id`, plus their
episodes and akas (ADR-021). Never joined into UI queries — the mapper reads them and nothing else
does.

### imdb_title
```
tconst            TEXT PRIMARY KEY
title_type        TEXT NOT NULL
primary_title     TEXT NOT NULL
original_title    TEXT NULL
is_adult          INTEGER NOT NULL
start_year        INTEGER NULL
end_year          INTEGER NULL
runtime_minutes   INTEGER NULL
genres            TEXT NULL                        -- comma list, verbatim
```
Filtered on import to `tvSeries`, `tvMiniSeries` only.

### imdb_episode
```
tconst          TEXT PRIMARY KEY
parent_tconst   TEXT NOT NULL -> imdb_title.tconst
season_number   INTEGER NULL                       -- NULL from \N, never 0
episode_number  INTEGER NULL
primary_title   TEXT NULL
start_year      INTEGER NULL
runtime_minutes INTEGER NULL
```
Populated in two passes (see `DATA_ANALYSIS.md` §6): mapping columns from `title.episode.tsv`, then
title/year/runtime updated from `tvEpisode` rows in `title.basics.tsv`.

### imdb_akas
```
title_id     TEXT NOT NULL
ordering     INTEGER NOT NULL
title        TEXT NULL
region       TEXT NULL
language     TEXT NULL
is_original_title INTEGER NULL
PRIMARY KEY (title_id, ordering)
```
Source of localized titles for `tracked_show.title_localized` and `episode.title_localized`.

### imdb_dataset_meta
```
dataset_name  TEXT PRIMARY KEY    -- 'title.basics' | 'title.episode' | 'title.akas'
downloaded_at TEXT NOT NULL
source_url    TEXT NOT NULL
row_count     INTEGER NULL
file_size     INTEGER NULL
checksum      TEXT NULL
```

---

## 4a. Derived reconciliation table — `episode_mismatch`

This is neither a source table nor a user table: it is a **derived, fully rebuildable** projection the
`Enrichment --imdb` mode materialises so the details page can show *what IMDb said that differed from
what TVmaze kept* (ADR-031). The UI cannot read the `imdb_*` source tables (rule 5 / SPEC R11), so the
disagreement has to be materialised — but it is derived from source + mapped rows and holds nothing the
user created, so wiping and recomputing it loses nothing (SPEC P5, like `title_localized` from
`tvmaze_akas`).

### episode_mismatch
```
id             INTEGER PK
show_id        INTEGER NOT NULL -> tracked_show.id
episode_id     INTEGER NULL -> episode.id          -- NULL for an IMDb-only episode (no episode row)
kind           TEXT    NOT NULL                    -- 'imdb_only' | 'tvmaze_only' | 'title' | 'runtime' | 'air_date_gap'
tvmaze_value   TEXT    NULL                        -- the value TVmaze kept (kept authoritative)
imdb_value     TEXT    NULL                        -- the value IMDb offered (rejected, shown for review)
season_number  INTEGER NULL                        -- denormalized for display of an imdb_only row with no episode
episode_number INTEGER NULL
```
No unique constraint: the enrichment mode **wipes a show's rows and re-inserts them each run**, so there
is never a stale row to collide with — the wipe-and-recompute is the idempotence mechanism, not an index.
The enrichment mode is the **sole writer**; the details-page read use case is its only reader.

`kind` is a closed vocabulary (ADR-031): `imdb_only` (IMDb has a (season, episode) TVmaze does not),
`tvmaze_only` (TVmaze has one IMDb does not — informational, TVmaze wins by definition), `title` /
`runtime` (both providers have the episode and disagree), `air_date_gap` (TVmaze has no air date; IMDb
offers only a year, recorded in `imdb_value` so the user sees the partial signal — never filled into
`air_date`). A **description gap is deliberately not surfaced**: the IMDb episode dataset carries no
episode summary at all, so there is nothing to compare against and flagging every summary-less episode
would be noise, not signal.

Index on `show_id` (the details page loads a show's mismatches in one query).

---

## 5. Staging tables — the myshows.me source layer

These are the source layer for the **myshows.me** provider, playing the same role for it that
`tvmaze_show` plays for TVmaze (SPEC P3). Populated by `--stage` and the scraper, **edited by the
user in DB Browser for SQLite**, read by `--report` and `--promote`. Short-lived by design — once
promoted, `myshows_id` has served its purpose and does not reach the result schema (ADR-019).

### staging_show
```
myshows_id            INTEGER PRIMARY KEY   -- the export's show.id (a MYSHOWS id, see DATA_ANALYSIS §3)
title_ru               TEXT    NULL          -- export 'title', TRIMMED
 title_original         TEXT    NULL          -- export 'titleOriginal'
year                   INTEGER NULL
alias                  TEXT    NULL
source_file            TEXT    NOT NULL      -- watching.json | later.json | ...
source_watch_status    TEXT    NOT NULL      -- watching | later | cancelled | finished
total_episodes         INTEGER NOT NULL      -- raw from export
watched_episodes       INTEGER NOT NULL      -- raw from export
total_seasons          INTEGER NULL
imdb_id                TEXT    NULL          -- from staging_imdb_id
tvmaze_id              INTEGER NULL          -- from /lookup?imdb=
tvmaze_resolution      TEXT    NOT NULL      -- 'scraped' | 'manual' | 'title_year' | 'unresolved'
needs_review           INTEGER NOT NULL DEFAULT 0
promoted_at            TEXT    NULL          -- set by --promote; NULL means not yet promoted
```
Note there is deliberately **no** `status` column carried forward: the export's `status` values
(`"Returning Series"`, `"Canceled/Ended"`) are myshows vocabulary and must not contaminate the result
schema. TVmaze's `status_raw` is the only status that maps (SPEC R12).

### staging_episode
```
show_myshows_id  INTEGER NOT NULL
source_batch_id  INTEGER NOT NULL     -- 58 in practice
episode_id_src   INTEGER NOT NULL     -- myshows episode id, for traceability
season_number    INTEGER NOT NULL
episode_number   INTEGER NOT NULL
title            TEXT NULL
air_date_utc     TEXT NULL
is_special       INTEGER NOT NULL DEFAULT 0
runtime_minutes  INTEGER NULL
UNIQUE (show_myshows_id, season_number, episode_number)
```
These rows are the **unwatched** set (see `DATA_ANALYSIS.md` §4). The column name says `episode` but
the semantics are "unwatched episode" — documented here because getting it backwards inverts the
entire import.

### staging_imdb_id
```
myshows_id   INTEGER PRIMARY KEY
imdb_id      TEXT NULL
kinopoisk_id TEXT NULL
scraped_at   TEXT NULL
http_status  INTEGER NULL
error        TEXT NULL
```
Resumable: `imdb_id IS NULL AND error IS NOT NULL` rows are retried, successful rows are skipped.

---

## 6. `v_staging_issues` — the promote gate

`--promote` refuses to run while this view returns any row with `severity = 'blocking'`. The view is
hand-written SQL (it is not EF-generated); its authoritative text lives in the migrations — created
in `0003_Staging`, redefined in `0004_StagingIssuesImdbWarning` (ADR-030). This section mirrors
migration 0004; if they ever disagree, the migration is what runs.

```sql
CREATE VIEW v_staging_issues AS
SELECT s.myshows_id,
       s.title_ru,
       CASE
         WHEN s.tvmaze_id IS NULL
           THEN 'unresolved_tvmaze_id'
         WHEN s.imdb_id IS NULL
           THEN 'unresolved_imdb_id'
         WHEN s.source_watch_status = 'watching'
              AND s.watched_episodes > s.total_episodes
           THEN 'watched_exceeds_total'
         WHEN s.total_episodes = 0
           THEN 'zero_total_episodes'
         WHEN s.total_episodes <> COALESCE(imdb.cnt, s.total_episodes)
           THEN 'imdb_count_mismatch'
         WHEN s.total_episodes <> COALESCE(tvm.cnt, s.total_episodes)
           THEN 'tvmaze_count_mismatch'
       END AS issue,
       CASE
         WHEN s.tvmaze_id IS NULL THEN 'blocking'
         WHEN s.watched_episodes > s.total_episodes THEN 'blocking'
         ELSE 'warning'
       END AS severity
FROM staging_show s
LEFT JOIN (SELECT parent_tconst p, COUNT(*) cnt
           FROM imdb_episode
           WHERE season_number IS NOT NULL
           GROUP BY parent_tconst) imdb ON imdb.p = s.imdb_id
LEFT JOIN (SELECT tvmaze_show_id s, COUNT(*) cnt
           FROM tvmaze_episode
           GROUP BY tvmaze_show_id) tvm ON tvm.s = s.tvmaze_id
WHERE issue IS NOT NULL;
```

The TVmaze count comes from `tvmaze_episode`, which is only populated once a show's episodes have
been fetched — so `tvmaze_count_mismatch` is **silent until episode fetch**, and the view is only
fully meaningful after step 2.3 plus an episode fetch. Do not treat an empty result before that point
as a clean bill of health.

The two count comparisons implement the **tri-source reconciliation** described in
`DATA_ANALYSIS.md` §6 — the check that already proved itself on Brilliant Minds (33 = 33 = 13+20).
`zero_total_episodes` is a warning, not blocking: such a show is legitimately un-trackable but must
not be silently dropped — it promotes to a `tracked_show` with `total_regular = 0` and
`auto_finish_eligible = 0` (SPEC R7).

**ADR-030** demoted `unresolved_imdb_id` from blocking to warning: a show with a `tvmaze_id` is never
blocking for promotion just because it lacks an `imdb_id` — IMDb is enrichment (step 4.1), not a
promotion prerequisite, and the mapper needs only the TVmaze id. The live review produced exactly two
such rows (`73305`, `96144`) — both tvmaze-resolved, both legitimately ready to promote. After this
change only two conditions are blocking: an unresolved `tvmaze_id` (the mapper cannot run without it)
and `watched_exceeds_total` (the watch backfill would be self-contradictory).

---

## 7. Indexes

Column names below must match §2–§5 exactly. Two earlier drafts cited columns that did not exist
(`tracked_show_id`, `air_date_utc`, `imdb_episode.imdb_id`), so this section is written against the
DDL rather than from memory of it.

```sql
-- Result layer, rebuildable
-- tracked_show.tvmaze_id is UNIQUE NOT NULL in the DDL, so it needs no index of its own.
CREATE UNIQUE INDEX ix_tracked_imdb_id     ON tracked_show(imdb_id)   WHERE imdb_id IS NOT NULL;
CREATE UNIQUE INDEX ix_season_show_num     ON season(show_id, season_number);
CREATE UNIQUE INDEX ix_episode_key         ON episode(show_id, season_number, episode_number);
CREATE INDEX ix_episode_airdate            ON episode(air_date)       WHERE air_date IS NOT NULL;
CREATE INDEX ix_episode_imdb               ON episode(imdb_id)        WHERE imdb_id IS NOT NULL;
CREATE INDEX ix_episode_tvmaze             ON episode(tvmaze_episode_id) WHERE tvmaze_episode_id IS NOT NULL;

-- Result layer, user-owned
-- The hottest query in the app: the category tabs. Partial on active rows, so the indexed set is
-- exactly the shows that are actually tracked and it shrinks as shows are untracked.
CREATE INDEX ix_user_show_category         ON user_show(category_id, deleted_at);
CREATE INDEX ix_watch_show                 ON episode_watch(show_id);
CREATE INDEX ix_ui_state_show              ON ui_season_state(show_id);

-- Source layer
CREATE INDEX ix_tvmaze_ep_show             ON tvmaze_episode(tvmaze_show_id, season_number, number);
CREATE INDEX ix_imdb_ep_parent             ON imdb_episode(parent_tconst, season_number, episode_number);

-- Bookkeeping
CREATE INDEX ix_job_run_name               ON job_run(job_name, started_at DESC);
```

Notes on the choices:

- **Partial** unique indexes where the column is nullable during the pipeline: a plain `UNIQUE` would
  collide across the many `NULL`s produced before id resolution finishes.
- `ix_user_show_category` replaced `ix_tracked_category` when user data moved out of `tracked_show`
  (ADR-024). The tab query is `user_show JOIN tracked_show ON id` ordered by `tracked_show.title`;
  the join is on a primary key over ~547 rows, so the denormalised single-table version was not worth
  losing rebuildability for. `deleted_at` is in the index rather than a `WHERE deleted_at IS NULL`
  partial predicate because the untracked set is also queried — a "previously tracked" view is the
  natural way to surface re-addable shows, and a partial index could not serve it.
- `ix_episode_airdate` backs the Upcoming block (SPEC F3): a range scan over future air dates.
- `ix_episode_tvmaze` is redundant with the column's own `UNIQUE` in SQLite, which creates an implicit
  index. It is listed here only if the EF mapping declares `UNIQUE` at the model level and the
  migration generator does not emit it — check `dotnet ef migrations script` and **delete whichever
  of the two the generator produced**. A duplicate index on 15,000 rows is pure write overhead.
- `ix_episode_key` is `UNIQUE` on `(show_id, season_number, episode_number)` even though the mapper
  upserts on it, because a duplicate here would mean the mapper is broken — the index is a test that
  runs on every write. It does not constrain `episode_number IS NULL` rows (§2, episode).
- No index on `tvmaze_show` beyond its primary key: it is read by id, never scanned.
  `tvmaze_status_seen.status_raw` **is** the primary key and doubles as the diagnostic list.
- No index on `imdb_title`: it is joined from `imdb_episode.parent_tconst` on its own primary key.

---

## 8. Size expectations

| Table | Expected rows |
|---|---|
| `tracked_show` | 547 initially, growing by search/discovery |
| `user_show` | 1:1 with `tracked_show`; **grows monotonically** — untracked rows are kept (ADR-024) |
| `season` | ~2,500 |
| `episode` | ~15,000 (The Simpsons alone is 800+) |
| `episode_watch` | ~8,000 |
| `ui_season_state` | one per expanded season, bounded by `season` |
| `tvmaze_show` / `tvmaze_episode` | ~547 / ~15,000 — same order as the mapped tables, plus `raw_json` at ~2 KB each |
| `tvmaze_status_seen` | single digits |
| `staging_*` | 547 / ~10,000 / 547 — transient after promote |
| `imdb_title` | ~547 (one per tracked show with a resolved `imdb_id`) |
| `imdb_episode` | ~15,000–25,000 — **scoped to tracked shows** (ADR-021) |
| `imdb_akas` | ~2,000–5,000 |
| `discovery_schedule_archive` | typically ~5 raw `/schedule/full` captures; bounded by the configured age ceiling and fetch cadence |

**Logical delete means these numbers only go up.** `user_show` rows and their `episode_watch` rows
survive untracking, so a user who tracks and untracks 100 shows a year adds 100 rows to `user_show`
and keeps whatever watch rows those shows accumulated. At personal-app scale this is irrelevant for
years — it is called out so nobody "optimises" it later with a purge that destroys the property the
design depends on.

**v1.1 correction:** the first draft projected `imdb_episode` at "tens of millions" and called it a
mirror of the dataset. That was the pre-ADR-021 design, and it would have made the database larger
than the app it serves by four orders of magnitude. The IMDb tables hold **only rows reachable from
tracked shows**, so they are the same order of magnitude as the TVmaze tables — which is what makes
keeping the `.tsv.gz` files on disk (ADR-021) the right trade: the disk cost moves to the files, and
re-loading is cheap.

`raw_json` in the TVmaze source tables is the largest deliberate redundancy in the database (~30 MB
at 15,000 episodes) and is kept for the reason stated in §3: re-mapping after a mapper bug must not
require re-crawling the API.
