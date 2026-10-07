using System.Net;

namespace Shows.Infrastructure.Import;

/// <summary>
/// Production downloader over the <c>Imdb</c> named <see cref="HttpClient"/> (step 7.4). The named client
/// carries the per-service proxy, throttle and User-Agent (ARCHITECTURE.md §4, SPEC R14) — this class must
/// not build its own client or inspect a URL to decide routing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Streams with <see cref="HttpCompletionOption.ResponseHeadersRead"/></b> and copies to disk in chunks.
/// The named client's <c>Timeout</c> (= <c>RequestTimeoutSeconds</c>) would otherwise apply to the whole
/// body and kill a healthy >50 MB download mid-stream (step 7.4 warns against shortening it for the proxy
/// check; streaming sidesteps the whole-file timeout entirely — the timeout then covers only the headers).
/// </para>
/// <para>
/// Writes to a <c>.part</c> file and moves it into place on success, so a failed download never leaves a
/// truncated file that a later run would mistake for a complete dataset. The completed file is kept on
/// disk (a schema change is a re-load, not a re-download). A transport/HTTP failure is reported, not
/// thrown (R14: the job leaves the dataset for the next run).
/// </para>
/// </remarks>
internal sealed class ImdbDatasetDownloader(IHttpClientFactory httpClientFactory) : IImdbDatasetDownloader
{
    public async Task<DatasetDownloadResult> DownloadAsync(
        string datasetName, string sourceUrl, string targetPath, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("Imdb");
        var partPath = targetPath + ".part";

        try
        {
            // Relative to the named client's BaseAddress (https://datasets.imdbws.com). The dataset files
            // are {name}.tsv.gz at the host root.
            using var response = await client.GetAsync(
                new Uri($"{datasetName}.tsv.gz", UriKind.Relative),
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (response.StatusCode != HttpStatusCode.OK)
            {
                return new DatasetDownloadResult(false, $"HTTP {(int)response.StatusCode}", 0);
            }

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var target = File.Create(partPath);
            await source.CopyToAsync(target, cancellationToken);

            var size = target.Length;
            await target.DisposeAsync();

            // Move into place, replacing any prior copy. Keep the file (step 7.4: re-load, not re-download).
            File.Move(partPath, targetPath, overwrite: true);
            return new DatasetDownloadResult(true, null, size);
        }
        catch (HttpRequestException ex)
        {
            TryDelete(partPath);
            return new DatasetDownloadResult(false, $"transport: {ex.GetType().Name}: {ex.Message}", 0);
        }
        catch (IOException ex)
        {
            TryDelete(partPath);
            return new DatasetDownloadResult(false, $"io: {ex.Message}", 0);
        }
        catch (Exception ex) when (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // HttpClient surfaces a timeout as TaskCanceledException with no inner cancellation.
            TryDelete(partPath);
            return new DatasetDownloadResult(false, $"timeout: {ex.Message}", 0);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup of the .part file; the next run overwrites it anyway.
        }
    }
}
