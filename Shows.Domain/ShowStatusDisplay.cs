namespace Shows.Domain;

/// <summary>
/// The one place that turns a <see cref="ShowStatus"/> into the human-readable text the UI shows.
/// </summary>
/// <remarks>
/// The enum names are PascalCase identifiers (<c>InDevelopment</c>, <c>ToBeDetermined</c>) and the
/// details page used to render them via <c>ToString()</c>, which surfaced "InDevelopment" and
/// "ToBeDetermined" without spaces. This is the single display-name table so the list card and the
/// details page agree, and so a new enum value has exactly one place to add its label. It is a
/// presentation mapping, not a business rule — the airing-state rules stay in
/// <see cref="ShowStatusRules"/>.
/// </remarks>
public static class ShowStatusDisplay
{
    /// <summary>
    /// The label to render for a status. Multi-word states read with spaces ("In Development",
    /// "To Be Determined"); <see cref="ShowStatus.Unknown"/> reads as "Unknown".
    /// </summary>
    public static string Text(ShowStatus status) => status switch
    {
        ShowStatus.Running => "Running",
        ShowStatus.Ended => "Ended",
        ShowStatus.Canceled => "Canceled",
        ShowStatus.ToBeDetermined => "To Be Determined",
        ShowStatus.InDevelopment => "In Development",
        _ => "Unknown",
    };
}
