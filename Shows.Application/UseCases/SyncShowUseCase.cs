namespace Shows.Application.UseCases;

/// <summary>
/// What a single-show sync did. <see cref="Changed"/> mirrors the mapper's
/// <see cref="MapResult.AnyChange"/> so the details page can tell the user whether anything moved;
/// a no-op sync (TVmaze unchanged) honestly reports <c>false</c>.
/// </summary>
public sealed record SyncResult(bool Changed);

/// <summary>
/// The details-page "Sync" button's on-demand path (user-issues improvement 7): re-fetch THIS show from
/// TVmaze and re-run the mapper for it — the single-show analogue of the <c>EpisodeRefresh</c> job, driven
/// through the same <see cref="IShowMapper.MapShowAsync(int)"/> fetch path the add path uses.
/// </summary>
/// <remarks>
/// <para>
/// It takes only interfaces and opens <b>no lease</b> (a single-show interactive fetch, like
/// <see cref="FetchLocalizedTitlesUseCase"/>). The mapper is the sole writer of mapped rows, so this use
/// case never touches a user table and never regenerates a surrogate id — those guarantees live in the
/// mapper (SPEC P4/R11), not here. It writes no show or episode field itself.
/// </para>
/// <para>
/// <b>R14:</b> an unreachable TVmaze throws from the provider (surfaced through the mapper's fetch seam)
/// and propagates to the caller — it is never swallowed into a silent no-change. The details page catches
/// it and shows the message; the show's existing rows are left untouched because the mapper throws before
/// writing anything.
/// </para>
/// </remarks>
public sealed class SyncShowUseCase(
    ITrackedShowRepository trackedShows,
    IShowMapper mapper,
    IUnitOfWork unitOfWork)
{
    public async Task<SyncResult> ExecuteAsync(
        long trackedShowId, CancellationToken cancellationToken)
    {
        // FindByIdAsync filters deleted_at IS NULL (SPEC R8): syncing an untracked show is refused, not a
        // silent no-op — the same guard the category-change path uses.
        var show = await trackedShows.FindByIdAsync(trackedShowId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No tracked show with id {trackedShowId}.");

        // The mapper owns the fetch (via its ITvmazeShowSource seam) and every mapped-row write. A
        // transport failure throws here, before any change is staged, so the show's rows are unchanged.
        var result = await mapper.MapShowAsync(show.TvmazeId, cancellationToken).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new SyncResult(result.AnyChange);
    }
}
