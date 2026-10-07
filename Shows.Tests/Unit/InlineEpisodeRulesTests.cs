using Shows.Domain;

namespace Shows.Tests.Unit;

/// <summary>
/// Improvement 4: the inline list on a My Shows card shows only episodes that are unwatched AND
/// already-aired regulars. The predicate lives in Domain so it is testable and shared by the card's
/// eager (expanded-season) and lazy (collapsed-season) load paths.
/// </summary>
public class InlineEpisodeRulesTests
{
    private static readonly DateOnly Today = new(2026, 9, 30);

    private static Episode Ep(
        EpisodeType type = EpisodeType.Regular,
        DateOnly? airDate = null) =>
        new()
        {
            ShowId = 1,
            SeasonId = 1,
            SeasonNumber = 1,
            EpisodeNumber = 1,
            EpisodeType = type,
            AirDate = airDate ?? new DateOnly(2026, 9, 1),
        };

    [Fact]
    public void Unwatched_aired_regular_is_listable()
    {
        Assert.True(InlineEpisodeRules.IsListable(Ep(), isWatched: false, Today));
    }

    [Fact]
    public void Watched_episode_is_not_listable()
    {
        Assert.False(InlineEpisodeRules.IsListable(Ep(), isWatched: true, Today));
    }

    [Fact]
    public void Insignificant_special_is_never_listable_even_when_unwatched_and_aired()
    {
        Assert.False(InlineEpisodeRules.IsListable(Ep(EpisodeType.InsignificantSpecial), isWatched: false, Today));
    }

    [Fact]
    public void Significant_special_is_listable_like_a_regular()
    {
        // ADR-034: a significant special counts toward progress and appears in the season grid, so it
        // belongs in the card's "what is left to watch" list too.
        Assert.True(InlineEpisodeRules.IsListable(Ep(EpisodeType.Special), isWatched: false, Today));
    }

    [Fact]
    public void Unaired_episode_is_not_listable()
    {
        var future = new Episode { AirDate = new DateOnly(2026, 12, 25) };
        Assert.False(InlineEpisodeRules.IsListable(future, isWatched: false, Today));
    }

    [Fact]
    public void Unknown_air_date_is_not_listable()
    {
        var noDate = new Episode { ShowId = 1, SeasonId = 1, SeasonNumber = 1, EpisodeNumber = 1, EpisodeType = EpisodeType.Regular, AirDate = null };
        Assert.False(InlineEpisodeRules.IsListable(noDate, isWatched: false, Today));
    }

    [Fact]
    public void Episode_airing_exactly_today_is_listable()
    {
        var today = new Episode { AirDate = Today };
        Assert.True(InlineEpisodeRules.IsListable(today, isWatched: false, Today));
    }

    [Fact]
    public void Unknown_type_is_listable_when_aired_and_unwatched()
    {
        // The inline filter excludes only specials; an Unknown-type episode with a known past air date is
        // still "left to watch". (Progress math treats Unknown as not-regular, but that is a separate rule.)
        Assert.True(InlineEpisodeRules.IsListable(Ep(EpisodeType.Unknown), isWatched: false, Today));
    }
}
