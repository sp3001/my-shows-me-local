using Shows.Domain;

namespace Shows.Tests.Unit;

/// <summary>SPEC R3 — title display selection.</summary>
public class TitleRulesTests
{
    [Fact]
    public void Localized_title_is_primary_with_the_original_beneath()
    {
        var pair = TitleRules.Select("Black Mirror", "Чёрное зеркало");

        Assert.Equal("Чёрное зеркало", pair.Primary);
        Assert.Equal("Black Mirror", pair.Secondary);
    }

    [Fact]
    public void Original_title_alone_is_primary_when_there_is_no_localized_title()
    {
        var pair = TitleRules.Select("Dark", null);

        Assert.Equal("Dark", pair.Primary);
        Assert.Null(pair.Secondary);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_localized_title_is_treated_as_absent(string localized)
    {
        // The import trims the export's titles (ADR-015), but an untrimmed field can arrive as
        // whitespace, which must not render as a blank heading over the real title.
        var pair = TitleRules.Select("Dark", localized);

        Assert.Equal("Dark", pair.Primary);
        Assert.Null(pair.Secondary);
    }
}
