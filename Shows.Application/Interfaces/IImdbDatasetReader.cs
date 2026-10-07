namespace Shows.Application;

/// <summary>
/// Streams the IMDb datasets, scoped to tracked shows (SPEC F6 <c>ImdbImport</c>, ADR-021).
/// </summary>
/// <remarks>
/// The job downloads the full dataset files but keeps only titles whose <c>tconst</c> matches an
/// <c>imdb_id</c> on a tracked show, plus their episodes and akas. The files are retained on disk so a
/// later re-run can widen the scope without re-downloading. This is why the reader is a streaming
/// interface over two &gt;50 MB TSVs rather than a materialised collection.
/// </remarks>
public interface IImdbDatasetReader
{
    /// <summary>
    /// Streams the title rows whose <c>tconst</c> is in <paramref name="wantedTconsts"/>.
    /// Filtered to <c>tvSeries</c> / <c>tvMiniSeries</c> only.
    /// </summary>
    /// <remarks>
    /// The join order matters and is a verified fact, not a style choice
    /// (<c>DATA_ANALYSIS.md</c> §6): episode.tsv → basics series → basics tvEpisode join-filter.
    /// Filtering <c>title.basics</c> to <c>tvSeries</c> first drops every episode row, because episode
    /// rows are not themselves typed <c>tvSeries</c>.
    /// </remarks>
    IAsyncEnumerable<ImdbTitleRow> ReadTitlesAsync(
        IReadOnlySet<string> wantedTconsts, CancellationToken cancellationToken);

    IAsyncEnumerable<ImdbEpisodeRow> ReadEpisodesAsync(
        IReadOnlySet<string> wantedParentTconsts, CancellationToken cancellationToken);

    IAsyncEnumerable<ImdbAkaRow> ReadAkasAsync(
        IReadOnlySet<string> wantedTitleIds, CancellationToken cancellationToken);
}

/// <summary>
/// A row from <c>title.basics.tsv</c>. <c>\N</c> is mapped to null, never to empty string or 0
/// (<c>SCHEMA.md</c> §1).
/// </summary>
public sealed record ImdbTitleRow(
    string Tconst,
    string TitleType,
    string PrimaryTitle,
    string? OriginalTitle,
    bool IsAdult,
    int? StartYear,
    int? EndYear,
    int? RuntimeMinutes,
    string? Genres);

/// <summary>
/// A row from <c>title.episode.tsv</c>. Season and episode numbers are null from <c>\N</c>, never 0 —
/// a zero would invent a season 0 for every legacy row.
/// </summary>
public sealed record ImdbEpisodeRow(
    string Tconst,
    string ParentTconst,
    int? SeasonNumber,
    int? EpisodeNumber,
    string? PrimaryTitle,
    int? StartYear,
    int? RuntimeMinutes);

/// <summary>A row from <c>title.akas.tsv</c> — the source of localized titles.</summary>
public sealed record ImdbAkaRow(
    string TitleId,
    int Ordering,
    string? Title,
    string? Region,
    string? Language,
    bool? IsOriginalTitle);
