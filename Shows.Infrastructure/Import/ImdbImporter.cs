using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Shows.Application;
using Shows.Infrastructure.Configuration;
using Shows.Infrastructure.Persistence;
using Shows.Infrastructure.Persistence.Sources;

namespace Shows.Infrastructure.Import;

/// <summary>
/// The <c>ImdbImport</c> job body (step 7.4). Downloads the IMDb datasets that are not already on disk,
/// then loads <b>only rows reachable from active tracked shows</b> (ADR-021) into the source tables, in
/// the order <c>DATA_ANALYSIS.md</c> §6 fixes:
/// </summary>
/// <remarks>
/// <para>
/// <b>Order is load-bearing.</b> <c>title.basics</c> mixes shows and episodes; filtering it to
/// <c>tvSeries</c>/<c>tvMiniSeries</c> first would silently drop every episode row (a real bug in the
/// first plan draft). The correct sequence is:
/// </para>
/// <list type="number">
///   <item><c>title.episode.tsv</c> → <c>imdb_episode</c> (mapping only: tconst, parent, season, episode).</item>
///   <item><c>title.basics</c> where <c>titleType IN (tvSeries, tvMiniSeries)</c> → <c>imdb_title</c>.</item>
///   <item><c>title.basics</c> where <c>titleType = tvEpisode</c> → UPDATE <c>imdb_episode</c> with title/year/runtime.</item>
/// </list>
/// <para>
/// Because <c>imdb_episode.parent_tconst</c> has a FK to <c>imdb_title</c> but episodes are inserted
/// before their parents, the whole load runs in one transaction with <c>PRAGMA defer_foreign_keys=ON</c>
/// — the FK is checked at COMMIT, by which point every parent a kept episode needs has been inserted.
/// </para>
/// <para>
/// <b>Raw parameterized <see cref="SqliteCommand"/> batches, not EF.</b> The datasets are >1 GB; EF's
/// ChangeTracker is far too slow at this volume (step 7.4). One prepared statement per pass, rebound per
/// row, inside a single transaction.
/// </para>
/// <para>
/// <b>Re-runnable</b> (a show may be added at any time): <c>INSERT OR IGNORE</c> for the two inserts and a
/// value-guarded <c>UPDATE</c> for pass 3, so a second run over the same files changes nothing and reports
/// <c>items_changed = 0</c>. <c>\N</c> maps to NULL throughout, never 0 or empty string.
/// </para>
/// <para>
/// <b>Lease-guarded</b> (ADR-028) as <c>imdb_import</c>; a heartbeat renewal that updates zero rows aborts
/// the transaction so a lost lease never leaves a half-written bulk load.
/// </para>
/// </remarks>
internal sealed class ImdbImporter(
    ShowsDbContext db,
    IImdbDatasetDownloader downloader,
    IWriterLease lease,
    IIdGenerator idGenerator,
    DataPaths paths,
    ShowsOptions options,
    ILogger<ImdbImporter> logger)
{
    private static readonly string[] Datasets = ["title.episode", "title.basics", "title.akas"];

    // Improvement 5: the datasets are millions of rows, so a pass can run for minutes with no output. A
    // pass logs every this-many scanned rows so a long run is visibly alive (the same reasoning as the
    // Promoter's per-show line — silence at this volume looks like a hang).
    private const int ProgressEveryRows = 500_000;

    // Emits a periodic progress line for a bulk pass: how far through the file it has scanned and how many
    // rows it has changed so far. Called from each pass loop every <see cref="ProgressEveryRows"/> rows.
    // Also forwards to the in-process progress sink (the Manage page) as an indeterminate tick — a bulk
    // scan has no item total, so it reports only a Detail line (Total 0 → the UI shows an indeterminate bar).
    private void LogPassProgress(string pass, int scanned, int changed)
    {
        logger.LogInformation("ImdbImport {Pass}: {Scanned:N0} rows scanned, {Changed:N0} changed so far.",
            pass, scanned, changed);
        _progress?.Report(new JobProgress("ImdbImport", 0, 0, $"{pass}: {scanned:N0} scanned, {changed:N0} changed"));
    }

    // The in-process progress sink for the current run (null from the console). Set once at the top of
    // RunAsync; this importer is scoped, so one instance serves exactly one run and the field is safe.
    private IProgress<JobProgress>? _progress;

    // progress is optional and LAST so the existing console/test call RunAsync(ct) still binds; only the
    // in-process Manage path passes it.
    public async Task<ImdbImportReport> RunAsync(CancellationToken cancellationToken, IProgress<JobProgress>? progress = null)
    {
        _progress = progress;

        await using var handle = await lease.TryAcquireAsync("imdb_import", cancellationToken);
        if (handle is null)
        {
            logger.LogWarning("Another writer holds the lease; ImdbImport refused.");
            return new ImdbImportReport(Refused: true, 0, []);
        }

        var startedAt = DateTimeOffset.UtcNow;
        // job_run.id is client-minted (ValueGenerated.Never, ADR-026/027).
        var run = new JobRun
        {
            Id = idGenerator.NextId(),
            JobName = "ImdbImport",
            StartedAt = startedAt,
        };
        db.JobRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);

        // The set of imdb ids to keep (ADR-021): active tracked shows only. A soft-deleted show keeps its
        // tracked_show row (ADR-024), so the join to user_show with deleted_at IS NULL is what stops the
        // import from loading IMDb rows for shows nobody tracks. Small (hundreds), so an in-memory set is
        // the filter — no temp table needed.
        var trackedImdbIds = await GetActiveImdbIdsAsync(cancellationToken);
        logger.LogInformation("ImdbImport: {Count} active tracked shows carry an imdb_id.", trackedImdbIds.Count);

        // Resolve each dataset: reuse a file already in data/imdb (a plain .tsv the user placed there, or
        // a .tsv.gz from a prior run), otherwise download it. Files are kept on disk — a schema change is a
        // re-load, not a re-download (step 7.4).
        var files = new Dictionary<string, string>();
        var meta = new List<DatasetMeta>();
        foreach (var dataset in Datasets)
        {
            var resolved = await ResolveFileAsync(dataset, cancellationToken);
            if (resolved is null)
            {
                // R14: a dataset that could not be downloaded is left for the next run; the others still load.
                logger.LogWarning("ImdbImport: {Dataset} unavailable this run; skipping.", dataset);
                continue;
            }

            var (path, downloadedAt, sourceUrl, fileSize) = resolved.Value;
            files[dataset] = path;
            meta.Add(new DatasetMeta(dataset, downloadedAt, sourceUrl, fileSize));
        }

        var changed = await LoadAsync(files, trackedImdbIds, handle, cancellationToken);

        await RecordMetaAsync(meta, cancellationToken);

        run.FinishedAt = DateTimeOffset.UtcNow;
        run.Succeeded = true;
        run.ItemsTotal = trackedImdbIds.Count;
        run.ItemsChanged = changed;
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("ImdbImport done: {Changed} rows changed across {Files} datasets.", changed, files.Count);
        return new ImdbImportReport(Refused: false, changed, files.Keys.ToList());
    }

    // The passes, in the order DATA_ANALYSIS §6 fixes (titles → episodes → episode-details → akas). The
    // load runs in SHORT BATCHED TRANSACTIONS, not one long one: ADR-028 rejects holding a write
    // transaction open for the whole run (it blocks the UI's writes and forfeits WAL), and the lease
    // renews on a SEPARATE connection that a held write transaction would lock out. So every batch
    // commits, renews the lease, and reopens — a renewal that updates zero rows aborts the run.
    //
    // Because imdb_episode.parent_tconst has a FK to imdb_title and the FK cannot be deferred across a
    // commit, imdb_title MUST be populated before imdb_episode. That is why titles are pass 1 and
    // episodes pass 2 (DATA_ANALYSIS §6's "episode first" was written for a single deferred-FK
    // transaction; the batched model inverts it — see the §6 note). The "don't filter basics to tvSeries
    // first" warning still holds: episode DETAIL comes from the tvEpisode rows in pass 3, never from the
    // tvSeries filter.
    private async Task<int> LoadAsync(
        Dictionary<string, string> files,
        HashSet<string> trackedImdbIds,
        IWriterLeaseHandle handle,
        CancellationToken cancellationToken)
    {
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(cancellationToken);

        var batch = new Batch(connection, handle, options.Sync.LeaseRenewIntervalSeconds, logger);
        await batch.BeginAsync(cancellationToken);
        var changed = 0;

        try
        {
            if (files.TryGetValue("title.basics", out var basicsPath))
            {
                changed += await LoadTitlesAsync(connection, batch, basicsPath, trackedImdbIds, cancellationToken);
            }

            if (files.TryGetValue("title.episode", out var episodePath))
            {
                changed += await LoadEpisodesAsync(connection, batch, episodePath, trackedImdbIds, cancellationToken);
            }

            if (files.TryGetValue("title.basics", out basicsPath))
            {
                changed += await UpdateEpisodeDetailsAsync(connection, batch, basicsPath, cancellationToken);
            }

            if (files.TryGetValue("title.akas", out var akasPath))
            {
                changed += await LoadAkasAsync(connection, batch, akasPath, trackedImdbIds, cancellationToken);
            }

            await batch.CommitAsync(cancellationToken);
            return changed;
        }
        catch
        {
            await batch.AbortAsync();
            throw;
        }
    }

    // Pass 1: title.basics where titleType IN (tvSeries, tvMiniSeries) → imdb_title. Filtered to tracked ids.
    // Runs FIRST so the imdb_episode FK has its parents (see LoadAsync). INSERT OR IGNORE keeps a re-run at zero.
    private async Task<int> LoadTitlesAsync(
        SqliteConnection connection,
        Batch batch,
        string path,
        HashSet<string> trackedImdbIds,
        CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = batch.Transaction;
        cmd.CommandText =
            "INSERT OR IGNORE INTO imdb_title " +
            "(tconst, title_type, primary_title, original_title, is_adult, start_year, end_year, runtime_minutes, genres) " +
            "VALUES ($t, $type, $pt, $ot, $adult, $sy, $ey, $rt, $g);";
        var t = cmd.Parameters.Add("$t", SqliteType.Text);
        var type = cmd.Parameters.Add("$type", SqliteType.Text);
        var pt = cmd.Parameters.Add("$pt", SqliteType.Text);
        var ot = cmd.Parameters.Add("$ot", SqliteType.Text);
        var adult = cmd.Parameters.Add("$adult", SqliteType.Integer);
        var sy = cmd.Parameters.Add("$sy", SqliteType.Integer);
        var ey = cmd.Parameters.Add("$ey", SqliteType.Integer);
        var rt = cmd.Parameters.Add("$rt", SqliteType.Integer);
        var g = cmd.Parameters.Add("$g", SqliteType.Text);

        var changed = 0;
        var scanned = 0;
        foreach (var row in TsvReader.ReadRows(path, 9))
        {
            cancellationToken.ThrowIfCancellationRequested();
            cmd.Transaction = await batch.MaybeFlushAsync(cancellationToken);
            if (++scanned % ProgressEveryRows == 0)
            {
                LogPassProgress("pass 1 (titles)", scanned, changed);
            }

            var titleType = TsvReader.Null(row[1]);
            if (titleType is not ("tvSeries" or "tvMiniSeries"))
            {
                continue;
            }

            var tconst = row[0];
            if (!trackedImdbIds.Contains(tconst))
            {
                continue;
            }

            t.Value = tconst;
            type.Value = titleType;
            pt.Value = TsvReader.Null(row[2]) ?? string.Empty;
            ot.Value = (object?)TsvReader.Null(row[3]) ?? DBNull.Value;
            adult.Value = TsvReader.Adult(row[4]) ? 1 : 0;
            sy.Value = (object?)TsvReader.NullInt(row[5]) ?? DBNull.Value;
            ey.Value = (object?)TsvReader.NullInt(row[6]) ?? DBNull.Value;
            rt.Value = (object?)TsvReader.NullInt(row[7]) ?? DBNull.Value;
            g.Value = (object?)TsvReader.Null(row[8]) ?? DBNull.Value;
            changed += await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        logger.LogInformation("ImdbImport pass 1 (titles): {Changed} inserted.", changed);
        return changed;
    }

    // Pass 2: title.episode.tsv → imdb_episode. Only rows whose parent is an active tracked show (ADR-021).
    // The parents exist (pass 1), so the FK holds at each batch commit. INSERT OR IGNORE keeps a re-run at zero.
    private async Task<int> LoadEpisodesAsync(
        SqliteConnection connection,
        Batch batch,
        string path,
        HashSet<string> trackedImdbIds,
        CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = batch.Transaction;
        cmd.CommandText =
            "INSERT OR IGNORE INTO imdb_episode " +
            "(tconst, parent_tconst, season_number, episode_number) VALUES ($t, $p, $s, $e);";
        var t = cmd.Parameters.Add("$t", SqliteType.Text);
        var p = cmd.Parameters.Add("$p", SqliteType.Text);
        var s = cmd.Parameters.Add("$s", SqliteType.Integer);
        var e = cmd.Parameters.Add("$e", SqliteType.Integer);

        var changed = 0;
        var scanned = 0;
        foreach (var row in TsvReader.ReadRows(path, 4))
        {
            cancellationToken.ThrowIfCancellationRequested();
            cmd.Transaction = await batch.MaybeFlushAsync(cancellationToken);
            if (++scanned % ProgressEveryRows == 0)
            {
                LogPassProgress("pass 2 (episodes)", scanned, changed);
            }

            var parent = TsvReader.Null(row[1]);
            if (parent is null || !trackedImdbIds.Contains(parent))
            {
                continue;
            }

            t.Value = row[0];
            p.Value = parent;
            s.Value = (object?)TsvReader.NullInt(row[2]) ?? DBNull.Value;
            e.Value = (object?)TsvReader.NullInt(row[3]) ?? DBNull.Value;
            changed += await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        logger.LogInformation("ImdbImport pass 2 (episodes): {Changed} inserted.", changed);
        return changed;
    }

    // Pass 3: title.basics where titleType = tvEpisode → UPDATE imdb_episode with title/year/runtime. The
    // UPDATE only touches tconsts already in imdb_episode (pass 2 kept only tracked-parent episodes), so it
    // is join-filtered to parents already in imdb_title by construction. The WHERE value-guard makes a
    // re-run change nothing.
    private async Task<int> UpdateEpisodeDetailsAsync(
        SqliteConnection connection,
        Batch batch,
        string path,
        CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = batch.Transaction;
        cmd.CommandText =
            "UPDATE imdb_episode SET primary_title = $pt, start_year = $sy, runtime_minutes = $rt " +
            "WHERE tconst = $t AND (primary_title IS NOT $pt OR start_year IS NOT $sy OR runtime_minutes IS NOT $rt);";
        var t = cmd.Parameters.Add("$t", SqliteType.Text);
        var pt = cmd.Parameters.Add("$pt", SqliteType.Text);
        var sy = cmd.Parameters.Add("$sy", SqliteType.Integer);
        var rt = cmd.Parameters.Add("$rt", SqliteType.Integer);

        var changed = 0;
        var scanned = 0;
        foreach (var row in TsvReader.ReadRows(path, 9))
        {
            cancellationToken.ThrowIfCancellationRequested();
            cmd.Transaction = await batch.MaybeFlushAsync(cancellationToken);
            if (++scanned % ProgressEveryRows == 0)
            {
                LogPassProgress("pass 3 (episode details)", scanned, changed);
            }

            if (TsvReader.Null(row[1]) != "tvEpisode")
            {
                continue;
            }

            t.Value = row[0];
            pt.Value = (object?)TsvReader.Null(row[2]) ?? DBNull.Value;
            sy.Value = (object?)TsvReader.NullInt(row[5]) ?? DBNull.Value;
            rt.Value = (object?)TsvReader.NullInt(row[7]) ?? DBNull.Value;
            changed += await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        logger.LogInformation("ImdbImport pass 3 (episode details): {Changed} updated.", changed);
        return changed;
    }

    // Pass 4: title.akas.tsv → imdb_akas, filtered to active tracked show ids (show-level localized titles).
    // INSERT OR IGNORE on the (title_id, ordering) PK keeps a re-run at zero.
    private async Task<int> LoadAkasAsync(
        SqliteConnection connection,
        Batch batch,
        string path,
        HashSet<string> trackedImdbIds,
        CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = batch.Transaction;
        cmd.CommandText =
            "INSERT OR IGNORE INTO imdb_akas " +
            "(title_id, ordering, title, region, language, is_original_title) VALUES ($id, $ord, $title, $region, $lang, $orig);";
        var id = cmd.Parameters.Add("$id", SqliteType.Text);
        var ord = cmd.Parameters.Add("$ord", SqliteType.Integer);
        var title = cmd.Parameters.Add("$title", SqliteType.Text);
        var region = cmd.Parameters.Add("$region", SqliteType.Text);
        var lang = cmd.Parameters.Add("$lang", SqliteType.Text);
        var orig = cmd.Parameters.Add("$orig", SqliteType.Integer);

        var changed = 0;
        var scanned = 0;
        foreach (var row in TsvReader.ReadRows(path, 8))
        {
            cancellationToken.ThrowIfCancellationRequested();
            cmd.Transaction = await batch.MaybeFlushAsync(cancellationToken);
            if (++scanned % ProgressEveryRows == 0)
            {
                LogPassProgress("pass 4 (akas)", scanned, changed);
            }

            var titleId = TsvReader.Null(row[0]);
            if (titleId is null || !trackedImdbIds.Contains(titleId))
            {
                continue;
            }

            var ordering = TsvReader.NullInt(row[1]);
            if (ordering is null)
            {
                continue;
            }

            id.Value = titleId;
            ord.Value = ordering.Value;
            title.Value = (object?)TsvReader.Null(row[2]) ?? DBNull.Value;
            region.Value = (object?)TsvReader.Null(row[3]) ?? DBNull.Value;
            lang.Value = (object?)TsvReader.Null(row[4]) ?? DBNull.Value;
            orig.Value = (object?)TsvReader.NullInt(row[7]) ?? DBNull.Value;
            changed += await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        logger.LogInformation("ImdbImport pass 4 (akas): {Changed} inserted.", changed);
        return changed;
    }

    // The active tracked imdb ids (deleted_at IS NULL). Reads tracked_show joined to user_show so a
    // soft-deleted show's id is excluded — the same predicate the repository uses, expressed here because
    // this is a bulk read of a projection, not an aggregate load.
    private async Task<HashSet<string>> GetActiveImdbIdsAsync(CancellationToken cancellationToken)
    {
        var ids = await db.TrackedShows
            .Where(s => s.ImdbId != null && s.User!.DeletedAt == null)
            .Select(s => s.ImdbId!)
            .Distinct()
            .ToListAsync(cancellationToken);

        return ids.ToHashSet(StringComparer.Ordinal);
    }

    // Reuse a dataset already in data/imdb (plain .tsv or .tsv.gz), else download it. Returns the path plus
    // provenance; null when a download was needed and failed (R14: leave it for the next run).
    private async Task<(string path, DateTimeOffset downloadedAt, string sourceUrl, long fileSize)?> ResolveFileAsync(
        string dataset, CancellationToken cancellationToken)
    {
        var plain = Path.Combine(paths.ImdbDatasetPath, $"{dataset}.tsv");
        var gz = Path.Combine(paths.ImdbDatasetPath, $"{dataset}.tsv.gz");

        if (File.Exists(plain))
        {
            logger.LogInformation("ImdbImport: reusing on-disk {File}.", plain);
            return (plain, DateTimeOffset.UtcNow, SourceUrl(dataset), new FileInfo(plain).Length);
        }

        if (File.Exists(gz))
        {
            logger.LogInformation("ImdbImport: reusing on-disk {File}.", gz);
            return (gz, DateTimeOffset.UtcNow, SourceUrl(dataset), new FileInfo(gz).Length);
        }

        var result = await downloader.DownloadAsync(dataset, SourceUrl(dataset), gz, cancellationToken);
        if (!result.Ok)
        {
            logger.LogWarning("ImdbImport: download of {Dataset} failed: {Error}.", dataset, result.Error);
            return null;
        }

        return (gz, DateTimeOffset.UtcNow, SourceUrl(dataset), result.FileSize);
    }

    private string SourceUrl(string dataset) =>
        $"{options.Imdb.BaseUrl.TrimEnd('/')}/{dataset}.tsv.gz";

    private async Task RecordMetaAsync(List<DatasetMeta> meta, CancellationToken cancellationToken)
    {
        var existing = await db.ImdbDatasetMeta.ToDictionaryAsync(x => x.DatasetName, cancellationToken);

        foreach (var m in meta)
        {
            if (existing.TryGetValue(m.DatasetName, out var row))
            {
                row.DownloadedAt = m.DownloadedAt;
                row.SourceUrl = m.SourceUrl;
                row.FileSize = m.FileSize;
            }
            else
            {
                db.ImdbDatasetMeta.Add(new ImdbDatasetMeta
                {
                    DatasetName = m.DatasetName,
                    DownloadedAt = m.DownloadedAt,
                    SourceUrl = m.SourceUrl,
                    FileSize = m.FileSize,
                });
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    // A short-batch write transaction with lease renewal at each boundary (ADR-028). The load is one
    // logical operation but many small transactions: each batch commits, renews the lease on the lease's
    // own connection (which a held write transaction would lock out), and reopens. A renewal that updates
    // zero rows means the lease was lost — throw so the caller aborts and the next run re-does the work.
    //
    // The FK on imdb_episode.parent_tconst is checked at each batch commit, so the caller must keep
    // imdb_title populated before any imdb_episode insert (LoadAsync orders the passes accordingly).
    private sealed class Batch(
        SqliteConnection connection,
        IWriterLeaseHandle handle,
        int intervalSeconds,
        ILogger logger)
    {
        private SqliteTransaction? _transaction;
        private DateTimeOffset _last = DateTimeOffset.UtcNow;
        private int _rowsInBatch;

        public SqliteTransaction? Transaction => _transaction;

        public async Task BeginAsync(CancellationToken cancellationToken)
        {
            _transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            _last = DateTimeOffset.UtcNow;
            _rowsInBatch = 0;
        }

        // Called once per row. When the interval has elapsed, commit the current batch, renew the lease,
        // and open a fresh transaction; returns the transaction the caller's command must now use. The
        // row-count floor keeps a batch from committing on every row when the interval is short relative
        // to the row rate (a 3 GB scan renews roughly once per interval, not once per row).
        public async Task<SqliteTransaction?> MaybeFlushAsync(CancellationToken cancellationToken)
        {
            _rowsInBatch++;
            if (_rowsInBatch < MinRows || DateTimeOffset.UtcNow - _last < TimeSpan.FromSeconds(intervalSeconds))
            {
                return _transaction;
            }

            await CommitBatchAsync(cancellationToken);
            _transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            _last = DateTimeOffset.UtcNow;
            _rowsInBatch = 0;
            return _transaction;
        }

        // Commit the final batch (no renewal after it — the run is ending and the lease is released on dispose).
        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            if (_transaction is not null)
            {
                await _transaction.CommitAsync(cancellationToken);
                await _transaction.DisposeAsync();
                _transaction = null;
            }
        }

        public async Task AbortAsync()
        {
            if (_transaction is null)
            {
                return;
            }

            try
            {
                await _transaction.RollbackAsync(CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                // Already completed/disposed — nothing to roll back.
            }

            await _transaction.DisposeAsync();
            _transaction = null;
        }

        private async Task CommitBatchAsync(CancellationToken cancellationToken)
        {
            if (_transaction is not null)
            {
                await _transaction.CommitAsync(cancellationToken);
                await _transaction.DisposeAsync();
                _transaction = null;
            }

            if (!await handle.RenewAsync(cancellationToken))
            {
                logger.LogError("Lost the imdb_import lease mid-load; aborting the run.");
                throw new InvalidOperationException("Lost the imdb_import lease mid-load; rolling back.");
            }
        }

        // Commit at most once per interval, but never more often than this many rows — so a tiny fixture
        // (a few hundred rows) commits once at the end, and a 3 GB scan commits on the interval.
        private const int MinRows = 5_000;
    }

    private sealed record DatasetMeta(string DatasetName, DateTimeOffset DownloadedAt, string SourceUrl, long FileSize);
}

/// <summary>What one ImdbImport run did. <see cref="Refused"/> means another writer held the lease.</summary>
internal sealed record ImdbImportReport(bool Refused, int Changed, IReadOnlyList<string> Datasets);
