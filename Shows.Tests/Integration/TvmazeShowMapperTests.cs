using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shows.Application;
using Shows.Domain;
using Shows.Infrastructure.Persistence;
using Shows.Infrastructure.Persistence.Sources;
using Xunit;

namespace Shows.Tests.Integration;

/// <summary>
/// Step 1.5 acceptance for <see cref="TvmazeShowMapper"/> — the single mapped-row write path
/// (SPEC R11, ADR-023). The fixtures are the real Black Mirror payloads from DATA_ANALYSIS.md §8; the
/// assertions are the ones that fail silently when wrong: a status mapped to the wrong enum, a runtime
/// collapsed into its average, or a re-map that regenerates an episode id and orphans a watch mark.
/// </summary>
public sealed class TvmazeShowMapperTests : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    // Black Mirror: TVmaze id 305, myshows id 22410 (the two id spaces never overlap — §8 fact 1).
    private const int BlackMirrorTvmazeId = 305;

    private readonly DatabaseFixture _fixture;
    private long _showId;

    public TvmazeShowMapperTests(DatabaseFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {
        _fixture.Reset();
        _showId = 7_000_001;
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Black_mirror_fixture_maps_to_the_expected_mapped_row()
    {
        await SeedTrackedShellAsync(BlackMirrorTvmazeId, _showId);
        await MapAsync(BlackMirrorPayload());

        await using var context = _fixture.CreateContext();
        var show = await context.TrackedShows.SingleAsync(s => s.TvmazeId == BlackMirrorTvmazeId);

        // status "Running" → Running, NOT Ended. This is the original bug: a predicate that mistook
        // the airing status for a finished one would auto-finish every running show (SPEC R12).
        Assert.Equal(ShowStatus.Running, show.Status);
        Assert.Equal("Black Mirror", show.Title);
        Assert.Equal("Scripted", show.ShowType);

        // premiered is a real ISO date, not a year (§8 fact 4).
        Assert.Equal(new DateOnly(2011, 12, 4), show.PremiereDate);

        // runtime null and average_runtime 62 are BOTH preserved — never collapsed at map time
        // (§8 fact 3).
        Assert.Null(show.RuntimeMinutes);
        Assert.Equal(62, show.AverageRuntimeMinutes);

        // externals.imdb → imdb_id (§8 fact 5).
        Assert.Equal("tt2085059", show.ImdbId);

        // summary HTML is stripped on the mapped copy; the raw markup stays in the source table.
        Assert.DoesNotContain("<p>", show.Description, StringComparison.Ordinal);
        var source = await context.TvmazeShows.SingleAsync(s => s.TvmazeId == BlackMirrorTvmazeId);
        Assert.Contains("<p>", source.SummaryHtml, StringComparison.Ordinal);

        // Improvement 21: Netflix carries no country in this payload, so the mapped country falls back to
        // the language. The source row keeps the payload's emptiness — the fallback is a display rule.
        Assert.Equal("English", show.Country);
        Assert.Null(source.CountryName);
        Assert.Null(source.CountryCode);
    }

    [Fact]
    public async Task Broadcaster_country_beats_the_language_on_the_mapped_row()
    {
        // The order matters: a show TVmaze does give a country to must not be downgraded to its language.
        await SeedTrackedShellAsync(BlackMirrorTvmazeId, _showId);
        var payload = BlackMirrorPayload();
        payload.Show.NetworkName = "FOX";
        payload.Show.CountryName = "United States";
        payload.Show.CountryCode = "US";
        await MapAsync(payload);

        await using var context = _fixture.CreateContext();
        var show = await context.TrackedShows.SingleAsync(s => s.TvmazeId == BlackMirrorTvmazeId);
        var source = await context.TvmazeShows.SingleAsync(s => s.TvmazeId == BlackMirrorTvmazeId);

        Assert.Equal("United States", show.Country);
        Assert.Equal("United States", source.CountryName); // verbatim, unchanged by the display rule
        Assert.Equal("US", source.CountryCode);
    }

    [Fact]
    public async Task A_show_with_neither_country_nor_language_maps_to_a_null_country()
    {
        // Genuinely unknown must stay null, not become an empty string — the Discovery facet distinguishes
        // "Not set" from a value, and "" would read as a value while rendering nothing.
        await SeedTrackedShellAsync(BlackMirrorTvmazeId, _showId);
        var payload = BlackMirrorPayload();
        payload.Show.Language = null;
        await MapAsync(payload);

        await using var context = _fixture.CreateContext();
        var show = await context.TrackedShows.SingleAsync(s => s.TvmazeId == BlackMirrorTvmazeId);
        Assert.Null(show.Country);
    }

    [Fact]
    public async Task Remapping_an_unchanged_payload_does_not_report_the_country_as_a_change()
    {
        // Idempotence covers the new column too: a second map of the same payload must write nothing, or
        // job_run.items_changed would lie about every refresh run.
        await SeedTrackedShellAsync(BlackMirrorTvmazeId, _showId);
        await MapAsync(BlackMirrorPayload());
        var second = await MapAsync(BlackMirrorPayload());

        Assert.False(second.AnyChange);
    }

    [Fact]
    public async Task Unrecognised_status_fails_closed_to_Unknown_and_records_the_gap()    {
        // The fail-closed test the plan calls the most important in the project: an unseen status must
        // land on Unknown (never a guess) AND leave a tvmaze_status_seen row with mapped_to NULL, so
        // the dictionary gap is visible rather than silent (SPEC R12).
        await SeedTrackedShellAsync(BlackMirrorTvmazeId, _showId);
        var payload = BlackMirrorPayload();
        payload.Show.StatusRaw = "On Hiatus";
        await MapAsync(payload);

        await using var context = _fixture.CreateContext();
        var show = await context.TrackedShows.SingleAsync(s => s.TvmazeId == BlackMirrorTvmazeId);
        Assert.Equal(ShowStatus.Unknown, show.Status);

        var seen = await context.TvmazeStatusSeen.SingleAsync(s => s.StatusRaw == "On Hiatus");
        Assert.Null(seen.MappedTo);
    }

    [Fact]
    public async Task Known_status_records_a_non_null_mapped_to()
    {
        await SeedTrackedShellAsync(BlackMirrorTvmazeId, _showId);
        await MapAsync(BlackMirrorPayload());

        await using var context = _fixture.CreateContext();
        var seen = await context.TvmazeStatusSeen.SingleAsync(s => s.StatusRaw == "Running");
        Assert.Equal((int)ShowStatus.Running, seen.MappedTo);
    }

    [Fact]
    public async Task Remapping_a_show_the_user_marked_watched_leaves_every_mark_intact()
    {
        await SeedTrackedShellAsync(BlackMirrorTvmazeId, _showId);
        await MapAsync(BlackMirrorPayload());

        long episodeId;
        await using (var context = _fixture.CreateContext())
        {
            var first = await context.Episodes
                .Where(e => e.ShowId == _showId)
                .OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber)
                .FirstAsync();
            episodeId = first.Id;
            context.EpisodeWatches.Add(new EpisodeWatch
            {
                Id = 5_000_001,
                ShowId = _showId,
                EpisodeId = episodeId,
                WatchedAt = DateTimeOffset.UtcNow,
            });
            await context.SaveChangesAsync();
        }

        // Re-map the same payload. The episode id must not change — episode_watch points at it, and a
        // regenerated id would silently orphan the mark while every row count still looked right.
        await MapAsync(BlackMirrorPayload());

        await using (var context = _fixture.CreateContext())
        {
            var watch = await context.EpisodeWatches.SingleAsync(w => w.ShowId == _showId);
            Assert.Equal(episodeId, watch.EpisodeId);
        }
    }

    [Fact]
    public async Task Mapping_the_same_payload_twice_changes_nothing()
    {
        await SeedTrackedShellAsync(BlackMirrorTvmazeId, _showId);
        var first = await MapAsync(BlackMirrorPayload());
        Assert.True(first.AnyChange); // the first map writes the show and its episodes

        var second = await MapAsync(BlackMirrorPayload());
        Assert.False(second.AnyChange); // idempotent: nothing measurable changed
        Assert.Equal(MapResult.Unchanged, second);
    }

    [Fact]
    public async Task Mapping_a_show_that_is_not_tracked_writes_only_source_rows()
    {
        // SPEC P2 / step 1.5: refreshing a source row is not the same as tracking a show. With no
        // tracked_show row, the mapper writes the source rows and returns Unchanged — it never creates
        // a tracked_show.
        var result = await MapAsync(BlackMirrorPayload());

        Assert.Equal(MapResult.Unchanged, result);
        await using var context = _fixture.CreateContext();
        Assert.False(await context.TrackedShows.AnyAsync(s => s.TvmazeId == BlackMirrorTvmazeId));
        Assert.True(await context.TvmazeShows.AnyAsync(s => s.TvmazeId == BlackMirrorTvmazeId));
    }

    [Fact]
    public async Task Cached_columns_reflect_watched_state_after_mapping()
    {
        await SeedTrackedShellAsync(BlackMirrorTvmazeId, _showId);
        await MapAsync(BlackMirrorPayload());

        // Mark every regular episode watched, then re-map with an ENDED payload. The mapper recomputes
        // the cached columns (SPEC R5/R7) reading episode_watch but never writing it; with all regulars
        // watched and a status that means "no more episodes expected", auto_finish_eligible becomes
        // true. (Re-mapping the Running payload would correctly reset status to Running and clear it.)
        await using (var context = _fixture.CreateContext())
        {
            var regulars = await context.Episodes
                .Where(e => e.ShowId == _showId && e.EpisodeType == EpisodeType.Regular)
                .ToListAsync();
            var id = 6_000_000;
            foreach (var ep in regulars)
            {
                context.EpisodeWatches.Add(new EpisodeWatch
                {
                    Id = ++id,
                    ShowId = _showId,
                    EpisodeId = ep.Id,
                    WatchedAt = DateTimeOffset.UtcNow,
                });
            }
            await context.SaveChangesAsync();
        }

        var endedPayload = BlackMirrorPayload();
        endedPayload.Show.StatusRaw = "Ended";
        await MapAsync(endedPayload);

        await using (var context = _fixture.CreateContext())
        {
            var show = await context.TrackedShows.SingleAsync(s => s.TvmazeId == BlackMirrorTvmazeId);
            Assert.Equal(ShowStatus.Ended, show.Status);
            Assert.True(show.TotalRegular > 0);
            Assert.Equal(show.TotalRegular, show.WatchedRegular);
            Assert.True(show.AutoFinishEligible);
        }
    }

    [Fact]
    public async Task Refresh_that_renumbers_existing_episodes_does_not_throw_a_circular_dependency()
    {
        // user-issues #4: a refresh that changed the unique-index columns (season_number/episode_number)
        // of several existing episodes in one SaveChanges made EF Core's update-batching dependency
        // resolver report a false "circular dependency" keyed on ix_episode_key. The mapper must treat an
        // existing episode's placement as immutable — it is identified by its stable TvmazeEpisodeId —
        // and refresh only its content, so the cycle never forms. episode.id and its placement stay put.
        await SeedTrackedShellAsync(BlackMirrorTvmazeId, _showId);

        // First map: three episodes numbered 1, 2, 3 under stable TVmaze ids 30902/30903/30904.
        await MapAsync(RenumberPayload(new[] { 1, 2, 3 }));

        long[] idsBefore;
        await using (var context = _fixture.CreateContext())
        {
            idsBefore = await context.Episodes
                .Where(e => e.ShowId == _showId)
                .OrderBy(e => e.TvmazeEpisodeId)
                .Select(e => e.Id)
                .ToArrayAsync();
        }

        // Second map: the SAME TVmaze ids, but TVmaze reports a rotation of the numbers (1->2, 2->3,
        // 3->1) and matching titles. Before the fix this throws InvalidOperationException (circular
        // dependency) from the mapper's internal SaveChanges.
        var result = await MapAsync(RenumberPayload(new[] { 2, 3, 1 }));
        Assert.True(result.AnyChange); // the titles did change — the rows really were flushed

        await using (var context = _fixture.CreateContext())
        {
            var eps = await context.Episodes
                .Where(e => e.ShowId == _showId)
                .OrderBy(e => e.TvmazeEpisodeId)
                .ToListAsync();

            // Ids are stable (episode_watch points at them) and placement is frozen at the original
            // numbers — the rotation is deliberately ignored, content is not.
            Assert.Equal(idsBefore, eps.Select(e => e.Id).ToArray());
            Assert.Equal(new[] { 1, 2, 3 }, eps.Select(e => e.EpisodeNumber!.Value).ToArray());
            Assert.Equal(new[] { "Ep 2", "Ep 3", "Ep 1" }, eps.Select(e => e.Title).ToArray());
        }

        // Idempotence: a third identical map changes nothing.
        var third = await MapAsync(RenumberPayload(new[] { 2, 3, 1 }));
        Assert.False(third.AnyChange);
    }

    // ---- helpers ----

    private async Task<MapResult> MapAsync(TvmazePayload payload)
    {
        await using var context = _fixture.CreateContext();
        var mapper = new TvmazeShowMapper(
            context, new SequentialIdGenerator(), NullLogger<TvmazeShowMapper>.Instance);
        var result = await mapper.MapShowAsync(payload, default);
        await context.SaveChangesAsync();
        return result;
    }

    private async Task SeedTrackedShellAsync(int tvmazeId, long showId)
    {
        await using var context = _fixture.CreateContext();

        // tracked_show.tvmaze_show_id is a NOT NULL FK (SCHEMA §2 — a mapped row cannot exist without
        // its source row, SPEC P4). The shell therefore needs a source row to point at; the mapper
        // overwrites it with the real payload on the first map. This mirrors AddShowToTracking, which
        // creates the shell and source row before handing off to the mapper.
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
            AddedAt = DateTimeOffset.UtcNow,
            CategoryChangedAt = DateTimeOffset.UtcNow,
        };
        context.TrackedShows.Add(show);
        await context.SaveChangesAsync();
    }

    // The Black Mirror show payload from DATA_ANALYSIS.md §8, abbreviated to the mapped fields.
    private static TvmazePayload BlackMirrorPayload(string? language = "English")
    {
        var show = new TvmazeShow
        {
            TvmazeId = BlackMirrorTvmazeId,
            Name = "Black Mirror",
            TypeRaw = "Scripted",
            Language = language,
            StatusRaw = "Running",
            RuntimeMinutes = null,
            AverageRuntime = 62,
            Premiered = "2011-12-04",
            Ended = null,
            OfficialSite = "https://www.netflix.com/title/70264888",
            ScheduleTime = "",
            ScheduleDays = "",
            RatingAverage = 8.4,
            NetworkName = null,
            WebchannelName = "Netflix",
            ImdbId = "tt2085059",
            ThetvdbId = 253463,
            GenresRaw = """["Drama","Science-Fiction","Thriller"]""",
            ImageOriginal = "https://static.tvmaze.com/uploads/images/original_untouched/1411764.jpg",
            ImageMedium = "https://static.tvmaze.com/uploads/images/medium_portrait/1411764.jpg",
            SummaryHtml = "<p>Over the last ten years, technology has transformed...</p>",
            ProviderUpdated = 1767985660,
            RawJson = "{}",
            FetchedAt = DateTimeOffset.UtcNow,
        };

        var episodes = new List<TvmazeEpisode>
        {
            new()
            {
                TvmazeEpisodeId = 30902,
                TvmazeShowId = BlackMirrorTvmazeId,
                Name = "The National Anthem",
                SeasonNumber = 1,
                Number = 1,
                TypeRaw = "regular",
                Airdate = "2011-12-04",
                Airstamp = "2011-12-04T21:00:00+00:00",
                RuntimeMinutes = 60,
                RatingAverage = 7.8,
                SummaryHtml = "<p>Prime Minister Michael Callow faces a huge and shocking dilemma...</p>",
                RawJson = "{}",
                FetchedAt = DateTimeOffset.UtcNow,
            },
            new()
            {
                TvmazeEpisodeId = 30903,
                TvmazeShowId = BlackMirrorTvmazeId,
                Name = "Fifteen Million Merits",
                SeasonNumber = 1,
                Number = 2,
                TypeRaw = "regular",
                Airdate = "2011-12-11",
                Airstamp = "2011-12-11T21:00:00+00:00",
                RuntimeMinutes = 62,
                SummaryHtml = "<p>Bing wakes up in a strange virtual world...</p>",
                RawJson = "{}",
                FetchedAt = DateTimeOffset.UtcNow,
            },
            new()
            {
                TvmazeEpisodeId = 30904,
                TvmazeShowId = BlackMirrorTvmazeId,
                Name = "Special",
                SeasonNumber = 0,
                Number = 1,
                TypeRaw = "special",
                Airdate = "2014-12-16",
                Airstamp = "2014-12-16T00:00:00+00:00",
                RawJson = "{}",
                FetchedAt = DateTimeOffset.UtcNow,
            },
        };

        return new TvmazePayload(show, episodes);
    }

    // A payload whose three episodes carry the SAME stable TVmaze ids (30902/30903/30904) but the
    // supplied episode numbers and matching titles. Used to drive a refresh that renumbers existing rows.
    private static TvmazePayload RenumberPayload(int[] numbers)
    {
        var show = new TvmazeShow
        {
            TvmazeId = BlackMirrorTvmazeId,
            Name = "Black Mirror",
            TypeRaw = "Scripted",
            StatusRaw = "Running",
            RawJson = "{}",
            FetchedAt = DateTimeOffset.UtcNow,
        };

        var tvmazeIds = new[] { 30902, 30903, 30904 };
        var episodes = new List<TvmazeEpisode>();
        for (var i = 0; i < tvmazeIds.Length; i++)
        {
            episodes.Add(new TvmazeEpisode
            {
                TvmazeEpisodeId = tvmazeIds[i],
                TvmazeShowId = BlackMirrorTvmazeId,
                Name = $"Ep {numbers[i]}",
                SeasonNumber = 1,
                Number = numbers[i],
                TypeRaw = "regular",
                RawJson = "{}",
                FetchedAt = DateTimeOffset.UtcNow,
            });
        }

        return new TvmazePayload(show, episodes);
    }

    // Deterministic, strictly-increasing ids so a re-map that regenerates an id is detectable: the
    // second map's generator starts higher, so any "new" id is distinguishable from a reused one.
    private sealed class SequentialIdGenerator : Shows.Application.IIdGenerator
    {
        private long _next = 1_000_000;
        public long NextId() => _next++;
    }
}
