using Shows.Domain;

namespace Shows.Tests.Unit;

/// <summary>
/// SPEC R5 and R7, plus the R12 fail-closed rule. Step 0.2's verification.
/// </summary>
public class ShowProgressTests
{
    private static EpisodeProgressFact Regular(bool watched) => new(EpisodeType.Regular, watched);

    // A significant special (TVmaze "significant_special") counts toward progress like a regular
    // (ADR-034); an insignificant one ("insignificant_special") is excluded and drives the note.
    private static EpisodeProgressFact Significant(bool watched) => new(EpisodeType.Special, watched);
    private static EpisodeProgressFact Insignificant(bool watched) => new(EpisodeType.InsignificantSpecial, watched);

    [Fact]
    public void Eligible_when_every_regular_episode_is_watched_and_show_has_ended()
    {
        var progress = ShowProgress.Compute(
            ShowStatus.Ended,
            [Regular(true), Regular(true), Regular(true)]);

        Assert.Equal(3, progress.WatchedRegular);
        Assert.Equal(3, progress.TotalRegular);
        Assert.True(progress.AutoFinishEligible);
    }

    [Fact]
    public void Not_eligible_while_the_show_is_running_even_if_fully_watched()
    {
        // SPEC R5: a show whose status indicates it is still airing is never promoted, because more
        // episodes are expected. This is the exact bug class ADR-022 exists to prevent.
        var progress = ShowProgress.Compute(
            ShowStatus.Running,
            [Regular(true), Regular(true)]);

        Assert.Equal(2, progress.WatchedRegular);
        Assert.Equal(2, progress.TotalRegular);
        Assert.False(progress.AutoFinishEligible);
    }

    [Fact]
    public void Not_eligible_while_any_regular_episode_is_unwatched()
    {
        var progress = ShowProgress.Compute(
            ShowStatus.Ended,
            [Regular(true), Regular(false)]);

        Assert.Equal(1, progress.WatchedRegular);
        Assert.Equal(2, progress.TotalRegular);
        Assert.False(progress.AutoFinishEligible);
    }

    [Fact]
    public void Degenerate_show_with_no_regular_episodes_is_never_eligible_and_never_divides_by_zero()
    {
        // SPEC R7. At least one such show exists in the real data (totalEpisodes: 0).
        var progress = ShowProgress.Compute(ShowStatus.Ended, []);

        Assert.Equal(0, progress.TotalRegular);
        Assert.False(progress.AutoFinishEligible);
        Assert.Null(progress.ProgressFraction);
    }

    [Fact]
    public void Degenerate_show_made_only_of_insignificant_specials_is_never_eligible()
    {
        // SPEC R4 / ADR-034: insignificant specials are excluded from every progress figure, so a show
        // whose only episodes are filler has total_regular = 0 and is degenerate under R7.
        var progress = ShowProgress.Compute(ShowStatus.Ended, [Insignificant(true), Insignificant(false)]);

        Assert.Equal(0, progress.TotalRegular);
        Assert.False(progress.AutoFinishEligible);
        Assert.True(progress.HasUnwatchedSpecials);
    }

    [Fact]
    public void Insignificant_specials_are_excluded_from_the_regular_counts()
    {
        var progress = ShowProgress.Compute(
            ShowStatus.Ended,
            [Regular(true), Regular(false), Insignificant(false), Insignificant(true)]);

        Assert.Equal(1, progress.WatchedRegular);
        Assert.Equal(2, progress.TotalRegular);
        Assert.True(progress.HasUnwatchedSpecials);
    }

    [Fact]
    public void Significant_specials_count_toward_the_regular_totals()
    {
        // ADR-034: a significant special is a real episode the user is expected to watch, so it
        // participates in the totals exactly like a regular.
        var progress = ShowProgress.Compute(
            ShowStatus.Ended,
            [Regular(true), Significant(true), Significant(false)]);

        Assert.Equal(2, progress.WatchedRegular);
        Assert.Equal(3, progress.TotalRegular);
        Assert.False(progress.HasUnwatchedSpecials); // significant specials never drive the note
        Assert.False(progress.AutoFinishEligible); // the unwatched significant special blocks finishing
    }

    [Fact]
    public void A_show_finished_on_regulars_plus_significant_specials_is_eligible()
    {
        // The finished-status bug this guards: an unwatched insignificant special must NOT block
        // finishing, but every regular and every significant special must be watched.
        var progress = ShowProgress.Compute(
            ShowStatus.Ended,
            [Regular(true), Significant(true), Insignificant(false)]);

        Assert.Equal(2, progress.WatchedRegular);
        Assert.Equal(2, progress.TotalRegular);
        Assert.True(progress.AutoFinishEligible);
        Assert.True(progress.HasUnwatchedSpecials); // the filler is surfaced, but does not block
    }

    [Fact]
    public void Unwatched_insignificant_specials_are_reported_but_do_not_block_eligibility()
    {
        // SPEC R4/R5: a show with only unwatched insignificant specials left is eligible on its
        // regulars, and the specials surface as a note rather than as a blocker.
        var progress = ShowProgress.Compute(ShowStatus.Ended, [Regular(true), Insignificant(false)]);

        Assert.True(progress.AutoFinishEligible);
        Assert.True(progress.HasUnwatchedSpecials);
    }

    [Fact]
    public void Watched_insignificant_specials_produce_no_note()
    {
        var progress = ShowProgress.Compute(ShowStatus.Ended, [Regular(true), Insignificant(true)]);

        Assert.False(progress.HasUnwatchedSpecials);
    }

    [Fact]
    public void Unknown_status_is_never_treated_as_ended()
    {
        // The fail-closed rule that prevents the ADR-022 bug class: an unrecognised provider string
        // lands on Unknown, and Unknown must not satisfy the "no more episodes expected" test.
        Assert.False(ShowStatusRules.NoMoreEpisodesExpected(ShowStatus.Unknown));

        var progress = ShowProgress.Compute(ShowStatus.Unknown, [Regular(true), Regular(true)]);

        Assert.False(progress.AutoFinishEligible);
    }

    [Fact]
    public void ToBeDetermined_is_not_treated_as_ended_because_a_renewal_is_possible()
    {
        // SPEC R12: "no - a renewal is still possible".
        Assert.False(ShowStatusRules.NoMoreEpisodesExpected(ShowStatus.ToBeDetermined));

        var progress = ShowProgress.Compute(ShowStatus.ToBeDetermined, [Regular(true)]);

        Assert.False(progress.AutoFinishEligible);
    }

    [Fact]
    public void InDevelopment_is_not_treated_as_ended_because_the_show_has_not_begun_airing()
    {
        // SPEC R12: a show in development has not begun airing and may still air, so — like
        // ToBeDetermined — it must never satisfy the auto-finish test.
        Assert.False(ShowStatusRules.NoMoreEpisodesExpected(ShowStatus.InDevelopment));

        var progress = ShowProgress.Compute(ShowStatus.InDevelopment, [Regular(true)]);

        Assert.False(progress.AutoFinishEligible);
    }

    [Fact]
    public void Canceled_is_treated_as_no_more_episodes_expected()
    {
        // SPEC R12's per-value table says Canceled can auto-finish. See the note in SPEC.md R5 about
        // the R5/R12 wording conflict resolved in favour of R12's table.
        Assert.True(ShowStatusRules.NoMoreEpisodesExpected(ShowStatus.Canceled));

        var progress = ShowProgress.Compute(ShowStatus.Canceled, [Regular(true)]);

        Assert.True(progress.AutoFinishEligible);
    }

    [Fact]
    public void Unknown_episode_type_is_not_counted_as_regular()
    {
        // Fail-closed on the episode axis too (SPEC R12): an unrecognised type is excluded from
        // progress rather than silently counted.
        var progress = ShowProgress.Compute(
            ShowStatus.Ended,
            [new EpisodeProgressFact(EpisodeType.Unknown, false), Regular(true)]);

        Assert.Equal(1, progress.TotalRegular);
        Assert.True(progress.AutoFinishEligible);
    }

    [Theory]
    [InlineData(0, 0, null)]
    [InlineData(1, 2, 0.5)]
    [InlineData(2, 2, 1.0)]
    [InlineData(0, 3, 0.0)]
    public void Progress_fraction_matches_total_or_is_null(int watched, int total, double? expected)
    {
        var episodes = new List<EpisodeProgressFact>();
        for (var i = 0; i < total; i++)
        {
            episodes.Add(Regular(i < watched));
        }

        var progress = ShowProgress.Compute(ShowStatus.Ended, episodes);

        Assert.Equal(expected, progress.ProgressFraction);
    }
}
