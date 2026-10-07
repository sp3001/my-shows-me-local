using Shows.Application;
using Shows.Infrastructure.Discovery;

namespace Shows.Tests.Unit;

/// <summary>
/// User-issues 19 acceptance for the Discovery read model: the <c>/schedule/full</c> parser aggregates
/// the flat episode array into one entry per show, and the index classifies each show into the New /
/// Airing / Upcoming buckets, applies the name/genre/network/country filters, and pages the result.
/// Pure — a small recorded fixture and an injected clock, no network and no wall-clock dependence.
/// </summary>
public sealed class DiscoveryIndexTests
{
    private static readonly DateOnly Today = new(2026, 10, 1);
    private const int Window = 30; // windowStart = 2026-09-01

    // ---- parser ----

    [Fact]
    public void Parse_aggregates_repeated_shows_into_one_entry_with_all_their_episodes()
    {
        // The schedule repeats a show once per scheduled episode; the parser must collapse by show id and
        // keep every episode fact (the bucket rules read across them).
        var json = Schedule(
            Episode(1, 1, "2026-09-10", show: Show(100, "New One", genres: ["Drama"], network: "HBO", country: "United States", code: "US")),
            Episode(1, 2, "2026-09-17", show: Show(100, "New One", genres: ["Drama"], network: "HBO", country: "United States", code: "US")));

        var entries = ScheduleParser.Parse(json);

        var entry = Assert.Single(entries);
        Assert.Equal(100, entry.TvmazeId);
        Assert.Equal("New One", entry.Show.Title);
        Assert.Equal("HBO", entry.NetworkName);
        Assert.Equal("United States", entry.CountryName);
        Assert.Equal(2, entry.Episodes.Count);
    }

    [Fact]
    public void Parse_reads_country_from_a_webChannel_when_there_is_no_network()
    {
        var json = Schedule(Episode(1, 1, "2026-09-10", show: Show(7, "Streamed", webChannel: "Netflix", country: "United States", code: "US")));

        var entry = Assert.Single(ScheduleParser.Parse(json));

        Assert.Equal("Netflix", entry.NetworkName);
        Assert.Equal("United States", entry.CountryName);
    }

    // ---- buckets ----

    [Fact]
    public void New_bucket_is_a_show_with_a_season_one_episode_after_the_window_start()
    {
        // S01E01 on 2026-09-10 is after windowStart (2026-09-01). The rule is "any season-1 episode after
        // the window start", not "S01E01 inside the window" — there is no upper bound at today.
        var entries = ScheduleParser.Parse(Schedule(
            Episode(1, 1, "2026-09-10", show: Show(1, "In Window"))));

        var page = DiscoveryIndex.Query(entries, DiscoveryBucket.New, new(), 1, 30, Today, Window);

        Assert.Equal(1, page.TotalItems);
        Assert.Equal("In Window", page.Items[0].Title);
    }

    [Fact]
    public void New_bucket_includes_a_show_whose_season_one_premiere_is_old_but_a_later_episode_is_recent()
    {
        // The user's fix: New is ANY season-1 episode after windowStart, not only S01E01. S01E01 aired
        // 2026-05-01 (before the window) but S01E05 airs 2026-09-15 (after it) → still a new show.
        var entries = ScheduleParser.Parse(Schedule(
            Episode(1, 1, "2026-05-01", show: Show(1, "Slow Start")),
            Episode(1, 5, "2026-09-15", show: Show(1, "Slow Start"))));

        var page = DiscoveryIndex.Query(entries, DiscoveryBucket.New, new(), 1, 30, Today, Window);

        Assert.Equal(1, page.TotalItems);
    }

    [Fact]
    public void New_bucket_includes_a_show_whose_season_one_airs_in_the_future()
    {
        // No upper bound at today: a first season that has not started yet (S01E01 on 2026-12-01) is still
        // "new" (and, being a future premiere, also Upcoming).
        var entries = ScheduleParser.Parse(Schedule(
            Episode(1, 1, "2026-12-01", show: Show(1, "Not Yet Aired"))));

        var page = DiscoveryIndex.Query(entries, DiscoveryBucket.New, new(), 1, 30, Today, Window);

        Assert.Equal(1, page.TotalItems);
    }

    [Fact]
    public void New_bucket_excludes_a_show_whose_season_one_ended_before_the_window()
    {
        // Every season-1 episode predates windowStart → not New.
        var entries = ScheduleParser.Parse(Schedule(
            Episode(1, 1, "2026-05-01", show: Show(1, "Too Old"))));

        var page = DiscoveryIndex.Query(entries, DiscoveryBucket.New, new(), 1, 30, Today, Window);

        Assert.Equal(0, page.TotalItems);
    }

    [Fact]
    public void Airing_bucket_is_a_show_with_any_episode_after_the_window_start()
    {
        // Any episode of any season after windowStart qualifies. Season 2 premieres 2026-09-05 (after it);
        // the old season-1 episode is irrelevant.
        var entries = ScheduleParser.Parse(Schedule(
            Episode(1, 1, "2025-01-01", show: Show(1, "Returning")),
            Episode(2, 1, "2026-09-05", show: Show(1, "Returning"))));

        var page = DiscoveryIndex.Query(entries, DiscoveryBucket.Airing, new(), 1, 30, Today, Window);

        Assert.Equal(1, page.TotalItems);
    }

    [Fact]
    public void Airing_bucket_includes_a_season_one_premiere()
    {
        // The user's fix: New ⊂ Airing. A show whose only dated episode is a season-1 premiere on 2026-09-10
        // (after windowStart) is both New and Airing — Airing is no longer restricted to season > 1.
        var entries = ScheduleParser.Parse(Schedule(
            Episode(1, 1, "2026-09-10", show: Show(1, "Brand New"))));

        var page = DiscoveryIndex.Query(entries, DiscoveryBucket.Airing, new(), 1, 30, Today, Window);

        Assert.Equal(1, page.TotalItems);
    }

    [Fact]
    public void Airing_bucket_excludes_a_show_whose_episodes_all_predate_the_window()
    {
        var entries = ScheduleParser.Parse(Schedule(
            Episode(1, 1, "2026-05-01", show: Show(1, "Old")),
            Episode(2, 1, "2026-06-01", show: Show(1, "Old"))));

        var page = DiscoveryIndex.Query(entries, DiscoveryBucket.Airing, new(), 1, 30, Today, Window);

        Assert.Equal(0, page.TotalItems);
    }

    [Fact]
    public void Upcoming_bucket_includes_a_show_whose_season_premiere_is_in_the_future()
    {
        // The rule (user's fix): a show is Upcoming when a SEASON PREMIERE (episode number 1) is in the
        // future — here a returning show whose season 2 opens on 2026-11-20. Its season 1 already aired.
        var entries = ScheduleParser.Parse(Schedule(
            Episode(1, 1, "2025-01-01", show: Show(1, "Returning Soon")),
            Episode(2, 1, "2026-11-20", show: Show(1, "Returning Soon"))));

        var page = DiscoveryIndex.Query(entries, DiscoveryBucket.Upcoming, new(), 1, 30, Today, Window);

        Assert.Equal(1, page.TotalItems);
    }

    [Fact]
    public void Upcoming_bucket_excludes_a_show_that_already_premiered_with_only_a_future_mid_season_episode()
    {
        // S01E01 already aired (2026-09-10); only a future E05 remains. No season premiere is in the
        // future, so under the fixed rule this show is NOT Upcoming (it is simply airing).
        var entries = ScheduleParser.Parse(Schedule(
            Episode(1, 1, "2026-09-10", show: Show(1, "Already Airing")),
            Episode(1, 5, "2026-11-20", show: Show(1, "Already Airing"))));

        var page = DiscoveryIndex.Query(entries, DiscoveryBucket.Upcoming, new(), 1, 30, Today, Window);

        Assert.Equal(0, page.TotalItems);
    }

    [Fact]
    public void Upcoming_bucket_includes_a_show_whose_S01E01_is_in_the_future()
    {
        var entries = ScheduleParser.Parse(Schedule(
            Episode(1, 1, "2026-12-01", show: Show(1, "Not Yet Premiered"))));

        var page = DiscoveryIndex.Query(entries, DiscoveryBucket.Upcoming, new(), 1, 30, Today, Window);

        Assert.Equal(1, page.TotalItems);
    }

    // ---- filters ----

    [Fact]
    public void Filters_match_name_genre_network_and_country_case_insensitively()
    {
        var entries = ScheduleParser.Parse(Schedule(
            Episode(1, 1, "2026-09-10", show: Show(1, "Breaking Bad", genres: ["Crime", "Drama"], network: "AMC", country: "United States", code: "US")),
            Episode(1, 1, "2026-09-11", show: Show(2, "Other", genres: ["Comedy"], network: "BBC", country: "United Kingdom", code: "GB"))));

        Assert.Single(Query(entries, new(Name: "breaking")).Items);
        Assert.Single(Query(entries, new(Genre: "crime")).Items);
        Assert.Single(Query(entries, new(Network: "amc")).Items);
        Assert.Single(Query(entries, new(Country: "kingdom")).Items);
        Assert.Single(Query(entries, new(Country: "gb")).Items); // country code also matches
        Assert.Empty(Query(entries, new(Name: "breaking", Genre: "comedy")).Items); // AND across facets
    }

    // ---- facets & type ----

    [Fact]
    public void Type_filter_matches_the_show_type_case_insensitively()
    {
        var entries = ScheduleParser.Parse(Schedule(
            Episode(1, 1, "2026-09-10", show: Show(1, "Scripted One", type: "Scripted")),
            Episode(1, 1, "2026-09-11", show: Show(2, "Reality One", type: "Reality"))));

        Assert.Single(Query(entries, new(ShowType: "scripted")).Items);
        Assert.Single(Query(entries, new(ShowType: "Reality")).Items);
    }

    [Fact]
    public void Facet_options_carry_counts_and_an_All_entry()
    {
        var entries = ScheduleParser.Parse(Schedule(
            Episode(1, 1, "2026-09-10", show: Show(1, "A", genres: ["Drama"], network: "HBO", country: "United States", code: "US", type: "Scripted")),
            Episode(1, 1, "2026-09-11", show: Show(2, "B", genres: ["Drama"], network: "Netflix", country: "United States", code: "US", type: "Scripted")),
            Episode(1, 1, "2026-09-12", show: Show(3, "C", genres: ["Comedy"], network: "Netflix", country: "Canada", code: "CA", type: "Scripted"))));

        var page = DiscoveryIndex.Query(entries, DiscoveryBucket.New, new(), 1, 30, Today, Window);

        // Genre facet: Drama appears twice, Comedy once, plus an "All" entry totalling three.
        Assert.Equal(3, Facet(page.GenreOptions, "").Single().Count);
        Assert.Equal(2, Facet(page.GenreOptions, "Drama").Single().Count);
        Assert.Equal(1, Facet(page.GenreOptions, "Comedy").Single().Count);
        // Network facet counts distinct shows per network.
        Assert.Equal(2, Facet(page.NetworkOptions, "Netflix").Single().Count);
        Assert.Equal(1, Facet(page.CountryOptions, "Canada").Single().Count);
        Assert.Equal(3, Facet(page.TypeOptions, "Scripted").Single().Count);
    }

    [Fact]
    public void Facet_counts_respect_every_other_active_filter()
    {
        // With Drama selected, the Network facet must reflect only the Drama shows: HBO (1) and Netflix (1),
        // and the Comedy-only network must not appear. This is the cross-filter behaviour the dropdowns rely on.
        var entries = ScheduleParser.Parse(Schedule(
            Episode(1, 1, "2026-09-10", show: Show(1, "A", genres: ["Drama"], network: "HBO")),
            Episode(1, 1, "2026-09-11", show: Show(2, "B", genres: ["Drama"], network: "Netflix")),
            Episode(1, 1, "2026-09-12", show: Show(3, "C", genres: ["Comedy"], network: "The CW"))));

        var page = DiscoveryIndex.Query(entries, DiscoveryBucket.New, new(Genre: "Drama"), 1, 30, Today, Window);

        Assert.Equal(1, Facet(page.NetworkOptions, "HBO").Single().Count);
        Assert.Equal(1, Facet(page.NetworkOptions, "Netflix").Single().Count);
        Assert.DoesNotContain(page.NetworkOptions, o => o.Value == "The CW");
    }

    [Fact]
    public void Not_set_facet_option_counts_only_the_shows_with_no_value()
    {
        // Two shows have a country, one has none. The country facet must offer a "Not set" entry counted as
        // the one country-less show — NOT as the whole set (which is what "All" means).
        var entries = ScheduleParser.Parse(Schedule(
            Episode(1, 1, "2026-09-10", show: Show(1, "A", network: "HBO", country: "United States", code: "US")),
            Episode(1, 1, "2026-09-11", show: Show(2, "B", network: "Netflix", country: "Canada", code: "CA")),
            Episode(1, 1, "2026-09-12", show: Show(3, "C")))); // no network/country

        var page = DiscoveryIndex.Query(entries, DiscoveryBucket.New, new(), 1, 30, Today, Window);

        Assert.Equal(1, Facet(page.CountryOptions, DiscoveryFilter.NotSet).Single().Count);
        // "All" still means everything (3), distinct from "Not set" (1).
        Assert.Equal(3, Facet(page.CountryOptions, "").Single().Count);
    }

    [Fact]
    public void Selecting_Not_set_filters_to_only_the_shows_with_no_value()
    {
        // Selecting "Not set" on country must return ONLY the country-less show, not every show — the bug
        // this guards against is "Not set" behaving like "All".
        var entries = ScheduleParser.Parse(Schedule(
            Episode(1, 1, "2026-09-10", show: Show(1, "A", network: "HBO", country: "United States", code: "US")),
            Episode(1, 1, "2026-09-11", show: Show(2, "B", network: "Netflix", country: "Canada", code: "CA")),
            Episode(1, 1, "2026-09-12", show: Show(3, "C")))); // no network/country

        var page = DiscoveryIndex.Query(entries, DiscoveryBucket.New, new(Country: DiscoveryFilter.NotSet), 1, 30, Today, Window);

        var only = Assert.Single(page.Items);
        Assert.Equal("C", only.Title);
    }

    // ---- country fallback to language (improvement 21) ----

    [Fact]
    public void A_show_whose_broadcaster_has_no_country_is_filed_under_its_language()
    {
        // The user's case (tvmaze 84377): Netflix with country null, language Korean. Before the fallback
        // this show had no country facet value at all and the card showed nothing after the network.
        var entries = ScheduleParser.Parse(Schedule(
            Episode(1, 1, "2026-09-10", show: "{\"id\":84377,\"name\":\"Dead-End Job\",\"genres\":[]," +
                "\"status\":\"In Development\",\"language\":\"Korean\",\"network\":null," +
                "\"webChannel\":" + BroadcasterWithoutCountry("Netflix") + "}")));

        var page = DiscoveryIndex.Query(entries, DiscoveryBucket.New, new(), 1, 30, Today, Window);

        // The facet offers "Korean" as a country, counted once, and the card's own CountryName agrees.
        Assert.Equal(1, Facet(page.CountryOptions, "Korean").Single().Count);
        Assert.Equal("Korean", page.Items[0].CountryName);
        Assert.Equal(0, Facet(page.CountryOptions, DiscoveryFilter.NotSet).SingleOrDefault()?.Count ?? 0);
    }

    [Fact]
    public void Selecting_a_language_country_returns_the_shows_that_fell_back_to_it()
    {
        // Filtering by the fallback value must work, not just displaying it — otherwise the facet would
        // offer a value that returns nothing.
        var entries = ScheduleParser.Parse(Schedule(
            Episode(1, 1, "2026-09-10", show: "{\"id\":1,\"name\":\"K-Drama\",\"genres\":[]," +
                "\"status\":\"Running\",\"language\":\"Korean\",\"network\":null," +
                "\"webChannel\":" + BroadcasterWithoutCountry("Netflix") + "}"),
            Episode(1, 1, "2026-09-11", show: Show(2, "US Show", network: "HBO", country: "United States", code: "US"))));

        var page = DiscoveryIndex.Query(entries, DiscoveryBucket.New, new(Country: "Korean"), 1, 30, Today, Window);

        var only = Assert.Single(page.Items);
        Assert.Equal("K-Drama", only.Title);
    }

    [Fact]
    public void A_real_country_still_wins_over_the_language_in_the_facet()
    {
        // The fallback must not downgrade shows TVmaze does give a country to, or the facet would collapse
        // every English show into "English".
        var entries = ScheduleParser.Parse(Schedule(
            Episode(1, 1, "2026-09-10", show: Show(1, "US Show", network: "HBO", country: "United States", code: "US", language: "English"))));

        var page = DiscoveryIndex.Query(entries, DiscoveryBucket.New, new(), 1, 30, Today, Window);

        Assert.Equal(1, Facet(page.CountryOptions, "United States").Single().Count);
        Assert.Empty(Facet(page.CountryOptions, "English"));
    }

    [Fact]
    public void A_show_with_neither_country_nor_language_still_counts_as_not_set()
    {
        // The guard against over-reach: the fallback must not empty the "Not set" entry, which is how the
        // user finds the shows TVmaze truly says nothing about.
        var entries = ScheduleParser.Parse(Schedule(
            Episode(1, 1, "2026-09-10", show: "{\"id\":1,\"name\":\"No Country\",\"genres\":[]," +
                "\"status\":\"Running\",\"language\":null,\"network\":null,\"webChannel\":null}"),
            Episode(1, 1, "2026-09-11", show: "{\"id\":2,\"name\":\"K-Drama\",\"genres\":[]," +
                "\"status\":\"Running\",\"language\":\"Korean\",\"network\":null," +
                "\"webChannel\":" + BroadcasterWithoutCountry("Netflix") + "}")));

        var page = DiscoveryIndex.Query(entries, DiscoveryBucket.New, new(), 1, 30, Today, Window);

        Assert.Equal(1, Facet(page.CountryOptions, DiscoveryFilter.NotSet).Single().Count);
        Assert.Equal(1, Facet(page.CountryOptions, "Korean").Single().Count);

        var filtered = DiscoveryIndex.Query(
            entries, DiscoveryBucket.New, new(Country: DiscoveryFilter.NotSet), 1, 30, Today, Window);
        Assert.Equal("No Country", Assert.Single(filtered.Items).Title);
    }

    private static IEnumerable<DiscoveryFacetOption> Facet(IReadOnlyList<DiscoveryFacetOption> options, string value) =>
        options.Where(o => o.Value == value);

    // ---- paging ----

    [Fact]
    public void Paging_returns_the_requested_slice_and_reports_the_total()
    {
        var json = string.Join(",", Enumerable.Range(1, 5)
            .Select(i => Episode(1, 1, "2026-09-10", show: Show(i, $"Show {i}"))));
        var entries = ScheduleParser.Parse("[" + json + "]");

        var page = DiscoveryIndex.Query(entries, DiscoveryBucket.New, new(), page: 2, pageSize: 2, Today, Window);

        Assert.Equal(5, page.TotalItems);
        Assert.Equal(3, page.TotalPages);
        Assert.Equal(2, page.Items.Count);
        Assert.True(page.HasPrevious);
        Assert.True(page.HasNext);
    }

    [Fact]
    public void Ordering_is_deterministic_so_a_show_never_appears_on_two_pages()
    {
        var json = string.Join(",", Enumerable.Range(1, 6)
            .Select(i => Episode(1, 1, "2026-09-10", show: Show(i, $"Show {i}"))));
        var entries = ScheduleParser.Parse("[" + json + "]");

        var seen = new List<string>();
        for (var p = 1; p <= 3; p++)
        {
            seen.AddRange(DiscoveryIndex.Query(entries, DiscoveryBucket.New, new(), p, 2, Today, Window).Items.Select(s => s.Title));
        }

        Assert.Equal(6, seen.Distinct().Count()); // no duplicates, none dropped
    }

    // ---- helpers ----

    private static DiscoveryPage Query(IReadOnlyList<DiscoveryEntry> entries, DiscoveryFilter filter) =>
        DiscoveryIndex.Query(entries, DiscoveryBucket.New, filter, 1, 30, Today, Window);

    private static string Schedule(params string[] episodes) => "[" + string.Join(",", episodes) + "]";

    private static string Episode(int season, int number, string airdate, string show) =>
        "{\"season\":" + season + ",\"number\":" + number + ",\"airdate\":\"" + airdate
        + "\",\"type\":\"regular\",\"_embedded\":{\"show\":" + show + "}}";

    // language=null emits a show with no language at all, so a fixture can express "no country and no
    // language" — the only case that must still land under the facet's "Not set" entry after the
    // improvement-21 fallback.
    private static string Show(
        int id, string name, string[]? genres = null,
        string? network = null, string? webChannel = null,
        string? country = null, string? code = null, string? type = null,
        string? language = null)
    {
        var g = genres is null ? "[]" : "[" + string.Join(",", genres.Select(x => "\"" + x + "\"")) + "]";
        var net = network is null ? "null" : CountryObject(network, country, code);
        var wc = webChannel is null ? "null" : CountryObject(webChannel, country, code);
        var ty = type is null ? "null" : "\"" + type + "\"";
        var lang = language is null ? "null" : "\"" + language + "\"";
        return "{\"id\":" + id + ",\"name\":\"" + name + "\",\"genres\":" + g
            + ",\"status\":\"Running\",\"type\":" + ty + ",\"language\":" + lang
            + ",\"network\":" + net + ",\"webChannel\":" + wc + "}";
    }

    // A broadcaster whose country object is explicitly null — the streaming shape TVmaze actually returns
    // ("webChannel": { "name": "Netflix", "country": null }).
    private static string BroadcasterWithoutCountry(string name) =>
        "{\"name\":\"" + name + "\",\"country\":null}";

    private static string CountryObject(string name, string? country, string? code) =>
        "{\"name\":\"" + name + "\",\"country\":{\"name\":\"" + country + "\",\"code\":\"" + code + "\"}}";
}
