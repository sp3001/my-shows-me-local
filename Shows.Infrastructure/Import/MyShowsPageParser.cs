using System.Text.RegularExpressions;

namespace Shows.Infrastructure.Import;

/// <summary>
/// Extracts the IMDb (and, when present, Kinopoisk) id from a myshows.me show page
/// (<c>https://myshows.me/view/{id}/</c>). Pure — it takes HTML text and returns ids, doing no I/O, so
/// the markup contract is unit-testable against captured page fragments without a network call.
/// </summary>
/// <remarks>
/// <para>
/// The IMDb link sits in the page's ratings block as <c>imdb.com/title/ttNNNNNNN/</c>. It appears twice
/// on a real page (the link and its tracking wrapper) but always to the same id, so the parser takes the
/// distinct match — more than one distinct id would mean the markup changed and the caller must treat
/// that as a failure, not pick one (step 2.2: "do not silently fall back").
/// </para>
/// <para>
/// Kinopoisk is noisier: <c>hd.kinopoisk.ru/film/&lt;hex&gt;</c> links are watch-ads for <em>other</em>
/// shows and <c>www.kinopoisk.ru/film/&lt;id&gt;/watch/</c> is a promo. The one reliable signal is the
/// rating-provider anchor <c>href="https://www.kinopoisk.ru/film/&lt;digits&gt;/"</c> (verified against
/// the live pages for 22410 and 86813). Kinopoisk is best-effort enrichment — its absence is not an error.
/// </para>
/// </remarks>
internal static partial class MyShowsPageParser
{
    [GeneratedRegex(@"imdb\.com/title/(tt\d{7,8})/")]
    private static partial Regex ImdbRegex();

    [GeneratedRegex(@"href=""https://www\.kinopoisk\.ru/film/(\d+)/""")]
    private static partial Regex KinopoiskRegex();

    /// <summary>
    /// Parse a page. <paramref name="imdbId"/> is the single distinct IMDb id, or null when the page
    /// carries none. <paramref name="ambiguous"/> is true when more than one distinct IMDb id matched —
    /// a markup change the caller must surface rather than guess through.
    /// </summary>
    public static (string? ImdbId, string? KinopoiskId, bool Ambiguous) Parse(string html)
    {
        var imdbIds = ImdbRegex().Matches(html)
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var kinopoisk = KinopoiskRegex().Matches(html)
            .Select(m => m.Groups[1].Value)
            .FirstOrDefault();

        return imdbIds.Count switch
        {
            0 => (null, kinopoisk, false),
            1 => (imdbIds[0], kinopoisk, false),
            _ => (null, kinopoisk, true),
        };
    }
}
