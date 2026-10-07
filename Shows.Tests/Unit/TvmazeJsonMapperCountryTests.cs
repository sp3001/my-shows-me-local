using System.Text.Json;
using Shows.Infrastructure.Tvmaze;

namespace Shows.Tests.Unit;

/// <summary>
/// The country half of the TVmaze payload reader (improvement 21), pinned against the two shapes that
/// matter: a broadcaster that carries a nested country, and a streaming broadcaster that carries none.
/// The split the tests enforce is the one the architecture depends on — the <b>source</b> row holds the
/// payload verbatim (no language fallback, SPEC R11), while the <b>read model</b> carries the finished
/// display label (CountryRules.Display).
/// </summary>
public class TvmazeJsonMapperCountryTests
{
    // The user's example, trimmed to the fields the country rule reads: Netflix with country null,
    // language Korean, no network.
    private const string DeadEndJob = """
        {"id":84377,"name":"Dead-End Job","type":"Scripted","language":"Korean",
         "genres":["Horror","Mystery","Supernatural"],"status":"In Development",
         "premiered":"2026-10-30","network":null,
         "webChannel":{"id":1,"name":"Netflix","country":null,"officialSite":"https://www.netflix.com/"},
         "image":null,"summary":"<p>A young man takes a job.</p>"}
        """;

    private const string WithNetworkCountry = """
        {"id":1,"name":"American Show","language":"English","status":"Running","type":"Scripted",
         "network":{"id":4,"name":"NBC","country":{"name":"United States","code":"US","timezone":"America/New_York"}},
         "webChannel":null}
        """;

    private const string WithWebChannelCountry = """
        {"id":2,"name":"Streamed With Country","language":null,"status":"Running","type":"Scripted",
         "network":null,
         "webChannel":{"id":10,"name":"Crave","country":{"name":"Canada","code":"CA"}}}
        """;

    private const string NoBroadcasterAtAll = """
        {"id":3,"name":"Orphan","language":"Japanese","status":"Ended","type":"Scripted",
         "network":null,"webChannel":null}
        """;

    // ---- source rows stay verbatim ----

    [Fact]
    public void MapShow_stores_the_nested_country_verbatim()
    {
        var src = TvmazeJsonMapper.MapShow(Parse(WithNetworkCountry), DateTimeOffset.UtcNow);

        Assert.Equal("United States", src.CountryName);
        Assert.Equal("US", src.CountryCode);
        Assert.Equal("NBC", src.NetworkName);
    }

    [Fact]
    public void MapShow_reads_the_country_from_webChannel_when_there_is_no_network()
    {
        var src = TvmazeJsonMapper.MapShow(Parse(WithWebChannelCountry), DateTimeOffset.UtcNow);

        Assert.Equal("Canada", src.CountryName);
        Assert.Equal("CA", src.CountryCode);
        Assert.Equal("Crave", src.WebchannelName);
    }

    [Fact]
    public void MapShow_leaves_the_source_country_null_rather_than_filling_it_with_the_language()
    {
        // SPEC R11: tvmaze_show is a copy of the payload. Writing "Korean" into country_name here would
        // make the source table claim something TVmaze never said, and a later re-map could not tell the
        // provider's value from ours.
        var src = TvmazeJsonMapper.MapShow(Parse(DeadEndJob), DateTimeOffset.UtcNow);

        Assert.Null(src.CountryName);
        Assert.Null(src.CountryCode);
        Assert.Equal("Korean", src.Language);   // the language is still captured, verbatim, in its own column
        Assert.Equal("Netflix", src.WebchannelName);
    }

    [Fact]
    public void MapShow_leaves_the_country_null_when_no_broadcaster_exists()
    {
        var src = TvmazeJsonMapper.MapShow(Parse(NoBroadcasterAtAll), DateTimeOffset.UtcNow);

        Assert.Null(src.CountryName);
        Assert.Null(src.CountryCode);
        Assert.Null(src.NetworkName);
        Assert.Null(src.WebchannelName);
    }

    // ---- the read model carries the finished label ----

    [Fact]
    public void MapCatalogShow_falls_back_to_the_language_when_the_broadcaster_has_no_country()
    {
        // The user's report, end to end: this is what makes the card read "Netflix · Korean · 2026-10-30".
        var catalog = TvmazeJsonMapper.MapCatalogShow(Parse(DeadEndJob));

        Assert.Equal("Netflix", catalog.NetworkName);
        Assert.Equal("Korean", catalog.CountryName);
    }

    [Fact]
    public void MapCatalogShow_prefers_the_broadcaster_country_over_the_language()
    {
        var catalog = TvmazeJsonMapper.MapCatalogShow(Parse(WithNetworkCountry));

        Assert.Equal("NBC", catalog.NetworkName);
        Assert.Equal("United States", catalog.CountryName);
    }

    [Fact]
    public void MapCatalogShow_keeps_a_webChannel_country_it_does_have()
    {
        var catalog = TvmazeJsonMapper.MapCatalogShow(Parse(WithWebChannelCountry));

        Assert.Equal("Canada", catalog.CountryName);
    }

    [Fact]
    public void MapCatalogShow_falls_back_to_the_language_when_there_is_no_broadcaster_at_all()
    {
        // A show with no broadcaster still has a language worth showing — the facet and the card agree
        // because both read this one field.
        var catalog = TvmazeJsonMapper.MapCatalogShow(Parse(NoBroadcasterAtAll));

        Assert.Null(catalog.NetworkName);
        Assert.Equal("Japanese", catalog.CountryName);
    }

    [Fact]
    public void MapCatalogShow_leaves_the_country_null_when_nothing_is_known()
    {
        // Neither country nor language: the show genuinely has no country and must land under "Not set".
        var catalog = TvmazeJsonMapper.MapCatalogShow(Parse(
            """{"id":9,"name":"Silent","status":"Ended","type":"Scripted","network":null,"webChannel":null}"""));

        Assert.Null(catalog.CountryName);
        Assert.Null(catalog.Language);
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
