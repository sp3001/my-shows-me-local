using Shows.Infrastructure.Import;
using Xunit;

namespace Shows.Tests.Unit;

/// <summary>
/// Step 2.1 acceptance for the envelope parser: the nesting (counts are entry-level siblings of
/// <c>show</c>, not inside it), the trim, and the tolerance for the empty/total-only batches. Pure —
/// no database, no filesystem.
/// </summary>
public sealed class MyShowsEnvelopeParserTests
{
    [Fact]
    public void Category_entry_reads_counts_from_entry_level_and_titles_from_show()
    {
        // Mirrors the real watching.json shape: totalEpisodes/watchedEpisodes/watchStatus are siblings
        // of show; title/year/totalSeasons are inside show.
        const string json = """
            { "jsonrpc": "2.0", "id": 51, "result": [
              { "show": { "id": 22410, "title": "Черное зеркало", "titleOriginal": "Black Mirror",
                          "alias": "black-mirror", "year": 2011, "totalSeasons": 7, "status": "Returning Series" },
                "watchStatus": "watching", "totalEpisodes": 32, "watchedEpisodes": 32, "rating": 5 } ] }
            """;

        var shows = MyShowsEnvelopeParser.ParseCategory(json, "watching.json");

        var show = Assert.Single(shows);
        Assert.Equal(22410, show.MyshowsId);
        Assert.Equal("Черное зеркало", show.TitleRu);
        Assert.Equal("Black Mirror", show.TitleOriginal);
        Assert.Equal(2011, show.Year);
        Assert.Equal(7, show.TotalSeasons);
        Assert.Equal(32, show.TotalEpisodes);
        Assert.Equal(32, show.WatchedEpisodes);
        Assert.Equal("watching", show.SourceWatchStatus);
        Assert.Equal("watching.json", show.SourceFile);
    }

    [Fact]
    public void Titles_are_trimmed_on_import()
    {
        // The export carries a trailing tab on some titles (DATA_ANALYSIS.md §2, ADR-015).
        const string json = """
            { "result": [ { "show": { "id": 1, "title": "Неизведанные\t", "titleOriginal": "Unknown  " },
                "watchStatus": "later", "totalEpisodes": 0, "watchedEpisodes": 0 } ] }
            """;

        var show = Assert.Single(MyShowsEnvelopeParser.ParseCategory(json, "later.json"));
        Assert.Equal("Неизведанные", show.TitleRu);
        Assert.Equal("Unknown", show.TitleOriginal);
    }

    [Fact]
    public void WatchStatus_falls_back_to_file_stem_when_absent()
    {
        const string json = """
            { "result": [ { "show": { "id": 7, "title": "x" }, "totalEpisodes": 3, "watchedEpisodes": 1 } ] }
            """;

        var show = Assert.Single(MyShowsEnvelopeParser.ParseCategory(json, "finished.json"));
        Assert.Equal("finished", show.SourceWatchStatus);
    }

    [Fact]
    public void Empty_result_array_yields_no_shows()
    {
        var shows = MyShowsEnvelopeParser.ParseCategory("""{ "result": [] }""", "later.json");
        Assert.Empty(shows);
    }

    [Fact]
    public void Episodes_file_extracts_only_batch_58_and_reads_batch_57_totals()
    {
        // An array of envelopes: 60 empty, 57 totals, 59 totals-with-shows (no episodes[]), 58 episodes.
        const string json = """
            [
              { "id": 60, "result": [] },
              { "id": 57, "result": [
                  { "watchStatus": "watching", "count": 78, "shows": [] },
                  { "watchStatus": "later", "count": 246, "shows": [] } ] },
              { "id": 59, "result": [
                  { "watchStatus": "later", "show": { "id": 5 }, "totalEpisodes": 4, "watchedEpisodes": 0 } ] },
              { "id": 58, "result": [
                  { "started": true, "show": { "id": 86813 },
                    "episodes": [
                      { "id": 1, "seasonNumber": 1, "episodeNumber": 2, "title": "Two",
                        "airDateUTC": "2026-02-24T12:00:00-0500", "isSpecial": false, "runtime": 24 },
                      { "id": 2, "seasonNumber": 1, "episodeNumber": 3, "title": "Three",
                        "isSpecial": true } ] } ] }
            ]
            """;

        var result = MyShowsEnvelopeParser.ParseEpisodesFile(json);

        Assert.Equal(78, result.Batch57Totals["watching"]);
        Assert.Equal(246, result.Batch57Totals["later"]);

        // Only batch 58 contributes episodes; batch 59's show (no episodes[]) is skipped.
        Assert.Equal(2, result.Episodes.Count);
        var first = result.Episodes[0];
        Assert.Equal(86813, first.ShowMyshowsId);
        Assert.Equal(58, first.SourceBatchId);
        Assert.Equal(1, first.SeasonNumber);
        Assert.Equal(2, first.EpisodeNumber);
        Assert.Equal("2026-02-24T12:00:00-0500", first.AirDateUtc);
        Assert.False(first.IsSpecial);
        Assert.Equal(24, first.RuntimeMinutes);
        Assert.True(result.Episodes[1].IsSpecial);
        Assert.Null(result.Episodes[1].AirDateUtc);
        Assert.Null(result.Episodes[1].RuntimeMinutes);
    }

    [Fact]
    public void Episodes_file_with_only_empty_and_total_batches_yields_no_episodes()
    {
        const string json = """
            [ { "id": 60, "result": [] },
              { "id": 57, "result": [ { "watchStatus": "watching", "count": 1, "shows": [] } ] } ]
            """;

        var result = MyShowsEnvelopeParser.ParseEpisodesFile(json);
        Assert.Empty(result.Episodes);
        Assert.Single(result.Batch57Totals);
    }
}
