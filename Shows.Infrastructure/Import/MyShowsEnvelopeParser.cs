using System.Text.Json;

namespace Shows.Infrastructure.Import;

/// <summary>
/// Parses the myshows.me JSON-RPC exports into staging-shaped records (DATA_ANALYSIS.md §2/§4). Pure
/// and side-effect free: it reads text and returns records, touching neither the filesystem nor the
/// database, so the whole envelope-shape contract is unit-testable without a database.
/// </summary>
/// <remarks>
/// <para>
/// The five input files are JSON-RPC 2.0 envelopes, not arrays. The four category files are a single
/// envelope each; <c>episodes.json</c> is an <em>array</em> of envelopes. Within a category entry,
/// <c>totalEpisodes</c>/<c>watchedEpisodes</c>/<c>watchStatus</c> are siblings of <c>show</c>, while
/// <c>title</c>/<c>year</c>/<c>totalSeasons</c> live inside <c>show</c> — getting that nesting wrong
/// silently yields zeros for every count.
/// </para>
/// <para>
/// Only batch <c>58</c> of <c>episodes.json</c> carries <c>episodes[]</c>; batches <c>57</c>/<c>59</c>
/// are per-status totals with no episode arrays, and batch <c>60</c> is empty. The parser keys episode
/// extraction on the presence of an <c>episodes</c> array rather than on the batch id, so an unexpected
/// batch id with the batch-58 shape still parses, and the empty/total batches are skipped without a
/// crash. Those arrays are the <b>unwatched</b> set (DATA_ANALYSIS.md §4) — the name lies, the semantics
/// are load-bearing.
/// </para>
/// </remarks>
internal static class MyShowsEnvelopeParser
{
    /// <summary>
    /// Parse one category envelope (<c>watching.json</c> etc.) into show records. <paramref name="sourceFile"/>
    /// is recorded verbatim on each row; <c>source_watch_status</c> comes from the entry's
    /// <c>watchStatus</c>, falling back to the file's stem when the field is absent.
    /// </summary>
    public static IReadOnlyList<ParsedShow> ParseCategory(string json, string sourceFile)
    {
        var fallbackStatus = Path.GetFileNameWithoutExtension(sourceFile);
        var shows = new List<ParsedShow>();

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("result", out var result)
            || result.ValueKind != JsonValueKind.Array)
        {
            return shows;
        }

        foreach (var entry in result.EnumerateArray())
        {
            if (!entry.TryGetProperty("show", out var show)
                || !show.TryGetProperty("id", out var idEl))
            {
                continue;
            }

            shows.Add(new ParsedShow(
                MyshowsId: idEl.GetInt32(),
                TitleRu: TrimmedString(show, "title"),
                TitleOriginal: TrimmedString(show, "titleOriginal"),
                Year: OptionalInt(show, "year"),
                Alias: TrimmedString(show, "alias"),
                SourceFile: sourceFile,
                SourceWatchStatus: OptionalString(entry, "watchStatus") ?? fallbackStatus,
                TotalEpisodes: RequiredInt(entry, "totalEpisodes"),
                WatchedEpisodes: RequiredInt(entry, "watchedEpisodes"),
                TotalSeasons: OptionalInt(show, "totalSeasons")));
        }

        return shows;
    }

    /// <summary>
    /// Parse <c>episodes.json</c> (an array of envelopes) into the batch-57 per-status totals and the
    /// batch-58 unwatched-episode records. Empty results and total-only batches contribute nothing.
    /// </summary>
    public static EpisodesFileResult ParseEpisodesFile(string json)
    {
        var batch57 = new Dictionary<string, int>(StringComparer.Ordinal);
        var episodes = new List<ParsedEpisode>();

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return new EpisodesFileResult(batch57, episodes);
        }

        foreach (var envelope in doc.RootElement.EnumerateArray())
        {
            var batchId = envelope.TryGetProperty("id", out var idEl) ? idEl.GetInt32() : 0;
            if (!envelope.TryGetProperty("result", out var result)
                || result.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var entry in result.EnumerateArray())
            {
                // Batch 57: per-status totals ({ watchStatus, count, shows[] }). No episodes[] array.
                if (batchId == 57
                    && entry.TryGetProperty("watchStatus", out var statusEl)
                    && statusEl.ValueKind == JsonValueKind.String
                    && entry.TryGetProperty("count", out var countEl))
                {
                    batch57[statusEl.GetString()!] = countEl.GetInt32();
                    continue;
                }

                // Batch 58: { episodes[], show{}, started }. Extract only when episodes[] is present,
                // so batches 57/59 (totals, no episodes) and 60 (empty) fall through untouched.
                if (entry.TryGetProperty("episodes", out var eps)
                    && eps.ValueKind == JsonValueKind.Array
                    && entry.TryGetProperty("show", out var show)
                    && show.TryGetProperty("id", out var showIdEl))
                {
                    var showMyshowsId = showIdEl.GetInt32();
                    foreach (var ep in eps.EnumerateArray())
                    {
                        episodes.Add(new ParsedEpisode(
                            ShowMyshowsId: showMyshowsId,
                            SourceBatchId: batchId,
                            EpisodeIdSrc: RequiredInt(ep, "id"),
                            SeasonNumber: RequiredInt(ep, "seasonNumber"),
                            EpisodeNumber: RequiredInt(ep, "episodeNumber"),
                            Title: TrimmedString(ep, "title"),
                            AirDateUtc: OptionalString(ep, "airDateUTC"),
                            IsSpecial: OptionalBool(ep, "isSpecial"),
                            RuntimeMinutes: OptionalInt(ep, "runtime")));
                    }
                }
            }
        }

        return new EpisodesFileResult(batch57, episodes);
    }

    private static string? OptionalString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // Titles carry trailing whitespace (e.g. a trailing tab) in the export (DATA_ANALYSIS.md §2); trim
    // on import so the result schema never stores it. ADR-015.
    private static string? TrimmedString(JsonElement el, string name) => OptionalString(el, name)?.Trim();

    private static int? OptionalInt(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static int RequiredInt(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32()
            : throw new InvalidDataException($"Expected numeric '{name}'.");

    private static bool OptionalBool(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}

/// <summary>A show row parsed from a category envelope, ready for <c>staging_show</c>.</summary>
internal sealed record ParsedShow(
    int MyshowsId,
    string? TitleRu,
    string? TitleOriginal,
    int? Year,
    string? Alias,
    string SourceFile,
    string SourceWatchStatus,
    int TotalEpisodes,
    int WatchedEpisodes,
    int? TotalSeasons);

/// <summary>An unwatched episode parsed from batch 58, ready for <c>staging_episode</c>.</summary>
internal sealed record ParsedEpisode(
    int ShowMyshowsId,
    int SourceBatchId,
    int EpisodeIdSrc,
    int SeasonNumber,
    int EpisodeNumber,
    string? Title,
    string? AirDateUtc,
    bool IsSpecial,
    int? RuntimeMinutes);

/// <summary>The two things <c>episodes.json</c> yields: batch-57 totals and the batch-58 unwatched set.</summary>
internal sealed record EpisodesFileResult(
    IReadOnlyDictionary<string, int> Batch57Totals,
    IReadOnlyList<ParsedEpisode> Episodes);
