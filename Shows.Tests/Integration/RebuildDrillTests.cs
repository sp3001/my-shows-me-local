using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shows.Application;
using Shows.Domain;
using Shows.Infrastructure.Persistence;
using Shows.Infrastructure.Persistence.Sources;
using Xunit;

namespace Shows.Tests.Integration;

/// <summary>
/// Step 1.5's rebuild drill (SCHEMA.md §2a) — the test the plan says to expect to fail the first time,
/// because the failure is the bug that would otherwise surface weeks later as orphaned watch marks.
/// </summary>
/// <remarks>
/// <para>
/// The property §2a names is: re-mapping after a mapper bug must not require restoring user data. The
/// plan's literal wording ("delete episode, season, tracked_show … re-run the mapper") is <b>not</b>
/// the sound form of that property, and the reason is exactly the reason §2a gives for rejecting
/// wipe-and-reload: <c>user_show</c> cascades from <c>tracked_show</c> and <c>episode_watch</c>
/// cascades from <c>episode</c>, so deleting the mapped tables deletes the user data the drill is
/// meant to protect — and with client-generated ids (ADR-027) a deleted episode's id cannot be
/// re-derived, so the watch row is orphaned no matter how the mapper is written.
/// </para>
/// <para>
/// The sound drill therefore tests the load-bearing invariant directly: re-running the mapper over
/// every <c>user_show</c> row leaves every surrogate id unchanged and the three user tables
/// byte-identical. That is what "the mapper upserts on natural keys with immutable ids" means, and it
/// is asserted on the ids, not the counts — a count that matches while the ids differ is the exact
/// silent corruption this drill exists to catch.
/// </para>
/// </remarks>
public sealed class RebuildDrillTests : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private readonly DatabaseFixture _fixture;

    // One generator per test, shared across every mapper construction, so ids never collide between
    // the seed maps and the rebuild map. A rebuild that reuses an existing row mints nothing; a rebuild
    // that wrongly re-inserts mints a fresh id from this same stream — distinguishable from the original.
    private readonly SequentialIdGenerator _ids = new();

    public RebuildDrillTests(DatabaseFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {
        _fixture.Reset();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Re_running_the_mapper_over_every_show_leaves_ids_and_user_data_unchanged()
    {
        // Two shows so "over every user_show row" is a real loop, not a single call.
        await SeedMappedShowAsync(tvmazeId: 305, showId: 8_000_001, payload: Payload(305, "Black Mirror"));
        await SeedMappedShowAsync(tvmazeId: 400, showId: 8_000_002, payload: Payload(400, "Severance"));

        // Give the user something irreplaceable to lose: watch marks and per-season UI state.
        await using (var context = _fixture.CreateContext())
        {
            var episodes = await context.Episodes.ToListAsync();
            var id = 9_000_000;
            foreach (var ep in episodes.Take(3))
            {
                context.EpisodeWatches.Add(new EpisodeWatch
                {
                    Id = ++id,
                    ShowId = ep.ShowId,
                    EpisodeId = ep.Id,
                    WatchedAt = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero),
                });
            }

            var seasons = await context.Seasons.ToListAsync();
            id = 0;
            foreach (var season in seasons)
            {
                context.UiSeasonStates.Add(new UiSeasonState
                {
                    Id = 9_500_000 + ++id,
                    ShowId = season.ShowId,
                    SeasonId = season.Id,
                    IsExpanded = id % 2 == 0,
                });
            }

            await context.SaveChangesAsync();
        }

        // Snapshot the irreplaceable data and the surrogate ids BEFORE the rebuild.
        var watchBefore = await SnapshotWatchesAsync();
        var userBefore = await SnapshotUserShowsAsync();
        var uiBefore = await SnapshotUiStateAsync();
        var episodeIdsBefore = await SnapshotEpisodeIdsAsync();
        var seasonIdsBefore = await SnapshotSeasonIdsAsync();

        // The rebuild: re-run the mapper over every user_show row, exactly as a post-bug rebuild would.
        await RebuildAllShowsAsync();

        // Assert on the ids, not the counts. A matching count with different ids is the silent
        // orphaning this drill exists to catch.
        Assert.Equal(episodeIdsBefore, await SnapshotEpisodeIdsAsync());
        Assert.Equal(seasonIdsBefore, await SnapshotSeasonIdsAsync());
        Assert.Equal(watchBefore, await SnapshotWatchesAsync());
        Assert.Equal(userBefore, await SnapshotUserShowsAsync());
        Assert.Equal(uiBefore, await SnapshotUiStateAsync());
    }

    [Fact]
    public async Task Delete_then_remap_orphans_the_watch_mark_proving_upsert_is_required()
    {
        // The counter-example that makes the drill meaningful: if the mapped rows ARE deleted and
        // re-inserted, the re-minted episode id no longer matches the watch row's episode_id. This is
        // the bug §2a forbids, demonstrated so a future reader cannot "simplify" the mapper back into
        // delete-and-reinsert and still see green tests.
        await SeedMappedShowAsync(tvmazeId: 305, showId: 8_100_001, payload: Payload(305, "Black Mirror"));

        long watchedEpisodeId;
        await using (var context = _fixture.CreateContext())
        {
            var ep = await context.Episodes.FirstAsync(e => e.ShowId == 8_100_001);
            watchedEpisodeId = ep.Id;
            context.EpisodeWatches.Add(new EpisodeWatch
            {
                Id = 9_100_001,
                ShowId = 8_100_001,
                EpisodeId = watchedEpisodeId,
                WatchedAt = DateTimeOffset.UtcNow,
            });
            await context.SaveChangesAsync();
        }

        // Simulate the forbidden wipe: delete the mapped episode rows (cascading the watch away), then
        // re-map. The new episode gets a fresh id; the watch row is gone (cascade), so the user's mark
        // is lost even though a row count could be made to match.
        await using (var context = _fixture.CreateContext())
        {
            context.Episodes.RemoveRange(await context.Episodes
                .Where(e => e.ShowId == 8_100_001).ToListAsync());
            await context.SaveChangesAsync();
        }

        await MapAsync(Payload(305, "Black Mirror"), showId: 8_100_001);

        await using (var context = _fixture.CreateContext())
        {
            // The watch mark no longer exists — it was cascade-deleted with the episode it pointed at.
            Assert.False(await context.EpisodeWatches.AnyAsync(w => w.EpisodeId == watchedEpisodeId));
        }
    }

    // ---- helpers ----

    private async Task RebuildAllShowsAsync()
    {
        // The payloads a real rebuild would re-fetch; keyed by tvmaze id here.
        var payloads = new Dictionary<int, TvmazePayload>
        {
            [305] = Payload(305, "Black Mirror"),
            [400] = Payload(400, "Severance"),
        };

        await using var context = _fixture.CreateContext();
        var tvmazeIds = await context.UserShows
            .Join(context.TrackedShows, u => u.TrackedShowId, s => s.Id, (u, s) => s.TvmazeId)
            .ToListAsync();

        var mapper = new TvmazeShowMapper(
            context, _ids, NullLogger<TvmazeShowMapper>.Instance);

        foreach (var tvmazeId in tvmazeIds)
        {
            await mapper.MapShowAsync(payloads[tvmazeId], default);
        }

        await context.SaveChangesAsync();
    }

    private async Task SeedMappedShowAsync(int tvmazeId, long showId, TvmazePayload payload)
    {
        await using (var context = _fixture.CreateContext())
        {
            context.TvmazeShows.Add(new TvmazeShow
            {
                TvmazeId = tvmazeId,
                StatusRaw = string.Empty,
                RawJson = "{}",
                FetchedAt = DateTimeOffset.UtcNow,
            });
            var show = new TrackedShow { Id = showId, TvmazeId = tvmazeId };
            context.Entry(show).Property("tvmaze_show_id").CurrentValue = tvmazeId;
            show.User = new UserShow
            {
                TrackedShowId = showId,
                Category = ShowCategory.Started,
                AddedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                CategoryChangedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            };
            context.TrackedShows.Add(show);
            await context.SaveChangesAsync();
        }

        await MapAsync(payload, showId);
    }

    private async Task MapAsync(TvmazePayload payload, long showId)
    {
        await using var context = _fixture.CreateContext();
        var mapper = new TvmazeShowMapper(
            context, _ids, NullLogger<TvmazeShowMapper>.Instance);
        await mapper.MapShowAsync(payload, default);
        await context.SaveChangesAsync();
    }

    // Snapshots are ordered projections so equality compares content, not object identity.

    private async Task<List<(long Id, long ShowId, long EpisodeId, DateTimeOffset WatchedAt)>> SnapshotWatchesAsync()
    {
        await using var context = _fixture.CreateContext();
        return await context.EpisodeWatches
            .OrderBy(w => w.Id)
            .Select(w => new ValueTuple<long, long, long, DateTimeOffset>(w.Id, w.ShowId, w.EpisodeId, w.WatchedAt))
            .ToListAsync();
    }

    private async Task<List<(long Id, ShowCategory Category, DateTimeOffset AddedAt, DateTimeOffset CategoryChangedAt, bool NeedsReview)>> SnapshotUserShowsAsync()
    {
        await using var context = _fixture.CreateContext();
        return await context.UserShows
            .OrderBy(u => u.TrackedShowId)
            .Select(u => new ValueTuple<long, ShowCategory, DateTimeOffset, DateTimeOffset, bool>(
                u.TrackedShowId, u.Category, u.AddedAt, u.CategoryChangedAt, u.NeedsReview))
            .ToListAsync();
    }

    private async Task<List<(long Id, long ShowId, long SeasonId, bool IsExpanded)>> SnapshotUiStateAsync()
    {
        await using var context = _fixture.CreateContext();
        return await context.UiSeasonStates
            .OrderBy(u => u.Id)
            .Select(u => new ValueTuple<long, long, long, bool>(u.Id, u.ShowId, u.SeasonId, u.IsExpanded))
            .ToListAsync();
    }

    private async Task<List<long>> SnapshotEpisodeIdsAsync()
    {
        await using var context = _fixture.CreateContext();
        return await context.Episodes.OrderBy(e => e.Id).Select(e => e.Id).ToListAsync();
    }

    private async Task<List<long>> SnapshotSeasonIdsAsync()
    {
        await using var context = _fixture.CreateContext();
        return await context.Seasons.OrderBy(s => s.Id).Select(s => s.Id).ToListAsync();
    }

    private static TvmazePayload Payload(int tvmazeId, string name) => new(
        new TvmazeShow
        {
            TvmazeId = tvmazeId,
            Name = name,
            TypeRaw = "Scripted",
            StatusRaw = "Running",
            GenresRaw = """["Drama"]""",
            RawJson = "{}",
            FetchedAt = DateTimeOffset.UtcNow,
        },
        new List<TvmazeEpisode>
        {
            new()
            {
                TvmazeEpisodeId = tvmazeId * 100 + 1,
                TvmazeShowId = tvmazeId,
                Name = "S1E1",
                SeasonNumber = 1,
                Number = 1,
                TypeRaw = "regular",
                Airdate = "2020-01-01",
                Airstamp = "2020-01-01T21:00:00+00:00",
                RawJson = "{}",
                FetchedAt = DateTimeOffset.UtcNow,
            },
            new()
            {
                TvmazeEpisodeId = tvmazeId * 100 + 2,
                TvmazeShowId = tvmazeId,
                Name = "S1E2",
                SeasonNumber = 1,
                Number = 2,
                TypeRaw = "regular",
                Airdate = "2020-01-08",
                Airstamp = "2020-01-08T21:00:00+00:00",
                RawJson = "{}",
                FetchedAt = DateTimeOffset.UtcNow,
            },
        });

    private sealed class SequentialIdGenerator : IIdGenerator
    {
        private long _next = 2_000_000;
        public long NextId() => _next++;
    }
}
