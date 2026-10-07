using Shows.Domain;

namespace Shows.Tests.Unit;

/// <summary>
/// Improvement 11: the details-page ordering comparators live in Domain so they are testable. Seasons
/// newest-first; episodes by air date desc then number desc, with nulls sorted last (never throwing).
/// </summary>
public class SeasonEpisodeOrderingTests
{
    private static Season Season(int number) => new() { Id = number, SeasonNumber = number };

    private static Episode Ep(int seasonNumber, int? number, DateOnly? airDate) =>
        new() { ShowId = 1, SeasonId = seasonNumber, SeasonNumber = seasonNumber, EpisodeNumber = number, AirDate = airDate };

    [Fact]
    public void Seasons_sort_by_number_descending()
    {
        var seasons = new[] { Season(1), Season(3), Season(0), Season(2) };

        var ordered = seasons.OrderBy(s => s, SeasonEpisodeOrdering.SeasonsDescending).Select(s => s.SeasonNumber).ToList();

        Assert.Equal(new[] { 3, 2, 1, 0 }, ordered);
    }

    [Fact]
    public void Episodes_sort_by_air_date_descending()
    {
        var episodes = new[]
        {
            Ep(1, 1, new DateOnly(2020, 1, 1)),
            Ep(1, 2, new DateOnly(2021, 6, 15)),
            Ep(1, 3, new DateOnly(2019, 12, 31)),
        };

        var ordered = episodes.OrderBy(e => e, SeasonEpisodeOrdering.EpisodesDescending).Select(e => e.EpisodeNumber).ToList();

        Assert.Equal(new int?[] { 2, 1, 3 }, ordered);
    }

    [Fact]
    public void Episodes_with_equal_air_date_sort_by_number_descending()
    {
        var same = new DateOnly(2021, 1, 1);
        var episodes = new[]
        {
            Ep(1, 2, same),
            Ep(1, 5, same),
            Ep(1, 1, same),
        };

        var ordered = episodes.OrderBy(e => e, SeasonEpisodeOrdering.EpisodesDescending).Select(e => e.EpisodeNumber).ToList();

        Assert.Equal(new int?[] { 5, 2, 1 }, ordered);
    }

    [Fact]
    public void Null_air_date_sorts_last_and_does_not_throw()
    {
        var episodes = new[]
        {
            Ep(1, 1, null),
            Ep(1, 2, new DateOnly(2020, 5, 5)),
            Ep(1, 3, null),
        };

        var ordered = episodes.OrderBy(e => e, SeasonEpisodeOrdering.EpisodesDescending).Select(e => e.EpisodeNumber).ToList();

        // The dated episode first; the two null-dated ones after, ordered by number desc among themselves.
        Assert.Equal(new int?[] { 2, 3, 1 }, ordered);
    }

    [Fact]
    public void Null_episode_number_sorts_last_within_equal_air_date()
    {
        var same = new DateOnly(2021, 1, 1);
        var episodes = new[]
        {
            Ep(1, null, same),
            Ep(1, 4, same),
        };

        var ordered = episodes.OrderBy(e => e, SeasonEpisodeOrdering.EpisodesDescending).Select(e => e.EpisodeNumber).ToList();

        Assert.Equal(new int?[] { 4, null }, ordered);
    }
}
