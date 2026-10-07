using Shows.Domain;

namespace Shows.Tests.Unit;

/// <summary>
/// The ShowStatus → display-name table. Guards the spacing fix (issue: the details page rendered
/// "InDevelopment"/"ToBeDetermined" via <c>ToString()</c>) and pins every enum value's label so a new
/// value cannot silently fall through to "Unknown".
/// </summary>
public class ShowStatusDisplayTests
{
    [Theory]
    [InlineData(ShowStatus.Unknown, "Unknown")]
    [InlineData(ShowStatus.Running, "Running")]
    [InlineData(ShowStatus.Ended, "Ended")]
    [InlineData(ShowStatus.Canceled, "Canceled")]
    [InlineData(ShowStatus.ToBeDetermined, "To Be Determined")]
    [InlineData(ShowStatus.InDevelopment, "In Development")]
    public void Text_returns_the_human_readable_label(ShowStatus status, string expected) =>
        Assert.Equal(expected, ShowStatusDisplay.Text(status));

    [Fact]
    public void Multiword_statuses_read_with_spaces_not_pascal_case()
    {
        // The original defect: ToString() on these two produced "InDevelopment"/"ToBeDetermined".
        Assert.Equal("In Development", ShowStatusDisplay.Text(ShowStatus.InDevelopment));
        Assert.Equal("To Be Determined", ShowStatusDisplay.Text(ShowStatus.ToBeDetermined));
        Assert.DoesNotContain(" ", ShowStatusDisplay.Text(ShowStatus.Running));
    }
}
