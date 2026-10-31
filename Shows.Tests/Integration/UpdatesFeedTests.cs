using Microsoft.EntityFrameworkCore;
using Shows.Application;
using Shows.Application.UseCases;
using Shows.Domain;
using Shows.Infrastructure.Persistence;
using Shows.Infrastructure.Persistence.Repositories;
using Shows.Infrastructure.Persistence.Sources;
using Xunit;

namespace Shows.Tests.Integration;

/// <summary>
/// The Updates tab's read (improvement 25) over the transition dates ADR-038 stamps. The assertions are
/// the ones that fail silently when wrong: a show that re-ended still listed as renewed, an untracked
/// show's watch marks leaking into Recently Watched, or a section ordered oldest-first.
/// </summary>
public sealed class UpdatesFeedTests : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    // A fixed clock so the 60/30-day windows are exact rather than "whenever the test ran".
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    private readonly DatabaseFixture _fixture;

    public UpdatesFeedTests(DatabaseFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _fixture.Reset();
        await SeedAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Ended_section_lists_terminal_shows_inside_the_window_newest_first()
    {
        var feed = await RunAsync();

        // Newest first: renewed-reended re-ended 2 days ago, then canceled-recent, ended-recent, ended-40d.
        // ended-90d is outside the 60-day window and renewed-airing is airable again, so neither appears.
        Assert.Equal(["Renewed-reended", "Canceled-recent", "Ended-recent", "Ended-40d"],
            feed.Ended.Select(r => r.Show.Title));
        Assert.Equal(Now.AddDays(-2), feed.Ended[0].At);
    }

    [Fact]
    public async Task A_Canceled_show_appears_in_the_Ended_section()
    {
        // The user's instruction: a cancelled show is in fact ended, so terminal is the set {Ended,
        // Canceled} from ShowStatusRules — not Ended alone.
        var feed = await RunAsync();

        Assert.Contains("Canceled-recent", feed.Ended.Select(r => r.Show.Title));
    }

    [Fact]
    public async Task Renewed_section_lists_shows_that_left_terminal_and_stay_airable()
    {
        var feed = await RunAsync();

        // Renewed-airing qualifies. Renewed-reended does NOT: its StatusRenewedAt is recent, but it has
        // since ended again, so calling it renewed would be wrong — and it appears in Ended instead.
        Assert.Equal(["Renewed-airing"], feed.Renewed.Select(r => r.Show.Title));
        Assert.Equal(ShowStatus.Running, feed.Renewed[0].Show.Status);
        Assert.Contains("Renewed-reended", feed.Ended.Select(r => r.Show.Title));
    }

    [Fact]
    public async Task Updated_section_uses_the_provider_timestamp_inside_30_days()
    {
        var feed = await RunAsync();

        // Updated-5d qualifies; updated-45d is outside the 30-day window. The source is the mapped copy of
        // TVmaze's own 'updated', never tracked_show.mapped_at, which also moves on our own recomputes.
        Assert.Equal(["Updated-5d"], feed.Updated.Select(r => r.Show.Title));
        Assert.Equal(Now.AddDays(-5), feed.Updated[0].At);
    }

    [Fact]
    public async Task Recently_watched_lists_episodes_newest_first_with_show_and_episode()
    {
        var feed = await RunAsync();

        // Three rows: watched-45d is outside the window and untracked-watched's show is soft-deleted. The
        // on-cutoff mark is included, so it sorts last.
        Assert.Equal(["Watched-yesterday", "Watched-10d", "On-cutoff"],
            feed.RecentlyWatched.Select(r => r.Show.Title));

        var newest = feed.RecentlyWatched[0];
        Assert.Equal("Ep-yesterday", newest.Episode.Title);
        Assert.Equal(1, newest.Episode.SeasonNumber);
        Assert.Equal(2, newest.Episode.EpisodeNumber);
        Assert.Equal(Now.AddDays(-1), newest.WatchedAt);
    }

    [Fact]
    public async Task An_untracked_shows_watch_marks_do_not_appear()
    {
        // ADR-024: episode_watch survives untracking, so the rows exist whether or not the user still
        // tracks the show. The section reads through an active-only lookup, so a soft-deleted show's mark
        // drops out rather than listing a show the user removed.
        var feed = await RunAsync();

        Assert.DoesNotContain("Untracked-watched", feed.RecentlyWatched.Select(r => r.Show.Title));
    }

    [Fact]
    public async Task A_watch_mark_exactly_on_the_cutoff_is_included()
    {
        // The raw-SQL cutoff is passed at second precision precisely so the boundary second is inclusive.
        // A provider-formatted parameter (7 fraction digits) would compare the whole-second row as LESS
        // than the cutoff and drop it — the shape mismatch this read works around.
        var feed = await RunAsync();

        Assert.Contains("On-cutoff", feed.RecentlyWatched.Select(r => r.Show.Title));
    }

    [Fact]
    public async Task A_show_with_no_transition_dates_appears_in_no_show_section()
    {
        // A plain airing show the user just added: no end date, no renewal, no provider timestamp inside
        // the window. It must not surface anywhere — the bug this guards is stamping a renewal on every
        // newly-mapped show.
        var feed = await RunAsync();

        var titles = feed.Ended.Select(r => r.Show.Title)
            .Concat(feed.Renewed.Select(r => r.Show.Title))
            .Concat(feed.Updated.Select(r => r.Show.Title));
        Assert.DoesNotContain("Plain-airing", titles);
    }

    [Fact]
    public async Task Sections_are_empty_when_nothing_is_recent()
    {
        // A clock far in the future puts every seeded event outside both windows, so all four sections
        // empty. The UI hides an empty section entirely; this is the read half of that.
        await using var context = _fixture.CreateContext();
        var useCase = new GetUpdatesFeedUseCase(
            new TrackedShowRepository(context), new EpisodeRepository(context));

        var feed = await useCase.ExecuteAsync(Now.AddYears(5), default);

        Assert.Empty(feed.Ended);
        Assert.Empty(feed.Renewed);
        Assert.Empty(feed.Updated);
        Assert.Empty(feed.RecentlyWatched);
    }

    // ---- helpers ----

    private async Task<UpdatesFeed> RunAsync()
    {
        await using var context = _fixture.CreateContext();
        var useCase = new GetUpdatesFeedUseCase(
            new TrackedShowRepository(context), new EpisodeRepository(context));
        return await useCase.ExecuteAsync(Now, default);
    }

    private async Task SeedAsync()
    {
        await using var context = _fixture.CreateContext();
        var id = 9_000_000L;

        AddShow(ShowStatus.Ended, statusChangedAt: Now.AddDays(-10), title: "Ended-recent");
        AddShow(ShowStatus.Ended, statusChangedAt: Now.AddDays(-40), title: "Ended-40d");
        AddShow(ShowStatus.Ended, statusChangedAt: Now.AddDays(-90), title: "Ended-90d");
        AddShow(ShowStatus.Canceled, statusChangedAt: Now.AddDays(-3), title: "Canceled-recent");
        AddShow(ShowStatus.Running, statusChangedAt: Now.AddDays(-200),
            statusRenewedAt: Now.AddDays(-12), title: "Renewed-airing");
        AddShow(ShowStatus.Ended, statusChangedAt: Now.AddDays(-2),
            statusRenewedAt: Now.AddDays(-8), title: "Renewed-reended");
        AddShow(ShowStatus.Running, tvmazeUpdatedAt: Now.AddDays(-5), title: "Updated-5d");
        AddShow(ShowStatus.Running, tvmazeUpdatedAt: Now.AddDays(-45), title: "Updated-45d");
        AddShow(ShowStatus.Running, title: "Plain-airing");
        // The four shows the watch marks hang off. The last one is soft-deleted, which is exactly the
        // ADR-024 case: its episode_watch rows survive untracking.
        AddShow(ShowStatus.Running, title: "Watched-yesterday");
        AddShow(ShowStatus.Running, title: "Watched-10d");
        AddShow(ShowStatus.Running, title: "Watched-45d");
        AddShow(ShowStatus.Running, title: "On-cutoff");
        AddShow(ShowStatus.Running, title: "Untracked-watched", deleted: true);

        // Watched rows: two inside 30 days, one outside, one on an untracked show, one exactly on the
        // cutoff. Each needs a real episode row because the section resolves ids to episodes.
        Watch("Watched-yesterday", Now.AddDays(-1), "Ep-yesterday", season: 1, number: 2);
        Watch("Watched-10d", Now.AddDays(-10), "Ep-10d", season: 2, number: 1);
        Watch("Watched-45d", Now.AddDays(-45), "Ep-45d", season: 1, number: 1);
        // The untracked one's show is soft-deleted by AddShow below; the watch row still exists, which is
        // exactly the ADR-024 behavior the section has to defend against.
        Watch("Untracked-watched", Now.AddDays(-2), "Ep-untracked", season: 1, number: 1);
        Watch("On-cutoff", Now.AddDays(-GetUpdatesFeedUseCase.RecentWindowDays), "Ep-cutoff",
            season: 1, number: 1);

        await context.SaveChangesAsync();

        void AddShow(
            ShowStatus status,
            DateTimeOffset? statusChangedAt = null,
            DateTimeOffset? statusRenewedAt = null,
            DateTimeOffset? tvmazeUpdatedAt = null,
            string title = "Show",
            bool deleted = false)
        {
            var showId = ++id;
            var tvmazeId = (int)showId;

            context.TvmazeShows.Add(new TvmazeShow
            {
                TvmazeId = tvmazeId,
                StatusRaw = "n/a",
                RawJson = "{}",
                FetchedAt = Now,
            });

            var show = new TrackedShow
            {
                Id = showId,
                TvmazeId = tvmazeId,
                Title = title,
                Status = status,
                StatusChangedAt = statusChangedAt,
                StatusRenewedAt = statusRenewedAt,
                TvmazeUpdatedAt = tvmazeUpdatedAt,
            };
            context.Entry(show).Property("tvmaze_show_id").CurrentValue = tvmazeId;
            show.User = new UserShow
            {
                TrackedShowId = showId,
                Category = ShowCategory.Started,
                AddedAt = Now.AddDays(-300),
                CategoryChangedAt = Now.AddDays(-300),
                DeletedAt = deleted ? Now.AddDays(-1) : null,
            };
            context.TrackedShows.Add(show);
        }

        void Watch(string showTitle, DateTimeOffset watchedAt, string episodeTitle, int season, int number)
        {
            var show = context.TrackedShows.Local.Single(s => s.Title == showTitle);
            var seasonRow = new Season
            {
                Id = ++id,
                ShowId = show.Id,
                SeasonNumber = season,
            };
            context.Seasons.Add(seasonRow);

            var episode = new Episode
            {
                Id = ++id,
                ShowId = show.Id,
                SeasonId = seasonRow.Id,
                SeasonNumber = season,
                EpisodeNumber = number,
                Title = episodeTitle,
                EpisodeType = EpisodeType.Regular,
            };
            context.Episodes.Add(episode);

            context.EpisodeWatches.Add(new EpisodeWatch
            {
                Id = ++id,
                ShowId = show.Id,
                EpisodeId = episode.Id,
                WatchedAt = watchedAt,
            });
        }
    }
}
