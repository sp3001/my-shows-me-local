using System.Globalization;
using System.Text.Json;
using Shows.Application;
using Shows.Infrastructure.Persistence.Sources;

namespace Shows.Infrastructure.Tvmaze;

/// <summary>
/// Pure translation from a TVmaze JSON payload to the verbatim source rows and to the read-model DTOs.
/// No I/O, no clock beyond the injected <paramref name="fetchedAt"/> — so the whole provider's data
/// path is testable against recorded fixtures with no network (IMPLEMENTATION_PLAN step 3.1).
/// </summary>
/// <remarks>
/// Navigating <see cref="JsonElement"/> directly rather than deserializing to a DTO hierarchy is
/// deliberate: the source tables store the payload <b>verbatim</b> (SPEC R11), so each show/episode
/// needs its raw JSON captured (<c>GetRawText()</c>), and TVmaze adds fields without notice
/// (<c>DATA_ANALYSIS.md</c> §8) — a rigid DTO would silently drop them. Every property read is
/// null-tolerant for the same reason.
/// </remarks>
internal static class TvmazeJsonMapper
{
    /// <summary>
    /// The country a show's broadcaster carries, read <b>verbatim</b> from the payload. TVmaze nests the
    /// country one level inside <em>whichever</em> broadcaster exists — a broadcast network carries
    /// <c>network.country</c>, a streaming service <c>webChannel.country</c> — and very often carries
    /// nothing at all.
    /// </summary>
    /// <param name="Name"><c>country.name</c>, e.g. "United States". Null when the broadcaster has no country.</param>
    /// <param name="Code">The two-letter <c>country.code</c>, e.g. "US".</param>
    internal readonly record struct BroadcasterCountry(string? Name, string? Code);

    /// <summary>
    /// Read the broadcaster's country from a show element. The broadcaster is whichever of
    /// <c>network</c> / <c>webChannel</c> is present — they are mutually exclusive on the source — and the
    /// country is the object nested inside it, never the broadcaster itself.
    /// </summary>
    /// <remarks>
    /// Shared with <c>ScheduleParser</c> so the Discovery facets read the country out of a
    /// <c>/schedule/full</c> embedded show exactly as the search read model reads it out of a
    /// <c>/search/shows</c> one. No display rule lives here: the fallback to language is
    /// <c>CountryRules.Display</c> (Domain), which every path that shows or filters a country calls.
    /// </remarks>
    internal static BroadcasterCountry ReadBroadcasterCountry(JsonElement show)
    {
        // The broadcaster is whichever of the two is present; they are mutually exclusive on the source.
        var broadcaster = OptObject(show, "network") ?? OptObject(show, "webChannel");
        var country = OptObject(broadcaster, "country");
        return new BroadcasterCountry(OptString(country, "name"), OptString(country, "code"));
    }

    /// <summary>
    /// The country to display for a show — the broadcaster's country when TVmaze has one, otherwise its
    /// <c>language</c>. Thin seam over <see cref="Shows.Domain.CountryRules.Display"/> (improvement 21) so
    /// every read model reaches the rule through the module that parses TVmaze, and a Discovery card, a
    /// search card and the tracked details page cannot name a show's country differently.
    /// </summary>
    internal static string? DisplayCountry(BroadcasterCountry country, string? language) =>
        Shows.Domain.CountryRules.Display(country.Name, country.Code, language);

    public static TvmazeShow MapShow(JsonElement show, DateTimeOffset fetchedAt)
    {
        var externals = OptObject(show, "externals");
        var image = OptObject(show, "image");
        var network = OptObject(show, "network");
        var webChannel = OptObject(show, "webChannel");
        var schedule = OptObject(show, "schedule");
        var broadcasterCountry = ReadBroadcasterCountry(show);

        return new TvmazeShow
        {
            TvmazeId = RequiredInt(show, "id"),
            Url = OptString(show, "url"),
            Name = OptString(show, "name"),
            TypeRaw = OptString(show, "type"),
            Language = OptString(show, "language"),
            StatusRaw = OptString(show, "status") ?? string.Empty,
            RuntimeMinutes = OptInt(show, "runtime"),
            AverageRuntime = OptInt(show, "averageRuntime"),
            Premiered = OptString(show, "premiered"),
            Ended = OptString(show, "ended"),
            OfficialSite = OptString(show, "officialSite"),
            ScheduleTime = OptString(schedule, "time") ?? string.Empty,
            ScheduleDays = JoinDays(schedule),
            RatingAverage = OptDouble(OptObject(show, "rating"), "average"),
            NetworkName = OptString(network, "name"),
            WebchannelName = OptString(webChannel, "name"),
            // Verbatim: the nested country only, with no language fallback — that is a display rule and
            // belongs to the mapped layer (SPEC R11: the source table holds the payload, not our reading
            // of it). raw_json remains the recovery path if the nesting ever turns out to be deeper.
            CountryName = broadcasterCountry.Name,
            CountryCode = broadcasterCountry.Code,
            ImdbId = OptString(externals, "imdb"),
            ThetvdbId = OptInt(externals, "thetvdb"),
            TvrageId = OptInt(externals, "tvrage"),
            GenresRaw = OptArrayRawText(show, "genres"),
            ImageOriginal = OptString(image, "original"),
            ImageMedium = OptString(image, "medium"),
            SummaryHtml = OptString(show, "summary"),
            ProviderUpdated = OptLong(show, "updated"),
            RawJson = show.GetRawText(),
            FetchedAt = fetchedAt,
        };
    }

    public static IReadOnlyList<TvmazeEpisode> MapEpisodes(JsonElement episodesArray, int showId, DateTimeOffset fetchedAt)
    {
        var list = new List<TvmazeEpisode>();
        foreach (var ep in episodesArray.EnumerateArray())
        {
            list.Add(MapEpisode(ep, showId, fetchedAt));
        }

        return list;
    }

    private static TvmazeEpisode MapEpisode(JsonElement ep, int showId, DateTimeOffset fetchedAt)
    {
        var image = OptObject(ep, "image");
        return new TvmazeEpisode
        {
            TvmazeEpisodeId = RequiredInt(ep, "id"),
            TvmazeShowId = showId,
            Url = OptString(ep, "url"),
            Name = OptString(ep, "name"),
            SeasonNumber = OptInt(ep, "season") ?? 0,
            Number = OptInt(ep, "number"),
            TypeRaw = OptString(ep, "type") ?? string.Empty,
            Airdate = OptString(ep, "airdate"),
            Airstamp = OptString(ep, "airstamp"),
            RuntimeMinutes = OptInt(ep, "runtime"),
            RatingAverage = OptDouble(OptObject(ep, "rating"), "average"),
            ImageOriginal = OptString(image, "original"),
            ImageMedium = OptString(image, "medium"),
            SummaryHtml = OptString(ep, "summary"),
            RawJson = ep.GetRawText(),
            FetchedAt = fetchedAt,
        };
    }

    /// <summary>A search/discovery/lookup show element → the read model. Never persisted (SPEC P2/R8a).</summary>
    public static CatalogShow MapCatalogShow(JsonElement show, bool isAlreadyTracked = false)
    {
        var externals = OptObject(show, "externals");
        var image = OptObject(show, "image");
        var network = OptObject(show, "network");
        var webChannel = OptObject(show, "webChannel");
        var language = OptString(show, "language");
        var broadcasterCountry = ReadBroadcasterCountry(show);
        var genres = new List<string>();
        if (show.TryGetProperty("genres", out var g) && g.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in g.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    genres.Add(item.GetString()!);
                }
            }
        }

        // CatalogShow is a display read model, so runtime ?? averageRuntime is collapsed here — the
        // "collapse at display, not at map" rule (DATA_ANALYSIS §8.3) applies to the source/mapped
        // tables, which keep both; this DTO is where the single display number is chosen.
        var runtime = OptInt(show, "runtime") ?? OptInt(show, "averageRuntime");

        return new CatalogShow(
            TvmazeId: RequiredInt(show, "id"),
            Title: OptString(show, "name") ?? string.Empty,
            TitleLocalized: null,
            Year: YearFromPremiered(OptString(show, "premiered")),
            StatusRaw: OptString(show, "status"),
            ShowType: OptString(show, "type"),
            Language: language,
            Genres: genres,
            // The card renders Overview as plain text (user-issues 19: the description fills the card's
            // empty space), so strip the markup HERE — the UI layer must not name Infrastructure's
            // HtmlStripper. The raw summary stays verbatim in the source table (SPEC R11); this is the
            // display read model, the same "collapse at display" point that folds runtime ?? averageRuntime.
            Overview: Persistence.HtmlStripper.Strip(OptString(show, "summary")),
            PosterUrl: OptString(image, "original"),
            PosterMediumUrl: OptString(image, "medium"),
            RatingAverage: OptDouble(OptObject(show, "rating"), "average"),
            RuntimeMinutes: runtime,
            ImdbId: OptString(externals, "imdb"),
            // Broadcaster + country facets for the card (user-issues 19 follow-up). The broadcaster is
            // whichever of network / webChannel exists; the country is the one display rule (broadcaster
            // country, else language) shared with the tracked mapping and the Discovery facets.
            NetworkName: OptString(network, "name") ?? OptString(webChannel, "name"),
            CountryName: DisplayCountry(broadcasterCountry, language),
            // Full premiere date for the card (the year alone already feeds Year above). Day-precision
            // comes only from TVmaze (DATA_ANALYSIS: IMDb is year-granular at best).
            PremiereDate: DateFromPremiered(OptString(show, "premiered")),
            // The official site (TVmaze officialSite) — authored data, not derived from an id, so it is
            // carried to the untracked-details page as a link (mirrors TrackedShow.OfficialSite).
            OfficialSite: OptString(show, "officialSite"),
            IsAlreadyTracked: isAlreadyTracked);
    }

    public static EpisodeCatalogInfo MapEpisodeCatalog(JsonElement ep)
    {
        var image = OptObject(ep, "image");
        return new EpisodeCatalogInfo(
            TvmazeEpisodeId: RequiredInt(ep, "id"),
            SeasonNumber: OptInt(ep, "season") ?? 0,
            EpisodeNumber: OptInt(ep, "number"),
            Title: OptString(ep, "name"),
            TypeRaw: OptString(ep, "type"),
            AirDateRaw: OptString(ep, "airdate"),
            AirStampRaw: OptString(ep, "airstamp"),
            RuntimeMinutes: OptInt(ep, "runtime"),
            ImageUrl: OptString(image, "original"),
            ImageMediumUrl: OptString(image, "medium"),
            // Stripped here for the same reason the show overview is (see MapCatalogShow): the details page
            // renders plain text and the UI layer must not name Infrastructure's stripper.
            Summary: Persistence.HtmlStripper.Strip(OptString(ep, "summary")),
            ImdbId: null,
            RatingAverage: OptDouble(OptObject(ep, "rating"), "average"));
    }

    // ---- tolerant readers (TVmaze adds/removes fields without notice) ----

    /// <summary>
    /// Parse the <c>/shows/{id}/akas</c> array into raw <see cref="ShowAka"/> records, in the API's array
    /// order (the order to trust — the mapper picks the lowest for the target country). The endpoint keys on
    /// <c>country.code</c>, not language (probed live 2026-09-30); an aka with no country code is skipped —
    /// it cannot participate in the pick and would collide on the source table's composite PK.
    /// </summary>
    public static IReadOnlyList<ShowAka> MapAkas(JsonElement akas)
    {
        var list = new List<ShowAka>();
        foreach (var aka in akas.EnumerateArray())
        {
            var name = OptString(aka, "name");
            var country = OptObject(aka, "country");
            var code = OptString(country, "code");
            if (name is null || code is null)
            {
                continue;
            }

            list.Add(new ShowAka(name, code, OptString(country, "name")));
        }

        return list;
    }

    private static JsonElement? OptObject(JsonElement? el, string name)
    {
        if (el is null)
        {
            return null;
        }

        return el.Value.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.Object
            ? child
            : null;
    }

    private static string? OptString(JsonElement? el, string name)
    {
        if (el is null || !el.Value.TryGetProperty(name, out var child))
        {
            return null;
        }

        return child.ValueKind switch
        {
            JsonValueKind.String => child.GetString(),
            JsonValueKind.Null => null,
            _ => child.GetRawText(),
        };
    }

    private static int? OptInt(JsonElement? el, string name)
    {
        if (el is null || !el.Value.TryGetProperty(name, out var child))
        {
            return null;
        }

        return child.ValueKind == JsonValueKind.Number && child.TryGetInt32(out var v) ? v : null;
    }

    private static long? OptLong(JsonElement? el, string name)
    {
        if (el is null || !el.Value.TryGetProperty(name, out var child))
        {
            return null;
        }

        return child.ValueKind == JsonValueKind.Number && child.TryGetInt64(out var v) ? v : null;
    }

    private static double? OptDouble(JsonElement? el, string name)
    {
        if (el is null || !el.Value.TryGetProperty(name, out var child))
        {
            return null;
        }

        return child.ValueKind == JsonValueKind.Number ? child.GetDouble() : null;
    }

    private static int RequiredInt(JsonElement el, string name) =>
        el.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.Number
            ? child.GetInt32()
            : throw new JsonException($"TVmaze payload is missing required numeric field '{name}'.");

    private static string? OptArrayRawText(JsonElement el, string name) =>
        el.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.Array
            ? child.GetRawText()
            : null;

    private static string JoinDays(JsonElement? schedule)
    {
        if (schedule is null
            || !schedule.Value.TryGetProperty("days", out var days)
            || days.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var parts = days.EnumerateArray()
            .Where(d => d.ValueKind == JsonValueKind.String)
            .Select(d => d.GetString());
        return string.Join(",", parts);
    }

    private static int? YearFromPremiered(string? premiered) =>
        !string.IsNullOrWhiteSpace(premiered)
        && premiered.Length >= 4
        && int.TryParse(premiered.AsSpan(0, 4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var year)
            ? year
            : null;

    // The full premiere date (yyyy-MM-dd) for the card. Null when absent or unparseable — TVmaze returns
    // null for a not-yet-aired show, and the tolerant-reader rule says never throw on a missing field.
    private static DateOnly? DateFromPremiered(string? premiered) =>
        DateOnly.TryParse(premiered, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}
