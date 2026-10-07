namespace Shows.Infrastructure.Import;

/// <summary>
/// The download seam for the IMDb datasets (step 7.4). Production streams a dataset's
/// <c>.tsv.gz</c> to disk over the <c>Imdb</c> named client; tests write a fixture file directly and
/// never touch the network. The file is <b>kept on disk</b> after the run — a schema change must be a
/// re-load, not a re-download (step 7.4).
/// </summary>
internal interface IImdbDatasetDownloader
{
    /// <summary>
    /// Download <paramref name="datasetName"/> (e.g. <c>title.basics</c>) from
    /// <paramref name="sourceUrl"/> to <paramref name="targetPath"/>. Returns the outcome; a transport
    /// failure is reported, not thrown, so the job can leave the dataset for the next run (R14).
    /// </summary>
    Task<DatasetDownloadResult> DownloadAsync(
        string datasetName, string sourceUrl, string targetPath, CancellationToken cancellationToken);
}

/// <summary>
/// The outcome of one dataset download. <see cref="Ok"/> is false on a transport or HTTP failure;
/// <see cref="Error"/> names the cause. <see cref="FileSize"/> is the bytes written (for
/// <c>imdb_dataset_meta.file_size</c>).
/// </summary>
internal sealed record DatasetDownloadResult(bool Ok, string? Error, long FileSize);
