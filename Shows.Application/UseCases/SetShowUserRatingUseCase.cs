namespace Shows.Application.UseCases;

/// <summary>
/// Sets (or clears) the user's own 1-10 rating for a tracked show. This is the user's opinion, distinct
/// from TVmaze's catalog rating (<see cref="Shows.Domain.TrackedShow.RatingAverage"/>), and is stored as
/// user data (SPEC P4) so it survives a rebuild.
/// </summary>
/// <remarks>
/// A tiny write use case rather than letting the ViewModel reach <see cref="IUserShowRepository"/> directly:
/// ViewModels talk only to use cases (<c>ARCHITECTURE.md</c> §3), and this keeps the one write to
/// <c>user_show.user_rating</c> behind the same seam as every other mutation. The 1-10 range is enforced
/// here (the single write seam), so a caller cannot persist an out-of-range value. A null clears the rating.
/// Orthogonal to category and the logical-delete state, so neither is touched.
/// </remarks>
public sealed class SetShowUserRatingUseCase(
    IUserShowRepository userShows,
    IUnitOfWork unitOfWork)
{
    /// <summary>The lowest rating the user may give.</summary>
    public const int MinRating = 1;

    /// <summary>The highest rating the user may give.</summary>
    public const int MaxRating = 10;

    /// <summary>
    /// Sets the rating and returns the new value, so the caller can reflect it without a reload.
    /// <paramref name="userRating"/> must be null (clear) or within <see cref="MinRating"/>..<see cref="MaxRating"/>;
    /// anything else throws. The repository requires the <c>user_show</c> row to exist (SPEC P4), so rating
    /// an untracked show throws rather than silently doing nothing.
    /// </summary>
    public async Task<int?> ExecuteAsync(
        long trackedShowId, int? userRating, CancellationToken cancellationToken)
    {
        if (userRating is { } rating && (rating < MinRating || rating > MaxRating))
        {
            throw new ArgumentOutOfRangeException(
                nameof(userRating),
                $"A user rating must be between {MinRating} and {MaxRating}, or null to clear it.");
        }

        await userShows
            .SetUserRatingAsync(trackedShowId, userRating, cancellationToken)
            .ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return userRating;
    }
}
