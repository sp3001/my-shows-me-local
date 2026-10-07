using Shows.Application;

namespace Shows.Infrastructure.Discovery;

/// <summary>
/// The in-memory query over a parsed <c>/schedule/full</c> snapshot: bucket classification, filtering,
/// ordering and paging (user-issues 19). Pure and clock-injected, so the whole Discovery read model is
/// testable against a small fixture with no network and no wall-clock dependence.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bucket rules</b> (SPEC, user-issues 19), evaluated against <paramref name="today"/> and the
/// look-back <paramref name="window"/> (<c>windowStart = today - window</c>):
/// </para>
/// <list type="bullet">
///   <item><b>New</b> — the show has any <b>season 1</b> episode airing after <c>windowStart</c>
///   (no upper bound: a first season still airing ahead of today is still "new").</item>
///   <item><b>Airing</b> — the show has any episode (any season) airing after <c>windowStart</c>.</item>
///   <item><b>Upcoming</b> — the show has a future-dated season premiere (an episode numbered 1 whose
///   air date is after <c>today</c>).</item>
/// </list>
/// <para>
/// The buckets are independent views, which is what the tab's switcher presents, and they overlap by
/// design: New is a subset of Airing (a season-1 episode is an episode), and a not-yet-premiered new
/// show is both New (its S01E01 is after windowStart) and Upcoming (that same date is after today).
/// </para>
/// </remarks>
internal static class DiscoveryIndex
{
    public static DiscoveryPage Query(
        IReadOnlyList<DiscoveryEntry> entries,
        DiscoveryBucket bucket,
        DiscoveryFilter filter,
        int page,
        int pageSize,
        DateOnly today,
        int windowDays)
    {
        if (pageSize <= 0)
        {
            pageSize = 30;
        }

        if (page < 1)
        {
            page = 1;
        }

        var windowStart = today.AddDays(-Math.Max(0, windowDays));

        // Bucket + name first (these always apply), then per-facet counts, then the full filter for the page.
        var baseMatched = entries
            .Where(e => InBucket(e, bucket, today, windowStart))
            .Where(e => Contains(e.Show.Title, filter.Name))
            .ToList();

        // Facet options: each facet's counts respect every OTHER active filter (standard faceted search), so
        // selecting a genre never zeroes the genre list itself — only the other facets narrow it.
        var genreOptions = FacetOptions(
            baseMatched.Where(e => MatchesFacets(e, filter, skipGenre: true)),
            e => e.Show.Genres);
        var networkOptions = FacetOptions(
            baseMatched.Where(e => MatchesFacets(e, filter, skipNetwork: true)),
            e => Single(e.NetworkName));
        var countryOptions = FacetOptions(
            baseMatched.Where(e => MatchesFacets(e, filter, skipCountry: true)),
            e => Single(e.CountryName));
        var typeOptions = FacetOptions(
            baseMatched.Where(e => MatchesFacets(e, filter, skipType: true)),
            e => Single(e.Show.ShowType));

        var matched = baseMatched
            .Where(e => MatchesFacets(e, filter))
            // Stable, sensible default order: soonest relevant air date first, then title. Ordering is
            // deterministic so paging never shows the same show on two pages or drops one between pages.
            .OrderBy(e => SortDate(e, bucket, today))
            .ThenBy(e => e.Show.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var total = matched.Count;
        var items = matched
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            // Discovery is the only provider with episode data, so it is the only place the card's
            // "Next Episode" line can be filled: the soonest future-dated episode in the snapshot.
            .Select(e => e.Show with { NextEpisode = NextEpisode(e, today) })
            .ToList();

        return new DiscoveryPage(
            items, page, pageSize, total,
            genreOptions, networkOptions, countryOptions, typeOptions);
    }

    private static bool InBucket(DiscoveryEntry e, DiscoveryBucket bucket, DateOnly today, DateOnly windowStart)
    {
        switch (bucket)
        {
            case DiscoveryBucket.New:
            {
                // Any season-1 episode airing after the window start (user fix: not only S01E01, and no
                // upper bound at today — a first season still airing ahead of us is still a new show).
                return e.Episodes.Any(ep => ep.Season == 1 && ep.AirDate is { } d && d > windowStart);
            }

            case DiscoveryBucket.Airing:
            {
                // Any episode of any season airing after the window start (user fix: the whole recent
                // schedule, not only a later-season premiere).
                return e.Episodes.Any(ep => ep.AirDate is { } d && d > windowStart);
            }

            case DiscoveryBucket.Upcoming:
            {
                // A future-dated SEASON PREMIERE (an episode numbered 1 airing after today) — unchanged by
                // the 2026-10-06 fix. A future mid-season episode of an already-airing show is NOT Upcoming.
                var anyFuture = e.Episodes.Any(ep => ep.Number == 1 && ep.AirDate is { } d && d > today);
                return anyFuture;
            }

            default:
                return false;
        }
    }

    private static DateOnly? Season1Episode1Date(DiscoveryEntry e) =>
        e.Episodes
            .Where(ep => ep.Season == 1 && ep.Number == 1 && ep.AirDate is not null)
            .Select(ep => ep.AirDate!.Value)
            .MinByOrNull();

    // The episode the card highlights. Normally the next one the user waits for: the soonest future-dated
    // episode in the snapshot (air date > today), flagged as a "next" episode. When the show has nothing
    // scheduled ahead but does have past episodes, fall back to the most recent already-aired one (the
    // greatest air date <= today) flagged recent, so a show with no future date still shows its latest
    // known episode rather than nothing. Null only when the snapshot has no dated episode at all. The
    // marker is rendered here (S02E05) so the card needs no season/number formatting logic.
    private static CatalogNextEpisode? NextEpisode(DiscoveryEntry e, DateOnly today)
    {
        var next = e.Episodes
            .Where(ep => ep.AirDate is { } d && d > today)
            .OrderBy(ep => ep.AirDate!.Value)
            .ThenBy(ep => ep.Season)
            .ThenBy(ep => ep.Number ?? 0)
            .FirstOrDefault();

        if (next is not null)
        {
            return new CatalogNextEpisode(Marker(next), next.Title, next.AirDate, IsRecent: false);
        }

        // No future episode: the latest known past episode (air date <= today), most recent first.
        var recent = e.Episodes
            .Where(ep => ep.AirDate is { } d && d <= today)
            .OrderByDescending(ep => ep.AirDate!.Value)
            .ThenByDescending(ep => ep.Season)
            .ThenByDescending(ep => ep.Number ?? 0)
            .FirstOrDefault();

        return recent is null
            ? null
            : new CatalogNextEpisode(Marker(recent), recent.Title, recent.AirDate, IsRecent: true);
    }

    // The S02E05 marker for an episode. A null number (a season-only row) yields just the season (S02).
    private static string Marker(EpisodeFact ep) =>
        ep.Number is null ? $"S{ep.Season:00}" : $"S{ep.Season:00}E{ep.Number:00}";

    // The date to order a page by: for Upcoming the soonest future date (what the user waits for);
    // otherwise the show's earliest scheduled date. Null sorts last.
    private static DateOnly? SortDate(DiscoveryEntry e, DiscoveryBucket bucket, DateOnly today)
    {
        if (bucket == DiscoveryBucket.Upcoming)
        {
            return e.Episodes
                .Where(ep => ep.AirDate is { } d && d > today)
                .Select(ep => ep.AirDate!.Value)
                .MinByOrNull()
                ?? Season1Episode1Date(e);
        }

        return e.Episodes
            .Where(ep => ep.AirDate is not null)
            .Select(ep => ep.AirDate!.Value)
            .MinByOrNull();
    }

    // Applies the four facet filters (genre/network/country/type), optionally skipping ONE so a facet's own
    // option counts are not zeroed by its own selection. Name is applied separately (it always applies).
    // Each facet predicate: an unset filter (null/empty) matches everything ("All"); the DiscoveryFilter.NotSet
    // sentinel matches only shows whose value for that facet is UNFILLED; otherwise matching is case-
    // insensitive substring — a dropdown hands an exact value (which matches), and a partial still finds it.
    private static bool MatchesFacets(
        DiscoveryEntry e, DiscoveryFilter filter,
        bool skipGenre = false, bool skipNetwork = false, bool skipCountry = false, bool skipType = false)
    {
        if (!skipGenre && !MatchesFacet(
                filter.Genre,
                hasValue: e.Show.Genres.Any(g => !string.IsNullOrWhiteSpace(g)),
                matches: () => e.Show.Genres.Any(g => Contains(g, filter.Genre))))
        {
            return false;
        }

        if (!skipNetwork && !MatchesFacet(
                filter.Network,
                hasValue: !string.IsNullOrWhiteSpace(e.NetworkName),
                matches: () => Contains(e.NetworkName, filter.Network)))
        {
            return false;
        }

        if (!skipCountry && !MatchesFacet(
                filter.Country,
                hasValue: !string.IsNullOrWhiteSpace(e.CountryName) || !string.IsNullOrWhiteSpace(e.CountryCode),
                matches: () => Contains(e.CountryName, filter.Country) || Contains(e.CountryCode, filter.Country)))
        {
            return false;
        }

        if (!skipType && !MatchesFacet(
                filter.ShowType,
                hasValue: !string.IsNullOrWhiteSpace(e.Show.ShowType),
                matches: () => Contains(e.Show.ShowType, filter.ShowType)))
        {
            return false;
        }

        return true;
    }

    // One facet's predicate. "All" (null/empty) matches everything; "Not set" matches only when the show's
    // value for the facet is unfilled; otherwise the substring match applies.
    private static bool MatchesFacet(string? filterValue, bool hasValue, Func<bool> matches)
    {
        if (string.IsNullOrWhiteSpace(filterValue))
        {
            return true; // All — the facet is unset
        }

        if (filterValue == Shows.Application.DiscoveryFilter.NotSet)
        {
            return !hasValue; // "Not set" — only shows with no value for this facet
        }

        return matches();
    }

    // Builds a facet's option list from the already-other-filtered entries: an "All" entry (empty value, the
    // total), then — when some shows have no value for this facet — a "Not set" entry (the NotSet sentinel,
    // counted as those unfilled shows), then one entry per distinct non-empty value, most-frequent first then
    // alphabetical. The selector yields a show's values for that facet (a show has many genres, one
    // network/country/type); a show with only null/blank values for the facet counts toward "Not set".
    private static List<DiscoveryFacetOption> FacetOptions(
        IEnumerable<DiscoveryEntry> entries, Func<DiscoveryEntry, IEnumerable<string?>> selector)
    {
        var list = entries.ToList();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var notSetCount = 0;
        foreach (var entry in list)
        {
            // Distinct per show so a show with the same value twice (shouldn't happen, but tolerant) counts once.
            var distinct = selector(entry)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (distinct.Count == 0)
            {
                notSetCount++; // the show carries no value for this facet
                continue;
            }

            foreach (var value in distinct)
            {
                counts[value!] = counts.GetValueOrDefault(value!) + 1;
            }
        }

        var options = new List<DiscoveryFacetOption> { new(string.Empty, list.Count) };
        if (notSetCount > 0)
        {
            options.Add(new DiscoveryFacetOption(Shows.Application.DiscoveryFilter.NotSet, notSetCount));
        }

        options.AddRange(counts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new DiscoveryFacetOption(kv.Key, kv.Value)));
        return options;
    }

    private static IEnumerable<string?> Single(string? value) => value is null ? [] : [value];

    // Case-insensitive substring; a null/whitespace needle matches anything (the facet is unset).
    private static bool Contains(string? haystack, string? needle) =>
        string.IsNullOrWhiteSpace(needle)
        || (haystack is not null && haystack.Contains(needle.Trim(), StringComparison.OrdinalIgnoreCase));

    private static DateOnly? MinByOrNull(this IEnumerable<DateOnly> source)
    {
        DateOnly? min = null;
        foreach (var d in source)
        {
            if (min is null || d < min)
            {
                min = d;
            }
        }

        return min;
    }
}
