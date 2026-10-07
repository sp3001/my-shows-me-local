namespace Shows.Domain;

/// <summary>
/// A canonical genre name, from the TVmaze <c>genres[]</c> array.
/// </summary>
/// <remarks>
/// IMDb episode-level genres must never be used — they are inherited from the parent title
/// (<c>DATA_ANALYSIS.md</c> §6). Linked to shows through the <c>show_genre</c> join table.
/// </remarks>
public sealed class Genre
{
    public long Id { get; set; }

    /// <summary>Unique by name.</summary>
    public string Name { get; set; } = string.Empty;
}
