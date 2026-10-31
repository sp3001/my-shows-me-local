---
status: current
authority:
  - decision-rationale
not_authoritative_for:
  - current-state
  - storage-schema
  - id-encoding
---
# DECISIONS — Architecture Decision Records

Locked decisions from the user, in ADR form. **Do not reverse one of these unilaterally.** If a
decision must change, add a new ADR here, mark the old one `Superseded by ADR-xxx`, and note it in
`STATE.md` and the Step Log in `LOG.md`.

Each entry records what was chosen and why, including what was rejected — the rejected options are
the main thing a later agent will otherwise re-litigate.

---

## ADR-001 — .NET MAUI with native XAML
**Status:** superseded by ADR-033 · 2026-09-28

**Decision** — .NET MAUI, native XAML views, `CommunityToolkit.Mvvm`, targeting
`net8.0-windows10.0.19041.0` only.

**Alternatives rejected**
- *Avalonia* — cross-platform and the natural fit for a later Docker/Linux story, but the user chose
  MAUI.
- *MAUI Blazor Hybrid with a shared Razor Class Library* — would have let the same components be
  hosted in Blazor Server/WASM later with almost no UI rewrite. **The user explicitly declined this
  and accepted a UI rewrite at Docker time.**
- *WinUI 3* — heavier packaging friction for the same Windows-only outcome.

**Consequence** — Only Domain, Application, and Infrastructure survive the migration. The UI is
disposable by design. Do not optimize for UI reuse; optimize for UI *thinness* so the rewrite is
cheap.

---

## ADR-002 — Layered libraries, no API host in v1
**Status:** accepted · 2026-09-28

**Decision** — `Domain` / `Application` / `Infrastructure` / `App` plus per-job console projects.
The UI calls Application use-cases in-process.

**Alternatives rejected**
- *Build the ASP.NET Core API now and make the desktop app an HTTP client* — proves the backend
  contract early, but adds serialization, auth, and latency concerns to every feature before any of
  them are needed.
- *Single project with folders* — fastest, but the eventual extraction becomes a refactor rather
  than a new host project.

---

## ADR-003 — EF Core + Microsoft.Data.Sqlite
**Status:** accepted · 2026-09-28

**Decision** — EF Core code-first with migrations for all normal persistence.

**Amendment (2026-09-28)** — the user chose plain EF Core over an EF+Dapper hybrid. Raw
parameterized `Microsoft.Data.Sqlite` batches are still required for the IMDb dataset import
(tens of millions of rows — EF's `ChangeTracker` cannot do it in reasonable time). That exception
is confined to `Shows.Infrastructure` bulk-import classes and is not a general escape hatch.

**Alternatives rejected**
- *Dapper + hand-written SQL migrations* — explicit and fast, but more persistence code for little
  gain at 547 shows.
- *Provider-agnostic design for a Postgres swap now* — premature; SQLite-specific pragmas are wanted.

---

## ADR-004 — .NET 8, one console project per job
**Status:** accepted · 2026-09-28

**Decision** — `net8.0`. Four separate job projects plus the import and scraper, run manually.

**Alternatives rejected**
- *.NET 10 LTS* — newer, but the user asked for .NET 8 for now.
- *One worker executable with a `--job` argument* — fewer projects, but separate projects keep each
  job's dependencies and failure modes isolated, which matters when they run on different cadences.

---

## ADR-005 — Data folder for database and datasets
**Status:** accepted · 2026-09-28

**Decision** — `shows.db` plus a `data/` folder for downloaded IMDb `.tsv.gz` files, both paths in
config, resolved relative to one base data directory.

**Consequence** — the Docker migration is a volume mount, not a code change.

---

## ADR-006 — Id resolution: scrape myshows → IMDb → TVmaze
**Status:** accepted · 2026-09-28

**Decision** — `myshows_id` → scrape `myshows.me/view/{id}/` for the IMDb `tconst` → TVmaze
`/lookup/shows?imdb=` for the TVmaze id. Fallback ladder: manual id entry → title+year search
flagged `needs_review`.

**Why this is required, not optional** — `show.id` in the exports is a **myshows.me internal id**,
not a TVmaze id (verified, `DATA_ANALYSIS.md` §3). There is no other join key in the data.

**Alternatives rejected**
- *TVmaze `externals.imdb` first, TSV only for enrichment* — the obvious cheap path, but it needs a
  TVmaze id to start from, which the export does not provide.
- *Fuzzy title+year matching* — rejected as a primary path; kept only as the last resort.
- *IMDb as the primary source* — IMDb has no day-precision air dates.

**Risk** — scraping is ToS-sensitive and the markup can change. Mitigated by ADR-007.

---

## ADR-007 — Standalone one-off scraper
**Status:** accepted · 2026-09-28

**Decision** — `Shows.Jobs.ScrapeMyShows` is a separate, resumable, throttled, hard-capped console
project writing to `staging_imdb_id`. It is **never** called by a recurring job.

**Alternatives rejected**
- *Built into the enrichment job* — would make a fragile, ToS-sensitive dependency part of routine
  operation.

**Consequence** — if myshows.me changes markup or blocks, only a one-time import breaks, and it can
be deleted without touching anything else.

---

## ADR-008 — Exactly four categories
**Status:** accepted · 2026-09-28

**Decision** — `Started`, `Later`, `Cancelled`, `Finished`. No `Paused`.

**Clarification the user gave explicitly** — the phrase "paused/finished/cancelled" in the job
requirements refers to the **TVmaze `status` string**, not to user categories. Jobs therefore filter
on the mapped `tracked_show.status_id` (SPEC R12), never on `TrackedShow.category_id`. This is why
`EpisodeRefresh` and `StatusRecheck` are two jobs: they poll opposite populations.

**Alternatives rejected** — adding `Paused` as a fifth category, and `Dropped`/`On Hold`.

---

## ADR-009 — Minimal fields
**Status:** accepted · 2026-09-28 · **scope narrowed by ADR-022** — "minimal" now applies to the
*result* tables only. Source tables deliberately store the full TVmaze payload.

**Decision** — store only identity, category, and episode watched state.

**Dropped from the export** — `rating`, `watchCount`, `totalHours`, `watchedHours`, `watching`,
`voted`, `blurhash`, `promoUrl`, `onlineCount`, `userShow`, `canRate`.

**Alternatives rejected** — keeping rating + watched stats (the user chose minimal over this), and
storing the raw source JSON blob for traceability.

**Consequence** — the user's historical ratings and watched-hours are **not** migrated. This is
accepted. The raw JSON files remain in the repo root untouched, so the data is recoverable if they
change their mind.

---

## ADR-010 — Two-phase import with a human review gate
**Status:** accepted · 2026-09-28

**Decision** — JSON → `staging_*` tables → the user reviews in **DB Browser for SQLite** →
`--promote` fills the main tables. `--promote` refuses to run while `v_staging_issues` has blocking
rows. Import is idempotent on `myshows_id`, with `--dry-run` and `--reset`.

**Alternatives rejected**
- *CSV report + CLI promote* — the user chose DB Browser instead.
- *In-app review screen* — rejected; building UI for a one-time operation is poor value.
- *No staging, import straight into main tables* — rejected: this runs against the user's only copy
  of their watch history and is not reversible.

---

## ADR-011 — IMDb is a structural authority; `episode.imdb_id` added
**Status:** accepted · 2026-09-28 · **supersedes an earlier planning assumption** · **partly
superseded by ADR-025** — `episode.imdb_id` stands and is now nullable *symmetrically* with
`tvmaze_episode_id`; `season.imdb_id` is **removed** (a season is a grouping entity and gets no
external id).

**Decision** — IMDb datasets are treated as a legitimate authority for episode **structure**,
**titles**, and **runtime**, and provide a stable per-episode `tconst`. `episode.imdb_id` and
`episode.season.imdb_id` are added to the schema. Air-date **day/month** remains TVmaze-only.

**What was believed before** — that IMDb datasets contain no episode titles, making IMDb useful only
for show-level metadata. **This was wrong** and was corrected by grepping the actual TSVs
(`DATA_ANALYSIS.md` §7).

**Consequence** — the `Enrichment` job can backfill missing episode titles and runtimes and detect
missing/misnumbered episodes by exact `(season, episode)` diff. It also enabled the tri-source count
reconciliation in `v_staging_issues`.

**Related open question** — whether `Enrichment` should *union* TVmaze and IMDb episode sets or only
*flag* disagreements. Deferred to step 7.3; recommendation is flag-only in v1.

---

## ADR-012 — Persisted UI season-expansion state in the domain schema
**Status:** accepted · 2026-09-28

**Decision** — `ui_season_state` table. All seasons collapsed by default; expansion restored per show.

**Alternatives rejected** — a generic `app_settings` key-value table (expansion is per-show and
per-season, not a global setting), and not persisting it at all.

---

## ADR-013 — Specials: season 0, excluded from progress
**Status:** superseded by ADR-034 · 2026-09-28

**Decision** — import specials as `season_number = 0`. **Never** auto-mark them watched. **Exclude**
them from `watched_regular`, `total_regular`, and all progress math, and therefore from the
auto-finish condition. Unwatched specials surface as a note on the show card.

**User's reasoning** — "unwatched specials may be some special note to show... but it's not count
towards show marked as finished, maybe add filter later to display shows with unwatched specials."

**Deferred** — the "shows with unwatched specials" filter.

**Alternatives rejected** — including specials in totals (myshows counts them in `totalEpisodes`,
which is exactly why the counts need care rather than blind inclusion), and dropping specials
entirely.

---

## ADR-014 — Auto-finish is automatic, but never for running shows
**Status:** accepted · 2026-09-28 · **predicate superseded by ADR-022** — the literal string
comparison below is myshows vocabulary and would misfire on TVmaze data. The *decision* (fully
automatic, never for airing shows) stands; the implementation is now the `ShowStatus` enum, SPEC R12.

**Decision** — fully automatic, implemented as a cached condition check:
`total_regular > 0 AND watched_regular = total_regular AND status NOT IN ('Running Series',
'Returning Series')`.

**User's reasoning** — "running shows is never promoted to finished. only shows where I watched all
episodes and no more episodes expected. this transition is fully automatic, in db its just cached
condition check basically."

**Alternatives rejected** — a confirmation banner, and unconditional auto-finish regardless of API
status.

---

## ADR-015 — `title` (original) + `title_localized`
**Status:** accepted · 2026-09-28

**Decision** — two columns. If `title_localized` is present it is displayed as primary with the
original beneath; otherwise the original alone is primary. Localized titles are enriched later from
IMDb `title.akas`.

**Note** — the export's Russian titles can contain trailing whitespace or tabs. Trim on import.

---

## ADR-016 — Show all unwatched episodes of an expanded season
**Status:** accepted · 2026-09-28 · **supersedes SPEC draft wording**

**Decision** — expanding a season shows **all** unwatched episodes in it. The original requirement
said "earliest 10 episodes"; the user revised this to "display all unwatched from expanded season
for now".

**Consequence** — no pagination needed in v1. Revisit if a show with a very large season makes the
list sluggish; The Simpsons has 800+ episodes.

---

## ADR-017 — TVmaze feeds for Discovery
**Status:** accepted · 2026-09-28

**Decision** — `/newshows` (brand-new series) and `/upcoming` (next airings).

**Alternatives rejected** — `/newshows` only, and adding all `Running Series` as a browse feed.

---

## ADR-018 — Watched-state backfill heuristic
**Status:** accepted · 2026-09-28

**Decision** — "everything older than the first unwatched episode is watched, excluding specials",
with `needs_review` set whenever the reconstructed count disagrees with the export's
`watchedEpisodes`. Full algorithm in `DATA_ANALYSIS.md` §5.

**Measured impact** — because `episodes.json` batch `58` lists the *unwatched* set, only **1** of 78
started shows actually needs the heuristic. 33 are fully watched, 31 have nothing watched, and 13
partial shows have exact lists.

**Alternatives rejected** — assume first-N for everything (far more guesswork than needed), leave
everything unwatched (maximum manual work), and report-only with no writes.

---

## ADR-019 — `myshows_id` is transient: it never reaches the result schema
**Status:** accepted · 2026-09-28 · supersedes the `myshows_id` column in the original `show` table

**Decision** — the myshows.me show id exists **only** in `staging_show` and `staging_imdb_id`,
where its job is to key the export rows and make the import idempotent and resumable. Once
`--promote` has run, it has no further purpose and is not copied to `tracked_show`. There is no
`MyShowsId` on any Domain type.

**Why** — an id from a service this project scrapes exactly once is not an identity; it is a foreign
key into a dataset that will never be read again. Keeping it in the result schema invites the
assumption that it means something later, and it would be the first thing a later agent tries to
join on.

**Evidence** — Black Mirror is `id: 22410` in `watching.json` and `id: 305` in TVmaze
(`DATA_ANALYSIS.md` §8). Two id spaces, no relationship, no arithmetic between them. The original
planning assumption that they were the same id was wrong (§7).

**Consequence** — after `--promote`, the only way back to a specific export row is the raw JSON in
the repo root. Accepted: those files are read-only inputs and stay in the repo.

**Alternatives rejected** — keeping it "for traceability" (the raw JSON is the traceability), and a
`legacy_id`/`legacy_source` pair (generic multi-id columns are how identity confusion starts).

---

## ADR-020 — Only tracked shows are stored; no local catalog
**Status:** accepted · 2026-09-28 · **delete behaviour superseded by ADR-024** — the *no local
catalog* decision stands; untracking is now a logical delete rather than a row deletion, and the
user's tracking data lives in `user_show` rather than on `tracked_show`.

**Decision** — `tracked_show` is the **only** show table. A row exists if and only if the user
added that show to a category. Search results, Discovery feed entries, and IMDb titles are never
persisted as shows; they are rendered from the API response and held only in `http_cache`.

**Why** — the user's library is ~547 shows. TVmaze's catalog is tens of thousands, IMDb's is
millions. Storing either turns a personal database into a broken mirror of someone else's dataset
that is stale the moment it is written and must be re-synced forever.

**Consequence** — untracking a show deletes its `tracked_show` row, which cascades to its seasons,
episodes, and **watched marks**. Resolved by ADR-024: hard delete or soft
delete. Until answered, the import backup is the only safety net, which is why the backup rule in
`AGENTS.md` is a hard rule.

**Alternatives rejected**
- *A `show` table plus a `tracked_show` pointer* — the original design. Rejected: it implies a
  catalog that would have to be populated to be useful, and nothing populates it.
- *Cache search hits into `show`* — a cache with no eviction policy and no correctness story.
- *Store all TVmaze shows* — a different, much larger project.

---

## ADR-021 — `ImdbImport` is scoped to tracked shows; the TSVs stay on disk
**Status:** accepted · 2026-09-28 · narrows the original "download the datasets" job

**Decision** — the IMDb job downloads the datasets, but loads **only rows reachable from tracked
shows**: `title.episode` for parents whose `tconst` is a tracked `imdb_id`, then the matching
`tvEpisode` and series rows from `title.basics` (`SCHEMA.md` §4, load order in
`DATA_ANALYSIS.md` §6). The downloaded `.tsv.gz` files are **kept on disk** after loading.

**Why** — the full datasets are >100 MB uncompressed and 99.9% irrelevant to a 547-show library.
Keeping the files means a schema change is a re-load, not a re-download, and the download is the slow
and rate-limited part.

**Consequence** — the load is a **scoped re-load**, so it must be re-runnable whenever a new show is
added. `imdb_dataset_meta` records what was fetched; `job_run.items_changed` records what landed.

**Alternatives rejected** — loading the whole dataset (a 100 MB+ table for 547 shows), and deleting
the `.tsv.gz` after load (forces a re-download on every schema change).

---

## ADR-022 — Source tables and mapped tables are separate layers; provider vocabularies map to enums
**Status:** accepted · 2026-09-28 · supersedes the predicate in ADR-014, narrows ADR-009.
**Refined by ADR-024**, which splits the derived group in two: `episode_watch` and `ui_season_state`
are **user** tables, not mapped ones, and `user_show` joins them. The layering direction below is
unchanged; only the grouping name and membership moved.

**Decision** — two layers, one direction of flow:
- **Source tables** (`tvmaze_show`, `tvmaze_episode`, `tvmaze_status_seen`, `imdb_title`,
  `imdb_episode`, `imdb_akas`, `staging_*`) hold provider payloads **verbatim**, in whatever
  shape and vocabulary the provider used.
- **Mapped tables** (`tracked_show`, `season`, `episode`, `genre`, `show_genre`) hold the mapped view
  the UI reads, with **internal enums** instead of provider strings. (`episode_watch` and
  `ui_season_state` were listed here originally; ADR-024 moved them to the user group, which the UI
  also reads but the mapper never writes.)
- Only the mapper (ADR-023) reads source tables. Nothing in `Shows.Application` may name one.

**The bug this prevents** — auto-finish was specified as
`status NOT IN ('Running Series', 'Returning Series')`. Those are **myshows** strings; TVmaze
returns `"Running"` (`DATA_ANALYSIS.md` §8). The predicate matches nothing, so every airing show
would have been auto-finished the moment its last known episode aired — silently, with plausible
output. Any *other* provider would have introduced a *third* vocabulary, and each new comparison
site is another chance at the same mistake.

**Mechanism** — raw strings live only in `tvmaze_show.status_raw` and are collected in
`tvmaze_status_seen` so unseen values are discoverable rather than silently swallowed. The mapper
translates through exactly one table (SPEC R12) into `ShowStatus`; **an unmapped value fails
closed to `Unknown`, and `Unknown` is never `Ended`**. A new vocabulary string therefore cannot
cause a wrong transition — it causes a missing one, which is visible and reviewable.

**Consequences**
- `ADR-009` "minimal fields" now means minimal *result* schema. Source tables deliberately keep
  `summary` HTML, `rating`, `image`, `weight`, `schedule`, `externals` — stripping them at
  ingest would be irreversible.
- Adding a second catalog provider (SPEC F2) means adding a source table and a mapper branch, not
  changing the result schema.
- `episode_watch`, `category_id`, `added_at`, `needs_review` and `ui_season_state` are
  **never** written by the mapper (SPEC P4). Re-running any job cannot damage user data.

**Alternatives rejected**
- *Store the provider string and compare it at each use site* — the original design, and the source
  of the bug above.
- *Normalize at ingest and keep no raw copy* — loses information, and makes mapper bugs
  unfixable without re-fetching everything.
- *A single `raw_json` blob column instead of typed source tables* — no queryable source layer, so
  no reconciliation and no way to diff what changed between fetches.

---

## ADR-023 — One shared mapper; "add a show" is Enrichment scoped to one show
**Status:** accepted · 2026-09-28

**Decision** — a single `IShowMapper` implementation (`TvmazeShowMapper`, Infrastructure) is the
only code that turns source rows into `tracked_show`/`season`/`episode`. It is invoked from
three places and **no fourth**: `--promote`, `Enrichment`, and `AddShowToTracking`. Adding a
show from search is not a separate write path — it is the same mapper called for one id.

**Contract** — idempotent upsert keyed on `tvmaze_id`; never writes user-authored columns
(SPEC P4); safe to call concurrently for different shows but serialized per host by the HTTP handler;
reports what it changed so `job_run.items_changed` is measured **after** mapping, not after fetch.

**Why** — four jobs plus an import plus a search-add flow is six places that could each invent their
own idea of how a TVmaze payload becomes a row. The first divergence between them is a data bug that
only shows up weeks later as wrong episode counts. One mapper makes "adding a show" and "refreshing a
show" the same operation, which also means search-add is tested by every job test for free.

**Consequence** — an architecture test (step 8.1) asserts that no type outside the mapper references
the `tvmaze_*`/`imdb_*` source tables, and that no mapper method writes `episode_watch`.

**Alternatives rejected** — per-job mapping (six chances to diverge), a shared *base class* rather
than an injected service (inheritance hides the dependency and makes the architecture test harder to
write), and having `AddShowToTracking` write the row directly (fastest to type, and exactly the
divergence being avoided).

---

## ADR-024 — User data is a separate table; untracking is a logical delete
**Status:** accepted · 2026-09-28 · supersedes the delete behaviour in ADR-020 and the column
placement in ADR-009/ADR-010

**Decision** — two changes at once, because they are the same change:
1. **`user_show`** holds everything the user decided about a show: `category_id`, `added_at`,
   `category_changed_at`, `needs_review`, `deleted_at`. It is 1:1 with `tracked_show` and shares its
   primary key. `tracked_show` keeps only provider-derived columns plus the four cached progress
   columns.
2. **Untracking sets `deleted_at`.** Nothing is deleted. The `tracked_show`, `season`, `episode`,
   `genre` and `episode_watch` rows all survive.

**Why (1)** — SPEC P4 claims everything in the mapped tables can be rebuilt from the sources. While
`category_id` and `needs_review` sat on `tracked_show`, that claim was false for those columns, so
P4 was a comment rather than a property. Splitting them out makes it testable: delete the mapped
tables, re-run the mapper, and the user's data must be untouched. That test is the rebuild drill in
`SCHEMA.md` §2a and it is worth more than any amount of prose about layer separation.

**Why (2)** — re-adding a show restores the exact watched state the user had built. With a hard
delete, untracking a 200-episode show that was mis-categorised destroys hours of marks that the app
cannot reconstruct. There is no undo worth the name otherwise.

**Consequences**
- Every read path filters `deleted_at IS NULL`. That predicate lives in the repository, once, not in
  each caller (`ARCHITECTURE.md` §3). It is deliberately **not** an EF global query filter, because
  the jobs and any "previously tracked" view must see soft-deleted rows.
- Every job's selection query must apply it too, or the jobs refresh shows nobody tracks. The failure
  is silent and the cost is external rate limit, so it never announces itself.
- The category-tab query now joins two tables instead of reading one denormalised row. Accepted: the
  join is on a primary key over ~547 rows, and `ix_user_show_category` covers the active set.
- `user_show` and its watch rows grow monotonically. At personal-app scale this is irrelevant for
  years; it is recorded so nobody "optimises" it later with a purge that destroys the property.
- `needs_review` moved with the user columns because it records a human judgement about the import,
  not a mapper fact. It must be written when `user_show` is created, not patched afterwards.

**Alternatives rejected**
- *Keep user columns on `tracked_show`* — the previous design. Cheaper to query, and it made the
  central architectural claim untestable.
- *A `tracked` boolean instead of `deleted_at`* — loses when a show was untracked, which is the only
  part of the event that is interesting when reconstructing what happened.
- *A separate `untracked_show` archive table* — moves the data rather than marking it, so re-adding
  becomes a cross-table move with its own failure modes.
- *Hard delete with a confirmation dialog* — the recommendation in the previous revision, superseded
  by the user's decision. The dialog was mitigating a problem the logical delete removes.

---

## ADR-025 — Derived values are not stored; season has no external id; episode ids are symmetric
**Status:** accepted · 2026-09-28 · supersedes the `season.imdb_id` part of ADR-011

**Decision** — three related cuts, all instances of one rule: **if a value can be reconstructed from
data already in the row, storing it creates a second source of truth that can silently disagree.**
(SPEC P5)

1. **No provider URL columns.** `tvmaze_url` on show and episode is dropped. Both URLs are a function
   of the id: `https://www.tvmaze.com/shows/{id}`, `.../episodes/{id}`. One link-builder is the only
   place either format appears. `official_site` stays — it is authored data with no derivation rule.
2. **`episode.tvmaze_episode_id` and `episode.imdb_id` are both nullable, symmetrically.** An episode
   may be known to TVmaze only, IMDb only, or both, and the row is the union of whichever sides
   exist. The previous draft made the TVmaze id the assumed-present key and the IMDb id the optional
   extra, which quietly encodes "TVmaze always has it" and would have blocked the IMDb-only episodes
   that open question 1 is about.
3. **`season` has no external id.** `season.imdb_id` is removed. A season is a grouping entity that
   carries metadata belonging to a season rather than to a show or an episode — `name`,
   `episode_count`, `poster_url` — and everything in it is derived by grouping episodes.

**Why (3) specifically** — neither provider gives a season a stable identifier that survives a
re-fetch. TVmaze has no season resource at all; IMDb's season rows are `tvSeries` children with their
own `tconst` but they are inconsistently populated and are not what the app groups episodes by. A
column named `imdb_id` on `season` would be a promise nothing can keep, and a column that is
sometimes wrong is worse than a column that does not exist — the first is a bug report, the second is
a silent join producing plausible nonsense.

**Consequence** — ADR-011's claim that `episode.season.imdb_id` was added to the schema is now
partly wrong: `episode.imdb_id` stands, `season.imdb_id` does not. ADR-011 is annotated rather than
edited, per this file's protocol.

**Alternatives rejected**
- *Storing URLs for convenience* — the two copies disagree the first time a provider changes its URL
  scheme, and the stored copy is the one a UI will read.
- *A nullable-but-usually-present TVmaze episode id* — asymmetric nullability implies one side is
  authoritative, which contradicts the reconciliation design.
- *Keeping `season.imdb_id` "for future use"* — the classic justification for a column that will
  never be populated correctly.

---

## ADR-026 — Surrogate keys are client-generated, not store-assigned
**Status:** accepted · 2026-09-28 · **partly superseded by ADR-027.** The *client-generated* decision
stands and is the load-bearing part. The *UUIDv7 / UUIDNext* mechanism in this ADR was **rejected** by
ADR-027 once it was verified that a 128-bit UUID cannot be stored in SQLite's 64-bit `INTEGER`.
Read the "Why UUIDv7 specifically" and "⚠️ The unresolved part" sections below as the historical
record of how we got there, not as the current design.

**Decision** — no `AUTOINCREMENT`, no identity columns, no store-generated values. Every surrogate key
is generated by the application before the row is inserted, through one injected `IIdGenerator`. The
generator produces **UUIDv7 via the UUIDNext package**.

**Why client-generated** — the concrete driver is the import. `--promote` and the mapper create a
`tracked_show`, its `season` rows, its `episode` rows and the `episode_watch` rows referencing them,
ideally in one transaction. With identity columns the child ids do not exist until `SaveChanges`, so
the code needs an intermediate flush and the transaction becomes two round trips with a partial state
in between. Client-generated ids remove that whole class of ordering problem. They also survive the
Docker migration, where two writers will exist and a single SQLite sequence will not.

**Why UUIDv7 specifically** — time-ordered, so index locality is preserved and B-tree pages do not
shatter the way they do under `Guid.NewGuid()` (v4). UUIDNext over `Guid.CreateVersion7` (.NET 9+)
because this project is pinned to .NET 8 (ADR-004), and because UUIDNext guarantees monotonicity
within the same millisecond — relevant here, since the import mints thousands of ids in one
transaction and same-ms ordering is the normal case, not the edge case.

**⚠️ The unresolved part: "converted to int" does not fit.** A UUIDv7 is 128 bits. SQLite's `INTEGER`
is 64 bits signed. There is no conversion from one to the other that preserves uniqueness — the
options are truncation (loses uniqueness), a 64-bit hash (birthday collisions at scale), or storing
the full value as `BLOB`/`TEXT` (not an int). This is a real fork, not a detail, and it is why step
1.1 is a hard stop. The viable shapes are recorded in `STATE.md` open question 5 with their
trade-offs; the schema deliberately says only "INTEGER PK" and flags the type as pending rather than
pretending the question is settled.

**What is unaffected regardless of the answer** — natural keys remain the merge and match targets.
The mapper matches on `tvmaze_id` and `imdb_id`, never on the surrogate key, so the surrogate never
appears in a reconciliation predicate. And the no-regeneration rule from ADR-024 still holds:
whatever the storage type, an `episode.id` once minted is permanent, because `episode_watch` points
at it.

**Alternatives rejected**
- *`INTEGER PRIMARY KEY AUTOINCREMENT`* — the previous design. Simple, and it forces the intermediate
  flush described above; also unusable once two processes write.
- *`Guid.NewGuid()` (v4)* — random ordering shatters the clustered index. This is the specific
  problem UUIDv7 exists to solve.
- *.NET 9 `Guid.CreateVersion7`* — would remove the dependency, but the project is pinned to .NET 8
  by ADR-004, and it lacks UUIDNext's same-ms monotonicity guarantee.
- *Truncating UUIDv7 to 64 bits to fit `INTEGER`* — sounds like what "convert to int" means and is
  unsound. A UUIDv7's first 48 bits are a timestamp, so a truncation either keeps the timestamp and
  throws away most of the randomness, or keeps the randomness and loses the ordering that motivated
  choosing v7.

---

## ADR-027 — Surrogate keys are 64-bit time-ordered integers, not UUIDv7
**Status:** accepted · 2026-09-28 · supersedes the mechanism of ADR-026 (client-generation itself
stands). Resolves `STATE.md` open question 5.

**Decision** — every surrogate key is a **positive 64-bit integer** (`INTEGER` in SQLite, `long` in
C#) minted in-process by `IIdGenerator`, laid out as:

```
 1 bit    always 0      -> keeps every id positive, so ORDER BY / BETWEEN behave
41 bits   milliseconds since a fixed epoch of 2026-01-01T00:00:00Z
10 bits   process salt  -> distinct per process launch
12 bits   per-millisecond sequence
```

`1 + 41 + 10 + 12 = 64` exactly — this is the Twitter-Snowflake split, and there is **no spare bit**.
Any future widening must come out of an existing field, not be added on top; a 65-bit layout silently
overflows `long` and the first id that overflows is negative, which sorts before everything else.
Capacity: 2⁴¹ ms ≈ **69.7 years** (epoch exhausts around 2095), 2¹² = **4,096 ids/ms/process**
≈ 4.1 M ids/s, 2¹⁰ = **1,024** process salts. The whole import mints ~15,000 ids, which is about
4 ms of capacity at full rate.

**Why this shape** — it keeps the two properties that motivated UUIDv7 in the first place, in the
one container SQLite can actually index as a rowid:
- **Time-ordered**, so `INTEGER PRIMARY KEY` (which *is* the rowid, i.e. the clustered key) gets
  append-only B-tree behaviour instead of the page-splitting churn a random v4 guid causes.
- **Same-millisecond monotonic** via the sequence field, which is the normal case for the import, not
  the edge case.

It also drops a NuGet dependency from the id path entirely: the generator is ~40 lines over
`Environment.TickCount64`-disciplined `DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()`, so
`Shows.Domain` and `Shows.Application` stay dependency-free and `Shows.Infrastructure` gains no
package for this (ADR-001, ADR-004).

**What makes it actually correct is the constraint, not the salt.** The salt only lowers the
*probability* of a same-millisecond clash between two live processes (1/1024 that they share a salt,
and even then they must also pick the same sequence value in the same ms). Correctness comes from
`UNIQUE` on every key plus **whole-unit-of-work retry**: mint ids in memory, and on a
`DbUpdateException` whose constraint name is a PK/unique index, **roll the transaction back and
re-run the unit of work with freshly minted ids**. Do not attempt to resume mid-transaction — EF Core
treats a failed `SaveChanges` as messy, and SQLite aborts the statement, not the transaction, so the
in-transaction state is not trustworthy. This is affordable only because every writer here is an
idempotent upsert keyed on natural keys (ADR-023); a non-idempotent writer could not be retried this
way, and that is a constraint on future steps, not a footnote.

**Clock rollback** — the generator keeps `lastTimestamp` and must never mint backwards:
- rollback ≤ 2 s: keep minting at `lastTimestamp`, incrementing the sequence; ids stay unique, ordering
  is very slightly off. A desktop app gets NTP adjustments; refusing to work for them is worse.
- rollback > 2 s: **throw**, do not guess. Log it and let the operator fix the clock. Minting ids that
  claim to be from the future corrupts the ordering guarantee silently, and the ordering is the only
  reason this layout exists.

**Sequence exhaustion** within a millisecond: spin to the next millisecond. At 4,096/ms this is not
reachable by this application's write rate; it is handled so that it degrades to a slight stall
rather than a collision.

**The trap this creates in EF Core** — the SQLite provider assumes an `INTEGER PRIMARY KEY` is a
store-generated rowid and will configure `ValueGenerated.OnAdd` unless told otherwise, which is
exactly the behaviour ADR-026 removed. So `ValueGenerated.None()` must be set **explicitly, by
convention, on every key** — and the generated migration must be grepped for `AUTOINCREMENT` rather
than trusted to follow from the model configuration. Step 1.1 asserts both.

**Alternatives rejected**
- *UUIDv7 in a `BLOB`* — the recommendation in open question 5, and the strongest of the UUID options:
  big-endian bytes sort correctly and all 128 bits survive. Rejected because the user chose to keep
  `INTEGER`, and `INTEGER` is genuinely better here: it is the rowid, it is readable in DB Browser for
  the manual-SQL review workflow, and it halves the size of every FK column and index entry.
- *UUIDv7 in `TEXT`* — readable, but 36 bytes per key and per index entry, and a lexical sort that is
  only correct because v7 hex is fixed-width. The most fragile of the options despite looking the
  friendliest.
- *Truncating UUIDv7 to 64 bits* — see ADR-026; unsound either way you cut it.
- *`Guid.NewGuid()` (v4)* — random ordering defeats the clustered index.
- *41 bits + 22-bit sequence, no salt* — 4.1 M ids/ms and simpler, but the user confirmed jobs may run
  while the app is open, so two processes do write concurrently; 10 bits of salt is nearly free.
- *63-bit random* — no clock-rollback or salt reasoning at all, but it throws away the ordering that
  is the entire point, and the import's insert pattern is the worst case for a random clustered key.

---

## ADR-028 — Concurrent writers are supported; bulk operations take a lease
**Status:** accepted · 2026-09-28

**Decision** — the MAUI app and the console jobs **may** write to the same SQLite file at the same
time. Consequences, all of them mechanical rather than clever:
- `PRAGMA journal_mode=WAL` (many readers + one writer) and `PRAGMA busy_timeout=5000` are mandatory,
  already in `SCHEMA.md` §1.
- Ids must be mintable without asking the database — which is ADR-026's original driver, now load-
  bearing for a second reason.
- Every PK/unique index is a real constraint and every writer has the ADR-027 retry path.

**The lease.** WAL serialises individual write transactions, but it does not make a *multi-transaction
bulk operation* safe. `--promote` is the dangerous one: it reads staging, calls the mapper, and writes
`tracked_show` + `user_show` + `season` + `episode` + `episode_watch` per show across many transactions.
A job running concurrently can observe a half-promoted database and make decisions from it — e.g.
`StatusRecheck` concluding a show has no episodes. So:

```
writer_lease
  name        TEXT PRIMARY KEY   -- 'promote' | 'episode_refresh' | 'status_recheck' | ...
  holder      TEXT NOT NULL      -- 'pid-31337@2026-09-28T10:00:00Z/7f3a' (pid + start + random)
  acquired_at TEXT NOT NULL
  expires_at  TEXT NOT NULL      -- now + 90s, renewed every 30s by a heartbeat task
```

- **Acquire** in one atomic statement (SQLite ≥ 3.24 upsert; `Microsoft.Data.Sqlite` ships far newer).
  `changes() == 1` means acquired; `0` means someone else holds it live:
  ```sql
  INSERT INTO writer_lease (name, holder, acquired_at, expires_at)
  VALUES (@name, @holder, @now, @until)
  ON CONFLICT (name) DO UPDATE
     SET holder = excluded.holder, acquired_at = excluded.acquired_at, expires_at = excluded.expires_at
   WHERE writer_lease.expires_at < excluded.acquired_at;
  ```
  The `WHERE` on the `DO UPDATE` arm is what makes this safe: a live lease is not overwritten, and
  there is no read-then-write window to lose a race in.
- **Renew** `UPDATE writer_lease SET expires_at = ? WHERE name = ? AND holder = ?`. **Zero rows means
  the lease was lost** (crash long enough to expire, then someone else took it). The job must then
  **stop writing**, not continue optimistically — two processes writing the same rows is the failure
  the lease exists to prevent, and discovering it at the end of a 10-minute run is worse than stopping.
- **Expiry, not cleanup.** A dead process leaves a row that simply becomes stale. No orphan-reaping
  code, and a `kill -9` costs 90 seconds of unavailability rather than a corrupted database.
- **`--promote` is exclusive**: it acquires `promote` **and** requires that no *other* lease is live
  (`SELECT COUNT(*) FROM writer_lease WHERE expires_at > @now AND name <> 'promote'` must be 0).
  It is the one operation that must not interleave with anything.
- **The app takes no lease.** Blocking the UI because a job happens to be running would be a worse
  product, and interactive writes are single-row and covered by WAL plus the retry path. This is the
  reason ids cannot be store-assigned, and it is worth stating so nobody "optimises" the id generator
  back to a sequence later.
- **Schema migration is the exception that does need exclusivity**: whoever runs `Migrate()` must first
  confirm zero live leases. A job does this after acquiring its own lease; the app refuses to start
  with a clear message ("a job is running: <name>") rather than migrating underneath it. Migrating
  mid-transaction in another connection is how you get a job failing on a column it never expected.

**Alternatives rejected**
- *Assume a single writer* — contradicted by the user directly. It would also have made the id decision
  easier, which is precisely why it needed to be asked rather than assumed.
- *A global mutex / named semaphore* — works on one machine, which is all v1 is, but it does not
  survive the Docker split (iteration 2) and leaves no audit trail. The lease row doubles as evidence
  of which job ran when, next to `job_run`.
- *A zero-row lock table with `BEGIN IMMEDIATE` held for the whole run* — genuinely simpler, but it
  holds a write transaction open for minutes, which blocks the UI's writes for the entire job and
  forfeits the point of WAL.
- *Let the app take a lease too* — see above; it converts a rare conflict into a constant annoyance.

---

## ADR-029 — Proxy configuration is per external service, resolved in one place
**Status:** accepted · 2026-09-29

**Context** — TVmaze is unreachable from the development machine while myshows.me and the IMDb dataset
host are not (`DATA_ANALYSIS.md` §9): `api.tvmaze.com` resolves and accepts a TCP connection, then the
request dies at ~12 s, or the socket is forcibly closed at ~19 s. The other two hosts return 200
directly. A verified HTTP proxy at `main-pc:10801` returns genuine TVmaze payloads (the response for
show 305 is `Black Mirror`, `Running`, `2011-12-04`; the error body from a bad path is TVmaze's own
JSON, not the proxy's), so the block is real and the workaround works.

The requirement is in SPEC R14; the reachability observations are in `DATA_ANALYSIS.md` §9; the keys are
in `ARCHITECTURE.md` §4. This ADR records only why the shape is what it is.

**Decision** — one proxy setting **per named HTTP client** (`Tvmaze`, `MyShows`, `Imdb`), applied where
`AddShowsInfrastructure()` configures the handlers. Empty means direct. A short connect timeout makes a
dead proxy fail before the request timeout.

**Why per service rather than one global switch** — the three hosts fail independently, so a single
switch is wrong in one direction or the other: with it off, TVmaze work is impossible from the dev
machine; with it on, traffic that works fine direct is routed through a box that adds latency and a
single point of failure for no benefit. Per-service keys also make the configuration self-documenting:
reading the file says exactly which service is blocked today.

**Why not the machine's own proxy settings** — measured, not assumed. `WebRequest.DefaultWebProxy`
resolves to an empty proxy on this machine, `IsBypassed("https://api.tvmaze.com/")` returns `true`, and
`HttpClientHandler` with `UseProxy = true` and no explicit `Proxy` behaves identically to
`UseProxy = false` — i.e. it times out exactly as the direct request does. Inheriting ambient
configuration would therefore look like support for proxies while doing nothing, and would make
behaviour depend on a registry key nobody is tracking. Hence: **absent configuration means direct,
explicitly**, and the two are distinguishable in the log.

**Alternatives rejected**
- *One global proxy setting* — see above; wrong for the two hosts that work direct.
- *`HttpClient.DefaultProxy` / ambient system proxy* — proven inert here, and it makes correctness
  depend on machine state outside the repo. Rejected on evidence, not taste.
- *Route by URL pattern* (`*tvmaze*` → proxy) — the URL is a config value (`Tvmaze:BaseUrl`), so a
  base-url change silently changes routing. Named clients make routing an explicit decision at the
  call site's registration, not a side effect of a string.
- *A SOCKS handler NuGet package* — unnecessary: .NET 8's `HttpClientHandler` accepts `socks4`,
  `socks4a` and `socks5` natively and rejects anything else with `NotSupportedException`
  (`DATA_ANALYSIS.md` §9.4). Adding a dependency for it would be a second code path to keep in sync.
- *Environment variables (`HTTP_PROXY`)* — the jobs run from a terminal and from the app, and the
  app is a packaged MAUI head; env-var behaviour differs across those three launch paths, which is
  exactly the ambiguity the explicit-empty-means-direct rule exists to remove.
- *Retry the direct route when the proxy fails* — this converts a clear transport failure into a slow
  mystery, and R14 requires the two be distinguishable. Fail fast and name the host.

**Consequences**
- Every HTTP-touching step (0.4, 2.2, 3.1, 7.4) must state which named client it uses; a provider that
  constructs its own `HttpClient` bypasses the proxy and will work on the CI machine while failing on
  the dev machine, which is the worst possible failure order.
- Unreachable must never present as "zero results" (R14). The import phases that need no network
  (`--dry-run`, `--stage`, `--report`) must stay runnable with TVmaze down — otherwise the block
  silently gates the whole project on a proxy box being up.
- Credentials are not modelled yet: the verified proxy needs none. If one ever does, the value becomes
  a `user:pass@host:port` URL and the *password must not be logged* — the handler config is the only
  place that string exists, which is another argument for the single reader.

## ADR-030 — A missing IMDb id is a warning, not a promotion blocker
**Status:** accepted · 2026-09-29

**Context** — `v_staging_issues` (SCHEMA §6) is the human-review gate that `--promote` reads before it
touches any mapped table. In migration 0003 its severity CASE treated a null `imdb_id` as `blocking`,
alongside a null `tvmaze_id`. When the step 2.5 review was run against the real export, exactly two of
547 staging rows were blocking — `73305` and `96144` — and **both had a resolved `tvmaze_id` and no
`imdb_id`**. They were not broken rows; they were shows IMDb indexes under a different work than the
scraper could match, and the user had already decided to track them.

**Decision** — `unresolved_imdb_id` is demoted from `blocking` to `warning`. The view still surfaces it
(the reviewer must see which shows lack IMDb enrichment), but it no longer stops `--promote`. After
this change only two conditions block promotion: an unresolved `tvmaze_id`, and `watched_exceeds_total`.
Implemented as migration `0004_StagingIssuesImdbWarning`, which DROPs and re-CREATEs the hand-written
view with the single `WHEN s.imdb_id IS NULL THEN 'blocking'` line removed; `Down` restores the 0003
text verbatim.

**Why a missing tvmaze_id stays blocking but a missing imdb_id does not** — the two ids play different
roles. The mapper's only input is the TVmaze id: without it there is no episode catalog to map, no
status to read, nothing to build a `tracked_show` from, so promotion is genuinely impossible. The IMDb
id is **enrichment** (step 4.1) — it links the poster, the rating, the IMDb episode list used for
tri-source reconciliation. A show can be perfectly trackable, fully watchable, and correctly categorised
with no IMDb id at all. Blocking promotion on it would hold the user's whole library hostage to a
scraper that is, by the data's own nature (`DATA_ANALYSIS.md` §on IMDb matching), unable to resolve every
title.

**Why demote rather than auto-resolve or drop** — auto-resolving the two rows would mean guessing an
IMDb work, which is exactly the silent-wrong-data failure the two-phase gate exists to prevent. Dropping
them would lose the user's tracking intent for two shows they chose to keep. Demotion keeps the row,
keeps the signal, and lets the enrichment step fill the id later (or never) without penalising the user.

**Consequences**
- The promote gate's blocking set is now exactly `{unresolved_tvmaze_id, watched_exceeds_total}`. Any
  future condition added to the view must decide, explicitly, which side of that line it falls on.
- `--promote` will proceed past `unresolved_imdb_id` rows; the enrichment step (4.1) and the report must
  keep counting them so the warning does not quietly become invisible.
- The migration is view-only: the EF model is unchanged, so `0004`'s Designer carries a byte-for-byte
  copy of `0003`'s `BuildTargetModel`. A future model change must not be smuggled into a view migration.

---

## ADR-031 — TVmaze is authoritative; IMDb fills gaps and its disagreements are flagged, never merged
**Status:** accepted · 2026-09-30

**Context** — Step 7.3 (the IMDb-driven `Enrichment` mode) was gated on `STATE.md` open question 1: when
TVmaze and IMDb disagree about a show's episode set, does the app take the **union** (insert IMDb-only
episodes into `episode`) or **flag only** (leave `episode` as TVmaze built it and surface the difference)?
The user decided: *"use tvmaze as main source of data and imdb just completes it … in case of collisions
I want to see them, but main source is still tvmaze"* and *"just flag them and display mismatched episodes
only on the details page."*

**Decision** — TVmaze is the authoritative episode catalog. The enrichment mode:
1. **Fills gaps only.** Where TVmaze left `episode.title` or `episode.runtime_minutes` null, the IMDb
   value is written and provenance recorded (`title_source = 'imdb'`). A TVmaze value is **never**
   overwritten.
2. **Never fabricates.** IMDb's episode dataset carries neither a full air date (`start_year` is
   year-only) nor a description, so `air_date`/`air_stamp`/`description` are **not** filled from IMDb —
   a year is not a date, and inventing a day/month would be exactly the silent-wrong-data this project
   exists to prevent. A TVmaze air-date or description gap is **flagged**, not filled.
3. **Flags, does not merge.** The `(season, episode)` set is diffed both directions. IMDb-only and
   TVmaze-only episodes, and any field-level disagreement (title, runtime, air-date gap, description
   gap), are recorded in a new **`episode_mismatch`** table. No IMDb-only episode is inserted into
   `episode`; no TVmaze episode is removed.

**Why flag-only, not union** — a union would mint `episode` rows whose ids `episode_watch` could later
point at, for episodes the user never saw on the authoritative source, and would make the episode list
depend on which provider happened to index a filler special. It also collides with the `UNIQUE
(show_id, season_number, episode_number)` key: an IMDb filler at a (season, episode) TVmaze already owns
is either a spurious conflict or a silent drop. Flagging keeps `episode` a faithful projection of TVmaze
(SPEC P3/P5) and puts the reconciliation decision where it belongs — in front of the user, on the details
page.

**Why a materialized `episode_mismatch` table, not a boolean flag** — the UI cannot read the `imdb_*`
source tables (ARCHITECTURE rule 5 / SPEC R11: source rows are read by the mapper and nothing else). To
show *what* IMDb said that differed from what TVmaze kept, the mapper must materialise both sides. The
table is **derived and rebuildable** (SPEC P5, like `title_localized` materialised from `tvmaze_akas`):
the enrichment mode wipes and recomputes a show's rows each run, so it never drifts and a schema change
is a re-run, not a migration of user data. It is keyed on the `episode` row (or the show, for an
IMDb-only episode with no `episode` row) plus a `kind`, and holds the TVmaze value kept and the IMDb
value rejected.

**Consequences**
- `episode_mismatch` is a mapped (derived) table, not a source table and not a user table: the
  enrichment mapper is its sole writer, the details-page read use case is its only reader, and it is
  fully rebuildable — deleting it loses nothing the user created.
- The enrichment mode is a bulk writer: lease-guarded (`enrichment_imdb`, ADR-028), re-runnable
  (`items_changed = 0` on a second run over unchanged data), and R14 per-show failure handling.
- `episode.title_source`/`air_date_source` are load-bearing here: they are what let a later run (or a
  human) tell a TVmaze gap that IMDb filled from a value TVmaze owned, after many job runs.

## ADR-032 — My Shows list uses a CollectionView for non-Started categories and a ScrollView+VerticalStackLayout for Started
**Status:** accepted · 2026-10-01

**Context** — The My Shows list renders each tracked show as a card whose inline season/episode list
expands and collapses (SPEC R9, ADR-016). When a season expanded, the card did not grow to reveal the
episodes until the whole window was resized — the item's measured height was stale. This was reported as
user-issues.md issue 10 and took three attempts to fix, so the decision is recorded to stop a future
agent from "optimising" the list back into a `CollectionView`.

**Failed attempts (do not retry these)** — both tried to keep a `CollectionView` and force the item to
re-measure:
1. A `WeakReferenceMessenger` message to the page/card calling `InvalidateMeasure()` (on the item and on
   `((IView)collectionView)`) — no effect at runtime.
2. Re-inserting the expanded season into the card's own bound `Seasons` collection (`RemoveAt`+`Insert`)
   on the theory that a bound-collection change forces a re-measure — also no effect.

The root cause: **MAUI's `CollectionView` on Windows caches each item's measured height and does not
re-measure a card whose nested `BindableLayout` content changes size**, regardless of invalidation calls
or inner-collection mutations. No item-level trick defeats that cache.

**Decision** — The list uses **one control per category**, chosen by whether that category's cards expand:
- **Started** (the only category with inline expandable seasons, and the smallest list — 78 shows) uses a
  `ScrollView` + `VerticalStackLayout` bound via `BindableLayout`. A plain layout re-measures its children
  on every content change, so a card grows/shrinks the instant a season opens — with **no** invalidation
  plumbing. Realizing all 78 cards synchronously is fast.
- **Later / Cancelled / Finished** (no inline seasons, so cards never expand; and the big lists — Later
  246, Finished 207) use a **`CollectionView`**. Its virtualization realizes only the visible cards, so
  those lists open instantly, and because their cards never expand, the cached-item-height bug never
  triggers.

`TrackingViewModel` routes the filtered/sorted projection to exactly one of two collections
(`StartedShows` for the layout, `Shows` for the `CollectionView`), so the hidden control realizes nothing.
All the failed invalidation plumbing was removed: the messenger, `CardLayoutChangedMessage`, the
`onLayoutChanged` callback on `SeasonGroupViewModel`, and the page/card `InvalidateMeasure` handlers.

**Why not one control for all** — the two requirements are in genuine tension for a single control:
virtualization (fast open of the 200+ lists) needs a `CollectionView`, which cannot re-measure an expanded
nested `BindableLayout`; re-measure-on-expand needs a plain layout, which realizes every card and hangs on
the big lists. Splitting by category puts each list on the control that fits it, and the split is free
because improvement 14 already made only Started render seasons.

**Why this is acceptable** — `CollectionView` exists for virtualization of large lists; the Started list is
small enough that its loss is irrelevant, and the big lists keep virtualization. Correctness (cards that
resize) and open speed are both satisfied.

**Consequences**
- Do **not** reintroduce a `CollectionView` for the **Started** list without first proving a nested
  expand/collapse re-measures — it does not, on Windows, and that is the whole bug.
- Do **not** move the big non-Started lists onto a `ScrollView`+`VerticalStackLayout` — realizing 200+
  cards synchronously is the 7-second hang this decision exists to avoid.
- `SeasonGroupViewModel` and `TrackedShowCardViewModel` must stay free of layout-invalidation code; they
  only mutate their bound collections and let the surrounding layout react.
- If a future change makes a big category render inline seasons, the tension returns — revisit then with a
  re-measure test, not by assumption.
- Localized titles and posters are **out of scope** for this mode: localized titles come from TVmaze
  `/akas` (step 7.5, already shipped), and the IMDb datasets carry neither posters nor episode
  descriptions — so "IMDb completes TVmaze" is bounded by what the datasets actually contain.

---

## ADR-033 — UI head is Avalonia; the MAUI head is removed
**Status:** accepted · 2026-10-01 · supersedes ADR-001

**Context** — A second UI head, `Shows.App.Avalonia`, was built over the SAME Application/Infrastructure
layers as the MAUI `Shows.App`, kept side by side so the two could be compared (per the user's request).
After using both, the user found the Avalonia head works better and directed that the MAUI head be deleted
and Avalonia adopted as the single UI.

**Decision** — `Shows.App` (MAUI, `net8.0-windows10.0.19041.0`) is **removed** from the solution and the
repo. `Shows.App.Avalonia` (plain `net8.0`, Avalonia 11.3.2, `CommunityToolkit.Mvvm`) is the sole UI head.
The layer graph is unchanged in shape: the UI head still depends on Application and touches Infrastructure
only at its composition root (`Program.cs` / `App.axaml.cs`), which is the Avalonia analogue of the MAUI
`MauiProgram.cs`. The architecture rule (Rule 4) was retargeted from scanning `Shows.App`/`MauiProgram.cs`
to scanning `Shows.App.Avalonia` and exempting `Program.cs` / `App.axaml.cs`.

**Why Avalonia (what the comparison showed)** — the MAUI `CollectionView` cached item heights and could not
re-measure an expanded nested `BindableLayout` (ADR-032 was a workaround for exactly this); Avalonia's
`ItemsControl`/`ScrollViewer` re-measure naturally. Avalonia is also a plain `net8.0` app — no Windows TFM,
no MAUI workload — which fits the iteration-2 Docker/Linux story that ADR-001 had rejected Avalonia for.

**Consequences**
- ADR-001's "MAUI, Avalonia rejected" is no longer current; this ADR owns the UI-framework choice.
- ADR-032's *invariant* still holds (Started expands inline and must re-measure; the big non-Started lists
  must stay virtualized for fast open), but its *mechanism* is Avalonia-specific now: the Avalonia head uses
  a `ListBox` for non-Started and a `ScrollViewer`+`ItemsControl` for Started. Do not read ADR-032 as
  mandating MAUI controls.
- Avalonia is pinned to **11.3.2** (the last line targeting .NET 8): 12.x's XAML source generators need a
  newer Roslyn than the pinned SDK 8.0.425 ships. The generators are essential (they emit
  `InitializeComponent`), so the version must match the SDK.
- The MAUI-only shims (`MauiProgram`, MAUI `Shell`/`Preferences`/`Browser`/`MainThread`) are gone; the
  Avalonia head carries its own equivalents under `Shows.App.Avalonia/Services/`.

---

## ADR-034 — Specials split into significant (counts) and insignificant (excluded)
**Status:** accepted · 2026-10-02 · supersedes ADR-013

**Context** — ADR-013 treated every special as one thing: season 0, never auto-marked, excluded from
all progress. The real TVmaze data does not have one special kind; `episode.type` returns
`"significant_special"` and `"insignificant_special"` (DATA_ANALYSIS §8 fact 11). The vocabulary only
mapped `"regular"` and `"special"`, so **both** special kinds fell through to `EpisodeType.Unknown`.
That produced two visible bugs and one latent one:
- Specials rendered in the season grid as a bare `S2` with no episode number (Unknown is not
  `Special`, so the UI's "pull specials out" filter never caught them) — the screenshot the user
  reported.
- The details page's separate Specials section stayed empty even though the model had rows for it.
- Because `ShowProgress.Compute` counts only `Regular`, specials were excluded from progress — which
  happened to match ADR-013, but for the wrong reason, and it meant a **significant** special (a real
  episode the user must watch) never counted toward finishing.

**User's intent** — significant specials are real episodes: show them in the main list and count them
toward finished status, just with a marker. Insignificant specials are filler: show them only on the
details page in a separate section, and do **not** let them affect finished status.

**Decision** — `EpisodeType` gains a fourth member and the enum is re-defined (SCHEMA §1a):
`Unknown = 0`, `Regular = 1`, `Special = 2` (significant), `InsignificantSpecial = 3`. The vocabulary
maps `significant_special → Special`, `insignificant_special → InsignificantSpecial`, and keeps the
bare `special` string as a synonym for `InsignificantSpecial` (legacy/fixture safety). The semantics of
`Special` **flip** from "excluded" to "counts like a regular but marked", so every `== Special` /
`!= Special` predicate was audited and re-expressed against the counted set:
- `ShowProgress.Compute`: `Regular` **and** `Special` count; only `InsignificantSpecial` drives
  `HasUnwatchedSpecials`; `Unknown` stays excluded (fail-closed).
- `InlineEpisodeRules`, the two `EpisodeRepository` LINQ filters, and the raw-SQL count
  (`episode_type_id <> 2` → `<> 3`): exclude only `InsignificantSpecial`.
- `Promoter` backfill: the counted set is `Regular or Special`, so a significant special can sit on the
  watched frontier while a season-0 filler cannot zero it out.
- `ShowDetailsViewModel`: the grid holds `!= InsignificantSpecial`; the bottom section holds
  `== InsignificantSpecial`.
- `EpisodeRowViewModel.NumberText` renders each kind distinctly so a vocabulary gap is visible. The
  season prefix is shown only where no season header supplies it: `S02E05` (regular), `E05 ★`
  (significant — in the season grid, so the season is redundant; filled star), `S02E05 ✧`
  (insignificant — in the season-less Specials section, so the full number is shown; hollow star,
  distinct from the filled one), `? S02E05` (unknown). The Specials section sits above the
  disagreements block so watchable content reads before metadata noise.

**Why no migration** — `episode.episode_type_id` is an unconstrained integer column; adding an enum
member is a value-space change, not a schema change. Existing rows keep their stored value; the next
`EpisodeRefresh` / `Enrichment` re-maps them from `tvmaze_episode.type_raw` through the corrected
vocabulary. No `AUTOINCREMENT`/generated-key rule (ADR-026/027) is touched.

**Consequences**
- ADR-013's "all specials excluded" is no longer current; this ADR owns the specials rule. Its
  "never auto-mark" survives for the **insignificant** kind only — a significant special is a normal
  watchable episode and is auto-marked by the backfill like any regular.
- The finished-status bug the user suspected is fixed: an unwatched insignificant special no longer
  blocks finishing, and an unwatched significant special now correctly does.
- `has_unwatched_specials` now means "unwatched **insignificant** specials" specifically.
- The untracked catalog page (`GetCatalogDetailsUseCase`) has no progress semantics, so it collapses
  every special kind to one marker; the significant/insignificant split is a tracked-page concern.

---

## ADR-035 — Store the Discovery schedule archive in SQLite
**Status:** accepted · 2026-10-07

**Context** — The Discovery look-back depends on retained raw `/schedule/full` captures, previously
stored as date-named files under `cache/discovery/`. The user requested moving this archive to the
database.

**Decision** — Store the same raw response body in `discovery_schedule_archive`, keyed by the UTC
capture date. Keep the existing age-triggered promotion, same-day replacement, and independent age
retention. Reparse stored JSON through the current schedule parser; do not persist parsed Discovery
models. Start the DB archive fresh: do not import or delete existing archive files.

**Consequences**
- The archive is in the SQLite database backup, although it remains rebuildable cache data rather than
  irreplaceable user watch history.
- A fresh DB archive refills the look-back window over time; pre-existing file captures remain unused
  and untouched.
- The archive remains outside the mapper's TVmaze/IMDb source-table flow.

---

## ADR-036 — A show's display country falls back to its language
**Status:** accepted · 2026-10-07

**Context** — The user asked that shows with no country but a filled language show the language in
place of the country "everywhere", naming tvmaze 84377 ("Dead-End Job"): `webChannel: Netflix`,
`webChannel.country: null`, `language: "Korean"`. TVmaze attaches a country to the **broadcaster**, not
the show, and streaming broadcasters frequently leave it null — so a large slice of the catalog
(a lot of Korean, Japanese and Chinese drama sits on Netflix/Crave/Viki) displays and filters with no
country at all. The country is a real display and filter dimension: tracked cards, tracked details,
search cards, discovery cards and the discovery country facet.

**Decision** — `country = country_name ?? country_code ?? language`, in one function
(`Shows.Domain.CountryRules.Display`), applied where provider data becomes app data: the mapper fills
`tracked_show.country`, `TvmazeJsonMapper.MapCatalogShow` fills `CatalogShow.CountryName`, and the
discovery facet reads that same mapped value so a facet option can never disagree with its cards.
Existing rows are backfilled by `Enrichment --country`, which re-parses the stored `tvmaze_show.raw_json`
through the mapper — no network, no re-crawl.

**Alternatives rejected**
- *Do it in the UI* — each of the five display/filter sites would re-derive it and drift; the discovery
  facet is built in Infrastructure and could not share a View-layer helper anyway.
- *Substitute at parse time only, leaving `tracked_show.country` null* — the tracked list would still
  show nothing, which is half the user's request.
- *A raw-SQL `UPDATE` in the migration* — the mapper is the sole writer of mapped rows (SPEC P3/R11);
  a migration that writes `tracked_show` is a second writer no architecture test would catch.
- *A `country_source` column recording which of the three won* — nothing reads it; `raw_json` is the
  recovery path (SCHEMA §3) and P5 forbids storing what is derivable.
- *Filling `tvmaze_show.country_name` with the language* — that would put a derived value in a verbatim
  source column, breaking R11 and making the fallback impossible to undo.

**Consequences**
- `tvmaze_show` gains `country_name`/`country_code` (verbatim, migration `0010`); `tracked_show` gains
  `country` (rule-applied). Both nullable; nothing back-fills at the SQL level.
- The language string is displayed as a country ("Korean", not "South Korea"). The user asked for
  exactly this; a language→country table would be a new authored dataset with no source.
- The discovery country facet now mixes countries and languages in one list. Accepted: the alternative
  is two facets for one dimension.
- `Enrichment --country` is a new lease (`enrichment_country`) and `job_run` name (`Enrichment.Country`),
  and joins the bulk-writer architecture rule.

---

## ADR-037 — The backfill never marks an episode that had not aired at import time
**Status:** accepted · 2026-10-31 · amends ADR-018

**Problem** — the user noticed episodes marked watched whose air date was *after* the import. The probe
is one query: `episode_watch JOIN episode WHERE watched_at < air_date`. Fourteen rows, every one of
them stamped inside the `--promote` window, and `needs_review` was 0 on all of them — so the import
had invented watch marks and reported nothing wrong.

Two independent defects in `Promoter.BackfillWatchMarksAsync`, both in branch 3:

1. **A 0/0 show took branch 3 instead of branch 4.** `watched == total` is `0 == 0` for a show myshows
   has no episode data for, which satisfies "fully watched". `DATA_ANALYSIS.md` §5 already says a 0/0
   show falls into case 4 — the code just did not enforce it, because the documented edge case was
   tested only with an *empty* catalog. When TVmaze already lists episodes, all of them got marked.
2. **`totalEpisodes` from myshows is not the size of the TVmaze catalog.** myshows counts only episodes
   it has seen; TVmaze lists future episodes too. So "fully watched" on myshows' list is *not* fully
   watched on TVmaze's list, and every future episode inherits a watched mark. `needs_review` stayed
   clean because branch 3 never compares its count against anything.

**Decision**
- Branch 3 requires `totalEpisodes > 0`. A 0/0 show reaches branch 4 and marks nothing.
- After whichever branch chooses its set, any episode whose `air_date` is later than the day the import
  ran is **withheld** from the marks, and the withhold is logged at Warning — branch 3 leaves
  `needs_review` false, so for a fully-watched show that line is the only surviving signal.
- `needs_review` for branch 1 is computed against the **final** mark set, not the branch's raw output.
  The guard legitimately shrinks that set, and the whole point of ADR-018 is that a disagreement with
  the export's count gets flagged — measuring it before the guard would swallow exactly the disagreement
  the user asked to be told about.

**Why withholding and not, say, trusting the count** — a wrongly-unwatched episode is one click in the
app; a fabricated watched mark is invisible, inflates progress, and can make an airing show look
auto-finish-eligible. The conservative direction is the one that fails visibly.

**Scope of the repair** — existing rows are fixed by `draft/fix-import-watched-before-air.sql`, which
deletes only marks made during the import (`watched_at <= max(promoted_at)`) on episodes that *still*
have not aired. Marks on episodes that aired between the import and today are left alone: the mark was
fabricated, but the user may genuinely have watched them since, and the UI would never have corrected
it because the row already read "watched".

**Alternatives rejected**
- *Cap the marks at `totalEpisodes` instead of filtering by air date* — it fixes the symptom for branch 3
  and silently corrupts branch 1, where the frontier is positional and a cap would truncate the wrong
  end. It also marks future episodes watched while leaving genuinely-unwatched past ones unmarked.
- *Re-run `--promote`* — impossible: `promoted_at IS NOT NULL` removes every show from the target set,
  and re-minting episode ids would orphan `episode_watch` (AGENTS.md). The user is not re-importing.
- *Treat a null `air_date` as future* — a missing date is a TVmaze gap, not evidence of a future
  episode; withholding on it would drop watchable episodes.
- *Set `needs_review` instead of withholding* — leaves the wrong mark in place and makes the user undo
  something the importer invented, when the importer can simply not invent it.

**Consequences**
- `Payload(...)` in `PromoterTests` gained `futureRegulars`, because no existing fixture could produce
  an episode dated after today — which is precisely why the bug survived 12 passing tests.
- The 0/0 test still cannot catch defect 1 on its own (its catalog is empty); the new
  `Zero_total_but_a_populated_catalog_marks_nothing` is the one that pins it.
- `--promote` will not run again on this database, so the code fix is prevention for a future import,
  not the repair. The repair is the SQL script.

## ADR-038 — Status transitions are dated, not remembered
**Status:** accepted · 2026-10-31 · adds `tracked_show.status_changed_at` / `status_renewed_at` / `tvmaze_updated_at` (migration `0011`)

**Problem** — the Updates tab (improvement 25, SPEC F4) asks four questions, three of which need a date
the database did not have. "Which shows ended in the last 60 days", "which came back", and "which did
TVmaze change" all presuppose a timestamp that nothing was writing.

The probe: TVmaze payloads carry no status-change field at all — 0 of the 554 stored shows have one.
The candidates that already existed were all wrong:

- `tracked_show.mapped_at` moves on *any* field edit, including our own recomputes. A show whose poster
  URL changed "ended" yesterday.
- `tvmaze_status_seen` is a global vocabulary log — it records that the string `"Ended"` was seen, not
  that *this* show became Ended.
- `tvmaze_show.provider_updated` is TVmaze's own timestamp, which is the right answer for "updated
  lately" and no answer at all for when a status changed.

**Decision**
- **Two dated columns, not a previous-status column.** `status_changed_at` is when the show became
  terminal; `status_renewed_at` is when it left terminal into an airable status. The mapper captures the
  status before it applies the payload and compares after, so the transition is observed rather than
  inferred.
- **Terminal is `{Ended, Canceled}`** — `ShowStatusRules.NoMoreEpisodesExpected`, the set SPEC R12
  already owns. A cancelled show ended in every sense this view cares about. `Unknown` never qualifies:
  a vocabulary gap is not evidence that a show finished, and it must not be read as a renewal either.
- **An end is dated from the last aired episode, not from the observation.** A show whose finale aired in
  2019 ended in 2019. Dating it from "now" would list a show the user added this afternoon as having
  ended this week. With no dated episode, the mapper writes the observation time **once** and holds it,
  then replaces that placeholder when `EpisodeRefresh` brings the finale — so the column self-heals
  without the backfill having to be right.
- **A first map of a non-terminal show records no renewal.** A new show has no prior status, so there is
  no transition; stamping one would put every newly-added show into the Renewed section. The guard keys
  on the show *being* terminal, not on `previous != Unknown` — a freshly-minted row starts at `Unknown`,
  so a previous-status guard would have skipped exactly the first-map case that matters.
- **`tvmaze_updated_at` is a mapped copy of `tvmaze_show.provider_updated`**, written unconditionally on
  every map and not counted as a change. The UI may not read a source table (SPEC P3/R11), so the value
  has to cross the boundary the same way `status_id` crosses from `status_raw`.
- **The 0011 backfill dates ends from `max(episode.air_stamp)` and does not backfill renewals at all.**
  Nothing observed a renewal before this change, so inventing dates would put the entire library in the
  Renewed section.

**Why dated columns and not `status_prev_id`** — the counter-example is Ended → Running → Ended. A
previous-status column ends up holding `Running` after the second end, which both loses the fact that a
renewal ever happened and makes the show look like it is about to renew. Two dated columns keep both
facts, and the re-end legitimately re-dates the end while the renewal in between stays.

**Alternatives rejected**
- *Derive "ended recently" from `end_date`* — that is the show's own finale date, which for a show that
  finished in 2019 is five years stale; it answers "when did it stop airing", not "when did we learn it
  stopped", and the user asked for the latter.
- *Derive it from `mapped_at`* — see above; it moves for reasons unrelated to status.
- *Read `tvmaze_show.provider_updated` from the Updates read* — source-table containment (step 8.1). The
  value must be a mapped copy, which is the same argument that made `status_id` a mapped copy of
  `status_raw`.
- *Store one "last status change" column and classify at read time* — it cannot express the
  Ended → Running → Ended case either, because after the second end the single column says "ended" and
  the renewal is gone.
- *Backfill `status_renewed_at` from `mapped_at`* — every show would appear renewed.

**Consequences**
- The view is only as complete as the jobs that observe transitions. On a database that has never run
  `StatusRecheck` since this change, Renewed is empty and Ended holds only what the backfill could date
  from episodes. That is honest emptiness, not a bug — but it is invisible to a user who expects
  history, so it is stated in `STATE.md` and in F4.
- The mapper now writes three more columns, and `items_changed` must not count the
  `tvmaze_updated_at` mirror. A refresh that re-maps every show would report every show as changed.
- `RecordStatusTransitionsAsync` runs **after** the mid-map flush that persists episodes, so a first map
  of an Ended show can date itself from the episodes it just inserted. Before that flush it would see an
  empty episode set and write the placeholder.
- The 0011 backfill's `max(air_stamp)` is lexicographic over TEXT. That equals chronological only
  because every stored `air_stamp` is the same 25-char `+00:00` form — verified against the live
  database (19,503 rows, one distinct length). If a future write introduces another offset, this
  silently becomes wrong, which is why the migration says so out loud.

