---
status: current
authority:
  - requirements
not_authoritative_for:
  - id-encoding
  - storage-schema
  - layer-graph
  - external-facts
  - decision-rationale
  - sequencing
---
# SPEC — Functional Specification

Version 1.5, 2026-10-07. v1.5 adds **R15**: a show's displayed country falls back to its language
when the broadcaster carries none (ADR-036). See ADR-036.

Version 1.4, 2026-09-28. Revised after user review: the data model follows **TVmaze as the base
model**, the local database stores **only tracked shows**, and **raw provider data is kept separate
from mapped results**. v1.3 added: **user data lives in its own table and untracking is a logical
delete**, **provider URLs are derived not stored**, **both provider ids on an episode are nullable**,
and **surrogate keys are client-generated**. v1.4 adds **R13**: the key layout is a 64-bit
time-ordered integer rather than UUIDv7, and **concurrent writers are supported** via a writer lease.
See ADR-019 through ADR-028.

---

## 1. Scope

**In scope (v1):** desktop app, one-time import of existing data, TVmaze search + add, upcoming/new
shows discovery, four tracking categories, per-episode watched state, show details page, four
background jobs run manually as separate console projects.

**Out of scope (v1):** user ratings on shows, watched-hours/`watchCount` statistics, blurhash/
`promoUrl`/`onlineCount` fields, an in-app staging review screen, a "shows with unwatched specials"
filter, authentication, multi-user support, PostgreSQL, and the Docker build itself.

**Explicitly out of scope — storage (v1):** a local catalog of shows the user does not track. There
is no `show` row for a search hit, a discovery-feed entry, or an IMDb title. See ADR-020.

### Two governing principles

**P1 — TVmaze is the base data model.** Field names, granularity and semantics follow the TVmaze
payload (`DATA_ANALYSIS.md` §8). The myshows.me JSON exports are **input to a one-time migration
only** and have no influence on the schema. Where the two disagree — most importantly the status
vocabulary — **TVmaze wins**.

**P2 — The database stores only tracked shows.** A `tracked_show` row exists only because the user
decided to track that show. Everything else is pulled from the service on demand and held in
`http_cache`. This keeps the database at the size of the user's actual library (~547 shows) instead of
a mirror of two external catalogs.

**P3 — Source data and mapped data are separate layers.** Every provider's payload is stored
**verbatim** in its own source table (`tvmaze_show`, `tvmaze_episode`, `imdb_title`, `imdb_episode`,
`imdb_akas`), and the mapped tables (`tracked_show`, `season`, `episode`) are produced from them by
one mapper. Nothing reads a source table except the mapper. This is what lets a raw string like
`"Running"` become an internal enum, and what makes a second catalog provider an extra source table
rather than a schema change. See R11 and ADR-022.

**P4 — Only user-authored data is irreplaceable, and it is physically separate.** Everything in
`tracked_show`, `season` and `episode` is a pure function of the source tables and can be rebuilt.
The irreplaceable set is exactly three tables: **`user_show`** (the tracking decision: category,
`added_at`, `needs_review`), **`episode_watch`**, and **`ui_season_state`**. Backups and destructive
operations are judged against this list, not against the database as a whole.

v1.3 makes P4 structural rather than a convention. While `category_id` sat on `tracked_show`, that
table was not rebuildable and P3 was only true of some of its columns. Now the claim is testable:
deleting and re-deriving `tracked_show`/`season`/`episode` must lose nothing. See ADR-024 and
`SCHEMA.md` §2a.

**P5 — Nothing derivable is stored.** A TVmaze or IMDb URL is a function of its id, so no URL column
exists for either provider (`SCHEMA.md` §2). `official_site` is stored because it is authored data
with no derivation rule. The general form: if a value can be reconstructed from data already in the
row, storing it creates a second source of truth that can silently disagree.

---

## 2. Domain model

### Show — table `tracked_show` (+ `user_show`)

**A row exists only for a tracked show.** There is no separate catalog table and no separate
`TrackedShow` entity: because untracked shows are never persisted, the catalog snapshot is one row.
Table name is `tracked_show` and the Domain type is `TrackedShow` — there is no `Show` type anywhere,
so a stale `Show` reference cannot quietly mean something else.

The user's tracking decision is a **separate 1:1 row in `user_show`** (ADR-024), so `tracked_show`
is entirely rebuildable. The Domain type composes the two; the split is a storage concern, not a
second concept in the UI.

Fields mirror the TVmaze show payload (`DATA_ANALYSIS.md` §8). Left column is the local column, right
column is the TVmaze key it comes from.

| Column | TVmaze | Notes |
|---|---|---|
| `tvmaze_id` | `id` | **NOT NULL**, unique. The natural key. A tracked show always has one |
| `title` | `name` | **original** title |
| `title_localized` | — | Russian, from the export or IMDb `akas`. Not a TVmaze field |
| `show_type` | `type` | `Scripted` / `Animated` / `Documentary` / `Reality` |
| `language` | `language` | |
| `status_id` | `status` | **internal `ShowStatus` enum**, mapped from the raw string per R12. The raw text lives only in `tvmaze_show` |
| `runtime_minutes` | `runtime` | **often null** |
| `average_runtime_minutes` | `averageRuntime` | usually the usable one when `runtime` is null |
| `premiere_date` | `premiered` | **full date** (`2011-12-04`), not a year |
| `end_date` | `ended` | full date, null while airing |
| `official_site` | `officialSite` | |
| `schedule_time`, `schedule_days` | `schedule.time`, `schedule.days` | days as a comma list |
| `rating_average` | `rating.average` | TVmaze's own rating. Catalog data, not the user's rating |
| `network_name`, `webchannel_name` | `network.name`, `webChannel.name` | mutually exclusive |
| `country` | `network.country` / `webChannel.country`, else `language` | **not a TVmaze show field** — see R15 |
| `imdb_id` | `externals.imdb` | `tt` + digits |
| `thetvdb_id` | `externals.thetvdb` | kept; `tvrage` is defunct and is not stored |
| `poster_url`, `poster_medium_url` | `image.original`, `image.medium` | remote URLs in v1 |
| `description` | `summary` | **plain text** — the mapper strips TVmaze's HTML. Raw markup stays in the source table |
| `mapped_at` | — | when the mapper last wrote this row |

**No URL column** (P5): `https://www.tvmaze.com/shows/{tvmaze_id}` is derived from the id at render
time, and the same holds for any IMDb URL. `official_site` is kept because it is authored data with
no derivation rule.

Provider bookkeeping (`updated`) is **not** duplicated here — it lives on `tvmaze_show` where it is
read from, per P3.

Tracking state — **a separate table, `user_show`**, 1:1 with `tracked_show` via a shared key:

| Column | Notes |
|---|---|
| `tracked_show_id` | PK and FK at once; the relationship is 1:1 |
| `category_id` | Started / Later / Cancelled / Finished. Kept while soft-deleted, so a restore has something to restore to |
| `added_at`, `category_changed_at` | |
| `needs_review` | set by import when watched-state reconstruction was a guess; cleared by the user |
| `deleted_at` | **logical delete**. NULL = actively tracked. ADR-024 |

The four cached columns (`watched_regular`, `total_regular`, `has_unwatched_specials`,
`auto_finish_eligible`) stay on `tracked_show`: they are derived from `episode` × `episode_watch`,
both of which survive, and recomputing them is exactly what a rebuild does.

**`myshows_id` is deliberately absent from both tables.** It exists only to bridge the export to a
TVmaze/IMDb id and lives in `staging_show` for the duration of the import. Once a show is promoted,
the linkage has served its purpose. See ADR-019.

**Consequence of P2 + ADR-024:** untracking a show no longer deletes anything. Its row, its episodes
and its watch marks all stay in the database, hidden by the `deleted_at` filter, and re-adding the
show restores them exactly — which is the reason the logical delete exists.

### Genre
`genre` + `show_genre` join table, populated from the TVmaze `genres[]` array. IMDb episode-level
genres must never be used — they are inherited from the parent (`DATA_ANALYSIS.md` §6).

### Season / Episode
Child rows of `tracked_show`; they exist only because the parent is tracked.

`season` is a **grouping entity** — it holds metadata that belongs to a season rather than to a show
or an episode, and it has **no external identifier** (ADR-025). Neither TVmaze nor IMDb gives a season
a stable id that survives a re-fetch, so a column claiming one would be a promise nothing can keep.
Fields: `season_number` (0 = specials), `name`, `episode_count`, `poster_url`.

`episode` mirrors the TVmaze episode payload:

| Column | TVmaze | Notes |
|---|---|---|
| `tvmaze_episode_id` | `id` | unique. **Nullable** — null when the episode is IMDb-only |
| `title` | `name` | |
| `title_localized` | — | from IMDb `akas` |
| `season_number`, `episode_number` | `season`, `number` | |
| `episode_type` | `type` | `regular` / `special`. **Primary specials signal** — better than `season == 0` |
| `air_date` | `airdate` | date, `2011-12-04` |
| `air_stamp` | `airstamp` | offset-aware instant, `2011-12-04T21:00:00+00:00`. Canonical ordering key |
| `runtime_minutes` | `runtime` | |
| `rating_average` | `rating.average` | |
| `image_url`, `image_medium_url` | `image.original`, `image.medium` | |
| `description` | `summary` | HTML → stripped on write |
| `imdb_id` | — | from IMDb reconciliation, ADR-011. **Nullable** — null when the episode is TVmaze-only |
| `title_source`, `air_date_source` | — | provenance: `tvmaze` / `imdb` / `manual` |

**The two provider ids are nullable and symmetric** (ADR-025). An episode may be known to TVmaze
only, to IMDb only, or to both, and the row is the union of whichever sides exist. Treating
`tvmaze_episode_id` as the assumed-present key would encode "TVmaze always has it" and block the
IMDb-only episodes that open question 1 is about. No URL column on either level (P5).

### EpisodeWatch
One row per watched episode with `watched_at`. **Absence of a row means unwatched.** Storing
watched state as rows rather than a boolean column keeps history available for future statistics.

### Source, mapped and user layers

| Layer | Tables | Written by | Read by |
|---|---|---|---|
| **Source** | `tvmaze_show`, `tvmaze_episode`, `imdb_title`, `imdb_episode`, `imdb_akas` | ingestors (add-show, jobs) | **the mapper only** |
| **Mapped** (rebuildable) | `tracked_show`, `season`, `episode`, `genre`, `show_genre` | the mapper only | the UI and the Application layer |
| **User** (irreplaceable) | `user_show`, `episode_watch`, `ui_season_state` | user actions and the import only — **never the mapper** | the UI and the Application layer |
| **Import staging** | `staging_*` | `--stage`, the scraper, the user in DB Browser | `--report`, `--promote`, the mapper |
| **Coordination** | `writer_lease`, `job_run` | the job or operation itself | the operation, and the operator |
| **Transport** | `http_cache` | the HTTP handler | itself |

The middle row is a pure function of the top row and the bottom row is not a function of anything —
that asymmetry is the whole design (P3 vs P4) and it is why they are different tables rather than
different columns.

The `staging_*` tables **are** the myshows.me source layer — same role as `tvmaze_show`, with a
shorter lifetime. Once promoted, the linkage has served its purpose and `myshows_id` does not reach
the mapped schema (ADR-019).

### What is *not* stored

Search hits, discovery-feed entries, and any IMDb title not reachable from a tracked show are never
written as domain data. They are fetched live and held in `http_cache` (`SCHEMA.md` §2), which is
their designated home. The source tables in `SCHEMA.md` §3–§4 are **scoped to tracked shows**, not a
mirror of the datasets.

---

## 3. Business rules

Each rule is stated so it can be implemented and tested without further interpretation.

**R1 — Four categories, closed set.** `Started`, `Later`, `Cancelled`, `Finished`. No `Paused`. The
word "paused" in job descriptions refers to the TVmaze `status` string, not a user category.

**R2 — Jobs key off `tracked_show.status_id`, never off `user_show.category_id`.** A show the user
put in `Cancelled` may still be `Running` on TVmaze, and a show the user marked `Started` may have
been cancelled by its network. These are independent axes — which is now visible in the schema, since
they live in different tables.

**R3 — Title display.** If `title_localized` is non-null, display it as primary with `title`
beneath. Otherwise display `title` alone as primary.

**R4 — Specials.** TVmaze returns two special kinds and the distinction is load-bearing (ADR-034).
A **significant** special (`episode.type == "significant_special"`) is a real episode the user is
expected to watch: it **counts** toward `watched_regular`, `total_regular`, every progress percentage,
and the auto-finish test — exactly like a regular — and it appears in the season grid with a marker.
An **insignificant** special (`episode.type == "insignificant_special"`) is filler: **excluded** from
every progress figure and from auto-finish, **never** auto-marked watched, and shown only in the
details page's separate Specials section. The `type` field is more reliable than `season_number == 0`
(some shows legitimately number specials in-season); where IMDb data is the only source, fall back to
`season_number == 0` and treat it as insignificant. Unwatched insignificant specials surface as a note
on the show card. A filter for them is deferred.

**R5 — Auto-finish is fully automatic and is a cached condition check.**
```
auto_finish_eligible = total_regular > 0
                   AND watched_regular = total_regular
                   AND status_id IN (Ended, Canceled)   -- NOT Running, NOT Unknown, NOT ToBeDetermined
```
When eligible, the show moves to `Finished` without prompting. A show whose TVmaze status indicates
it is still running is **never** promoted, even if every known episode is watched — more episodes
are expected.

> **R5/R12 wording, resolved 2026-09-29.** This rule previously read `status_id = ShowStatus.Ended`,
> which contradicted R12's per-value table (which marks `Canceled` as "Can auto-finish: yes"). The
> table and ADR-014's rationale ("no more episodes expected") are the more specific statement, so the
> formula now includes `Canceled`. `ToBeDetermined` stays excluded — a renewal is still possible.
> Evidence and the one-line revert path: `LOG.md`, 2026-09-29, steps 0.2–0.4 entry. Flagged for user
> review because the user was not available to confirm at implementation time.

> ⚠️ **Status vocabulary is TVmaze's, not myshows'.** The live payload for Black Mirror returns
> `"status":"Running"`. The string `"Returning Series"` appears only in the **myshows.me export**
> (`DATA_ANALYSIS.md` §8). A predicate written against `"Returning Series"` would match nothing and
> auto-finish would silently fire on every fully-watched airing show. Derive the exact value set from
> live TVmaze data at step 3.1 and record it in the Step Log in `LOG.md`; do not copy strings out of the export.
>
> Because R12 forces the raw string through an enum, this class of bug is caught at the mapper
> instead of in every query — an unrecognised string lands in `Unknown`, which is **not** eligible to
> finish. Failing closed is deliberate.

**R6 — Manual category changes always win.** If the user moves a show out of `Finished` while it
remains eligible, do not fight them: record `category_changed_at` and let the recompute leave it
alone until the next watch-state change.

**R7 — Degenerate shows.** A show with `total_regular = 0` is never `auto_finish_eligible` and must
never cause a division by zero in progress math. At least one such show exists in the real data
(`totalEpisodes: 0`, `watchedEpisodes: 0`).

**R8 — Adding a show creates two rows; removing one is a logical delete.** Adding from Search or
Discovery inserts the `tracked_show` row from the live TVmaze payload, creates the `show_genre`
links, fetches and stores the full episode catalog, computes the cached columns, and creates the
`user_show` row holding the category and `added_at`. Before that moment the show does not exist
locally at all (P2).

**Removing a show sets `user_show.deleted_at` and nothing else** (ADR-024). The `tracked_show`,
`season`, `episode` and `episode_watch` rows all survive, so re-adding the show restores the exact
watch state the user had built. Two consequences that are features, not leaks:
- every read path must filter `deleted_at IS NULL`, and that filter belongs in the repository so a
  caller cannot forget it;
- source rows for a soft-deleted show are still refreshed by the jobs unless the job query also
  filters on `user_show.deleted_at`. It should — refreshing a show nobody tracks is wasted API calls.
  Filter it in the job's show-selection query, not in the mapper.

**R8a — Search and Discovery are stateless reads.** Rendering results writes nothing except an
`http_cache` entry. There is no "import into local catalog" step, no staging of browse results, and
no deduplication against a local catalog table — deduplication is against `tracked_show` only, so a
tracked show is visibly marked as such in the result list.

**R9 — Season expansion.** Expanding a season lists **all** unwatched episodes in it. No 10-episode
cap in v1. All seasons are collapsed by default. Expanded state is persisted per show in
`ui_season_state` and restored on next launch.

**R10 — Watched toggling is bidirectional** from the details page and from the list. Toggling
recomputes cached columns and re-evaluates R5.

**R11 — One mapper, invoked from three places.** Ingest and mapping are distinct steps, and the
mapping step is a single implementation (`IShowMapper.MapShowAsync`, implemented by
`TvmazeShowMapper`) shared by every path that produces result rows:

| Trigger | Ingest step | Then |
|---|---|---|
| Add show from Search/Discovery | store the live payload verbatim into `tvmaze_show` / `tvmaze_episode`; create the `tracked_show` row holding the ids and the `user_show` row holding the category | run the mapper for **that one show** |
| `EpisodeRefresh` / `StatusRecheck` | refresh the source rows | run the mapper for changed shows |
| `Enrichment` | refresh source rows for all tracked shows, including IMDb | run the mapper for all tracked shows |

So **adding a show is `Enrichment` scoped to one show**, exactly as the user described. There is no
second code path that writes `tracked_show` fields from provider data.

Hard requirements on the mapper:
- **Idempotent** — running it twice on unchanged source rows produces no changes.
- **Never writes user data** — the whole of `user_show`, `episode_watch` and `ui_season_state` is
  off-limits (P4). It writes catalog columns and cached progress columns only.
- **Never regenerates a surrogate id.** It upserts on natural keys and leaves existing ids alone,
  because `episode_watch.episode_id` points at them. Delete-then-insert is forbidden even in a
  rebuild (`SCHEMA.md` §2a).
- **Reads only source tables** — it must not touch `http_cache` or the network.
- **Logs every unmapped value** at warning, per `ARCHITECTURE.md` §7.

**R12 — External vocabularies map to internal enums at one point of contact.**

`ShowStatus`: `Unknown` (0), `Running`, `Ended`, `Canceled`, `ToBeDetermined`. Mapping is exact
case-sensitive string match against `tvmaze_show.status_raw`, held in one dictionary.

| Raw TVmaze value | `ShowStatus` | Can auto-finish? |
|---|---|---|
| `Running` | `Running` | no |
| `Ended` | `Ended` | yes |
| `Canceled` | `Canceled` | yes |
| `To Be Determined` | `ToBeDetermined` | no — a renewal is still possible |
| anything unrecognised | `Unknown` | **no** — fail closed and log |

The same rule applies to `episode_type` → `EpisodeType { Unknown, Regular, Special, InsignificantSpecial }`
where `Special` is TVmaze `significant_special` (counts) and `InsignificantSpecial` is
`insignificant_special` (excluded) — see R4 / ADR-034. `show_type`
stays as text because it carries no behaviour. The enum exists so a provider vocabulary change costs
one dictionary edit plus a warning log, rather than silently changing R5's behaviour.

**R13 — Identity is minted in-process; concurrent writers are expected.**
Every surrogate key is generated by the application before insert, as a positive 64-bit integer
(`long`). No database ever assigns an id, because the app and the jobs write to the same file at the
same time (ADR-028) and a store-generated key forces a round trip in the middle of the import's
transaction. **The bit layout is deliberately not specified here** — it is owned by `SCHEMA.md` §1a.
Repeating an encoding in a functional specification is how a storage detail quietly becomes a
business rule, and this rule was the case that proved it.

Three obligations follow, and each is a rule rather than an implementation detail because getting it
wrong is silent:
- **A key is never re-minted.** `episode.id` is permanent for the life of the row; `episode_watch`
  points at it (R11, ADR-024).
- **A constraint violation retries the whole unit of work**, never the failed statement. Every writer
  here is an idempotent upsert keyed on natural keys, which is what makes retry safe at all.
- **A bulk write holds a lease.** `--promote` is exclusive of all other writers; each job holds its own
  lease and **stops writing the moment a renewal fails**. WAL serialises single transactions, not
  multi-transaction jobs, and a job reading a half-promoted database will draw a confident wrong
  conclusion from it.

**R14 — Every external service is independently routable through a proxy.**

Three hosts are contacted: TVmaze, myshows.me, and the IMDb dataset host. They are blocked and
reachable *independently* — on the development machine TVmaze is unreachable directly while the other
two work fine (`DATA_ANALYSIS.md` §9). Proxy configuration is therefore **per service**, never global:
one host being blocked says nothing about the others, and a single switch would push traffic that
already works through a proxy for no reason.

The config keys and their shape are owned by `ARCHITECTURE.md` §4. What is owned here is the
behaviour, and each rule is a rule because the alternative failure is silent:

- **Unreachable means stop and say so, never an empty result.** A job that treats a blocked host as
  "no data" reports success and leaves the database quietly incomplete — the worst outcome available
  here, because every row count still looks plausible and the gap is invisible until a user notices a
  show with no episodes.
- **A proxy failure must be distinguishable from a provider failure** in the log and in `job_run`.
  "Cannot reach the proxy" and "TVmaze answered 5xx" call for opposite actions; collapsing them sends
  the user to debug the wrong thing.
- **Absent proxy configuration means direct, explicitly** — not "inherit whatever the machine is
  configured with". `DATA_ANALYSIS.md` §9 records why ambient settings do not rescue this case.
- **A blocked service must not block the phases that do not need it.** Import and staging run offline
  against local files; only TVmaze-dependent steps are gated on reachability. Losing TVmaze must not
  make the app unable to import the user's own history.

**R15 — A show's displayed country falls back to its language.**

TVmaze puts a show's country on its **broadcaster** (`network.country` / `webChannel.country`), and
streaming broadcasters often answer `country: null` while still filling `language` — e.g. tvmaze 84377
("Dead-End Job", Netflix, `language: "Korean"`). The user reads "no country" as missing data, so:

- Wherever the app **displays or filters** a show's country — tracked cards, tracked details, search
  cards, discovery cards, the discovery country facet — the value is
  `broadcaster country name ?? broadcaster country code ?? language`.
- The rule lives in **one function** (`CountryRules.Display`, ADR-036) and is applied at the mapped
  layer, so every read path shows the same string and a facet value always matches its cards. The UI
  never re-derives it.
- The fallback is a **display/mapped-layer rule only**. Source rows keep the broadcaster's country
  verbatim, nulls included (R11) — `language` keeps its own column and is never overwritten by it.
- A show with neither country nor language has no country: it stays NULL and the facet files it under
  "Not set". The fallback must not empty that entry.

---

## 4. Features

### F1 — One-time import (two-phase)
1. `--stage` parses the four category JSONs plus `episodes.json` into `staging_show` and
   `staging_episode`. Idempotent on `myshows_id`. `--dry-run` reports without writing. `--reset`
   wipes tracked data first.
2. `ScrapeMyShows` resolves `myshows_id → imdb_id` into `staging_imdb_id`.
3. TVmaze `/lookup/shows?imdb=` resolves `imdb_id → tvmaze_id`.
4. `--report` exports `v_staging_issues` to CSV.
5. **Human gate:** the user reviews and edits staging tables in DB Browser for SQLite. No review UI
   is built.
6. `--promote` refuses to run while `v_staging_issues` has blocking rows, then writes the source
   rows, creates the `tracked_show` and `user_show` rows, and invokes the mapper (R11) to produce
   `season` / `episode`, applying the backfill algorithm in `DATA_ANALYSIS.md` §5 for watch state.
   The category and `needs_review` land on `user_show`, which the mapper does not touch.

### F2 — Search
Debounced name search against TVmaze. Results show poster, title (per R3), year, status, genre, and
whether the show is already tracked — a lookup against `user_show` where `deleted_at IS NULL`, not
against `tracked_show`. The distinction matters now that untracking is soft: a soft-deleted show
still has a `tracked_show` row, so checking that table would report it as tracked and block the very
re-add the design is built around. A detail drawer shows full info and a category picker. Adding
follows R8. Nothing is persisted until the user adds (R8a). The provider sits behind
`ICatalogProvider` so a second provider can be added later without touching the UI.

### F3 — Discovery ("upcoming block")
Three buckets over the TVmaze `/schedule/full` snapshot (the `/newshows` and `/upcoming` endpoints 404 —
issue 1), each a filtered/ordered/paged view of the same cached snapshot. With `windowStart = now - window`
(`window` is configuration, `ARCHITECTURE.md` §4):

- **New** — the show has any **season 1** episode airing after `windowStart` (no upper bound: a first
  season still airing ahead of today is still new).
- **Airing** — the show has any episode (any season) airing after `windowStart`. New ⊂ Airing.
- **Upcoming** — the show has a future-dated **season premiere** (an episode numbered 1 airing after `now`).

The buckets overlap by design (a not-yet-premiered new show is both New and Upcoming). Same detail drawer
and add flow as Search, sharing `ShowCardViewModel`. Also stateless per R8a — the feed is not
materialised locally, so it always reflects the live service.

> **The look-back buckets need history the endpoint does not serve** (2026-10-07). `/schedule/full` is
> forward-only (DATA_ANALYSIS §8 fact 17), so `windowStart` can only be reached by captures taken before
> the episodes in question aired. The source therefore retains dated **raw** responses and folds them
> into the query; the feed is still not *materialised* in R8a's sense — no derived rows are stored, the
> raw captures are re-parsed through the same mapper path on every read, and a fresh fetch still answers
> for everything from today forward.

### F4 — Tracking view
Four category tabs over `user_show` joined to `tracked_show`, filtered `deleted_at IS NULL`. Each tab
is a virtualized list of tracked shows with poster, title, and progress
`watched_regular / total_regular`. Expansion follows R9. Unwatched specials surface per R4.

### F5 — Details page
Genres, description, poster, external links to IMDb and TVmaze, and the full season/episode grid
with watched state visible and toggleable in both directions. Rendered entirely from the local
`tracked_show` / `episode` rows — no live fetch is required to open it.

### F6 — Background jobs (separate console projects, run manually)

| Job | Selects shows | Does |
|---|---|---|
| `EpisodeRefresh` | `status_id != Ended` | refresh `tvmaze_show` / `tvmaze_episode` source rows, then map |
| `StatusRecheck` | `status_id = Ended` or `Canceled` | re-poll for cancellation/renewal, update source rows, then map |
| `Enrichment` | all tracked shows | refresh source rows from TVmaze **and** IMDb, then map all |
| `ImdbImport` | n/a | download the IMDb datasets and import **only rows reachable from tracked shows** |

Both status-driven jobs filter on `status_id` per R2, which is exactly why the two exist separately:
they poll opposite populations at different cadences. Note the direction — `EpisodeRefresh` targets
airing shows because their episode lists change, while `StatusRecheck` targets finished ones because
only their *status* can change.

**Jobs do not write mapped or user tables directly.** A job refreshes source rows and then calls the
mapper (R11). This is the difference between "four jobs that each have their own idea of how to fill
`tracked_show`" and "four fetchers plus one mapper".

**`ImdbImport` is scoped, not a mirror.** It downloads the full dataset files but keeps only the
titles whose `tconst` matches an `imdb_id` on a tracked show, plus their episodes and akas. The
files are retained on disk under `Data:ImdbDatasetPath` so a later re-run can widen the scope without
re-downloading. See `SCHEMA.md` §4 and ADR-021.

---

## 5. Acceptance criteria

Import:
- `--dry-run` reports 78 / 246 / 16 / 207 shows, matching `episodes.json` batch `id:57`.
- Show `86813` resolves to `tt26448526`; show `22410` resolves to `tt2085059`.
- After `--promote`, `SELECT COUNT(*) FROM v_staging_issues` where blocking = 0.
- Show `86813` has exactly **1** watched regular episode out of 33.
- The 13 `started: true` shows in `episodes.json` reconcile against `episode_watch`.
- The `totalEpisodes: 0` show is present, is not `auto_finish_eligible`, and produced no errors.

Behavior:
- Search "black mirror", add to Started, episodes appear.
- **Adding a show writes `tvmaze_show` verbatim first**, then produces `tracked_show` via the mapper.
  Assert both. Assert there is **no** state where `status_raw = 'Running'` and
  `status_id <> Running` — that combination must be impossible, not merely unlikely.
- Adding a show and then running `Enrichment` immediately produces **zero changes** (mapper
  idempotence, R11).
- Toggle the last unwatched episode on an **Ended** show → moves to `Finished` automatically.
- Toggle the last unwatched episode on a **Running** show → does **not** move.
- Feed the mapper an unknown status string → lands in `Unknown`, does **not** auto-finish, and logs a
  warning.
- Expand a season, restart the app → expansion restored.
- A show with only unwatched **insignificant** specials remaining shows the specials note and **is**
  finished on its regulars (R4 / ADR-034); an unwatched **significant** special blocks finishing.

Untracking (ADR-024) — the criteria that make the logical delete real:
- Untrack a show with watched episodes → it disappears from every tab, and
  `SELECT COUNT(*) FROM episode_watch WHERE show_id = …` is **unchanged**.
- Re-add the same show → the previous watched state is intact, episode ids are unchanged, and no
  episode row was re-inserted.
- A soft-deleted show is reported as **not tracked** by search (R8/F2), and re-adding it does not
  create a second `tracked_show` row.
- Untracking then running every job → no source refresh for that show, and no crash.

Rebuildability (P3/P4) — the property the split exists to buy:
- Snapshot `episode_watch`, `user_show` and `ui_season_state`; delete `episode`, `season`,
  `tracked_show`, `show_genre`, `genre`; re-run the mapper over every `user_show` row; the three
  snapshots are **byte-identical** and the content is equivalent.
- This must pass via upsert, not restore-from-backup. If it only passes after restoring the
  snapshots, the mapper is regenerating ids and the property is fake (`SCHEMA.md` §2a).

Architecture:
- `dotnet build` clean with warnings as errors.
- Layer tests prove `Shows.Application` references no EF Core, HTTP, or MAUI types.
- A test proves **no code outside the mapper reads a source table** (`tvmaze_*`, `imdb_*`).
- A test proves **the mapper never writes `user_show`, `episode_watch` or `ui_season_state`**.
- A test proves **no entity has a store-generated value** (ADR-026). The requirement is owned here;
  the mechanism that makes it easy to get wrong, and how to assert it, are in `SCHEMA.md` §1a.
- Identity (R13/ADR-027): ids from one generator are distinct and **strictly increasing**; a clock
  rollback within tolerance keeps producing them, a larger one fails loudly, and the generator never
  reuses an id. The tests that pin the encoding itself live with the encoding — `SCHEMA.md` §1a and
  `IMPLEMENTATION_PLAN.md` step 1.6.
- Concurrency (R13/ADR-028):
  - A second lease request for the same name fails while held, and succeeds once the holder releases
    or its lease expires.
  - Forcing lease loss mid-run makes the job **stop writing**, observably, rather than finish anyway.
  - `--promote` refuses to start while any other lease is live, and proceeds when the lease is stale.
  - `Migrate()` refuses to run under a live lease and names the job in the message.
- Logging: every app writes to `log/<appname>.log` and the level comes from one shared config file
  (`ARCHITECTURE.md` §7) — verified by checking two different apps produce the expected levels.
- Network (R14): with no proxy configured and TVmaze unreachable, the app **fails with a message
  naming the host**, and the import phases that need no network still complete. A test injects an
  unreachable proxy and asserts the failure is reported as a transport problem, not as zero results —
  the distinction is the whole point of the rule.
