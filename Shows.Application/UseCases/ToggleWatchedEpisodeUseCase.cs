using Shows.Domain;

namespace Shows.Application.UseCases;

/// <summary>
/// Marks an episode watched, or unmarks it (SPEC R10 — bidirectional).
/// </summary>
/// <remarks>
/// Toggling recomputes the cached columns and re-evaluates R5, so this delegates to
/// <see cref="RecomputeProgressUseCase"/> rather than updating a counter inline. The auto-finish
/// behaviour the user sees on the last episode of an Ended show is that recompute, not a special case
/// here.
/// </remarks>
public sealed class ToggleWatchedEpisodeUseCase(
    IEpisodeRepository episodes,
    IIdGenerator idGenerator,
    RecomputeProgressUseCase recompute,
    IUnitOfWork unitOfWork)
{
    /// <summary>
    /// Returns the show's progress after the toggle, so the list can update its bar without a reload.
    /// </summary>
    public async Task<ShowProgress> ExecuteAsync(
        long episodeId, CancellationToken cancellationToken)
    {
        var episode = await episodes.FindByIdAsync(episodeId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No episode with id {episodeId}.");

        var existing = await episodes.ListWatchesByShowIdAsync(episode.ShowId, cancellationToken)
            .ConfigureAwait(false);

        var wasWatched = existing.Any(w => w.EpisodeId == episodeId);

        if (wasWatched)
        {
            // The one user action that is a real delete: absence of a row is the meaning
            // (SCHEMA.md §2 episode_watch).
            await episodes.RemoveWatchAsync(episodeId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await episodes.AddWatchAsync(
                new EpisodeWatch
                {
                    Id = idGenerator.NextId(),
                    ShowId = episode.ShowId,
                    EpisodeId = episodeId,
                    WatchedAt = DateTimeOffset.UtcNow,
                },
                cancellationToken).ConfigureAwait(false);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return await recompute.ExecuteAsync(episode.ShowId, cancellationToken).ConfigureAwait(false)
            ?? new ShowProgress(0, 0, false, false);
    }
}
