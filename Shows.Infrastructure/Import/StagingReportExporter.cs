using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shows.Infrastructure.Configuration;
using Shows.Infrastructure.Persistence;

namespace Shows.Infrastructure.Import;

/// <summary>
/// The <c>--report</c> behavior of the import job (step 2.4): export <c>v_staging_issues</c> to a CSV in
/// the data folder, ordered blocking-first, and report the per-severity counts. Read-only against the
/// database — it never migrates and never writes a table, only the CSV file.
/// </summary>
/// <remarks>
/// The counts are derived from the same materialized rows the CSV is written from, so the numbers printed
/// can never disagree with the file. The plan's cross-check is that these counts equal
/// <c>SELECT severity, COUNT(*) FROM v_staging_issues GROUP BY severity</c>.
/// </remarks>
internal sealed class StagingReportExporter(
    ShowsDbContext db,
    DataPaths paths,
    ILogger<StagingReportExporter> logger)
{
    /// <summary>Write the CSV and return the counts it contains. The file path is <see cref="DataPaths.ReportPath"/>.</summary>
    public async Task<ReportResult> ExportAsync(CancellationToken ct)
    {
        // One query feeds both the file and the counts, so they cannot drift apart.
        var rows = await db.StagingIssues
            .OrderBy(i => i.Severity == "blocking" ? 0 : 1) // blocking before warning
            .ThenBy(i => i.MyshowsId)
            .ToListAsync(ct);

        var path = paths.ReportPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"'{path}' has no parent directory."));

        await using (var writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)))
        {
            await writer.WriteLineAsync("myshows_id,title_ru,issue,severity");
            foreach (var row in rows)
            {
                await writer.WriteLineAsync(string.Join(
                    ',',
                    row.MyshowsId.ToString(CultureInfo.InvariantCulture),
                    CsvField(row.TitleRu),
                    CsvField(row.Issue),
                    CsvField(row.Severity)));
            }
        }

        var blocking = rows.Count(r => r.Severity == "blocking");
        var warning = rows.Count(r => r.Severity == "warning");
        logger.LogInformation(
            "Report written to {Path}: {Total} issue(s) — {Blocking} blocking, {Warning} warning.",
            path, rows.Count, blocking, warning);

        return new ReportResult(path, rows.Count, blocking, warning);
    }

    // RFC 4180: quote a field that contains a comma, quote, CR or LF; double any embedded quote. Titles
    // are user data and can contain any of these, so every field goes through here.
    private static string CsvField(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value.Contains(',', StringComparison.Ordinal)
            || value.Contains('"', StringComparison.Ordinal)
            || value.Contains('\n', StringComparison.Ordinal)
            || value.Contains('\r', StringComparison.Ordinal))
        {
            return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        }

        return value;
    }
}

/// <summary>What <c>--report</c> wrote: the file path and the counts it contains.</summary>
internal sealed record ReportResult(string Path, int Total, int Blocking, int Warning);
