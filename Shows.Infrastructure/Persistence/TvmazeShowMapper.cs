using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shows.Application;
using Shows.Domain;
using Shows.Infrastructure.Persistence.Sources;
using Shows.Infrastructure.Persistence.Vocabulary;
using Shows.Infrastructure.Tvmaze;

namespace Shows.Infrastructure.Persistence;

/// <summary>
/// The single enrichment mapper — the only code path that writes mapped rows (SPEC R11, ADR-023).
/// </summary>
/// <remarks>
/// <para>
/// Order of work: write the source rows verbatim, translate vocabulary through the one dictionary
/// (SPEC R12), upsert the mapped rows on natural keys, then recompute the cached columns via the
/// Domain function. It never creates a <c>tracked_show</c> row (the caller does), never writes a user
/// table, and never regenerates a surrogate id — <c>episode_watch.episode_id</c> and
/// <c>ui_season_state.season_id</c> point at those ids, so delete-then-insert would silently orphan
/// the user's watch marks while every row count still looked correct (SCHEMA.md §2a).
/// </para>
/// <para>
/// Idempotency is a measured property, not an aspiration: a second map of an unchanged payload
/// returns <see cref="MapResult.Unchanged"/> and writes nothing, because every field assignment is
/// guarded by a comparison. That is what lets <c>job_run.items_changed</c> report 0 honestly.
/// </para>
/// </remarks>
public sealed class TvmazeShowMapper(
    ShowsDbContext context,
    IIdGenerator idGenerator,
    ILogger<TvmazeShowMapper> logger,
    ITvmazeShowSource? source = null) : IShowMapper
{
    public async Task<MapResult> MapShowAsync(int tvmazeId, CancellationToken cancellationToken)
    {
        // The int path needs the fetch seam, which the TVmaze provider implements in Phase 3. The DTO
        // overload is the one callers with an already-fetched payload use, and the one step 1.5 tests.
        if (source is null)
        {
            throw new InvalidOperationException(
                "MapShowAsync(int) needs an ITvmazeShowSource (the TVmaze provider, Phase 3). Callers "
                + "that already hold the payload must use the TvmazePayload overload.");
        }

        var payload = await source.FetchAsync(tvmazeId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"TVmaze has no show {tvmazeId}; nothing was mapped.");

        return await MapShowAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Mints a minimal <c>tvmaze_show</c> shell so a caller can insert a <c>tracked_show</c> whose NOT
    /// NULL <c>tvmaze_show_id</c> FK points at it (SCHEMA §2). The mapper is the sole writer of source
    /// rows (SPEC R11), so the shell is created here — not by the repository or the use case — and
    /// <see cref="MapShowAsync(TvmazePayload, CancellationToken)"/> overwrites it with the real payload
    /// on the first map. No-op when the row already exists. Writes nothing to the mapped or user tables.
    /// </summary>
    public async Task EnsureSourceRowAsync(int tvmazeId, CancellationToken cancellationToken)
    {
        var exists = await context.TvmazeShows
            .AnyAsync(s => s.TvmazeId == tvmazeId, cancellationToken).ConfigureAwait(false);
        if (exists)
        {
            return;
        }

        context.TvmazeShows.Add(new TvmazeShow
        {
            TvmazeId = tvmazeId,
            StatusRaw = string.Empty,
            RawJson = "{}",
            FetchedAt = DateTimeOffset.UtcNow,
        });
    }

    /// <summary>
    /// The <c>provider_updated</c> (unix seconds) the mapper last stored for a show, or <c>null</c> when no
    /// source row exists or it was never stamped. This is the <b>only</b> read of a source column exposed to
    /// a job: source-table containment (SPEC R11, step 8.1) forbids a job reaching into <c>tvmaze_show</c>
    /// itself, so the <c>RefreshUpdated</c> job asks the mapper for the stored timestamp instead of querying
    /// the table. Read-only — it writes nothing.
    /// </summary>
    public async Task<long?> GetProviderUpdatedAsync(int tvmazeId, CancellationToken cancellationToken)
    {
        var src = await context.TvmazeShows
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.TvmazeId == tvmazeId, cancellationToken)
            .ConfigureAwait(false);
        return src?.ProviderUpdated;
    }

    /// <summary>
    /// Maps an already-fetched payload. This is the overload a job uses — it owns fetching so
    /// <c>http_cache</c> and throttling apply (step 2.6), and hands the verbatim rows here.
    /// </summary>
    public async Task<MapResult> MapShowAsync(TvmazePayload payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        await WriteSourceRowsAsync(payload, cancellationToken).ConfigureAwait(false);

        // The mapper does not create tracked_show (SPEC P2/step 1.5): refreshing a source row is not
        // the same as tracking a show. If no tracked_show exists, the source rows are still written and
        // the map is a no-op on the mapped layer.
        var show = await context.TrackedShows
            .FirstOrDefaultAsync(s => s.TvmazeId == payload.Show.TvmazeId, cancellationToken)
            .ConfigureAwait(false);

        if (show is null)
        {
            return MapResult.Unchanged;
        }

        var showChanged = ApplyShowFields(show, payload.Show);

        // Keep the shadow FK to the source row equal to the readable copy (SPEC P3/P4 — a mapped row
        // cannot exist without its source row).
        context.Entry(show).Property<int>("tvmaze_show_id").CurrentValue = payload.Show.TvmazeId;

        var (added, updated) = await UpsertSeasonsAndEpisodesAsync(show, payload, cancellationToken)
            .ConfigureAwait(false);

        await SyncGenresAsync(show, payload.Show, cancellationToken).ConfigureAwait(false);

        // Flush the mapped rows before recomputing progress: the cached columns are a query over
        // episode, and the episodes just added are not visible to a query until they are persisted.
        // Without this, the first map computes progress against an empty episode set and the second
        // map sees the now-persisted episodes and reports a spurious change — breaking idempotency.
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Recompute the cached columns over every episode of the show, watched state included. The
        // mapper READS episode_watch to do this and never writes it (SPEC P4).
        showChanged |= await RecomputeCachedColumnsAsync(show, cancellationToken).ConfigureAwait(false);

        if (showChanged)
        {
            show.MappedAt = DateTimeOffset.UtcNow;
        }

        return new MapResult(showChanged, added, updated);
    }

    // ---- source rows, verbatim (SPEC P3) ----

    /// <summary>
    /// Write a show's <c>/akas</c> source rows verbatim and map the target-country aka into
    /// <c>tracked_show.title_localized</c> (step 7.5). Returns whether the mapped title changed — the
    /// signal the <c>--akas</c> job counts as <c>items_changed</c>; source-row churn is not a change.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The source rows are a fully rebuildable child set keyed on <c>(show, country, name)</c> with no
    /// surrogate id any user row points at, so the mapper refreshes them delete-then-insert per show —
    /// unlike the mapped tables, where that would orphan watch marks (SCHEMA §2a). <c>aka_order</c> is the
    /// API array index, the order to trust.
    /// </para>
    /// <para>
    /// The pick is the lowest <c>aka_order</c> whose country code equals <paramref name="targetCountry"/>
    /// (exact, ordinal). No match leaves <c>title_localized</c> <b>null</b> — never a wrong-country guess
    /// (step 7.5). The target country is passed in by the caller (configuration), not hard-coded here.
    /// </para>
    /// </remarks>
    public async Task<AkaMapResult> MapAkasAsync(
        int tvmazeId, IReadOnlyList<ShowAka> akas, string targetCountry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(akas);

        // Source rows first (SPEC R11 — the mapper is the sole writer), refreshed verbatim.
        var existing = await context.TvmazeAkas
            .Where(a => a.TvmazeShowId == tvmazeId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (existing.Count > 0)
        {
            context.TvmazeAkas.RemoveRange(existing);
        }

        var fetchedAt = DateTimeOffset.UtcNow;
        for (var i = 0; i < akas.Count; i++)
        {
            var aka = akas[i];
            context.TvmazeAkas.Add(new TvmazeAka
            {
                TvmazeShowId = tvmazeId,
                CountryCode = aka.CountryCode,
                CountryName = aka.CountryName,
                Name = aka.Name,
                AkaOrder = i,
                FetchedAt = fetchedAt,
            });
        }

        // The mapped effect: the lowest-order aka for the target country, or null. The mapper does not
        // create tracked_show (SPEC P2) — refreshing a source row is not tracking a show.
        var show = await context.TrackedShows
            .FirstOrDefaultAsync(s => s.TvmazeId == tvmazeId, cancellationToken).ConfigureAwait(false);
        if (show is null)
        {
            return AkaMapResult.Unchanged;
        }

        var picked = akas
            .Where(a => string.Equals(a.CountryCode, targetCountry, StringComparison.Ordinal))
            .Select((a, index) => (a.Name, index))
            .OrderBy(x => x.index)
            .FirstOrDefault()
            .Name;

        var changed = Set(show, nameof(show.TitleLocalized), () => show.TitleLocalized,
            v => show.TitleLocalized = v, picked);
        return new AkaMapResult(changed, picked);
    }

    /// <summary>
    /// The <c>Enrichment --imdb</c> map (step 7.3, ADR-031): TVmaze is authoritative, IMDb only completes it.
    /// For one show, fills episode gaps IMDb can supply (<c>title</c>, <c>runtime_minutes</c>, and the
    /// <c>imdb_id</c> link) <b>only where TVmaze left them null</b>, then wipes and recomputes the show's
    /// <c>episode_mismatch</c> rows so the details page can show every disagreement. It never overwrites a
    /// TVmaze value, never fabricates an air date (IMDb has only a year) or a description (IMDb has none),
    /// and never inserts an IMDb-only episode into <c>episode</c> — those are flagged, not merged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reconciliation is on the exact <c>(season, episode)</c> key. Rows with a null season or episode on
    /// either side cannot be keyed and are left alone — neither matched nor flagged. A show whose
    /// <c>imdb_id</c> has no <c>imdb_episode</c> rows at all is skipped entirely: with no IMDb episode data
    /// there is nothing to reconcile, and flagging every episode as <c>tvmaze_only</c> would be noise.
    /// </para>
    /// <para>
    /// <b>Idempotence:</b> <see cref="EnrichResult.Changed"/> counts only the episode field writes (a gap
    /// filled or the imdb link set), so a second run over unchanged data reports 0. The mismatch rows are
    /// wiped and re-inserted every run — that churn is not a change, exactly as source-row churn is not a
    /// change in <see cref="MapAkasAsync"/>.
    /// </para>
    /// </remarks>
    public async Task<EnrichResult> EnrichFromImdbAsync(long showId, CancellationToken cancellationToken)
    {
        var show = await context.TrackedShows
            .FirstOrDefaultAsync(s => s.Id == showId, cancellationToken).ConfigureAwait(false);
        if (show is null || show.ImdbId is null)
        {
            return EnrichResult.Unchanged;
        }

        var imdbEpisodes = await context.ImdbEpisodes
            .Where(e => e.ParentTconst == show.ImdbId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (imdbEpisodes.Count == 0)
        {
            // IMDb indexes no episodes for this show — nothing to fill or reconcile. Skip rather than
            // flag every TVmaze episode as tvmaze_only (ADR-031: IMDb completes TVmaze only where it has data).
            return EnrichResult.Unchanged;
        }

        var episodes = await context.Episodes
            .Where(e => e.ShowId == showId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // Key both sides on (season, episode); rows missing either number cannot be keyed and are skipped.
        var episodeByKey = new Dictionary<(int Season, int Episode), Episode>();
        foreach (var e in episodes)
        {
            if (e.EpisodeNumber is not null)
            {
                episodeByKey[(e.SeasonNumber, e.EpisodeNumber.Value)] = e;
            }
        }

        var imdbByKey = new Dictionary<(int Season, int Episode), ImdbEpisode>();
        foreach (var e in imdbEpisodes)
        {
            if (e.SeasonNumber is not null && e.EpisodeNumber is not null)
            {
                imdbByKey[(e.SeasonNumber.Value, e.EpisodeNumber.Value)] = e;
            }
        }

        // Wipe the show's mismatches — the wipe-and-recompute is the idempotence mechanism (SCHEMA §4a).
        var stale = await context.EpisodeMismatches
            .Where(m => m.ShowId == showId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (stale.Count > 0)
        {
            context.EpisodeMismatches.RemoveRange(stale);
        }

        var changed = false;
        var mismatches = 0;

        void AddMismatch(long? episodeId, string kind, string? tvmazeValue, string? imdbValue,
            int? season, int? episode)
        {
            context.EpisodeMismatches.Add(new EpisodeMismatch
            {
                Id = idGenerator.NextId(),
                ShowId = showId,
                EpisodeId = episodeId,
                Kind = kind,
                TvmazeValue = tvmazeValue,
                ImdbValue = imdbValue,
                SeasonNumber = season,
                EpisodeNumber = episode,
            });
            mismatches++;
        }

        foreach (var (key, imdb) in imdbByKey)
        {
            if (!episodeByKey.TryGetValue(key, out var episode))
            {
                // IMDb knows an episode TVmaze never created — flag it, do NOT insert it into episode.
                AddMismatch(null, "imdb_only", tvmazeValue: null, imdb.PrimaryTitle, key.Season, key.Episode);
                continue;
            }

            // Link the episode to its IMDb id (IMDb completes TVmaze). Idempotent: set once, then unchanged.
            changed |= Set(episode, nameof(episode.ImdbId), () => episode.ImdbId,
                v => episode.ImdbId = v, imdb.Tconst);

            // Title: fill a TVmaze gap, or flag a disagreement — never overwrite a TVmaze title.
            if (episode.Title is null && imdb.PrimaryTitle is not null)
            {
                episode.Title = imdb.PrimaryTitle;
                episode.TitleSource = "imdb";
                changed = true;
            }
            else if (episode.Title is not null && imdb.PrimaryTitle is not null
                     && !string.Equals(episode.Title, imdb.PrimaryTitle, StringComparison.Ordinal))
            {
                AddMismatch(episode.Id, "title", episode.Title, imdb.PrimaryTitle, key.Season, key.Episode);
            }

            // Runtime: same rule.
            if (episode.RuntimeMinutes is null && imdb.RuntimeMinutes is not null)
            {
                episode.RuntimeMinutes = imdb.RuntimeMinutes;
                changed = true;
            }
            else if (episode.RuntimeMinutes is not null && imdb.RuntimeMinutes is not null
                     && episode.RuntimeMinutes.Value != imdb.RuntimeMinutes.Value)
            {
                AddMismatch(episode.Id, "runtime",
                    episode.RuntimeMinutes.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    imdb.RuntimeMinutes.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    key.Season, key.Episode);
            }

            // Air-date gap: TVmaze has no date. IMDb offers only a year — recorded as the partial signal,
            // never written into air_date (a year is not a date; fabricating a day/month is forbidden).
            if (episode.AirDate is null)
            {
                AddMismatch(episode.Id, "air_date_gap", tvmazeValue: null,
                    imdb.StartYear?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    key.Season, key.Episode);
            }
        }

        // TVmaze-only episodes: informational (TVmaze wins by definition), but part of the set diff the
        // user asked to see.
        foreach (var (key, episode) in episodeByKey)
        {
            if (!imdbByKey.ContainsKey(key))
            {
                AddMismatch(episode.Id, "tvmaze_only", episode.Title, imdbValue: null, key.Season, key.Episode);
            }
        }

        return new EnrichResult(changed, mismatches);
    }

    /// <summary>
    /// Re-derive <c>tracked_show.country</c> (and the <c>tvmaze_show.country_*</c> columns it comes from)
    /// out of the payloads <b>already on disk</b>, with no network call. Returns the number of shows whose
    /// <b>mapped</b> country changed — the count a backfill run reports as <c>items_changed</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what <c>tvmaze_show.raw_json</c> is for (SCHEMA §3): it is "the difference between we can
    /// re-map after a bug and we must re-crawl 547 shows and hope the data is the same". When a derived
    /// mapped column is added — <c>country</c> today (improvement 21) — every existing row is stale, and
    /// re-fetching would both cost one request per tracked show and silently re-base every other field to
    /// whatever TVmaze says <em>today</em>. The stored payload answers the new question without either.
    /// </para>
    /// <para>
    /// <b>Both layers are refreshed.</b> The source columns are new too, so they are empty for every row
    /// written before this change; they are filled from <c>raw_json</c> <em>verbatim</em> (nested country
    /// only, no fallback — SPEC R11 keeps the source table a copy of the payload). The mapped column then
    /// gets the display rule applied to those verbatim values plus the language. Source-column churn does
    /// not count as a change, exactly as in <see cref="MapAkasAsync"/>.
    /// </para>
    /// <para>
    /// <b>Scope is deliberately narrow:</b> only the country fields. Episodes, genres and the cached
    /// progress columns are left exactly as they are — the payload already produced them once, and a
    /// narrow re-map cannot disturb a user row (SPEC P4).
    /// </para>
    /// <para>
    /// <b>Idempotence:</b> every write goes through <see cref="Set"/>, so a second pass over the same
    /// payloads returns <c>0</c> and writes nothing.
    /// </para>
    /// </remarks>
    public async Task<int> RemapCountryFromStoredPayloadAsync(
        IReadOnlyList<long> trackedShowIds, CancellationToken cancellationToken)
    {
        if (trackedShowIds.Count == 0)
        {
            return 0;
        }

        var shows = await context.TrackedShows
            .Where(s => trackedShowIds.Contains(s.Id))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (shows.Count == 0)
        {
            return 0;
        }

        // One source-row read for the batch, keyed by tvmaze_id. A tracked show always has its source row
        // (SPEC P4); a shell minted before its first map carries raw_json = "{}" and yields no country,
        // which is the honest answer rather than a guess.
        var tvmazeIds = shows.Select(s => s.TvmazeId).ToList();
        var sources = (await context.TvmazeShows
            .Where(s => tvmazeIds.Contains(s.TvmazeId))
            .ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(s => s.TvmazeId);

        var changedShows = 0;
        foreach (var show in shows)
        {
            if (!sources.TryGetValue(show.TvmazeId, out var src))
            {
                // No source row at all: nothing to derive from. Loud, because it means a tracked show has
                // never been mapped (SPEC P4) — not a condition to paper over with a null.
                logger.LogWarning(
                    "Country re-map: tracked show {ShowId} (tvmaze {TvmazeId}) has no source row; skipped.",
                    show.Id, show.TvmazeId);
                continue;
            }

            // Read the nested country out of the stored payload. A payload that cannot be parsed (a
            // truncated capture) means the show is skipped entirely rather than cleared — losing a known
            // country to a parsing gap is the worse error (the same rule ScheduleMerge follows).
            if (TryReadStoredPayloadCountry(src) is not { } stored)
            {
                continue;
            }

            // Source columns: verbatim, no language fallback. Churn here is deliberately not counted as a
            // change — the same discipline as source-row churn in MapAkasAsync.
            var sourceChanged = Set(src, nameof(src.CountryName), () => src.CountryName,
                v => src.CountryName = v, stored.Name);
            sourceChanged |= Set(src, nameof(src.CountryCode), () => src.CountryCode,
                v => src.CountryCode = v, stored.Code);

            // Mapped column: the one display rule over the verbatim values plus the language.
            var mappedChanged = Set(show, nameof(show.Country), () => show.Country, v => show.Country = v,
                CountryRules.Display(src.CountryName, src.CountryCode, src.Language));

            if (mappedChanged)
            {
                show.MappedAt = DateTimeOffset.UtcNow;
                changedShows++;
            }
            else if (sourceChanged)
            {
                // The payload carried a country we had never stored. Worth a line: the mapped value did not
                // move, so it is not a change, but the source row did grow.
                logger.LogDebug(
                    "Country re-map: tvmaze {TvmazeId} source columns filled from raw_json.", src.TvmazeId);
            }
        }

        return changedShows;
    }

    // The broadcaster country out of a stored payload, or <b>null</b> when the payload cannot be read at
    // all. raw_json is whatever TVmaze returned at capture time, and a shell row legitimately holds "{}" —
    // that parses cleanly to "no country", which is a real answer. Unparseable JSON is not an answer, so it
    // is reported as null and the caller leaves every existing value untouched; the row's raw_json is never
    // rewritten, so a failed re-read cannot destroy the data it failed to read.
    private TvmazeJsonMapper.BroadcasterCountry? TryReadStoredPayloadCountry(TvmazeShow src)
    {
        if (string.IsNullOrWhiteSpace(src.RawJson))
        {
            logger.LogWarning(
                "Country re-map: tvmaze {TvmazeId} has no raw_json; existing country kept.", src.TvmazeId);
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(src.RawJson);
            return TvmazeJsonMapper.ReadBroadcasterCountry(doc.RootElement);
        }
        catch (JsonException ex)
        {
            // Not fatal — the mapped and source columns simply stay as they were. Loud, never silent
            // (global rule: a catch must log or rethrow).
            logger.LogWarning(ex,
                "Country re-map: tvmaze {TvmazeId} has an unparseable raw_json; existing country kept.",
                src.TvmazeId);
            return null;
        }
    }

    private async Task WriteSourceRowsAsync(TvmazePayload payload, CancellationToken cancellationToken)
    {
        var src = payload.Show;
        var existing = await context.TvmazeShows
            .FindAsync([src.TvmazeId], cancellationToken).ConfigureAwait(false);

        if (existing is null)
        {
            context.TvmazeShows.Add(src);
        }
        else
        {
            context.Entry(existing).CurrentValues.SetValues(src);
        }

        foreach (var episode in payload.Episodes)
        {
            var existingEpisode = await context.TvmazeEpisodes
                .FindAsync([episode.TvmazeEpisodeId], cancellationToken).ConfigureAwait(false);

            if (existingEpisode is null)
            {
                context.TvmazeEpisodes.Add(episode);
            }
            else
            {
                context.Entry(existingEpisode).CurrentValues.SetValues(episode);
            }
        }

        await RecordStatusSeenAsync(src.StatusRaw, cancellationToken).ConfigureAwait(false);
    }

    // INSERT ... ON CONFLICT: a repeated status increments the counter rather than double-inserting
    // (SCHEMA.md §3). mapped_to is NULL for an unrecognised string — the visible signal that the
    // dictionary needs an edit (SPEC R12).
    private async Task RecordStatusSeenAsync(string statusRaw, CancellationToken cancellationToken)
    {
        var mappedTo = TvmazeVocabulary.MapStatus(statusRaw);
        var mappedColumn = TvmazeVocabulary.IsKnownStatus(statusRaw) ? (int?)mappedTo : null;
        var now = DateTimeOffset.UtcNow;

        // mapped_to is NULL for an unrecognised string — the visible signal that the dictionary needs
        // an edit (SPEC R12). EF's raw-SQL parameter binding has no store mapping for DBNull, so the
        // null is expressed as a sentinel (-1, never a valid ShowStatus) converted back with NULLIF.
        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO tvmaze_status_seen (status_raw, first_seen_at, last_seen_at, occurrences, mapped_to)
            VALUES ({0}, {1}, {2}, 1, NULLIF({3}, -1))
            ON CONFLICT(status_raw) DO UPDATE
              SET last_seen_at = excluded.last_seen_at,
                  occurrences = occurrences + 1,
                  mapped_to = COALESCE(excluded.mapped_to, tvmaze_status_seen.mapped_to);
            """,
            new object[] { statusRaw, now, now, mappedColumn ?? -1 }, cancellationToken);

        if (mappedColumn is null)
        {
            // Fail closed and make it loud: an unmapped status is a dictionary gap, not a silent Unknown.
            logger.LogWarning(
                "Unrecognised TVmaze status {StatusRaw!r} mapped to Unknown (SPEC R12). Add it to "
                + "TvmazeVocabulary.", statusRaw);
        }
    }

    // ---- mapped rows: tracked_show fields ----

    private static bool ApplyShowFields(TrackedShow show, TvmazeShow src)
    {
        var changed = false;

        changed |= Set(show, nameof(show.Title), () => show.Title, v => show.Title = v, src.Name ?? string.Empty);
        changed |= Set(show, nameof(show.ShowType), () => show.ShowType, v => show.ShowType = v, src.TypeRaw);
        changed |= Set(show, nameof(show.Language), () => show.Language, v => show.Language = v, src.Language);
        changed |= Set(show, nameof(show.Status), () => show.Status, v => show.Status = v,
            TvmazeVocabulary.MapStatus(src.StatusRaw));
        changed |= Set(show, nameof(show.RuntimeMinutes), () => show.RuntimeMinutes,
            v => show.RuntimeMinutes = v, src.RuntimeMinutes);
        // runtime and average_runtime stay separate — collapsing them loses the distinction between
        // "60 min per episode" and "62 min on average" (DATA_ANALYSIS.md §8 fact 3).
        changed |= Set(show, nameof(show.AverageRuntimeMinutes), () => show.AverageRuntimeMinutes,
            v => show.AverageRuntimeMinutes = v, src.AverageRuntime);
        changed |= Set(show, nameof(show.PremiereDate), () => show.PremiereDate,
            v => show.PremiereDate = v, ParseDate(src.Premiered));
        changed |= Set(show, nameof(show.EndDate), () => show.EndDate, v => show.EndDate = v, ParseDate(src.Ended));
        changed |= Set(show, nameof(show.OfficialSite), () => show.OfficialSite,
            v => show.OfficialSite = v, src.OfficialSite);
        changed |= Set(show, nameof(show.ScheduleTime), () => show.ScheduleTime,
            v => show.ScheduleTime = v, ParseTime(src.ScheduleTime));
        changed |= Set(show, nameof(show.ScheduleDays), () => show.ScheduleDays,
            v => show.ScheduleDays = v, src.ScheduleDays);
        changed |= Set(show, nameof(show.RatingAverage), () => show.RatingAverage,
            v => show.RatingAverage = v, src.RatingAverage);
        changed |= Set(show, nameof(show.NetworkName), () => show.NetworkName,
            v => show.NetworkName = v, src.NetworkName);
        changed |= Set(show, nameof(show.WebchannelName), () => show.WebchannelName,
            v => show.WebchannelName = v, src.WebchannelName);
        // The country the UI shows and filters on (improvement 21). TVmaze carries a country only inside
        // the broadcaster and a streaming broadcaster usually has none, so CountryRules falls back to the
        // language — a Korean drama on Netflix reads "Korean" instead of nothing. The rule is applied HERE,
        // once, on the mapped copy: the source columns stay verbatim (SPEC R11) and the UI never re-derives.
        changed |= Set(show, nameof(show.Country), () => show.Country,
            v => show.Country = v,
            CountryRules.Display(src.CountryName, src.CountryCode, src.Language));
        changed |= Set(show, nameof(show.ImdbId), () => show.ImdbId, v => show.ImdbId = v, src.ImdbId);
        changed |= Set(show, nameof(show.TheTVDbId), () => show.TheTVDbId, v => show.TheTVDbId = v, src.ThetvdbId);
        changed |= Set(show, nameof(show.PosterUrl), () => show.PosterUrl, v => show.PosterUrl = v, src.ImageOriginal);
        changed |= Set(show, nameof(show.PosterMediumUrl), () => show.PosterMediumUrl,
            v => show.PosterMediumUrl = v, src.ImageMedium);
        // summary HTML is stripped here, at the mapped layer; the raw markup stays in the source table
        // (DATA_ANALYSIS.md §8 fact 6 — stripping is irreversible, so it happens once, on the copy).
        changed |= Set(show, nameof(show.Description), () => show.Description,
            v => show.Description = v, HtmlStripper.Strip(src.SummaryHtml));

        return changed;
    }

    // ---- mapped rows: season + episode upserts (ids immutable — SCHEMA.md §2a) ----

    private async Task<(int Added, int Updated)> UpsertSeasonsAndEpisodesAsync(
        TrackedShow show, TvmazePayload payload, CancellationToken cancellationToken)
    {
        var existingSeasons = await context.Seasons
            .Where(s => s.ShowId == show.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var seasonByNumber = existingSeasons.ToDictionary(s => s.SeasonNumber);

        var existingEpisodes = await context.Episodes
            .Where(e => e.ShowId == show.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var episodeByTvmazeId = existingEpisodes
            .Where(e => e.TvmazeEpisodeId is not null)
            .ToDictionary(e => e.TvmazeEpisodeId!.Value);

        var added = 0;
        var updated = 0;

        foreach (var src in payload.Episodes)
        {
            if (!seasonByNumber.TryGetValue(src.SeasonNumber, out var season))
            {
                season = new Season
                {
                    Id = idGenerator.NextId(),
                    ShowId = show.Id,
                    SeasonNumber = src.SeasonNumber,
                };
                context.Seasons.Add(season);
                seasonByNumber[src.SeasonNumber] = season;
            }

            if (episodeByTvmazeId.TryGetValue(src.TvmazeEpisodeId, out var episode))
            {
                // Existing episode: refresh content only, NEVER touch its Id or its placement
                // (season_id / season_number / episode_number). episode_watch points at the id, and the
                // placement columns are the unique index ix_episode_key — writing them on several rows in
                // one SaveChanges makes EF's update-batching resolver infer an ordering between the rows
                // and report a false circular dependency (user-issues #4). An episode is identified by its
                // stable TvmazeEpisodeId, so its placement is fixed once minted; a provider renumbering is
                // ignored, not applied.
                if (ApplyEpisodeContent(episode, src))
                {
                    updated++;
                }
            }
            else
            {
                episode = new Episode
                {
                    Id = idGenerator.NextId(),
                    ShowId = show.Id,
                    SeasonId = season.Id,
                    TvmazeEpisodeId = src.TvmazeEpisodeId,
                    SeasonNumber = src.SeasonNumber,
                    EpisodeNumber = src.Number,
                    TitleSource = "tvmaze",
                    AirDateSource = "tvmaze",
                };
                ApplyEpisodeContent(episode, src);
                context.Episodes.Add(episode);
                episodeByTvmazeId[src.TvmazeEpisodeId] = episode;
                added++;
            }
        }

        // season.episode_count is a cache of a count over episode, not an authority (SCHEMA.md §2).
        foreach (var season in seasonByNumber.Values)
        {
            var count = episodeByTvmazeId.Values.Count(e => e.SeasonId == season.Id);
            if (season.EpisodeCount != count)
            {
                season.EpisodeCount = count;
            }
        }

        return (added, updated);
    }

    // Refresh an episode's CONTENT only. The placement columns — season_id, season_number,
    // episode_number — are deliberately NOT written here: they form the unique index ix_episode_key, and
    // updating them on several existing rows in one SaveChanges makes EF Core's update-batching dependency
    // resolver order the rows against each other and report a false circular dependency (user-issues #4).
    // An existing episode is identified by its immutable TvmazeEpisodeId, so its placement is fixed when
    // the row is first minted and never re-derived from a later payload. New rows set placement in their
    // object initializer (UpsertSeasonsAndEpisodesAsync); this method is shared by both paths and writes
    // only the provider-supplied content fields.
    private static bool ApplyEpisodeContent(Episode episode, TvmazeEpisode src)
    {
        var changed = false;
        changed |= Set(episode, nameof(episode.Title), () => episode.Title, v => episode.Title = v, src.Name);
        changed |= Set(episode, nameof(episode.EpisodeType), () => episode.EpisodeType,
            v => episode.EpisodeType = v, TvmazeVocabulary.MapEpisodeType(src.TypeRaw));
        changed |= Set(episode, nameof(episode.AirDate), () => episode.AirDate,
            v => episode.AirDate = v, ParseDate(src.Airdate));
        changed |= SetStamp(episode, nameof(episode.AirStamp), () => episode.AirStamp,
            v => episode.AirStamp = v, ParseStamp(src.Airstamp));
        changed |= Set(episode, nameof(episode.RuntimeMinutes), () => episode.RuntimeMinutes,
            v => episode.RuntimeMinutes = v, src.RuntimeMinutes);
        changed |= Set(episode, nameof(episode.RatingAverage), () => episode.RatingAverage,
            v => episode.RatingAverage = v, src.RatingAverage);
        changed |= Set(episode, nameof(episode.ImageUrl), () => episode.ImageUrl,
            v => episode.ImageUrl = v, src.ImageOriginal);
        changed |= Set(episode, nameof(episode.ImageMediumUrl), () => episode.ImageMediumUrl,
            v => episode.ImageMediumUrl = v, src.ImageMedium);
        changed |= Set(episode, nameof(episode.Description), () => episode.Description,
            v => episode.Description = v, HtmlStripper.Strip(src.SummaryHtml));
        return changed;
    }

    // ---- genre / show_genre (join table is rebuildable; genre rows are shared, never deleted) ----

    private async Task SyncGenresAsync(TrackedShow show, TvmazeShow src, CancellationToken cancellationToken)
    {
        await context.Entry(show).Collection(s => s.Genres).LoadAsync(cancellationToken)
            .ConfigureAwait(false);

        var wanted = ParseGenres(src.GenresRaw);
        var currentByName = show.Genres.ToDictionary(g => g.Name, StringComparer.Ordinal);
        var wantedSet = new HashSet<string>(wanted, StringComparer.Ordinal);

        foreach (var stale in show.Genres.Where(g => !wantedSet.Contains(g.Name)).ToList())
        {
            show.Genres.Remove(stale);
        }

        foreach (var name in wanted.Where(n => !currentByName.ContainsKey(n)))
        {
            var genre = await context.Genres.FirstOrDefaultAsync(g => g.Name == name, cancellationToken)
                .ConfigureAwait(false);

            if (genre is null)
            {
                genre = new Genre { Id = idGenerator.NextId(), Name = name };
                context.Genres.Add(genre);
            }

            show.Genres.Add(genre);
        }
    }

    // ---- cached columns (SPEC R5/R7 via the one Domain function) ----

    private async Task<bool> RecomputeCachedColumnsAsync(TrackedShow show, CancellationToken cancellationToken)
    {
        // Read the watched set (never write it — SPEC P4). Absence of a row means unwatched.
        var watchedIds = (await context.EpisodeWatches
            .Where(w => w.ShowId == show.Id)
            .Select(w => w.EpisodeId)
            .ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();

        var allEpisodes = await context.Episodes
            .Where(e => e.ShowId == show.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var facts = allEpisodes.Select(e => new EpisodeProgressFact(e.EpisodeType, watchedIds.Contains(e.Id)));
        var progress = ShowProgress.Compute(show.Status, facts);

        var changed = false;
        changed |= Set(show, nameof(show.WatchedRegular), () => show.WatchedRegular,
            v => show.WatchedRegular = v, progress.WatchedRegular);
        changed |= Set(show, nameof(show.TotalRegular), () => show.TotalRegular,
            v => show.TotalRegular = v, progress.TotalRegular);
        changed |= Set(show, nameof(show.HasUnwatchedSpecials), () => show.HasUnwatchedSpecials,
            v => show.HasUnwatchedSpecials = v, progress.HasUnwatchedSpecials);
        changed |= Set(show, nameof(show.AutoFinishEligible), () => show.AutoFinishEligible,
            v => show.AutoFinishEligible = v, progress.AutoFinishEligible);
        return changed;
    }

    // ---- helpers ----

    // Assign only when the value actually differs, so a no-op map reports no change (idempotency).
    // entity/field are passed for readability at the call site; the comparison itself is on the value.
    private static bool Set<T>(object entity, string field, Func<T> get, Action<T> set, T value)
    {
        if (EqualityComparer<T>.Default.Equals(get(), value))
        {
            return false;
        }

        set(value);
        return true;
    }

    // DateTimeOffset.Equals also compares the UTC offset, so the same instant re-expressed with a
    // different offset (a DB round-trip, or a provider that changes its offset) would read as a change.
    // Compare by instant (UtcDateTime) and store UTC-normalized, so idempotency holds across offsets.
    private static bool SetStamp(
        object entity, string field, Func<DateTimeOffset?> get, Action<DateTimeOffset?> set, DateTimeOffset? value)
    {
        var current = get();
        if (current?.ToUniversalTime() == value?.ToUniversalTime())
        {
            return false;
        }

        set(value?.ToUniversalTime());
        return true;
    }

    private static IReadOnlyList<string> ParseGenres(string? genresRaw)
    {
        if (string.IsNullOrWhiteSpace(genresRaw))
        {
            return Array.Empty<string>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(genresRaw) ?? new List<string>();
        }
        catch (JsonException)
        {
            return new List<string>();
        }
    }

    private static DateOnly? ParseDate(string? value) =>
        DateOnly.TryParse(value, null, System.Globalization.DateTimeStyles.None, out var date) ? date : null;

    private static TimeOnly? ParseTime(string? value) =>
        TimeOnly.TryParse(value, null, System.Globalization.DateTimeStyles.None, out var time) ? time : null;

    private static DateTimeOffset? ParseStamp(string? value) =>
        DateTimeOffset.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var stamp)
            ? stamp
            : null;
}
