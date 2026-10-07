using Shows.Infrastructure.Import;
using Xunit;

namespace Shows.Tests.Unit;

/// <summary>
/// Step 2.2 acceptance for the page parser: the IMDb regex isolates the single id even though a real
/// page carries the link twice, the Kinopoisk rating-provider anchor is preferred over the watch-ad
/// noise, and a page with two distinct IMDb ids is flagged ambiguous rather than guessed through.
/// </summary>
public sealed class MyShowsPageParserTests
{
    [Fact]
    public void Single_imdb_id_is_extracted_even_when_the_link_appears_twice()
    {
        // A real page repeats the imdb link (anchor + tracking wrapper) but to the same id.
        var html = """
            <a href="https://www.imdb.com/title/tt2085059/">IMDb</a>
            <a class="rating" href="https://www.imdb.com/title/tt2085059/?ref_=tt">7.8</a>
            """;

        var (imdb, _, ambiguous) = MyShowsPageParser.Parse(html);
        Assert.Equal("tt2085059", imdb);
        Assert.False(ambiguous);
    }

    [Fact]
    public void Eight_digit_imdb_id_is_matched()
    {
        var (imdb, _, _) = MyShowsPageParser.Parse("""<a href="https://www.imdb.com/title/tt26448526/">x</a>""");
        Assert.Equal("tt26448526", imdb);
    }

    [Fact]
    public void Kinopoisk_rating_anchor_is_preferred_over_watch_ad_noise()
    {
        // hd.kinopoisk.ru/film/<hex> are watch-ads for other shows; the rating anchor is the real id.
        var html = """
            <a href="https://www.kinopoisk.ru/film/655800/"><img alt="kinopoisk logo"></a>
            <a href="https://hd.kinopoisk.ru/film/b04631f828704a91b2c4730cf1c32699?erid=x">watch</a>
            <a href="https://www.kinopoisk.ru/film/412342/watch/?utm_source=myshows">promo</a>
            """;

        var (_, kino, _) = MyShowsPageParser.Parse(html);
        Assert.Equal("655800", kino);
    }

    [Fact]
    public void Page_with_no_imdb_link_yields_null_not_ambiguous()
    {
        var (imdb, kino, ambiguous) = MyShowsPageParser.Parse("<html><body>nothing here</body></html>");
        Assert.Null(imdb);
        Assert.Null(kino);
        Assert.False(ambiguous);
    }

    [Fact]
    public void Two_distinct_imdb_ids_are_flagged_ambiguous()
    {
        // The markup-change guard: two different ids must not be silently resolved to one.
        var html = """
            <a href="https://www.imdb.com/title/tt1111111/">a</a>
            <a href="https://www.imdb.com/title/tt2222222/">b</a>
            """;

        var (imdb, _, ambiguous) = MyShowsPageParser.Parse(html);
        Assert.True(ambiguous);
        Assert.Null(imdb);
    }
}
