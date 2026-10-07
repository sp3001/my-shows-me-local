using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Shows.Infrastructure.Import;

/// <summary>
/// Reads an IMDb dataset file as tab-separated rows, transparently handling both the plain
/// <c>.tsv</c> the user may have placed in <c>data/imdb</c> and the gzipped <c>.tsv.gz</c> the
/// downloader produces (step 7.4: keep the file on disk, decompress on read — a schema change is a
/// re-load, not a re-download).
/// </summary>
/// <remarks>
/// <b>Streaming, never <c>ReadAllLines</c>.</b> <c>title.akas.tsv</c> is ~3 GB; the whole point of the
/// reader is to hold one line in memory at a time. The header line is skipped by the caller (it is a
/// real line, not a comment).
/// </remarks>
internal static class TsvReader
{
    /// <summary>The IMDb null marker. Never empty string, never 0 (DATA_ANALYSIS.md §6).</summary>
    public const string NullMarker = @"\N";

    /// <summary>
    /// Open <paramref name="path"/> (plain or <c>.gz</c>) and yield each data line split on tab.
    /// The first line (the header) is skipped. A line with fewer fields than
    /// <paramref name="minColumns"/> is skipped (a malformed tail line must not abort a 3 GB scan).
    /// </summary>
    public static IEnumerable<string[]> ReadRows(string path, int minColumns)
    {
        using var file = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 16, FileOptions.SequentialScan);

        Stream source = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? new GZipStream(file, CompressionMode.Decompress)
            : file;

        using var reader = new StreamReader(source, Encoding.UTF8);

        // Skip the header line.
        if (reader.ReadLine() is null)
        {
            yield break;
        }

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var fields = line.Split('\t');
            if (fields.Length >= minColumns)
            {
                yield return fields;
            }
        }
    }

    /// <summary><c>\N</c> → null; otherwise the value verbatim (DATA_ANALYSIS.md §6).</summary>
    public static string? Null(string? value) =>
        value is null || value == NullMarker ? null : value;

    /// <summary>Parse an IMDb integer field; <c>\N</c>, empty, or non-numeric → null (never 0).</summary>
    public static int? NullInt(string? value) =>
        int.TryParse(Null(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>IMDb <c>isAdult</c> is <c>0</c>/<c>1</c>; anything else (incl. <c>\N</c>) is false.</summary>
    public static bool Adult(string? value) => Null(value) == "1";
}
