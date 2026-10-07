using Shows.Domain;

namespace Shows.Tests.Unit;

/// <summary>
/// The one country rule (improvement 21). The case that motivated it is real: TVmaze show 84377
/// ("Dead-End Job") is a Korean Netflix drama whose payload has <c>"webChannel": { "name": "Netflix",
/// "country": null }</c> and <c>"language": "Korean"</c> — so before the fallback it displayed no country
/// anywhere and filed under "Not set" in the Discovery facet.
/// </summary>
public class CountryRulesTests
{
    [Fact]
    public void Broadcaster_country_name_wins()
    {
        // A name is more readable than a code, so it wins even when the language is also known.
        Assert.Equal("United States",
            CountryRules.Display("United States", "US", "English"));
    }

    [Fact]
    public void Country_code_is_used_when_the_name_is_absent()
    {
        // TVmaze has been seen returning a country object with only a code; the code is still a country.
        Assert.Equal("US", CountryRules.Display(null, "US", "English"));
    }

    [Fact]
    public void Language_is_the_fallback_when_the_broadcaster_has_no_country()
    {
        // The motivating case: Netflix carries no country, the language says what the show is.
        Assert.Equal("Korean", CountryRules.Display(null, null, "Korean"));
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("", "", "")]
    [InlineData("   ", "  ", " \t ")]
    public void Nothing_is_returned_when_neither_country_nor_language_is_known(
        string? name, string? code, string? language)
    {
        // A show with genuinely no country and no language must stay null so the facet files it under
        // "Not set" rather than inventing a value.
        Assert.Null(CountryRules.Display(name, code, language));
    }

    [Theory]
    [InlineData("", "US")]
    [InlineData("   ", "US")]
    public void A_blank_country_does_not_win_the_coalescing_chain(string? name, string? code)
    {
        // TVmaze returns "" for a field it chose not to fill. If "" won, the UI would render an empty
        // country and the facet would count the show as filled — the invisible failure this guards.
        Assert.Equal("US", CountryRules.Display(name, code, null));
    }

    [Fact]
    public void Language_fallback_survives_a_blank_country_name_from_a_present_broadcaster()
    {
        // The shape the user reported: broadcaster present, country object absent, language filled.
        Assert.Equal("Korean", CountryRules.Display(countryName: "", countryCode: null, language: "Korean"));
    }
}
