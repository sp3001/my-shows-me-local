using Shows.Domain;
using Shows.Infrastructure.Persistence.Vocabulary;

namespace Shows.Tests.Unit;

/// <summary>
/// SPEC R12: the raw-string → enum dictionary is the only place a provider status becomes an enum,
/// matching is exact/case-sensitive, and an unrecognised value fails closed to Unknown.
/// </summary>
public class TvmazeVocabularyTests
{
    [Theory]
    [InlineData("Running", ShowStatus.Running)]
    [InlineData("Ended", ShowStatus.Ended)]
    [InlineData("Canceled", ShowStatus.Canceled)]
    [InlineData("To Be Determined", ShowStatus.ToBeDetermined)]
    [InlineData("In Development", ShowStatus.InDevelopment)]
    public void Known_status_maps_to_its_enum(string statusRaw, ShowStatus expected)
    {
        Assert.Equal(expected, TvmazeVocabulary.MapStatus(statusRaw));
        Assert.True(TvmazeVocabulary.IsKnownStatus(statusRaw));
    }

    [Fact]
    public void InDevelopment_is_recognised_not_failed_closed()
    {
        // The issue-5 fix: "In Development" used to be absent from the dictionary and landed on
        // Unknown (writing a tvmaze_status_seen.mapped_to NULL row). It is now its own status.
        Assert.Equal(ShowStatus.InDevelopment, TvmazeVocabulary.MapStatus("In Development"));
        Assert.True(TvmazeVocabulary.IsKnownStatus("In Development"));
    }

    [Theory]
    [InlineData("in development")] // case-sensitive: exact match only (SPEC R12)
    [InlineData("In  Development")] // double space is not the provider string
    [InlineData("On Hiatus")] // genuinely unmapped
    [InlineData("")]
    [InlineData(null)]
    public void Unrecognised_status_fails_closed_to_Unknown(string? statusRaw)
    {
        Assert.Equal(ShowStatus.Unknown, TvmazeVocabulary.MapStatus(statusRaw));
        Assert.False(TvmazeVocabulary.IsKnownStatus(statusRaw));
    }
}
