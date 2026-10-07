---
status: current
authority:
  - external-facts
not_authoritative_for:
  - storage-schema
  - requirements
---
# DATA_ANALYSIS — Verified Facts About the Input Data

Every fact here was verified against the actual files in this workspace on 2026-09-28. **Do not
re-derive these, and do not trust your priors over this document.** Two widely-believed assumptions
turned out to be wrong here (see §7). If you find a contradiction, fix this file in the same commit
as your code and log it in the Step Log in `LOG.md`.

---

## 1. File inventory

| File | Records | Role |
|---|---|---|
| `watching.json` | 78 shows | category **Started** |
| `later.json` | 246 shows | category **Later** |
| `cancelled.json` | 16 shows | category **Cancelled** |
| `finished.json` | 207 shows | category **Finished** |
| `episodes.json` | 4 RPC batches | totals + unwatched episode lists |
| `title.basics.tsv` | >50 MB | IMDb titles incl. every episode |
| `title.episode.tsv` | >50 MB | IMDb episode → parent mapping |

**Total tracked shows: 547.**

Both `.tsv` files exceed 50 MB and **cannot be opened by editor tooling** — `read_file` fails on
them. Inspect with `grep`/`Select-String`; process in code by streaming.

---

## 2. JSON envelope shape

All five JSON files are **JSON-RPC 2.0 envelopes**, not plain arrays. `episodes.json` is an **array
of envelopes**; the four category files are a **single envelope each**. Parse `result[]` and tolerate
empty `result` arrays (batch `id: 60` is empty).

Category files:
```json
{ "jsonrpc": "2.0", "id": 51, "result": [ { "show": { ... }, "watchStatus": "watching",
    "rating": 5, "watchCount": 1, "totalEpisodes": 32, "watchedEpisodes": 32,
    "runtime": 63, "totalHours": 33.6, "watchedHours": 33.6 } ] }
```

`show` object fields present: `id`, `title` (Russian), `titleOriginal`, `alias`, `status`,
`totalSeasons`, `genreIds`, `year`, `watching`, `voted`, `rating`, `images[]`, `image`,
`imageInfo{}`, `onlineCount`, `promoUrl`, `category`, `canRate`, `userShow{}`, `genres[]`.

`watchStatus` values observed: `watching`, `later`, `cancelled`, `finished`.

**Fields to discard** (user chose minimal schema): `rating`, `watchCount`, `totalHours`,
`watchedHours`, `watching`, `voted`, `imageInfo.blurhash`, `onlineCount`, `promoUrl`, `userShow`,
`canRate`. Keep `totalEpisodes` and `watchedEpisodes` — they drive the backfill.

Note: `title` values can carry trailing whitespace, e.g.
`"Неизведанные тайны парка \"Царство Тьмы\"\t"`. **Trim on import.**

---

## 3. `show.id` is a myshows.me id — NOT a TVmaze id

This was the single most consequential finding. Evidence:

- The Simpsons is `id: 50` in the export but its TVmaze id is 105.
- Live fetch `https://myshows.me/view/22410/` → **Black Mirror**. ✓
- Live fetch `https://myshows.me/view/86813/` → **Brilliant Minds**. ✓
- `imageInfo.source: "tvmaze"` means the *image* was sourced from TVmaze. It does **not** mean the
  id is a TVmaze id. This is what made the wrong assumption so plausible.

Consequence: `myshows_id` is the only join key available from the export, and the id-resolution
chain in ADR-006 is **required**, not optional.

### The id-resolution chain
```
myshows_id → scrape myshows.me/view/{id}/ → imdb tconst → TVmaze /lookup/shows?imdb= → tvmaze_id
```
Verified scrapable without authentication. The IMDb link sits in the ratings block:
- `myshows.me/view/22410/` → `https://www.imdb.com/title/tt2085059/`
- `myshows.me/view/86813/` → `https://www.imdb.com/title/tt26448526/`

Suggested pattern: `imdb\.com/title/(tt\d{7,8})/`. Also capture the Kinopoisk link if present — it
is free and useful for localized-title enrichment later.

Fallback ladder when scraping or lookup fails: manual id entry (user edits a CSV or staging row) →
title+year `singlesearch` flagged `needs_review`.

---

## 4. `episodes.json` — the batches

| Batch `id` | Content |
|---|---|
| `60` | empty `result` |
| `57` | per-status totals: `watching` 78, `later` 246, `cancelled` 16, `finished` 207 — **matches the four files exactly**, so it is a reliable cross-check |
| `59` | 246 `later` shows with `totalEpisodes`/`watchedEpisodes` |
| `58` | **43 shows** with explicit `episodes[]` arrays + a `started` boolean |

Batch `58` entry shape: `{ "episodes": [ ... ], "show": { ... }, "started": bool }`.
`started` distribution: **13 true, 30 false**.

Episode object fields: `id`, `title`, `showId`, `seasonNumber`, `episodeNumber`, `airDate`
(`2026-02-24T20:00:00+0300`), `airDateUTC` (`2026-02-24T12:00:00-0500`), `images[]`, `image`,
`imageInfo{}`, `shortName` (`s01e08`), `commentsCount`, `commentsNew`, `isSpecial`, `hasEmbed`,
`rating`, `runtime`.

Store `airDateUTC` as the canonical air date; keep the offset-aware `airDate` only if convenient.

### The arrays are the UNWATCHED set — proven

Show `86813` (Brilliant Minds): `totalEpisodes: 33`, `watchedEpisodes: 1`, and the batch lists
exactly **32** episodes. 33 − 1 = 32. The listed episodes run from s01e02 through s02e20, i.e. the
single watched episode is **s01e01**.

This is the key to the whole import: watched state is recoverable by **inversion**, so only one show
in the entire dataset needs the heuristic.

---

## 5. Watched-state backfill algorithm

User's rule: *"consider everything older than first unwatched episode — watched, excluding specials.
I could manually review if watched count won't match."*

Measured distribution of the 78 `watching.json` shows:

| Case | Count | Reconstruction |
|---|---|---|
| `watched == total` (fully watched) | **33** | unambiguous — all regular episodes watched |
| `watched == 0` | **31** | unambiguous — nothing watched |
| partially watched | **14** | 13 have an exact unwatched list; **1** needs the heuristic |

Algorithm, per show, in order. **Specials are never auto-marked in any branch.**

1. **Explicit list exists** → unwatched = staging list ∩ TVmaze catalog (match on
   `season_number, episode_number`; ignore entries absent from the catalog). Mark every regular
   episode sorting **before** `min(unwatched)` as watched. Set `needs_review = 1` if the resulting
   count ≠ `watchedEpisodes`.
2. **No list, `watchedEpisodes > 0`** → mark the first N regular episodes in absolute
   (season, episode) order as watched. Set `needs_review = 1` unconditionally.
3. **`watchedEpisodes == totalEpisodes`** → all regular episodes watched. `needs_review = 0`.
4. **`watchedEpisodes == 0`** → nothing watched. `needs_review = 0`.

Edge case that must not crash: at least one show has `totalEpisodes: 0` and `watchedEpisodes: 0`.
It falls into case 4 and must end up with `total_regular = 0`, `auto_finish_eligible = 0` (SPEC R7).

---

## 6. IMDb datasets — what they actually contain

### `title.basics.tsv`
Header: `tconst  titleType  primaryTitle  originalTitle  isAdult  startYear  endYear  runtimeMinutes  genres`

**Contains every episode as its own row** with `titleType = tvEpisode`, a real `primaryTitle`,
`startYear`, and `runtimeMinutes`:
```
tt2089051  tvEpisode  The National Anthem        0  2011  \N  44  Crime,Drama,Mystery
tt2089050  tvEpisode  The Entire History of You  0  2011  \N  49  Crime,Drama,Mystery
tt20247352 tvEpisode  Joan Is Awful              0  2023  \N  58  Crime,Drama,Mystery
tt30127325 tvEpisode  Common People              0  2025  \N  56  Crime,Drama,Mystery
```
Parent rows: `tt2085059 tvSeries Black Mirror 0 2011 \N 60 ...`,
`tt26448526 tvSeries Brilliant Minds 0 2024 2026 \N Drama`.

### `title.episode.tsv`
Header: `tconst  parentTconst  seasonNumber  episodeNumber`
```
tt2089051  tt2085059  1  1     The National Anthem, S1E1
tt30127325 tt2085059  7  1     Common People, S7E1
```

### Tri-source agreement — a free validator
`tt26448526` (Brilliant Minds) has exactly **33** episode rows in `title.episode.tsv`, matching both
the export's `totalEpisodes: 33` and TVmaze's 13 + 20 split. IMDb, the export, and TVmaze already
agree.

**Use this.** Compare `myshows totalEpisodes` vs IMDb episode-row count vs TVmaze catalog count for
every show during staging. Any disagreement is a `v_staging_issues` row for the user to review. It
turns a one-way import into a verifiable three-way reconciliation.

### Genuine limitations
- **No precise air date.** `startYear` is year-granularity (`2011`, never `2011-12-04`). Some
  `tvEpisode` rows have `\N` even for `startYear`. **Day/month precision comes only from TVmaze.**
- **Episode `genres` are inherited from the parent show** — every Black Mirror episode row says
  `Crime,Drama,Mystery`. Never treat them as per-episode data.
- **`\N` season/episode numbers exist** in `title.episode.tsv` (31 sampled rows, mostly legacy or
  odd episodes). Map to **unknown**, never to `0`.
- `\N` is the null marker throughout, not empty string.

### Import order matters
`title.basics` mixes shows and episodes. Filtering it to `tvSeries`/`tvMiniSeries` **would silently
drop every episode row** — this was a real bug in the first draft of the plan. The load is:
1. `title.basics` where `titleType IN (tvSeries, tvMiniSeries)` → `imdb_title`
2. `title.episode.tsv` → `imdb_episode` (mapping only, 4 cheap columns)
3. `title.basics` where `titleType = tvEpisode` → join-filter to parents already in `imdb_title`,
   then `UPDATE imdb_episode` with title/year/runtime
4. `title.akas.tsv` → `imdb_akas` (show-level localized titles, filtered to tracked ids)

**Why titles come before episodes here, not after.** An earlier draft of this section listed
`title.episode` first, assuming one big transaction with `PRAGMA defer_foreign_keys=ON` so the
`imdb_episode.parent_tconst → imdb_title` FK could be checked at the end. That is impossible under
ADR-028: the lease renews on a *separate connection*, and a write transaction held open across the
whole load locks that renewal out (`database is locked`) — and ADR-028 explicitly rejects holding a
write transaction open for the whole run anyway. So the load runs in **short batched transactions**
(commit → renew lease → reopen), and a FK cannot be deferred across a commit. `imdb_title` must
therefore be populated before any `imdb_episode` row is inserted. The "don't filter basics to tvSeries
first" warning still holds — episode *detail* comes from the `tvEpisode` rows in step 3, never from the
`tvSeries` filter.

Step 3 is the expensive one. Use raw parameterized `Microsoft.Data.Sqlite` batches — EF Core's
`ChangeTracker` is far too slow at this volume.

---

## 7. Corrections on the record

Both of these were asserted confidently during planning and were **wrong**. They are kept here so
nobody re-introduces them.

1. ❌ *"`show.id` in the exports is a TVmaze id."*
   ✅ It is a **myshows.me internal id**. See §3. The `imageInfo.source: "tvmaze"` field is what made
   this look true.

2. ❌ *"IMDb datasets carry no episode titles or air dates."*
   ✅ They **do** carry episode titles, season/episode numbering, per-episode runtime, and a stable
   per-episode `tconst`. Only **precise air date** is genuinely absent. See §6. This correction added
   `episode.imdb_id` to the schema and made the `Enrichment` job substantially more capable.

3. ❌ *"Auto-finish fires when `status NOT IN ('Running Series', 'Returning Series')`."*
   ✅ Those two strings are **myshows.me** vocabulary, not TVmaze's. TVmaze calls an airing show
   `"Running"` (see §8). The predicate as written would match every airing show and mark it finished
   the moment its last known episode aired. Fixed by SPEC R12: raw provider strings are never
   compared in code — they map to the `ShowStatus` enum through one table, and an unmapped string
   fails closed to `Unknown`, which is never `Ended`.

**Lesson: verify against the files before asserting.** Errors 1 and 2 would have caused real rework —
the first would have broken id resolution entirely, the second would have made `Enrichment` useless.
Error 3 is worse than either: it fails silently, produces plausible results, and quietly corrupts the
one thing the app exists to record.

---

## 8. TVmaze payloads — the base model

Captured from real API responses for **Black Mirror** (`/shows/305`, `/shows/305/episodes`) on
2026-09-28. These are the shape the schema is designed around (SPEC P1). Do not re-derive them.

### Show payload — abbreviated
```json
{"id":305,"name":"Black Mirror","type":"Scripted","language":"English",
 "genres":["Drama","Science-Fiction","Thriller"],
 "status":"Running","runtime":null,"averageRuntime":62,
 "premiered":"2011-12-04","ended":null,
 "officialSite":"https://www.netflix.com/title/70264888",
 "schedule":{"time":"","days":[]},"rating":{"average":8.4},"weight":100,
 "network":null,"webChannel":{"id":1,"name":"Netflix","country":null},
 "externals":{"tvrage":30348,"thetvdb":253463,"imdb":"tt2085059"},
 "image":{"medium":".../1411764.jpg","original":".../1411764.jpg"},
 "summary":"<p>Over the last ten years, technology has transformed...</p>",
 "updated":1767985660,
 "_links":{"self":{"href":".../shows/305"},
           "previousepisode":{"href":".../episodes/3172697","name":"USS Callister: Into Infinity"}}}
```

### Episode payload — first of the list
```json
{"id":30902,"name":"The National Anthem","season":1,"number":1,"type":"regular",
 "airdate":"2011-12-04","airtime":"21:00","airstamp":"2011-12-04T21:00:00+00:00",
 "runtime":60,"rating":{"average":7.8},
 "image":{"medium":".../1008339.jpg","original":".../1008339.jpg"},
 "summary":"<p>Prime Minister Michael Callow faces a huge and shocking dilemma...</p>",
 "_links":{"self":{"href":".../episodes/30902"},"show":{"href":".../shows/305"}}}
```

### Facts that drive schema and code

1. **`id: 305` vs myshows `id: 22410` for the same show.** The two id spaces never overlap and there
   is no arithmetic between them. This is the strongest confirmation of §3 and the reason
   `myshows_id` is confined to `staging_*` (ADR-019). Any code that treats one as the other is wrong.
2. **`status: "Running"`** for a show still airing on Netflix. The full observed vocabulary is
   enumerated in `tvmaze_status_seen` (SPEC R12); code compares the enum, never the string.
3. **`runtime: null` while `averageRuntime: 62`.** Runtime is *sometimes absent at show level and
   present as an average*. Store both; fall back `runtime ?? averageRuntime` at display time, not at
   map time — collapsing them loses the distinction between "60 min per episode" and "62 min on
   average".
4. **`premiered: "2011-12-04"` is a real ISO date**, not a year. This is why `tracked_show.premiered`
   is a date and why the export's year-only values are an upgrade, not a format change.
5. **`externals.imdb` is present here** but is not guaranteed. It is a *confirmation* source for an
   id already obtained via `/lookup?imdb=`, and a fallback when the scrape→lookup chain fails. Never
   assume non-null.
6. **`summary` contains raw HTML** (`<p>`, `<b>`, `<i>`). Store as-is in the source table; strip at
   render time. Stripping at map time destroys information and cannot be undone.
7. **`updated: 1767985660` is a Unix timestamp in seconds.** Store as the integer TVmaze gives;
   convert at display.
8. **`_links.previousepisode.href` carries the episode id and a `name`** — a cheap way to learn the
   latest episode without fetching the full episode list. Useful for the status-recheck job.
9. **`network: null` with `webChannel` populated.** Streaming shows put the broadcaster in
   `webChannel`; broadcast shows put it in `network`. Read both, store both, never assume either.
10. **`schedule.time`/`days` are empty strings/arrays** for a streaming drop. Absent schedule is
    normal, not an error — do not treat it as `needs_review`.
11. **Episode `type` is `"regular"` for normal episodes; specials come back as
    `"significant_special"` or `"insignificant_special"`** (observed 2026-10-02, ADR-034). The
    vocabulary maps these to the `EpisodeType` enum with `Unknown` for anything else (SPEC R12).
    The significant/insignificant split is what R4/R5/R7 count on: a significant special counts toward
    progress like a regular, an insignificant one is excluded — so a wrong mapping changes watched
    progress and the auto-finish result.
12. **`airstamp` carries a UTC offset** (`+00:00` here, others vary). `airdate` is the date the user
    sees in the show's locale; `airstamp` is the instant. Store `airstamp` as the UTC instant and
    `airdate` as the text date — do not derive one from the other, since the offset can move a date
    across midnight.
13. **Episode list is a JSON array with no envelope**, so `Content-Type` handling and
    deserialization must expect a root array. `_links.show.href` is present per episode, which makes
    the flat `/shows/{id}/episodes` endpoint safe to attribute rows to the right show.
14. **`/search/shows` returns a score envelope, not flat shows** (verified live 2026-09-29, LOG step
    3.1): the array elements are `{ "score": <float>, "show": {…} }`, so the show object is nested under
    a `show` property. The **discovery** endpoints (`/newshows`, `/upcoming`) and the single-show endpoint
    return **flat** show objects with no wrapper. Two array shapes therefore reach the same list-mapping
    code; the provider unwraps `show` when present and maps the element directly otherwise. Mapping a
    search wrapper as a flat show throws (the wrapper has no `id`) — this was the live-probe bug the
    step-3.1 fixtures initially hid.
15. **`/shows/{id}/episodes` omits specials unless `?specials=1` is passed** (observed 2026-09-30). The
    plain endpoint returns only regular episodes, so a show's specials are invisible to the mapper and
    `has_unwatched_specials` would always be false. The mapper's fetch path requests `?specials=1` so
    specials exist as `episode` rows (SPEC R4 excludes them from progress; the import backfill never
    marks them watched — §5). The cache keys on the full relative URL, so `…/episodes?specials=1` is a
    distinct entry from `…/episodes`.
16. **`/shows/{id}/akas` keys on country, not language** (verified live 2026-09-30 through the proxy,
    `shows/59985/akas`): the array elements are `{ "name": …, "country": { "name": …, "code": "RU",
    "timezone": … } }` — there is **no `language` field**. Show 59985 returns three `RU` akas
    (`Начало`, `Сначала`, `Перезагрузка`) and two `CN`. So "several titles for one language" is really
    "several akas for one **country code**"; the pick is the first (lowest array order) for the target
    country. This is why localized titles can come from TVmaze directly, without the IMDb dataset (§6) —
    see IMPLEMENTATION_PLAN step 7.5.
17. **`/schedule/full` is forward-only — it contains no past episodes** (verified 2026-10-06 by streaming
    the saved 14 MB `schedule-full.json` and taking min/max of every `airdate`: **7,338 dated episodes,
    min `2026-09-28`, max `2028-03-05`**, where 2026-09-28 is the day the sample was captured). The
    horizon is therefore ~17 months **ahead** and essentially **zero behind**. Consequences: a look-back
    window of `today - N` has nothing below `today` to match, so the New/Airing buckets fill with
    far-future premieres rather than recent ones; and a show whose season finished is not filtered out of
    the response, it is **absent** from it. No query can recover a recently-aired episode from a live
    fetch — only from a snapshot captured while that episode was still in the future, which is why the
    Discovery source retains dated raw responses (`ARCHITECTURE.md` §4.1). Because the forward horizon is
    so long, **one** capture ~14 days old already spans a month-scale window, so daily retention is
    overhead rather than necessity.

---

## 9. Network reachability from the development machine

Verified 2026-09-28/29 on the dev PC. These are observations about third parties, not requirements —
SPEC R14 owns what the software must do about them.

| Host | Direct | Via `main-pc:10801` |
|---|---|---|
| `api.tvmaze.com` | **unreachable** | 200, ~0.5 s |
| `myshows.me` | 200, ~0.5 s | 200, ~1.1 s |
| `datasets.imdbws.com` | 200 | 200 |

**TVmaze is blocked; the other two are not.** This is the fact that makes proxy config per-service
rather than global.

Observations, each with the evidence that produced it:

1. **The block is not DNS and not TCP.** `api.tvmaze.com` resolves (94.16.110.194) and TCP :443
   connects in milliseconds. The request then dies: `Invoke-WebRequest` times out at ~12 s, and
   `HttpClient` reports `SocketException: An existing connection was forcibly closed by the remote
   host` at ~19 s. Source: direct requests from the dev machine, 2026-09-28.
   *Consequence:* code cannot detect this at connect time. It surfaces as a timeout or a reset
   mid-request, which is why R14 forbids treating it as "no data".
2. **The proxy returns genuine TVmaze data, not a captive portal.** `GET /shows/305` through the
   proxy returns `id=305 name="Black Mirror" status="Running" premiered=2011-12-04`, matching §8.
   `GET /status` (a path TVmaze has no route for) returns TVmaze's own JSON error body
   `{"name":"Not Found",…}`, which only the real service would produce. Source: `curl` via proxy,
   2026-09-28.
3. **The proxy accepts both HTTP CONNECT and SOCKS on one port.** `curl` succeeded with
   `http://`, `socks5://`, and `socks4://` against `main-pc:10801`. Source: 2026-09-29.
4. **`HttpClientHandler` on .NET 8 accepts `http`, `https`, `socks4`, `socks4a`, `socks5` for a proxy
   and rejects anything else** with `NotSupportedException: Only the 'http', 'https', 'socks4',
   'socks4a' and 'socks5' schemes are allowed for proxies`. A bare `host:port` is accepted and treated
   as HTTP. Source: probe project on SDK 8.0.425, 2026-09-29.
   *Consequence:* SOCKS needs no extra NuGet package. Do not add a SOCKS handler dependency on the
   assumption that it does.
5. **Ambient proxy settings do not help here.** WinHTTP reports "Direct access", the registry has
   `ProxyEnable = 0`, and no `HTTP_PROXY`/`HTTPS_PROXY`/`ALL_PROXY` is set. `WebRequest.DefaultWebProxy`
   resolves to empty and reports `IsBypassed(tvmaze) = True`, so `UseProxy = true` with no explicit
   proxy behaves exactly like `UseProxy = false` — both time out. Source: probe project, 2026-09-29.
   *Consequence:* the proxy must be configured explicitly in app settings. Relying on the machine's
   settings would silently do nothing on this machine.
6. **A dead proxy port fails in one of two ways, and only one of them is informative.**
   - Port closed/refused (`http://127.0.0.1:1`) → `SocketException: No connection could be made
     because the target machine actively refused it`, at **~2 s** — well inside any useful timeout.
   - Port filtered/unroutable (`http://main-pc:10899`) → the client waits for the **entire**
     `HttpClient.Timeout` (measured: `TaskCanceledException` at 6015 ms against a 6 s timeout,
     12011 ms against 12 s) and reports nothing about the cause.
   Source: `draft/probe-socks.ps1` on SDK 8.0.425, 2026-09-29.
   *Consequence:* the refused case needs no help, so a short connect timeout exists for the filtered
   case — it is what separates "proxy is down" from "provider is slow". Without it, R14's requirement
   to distinguish the two is unimplementable. Note also that `TaskCanceledException` is what a *blocked
   provider* produces too (§9.1, same exception at the same 12 s), so the exception type alone never
   identifies the culprit: the log must state which route was in use.

**Superseded:** none yet. If TVmaze becomes reachable directly, add a new dated entry and mark this one
superseded rather than deleting it — the per-service proxy config exists because of this observation,
and a future reader needs to know why.
