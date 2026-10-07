using System.Text.RegularExpressions;

namespace Shows.Infrastructure.Persistence;

/// <summary>
/// Strips HTML markup from a TVmaze <c>summary</c> for the mapped <c>description</c> column. The raw
/// markup stays in the source table; stripping is irreversible, so it happens once, on the mapped copy
/// (DATA_ANALYSIS.md §8 fact 6).
/// </summary>
/// <remarks>
/// Deliberately minimal: drop tags, decode the handful of entities TVmaze actually emits, and collapse
/// whitespace. This is not a general HTML parser — the source is a short synopsis, and a full parser
/// would be more machinery than the data warrants.
/// </remarks>
public static class HtmlStripper
{
    private static readonly Regex Tags = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    public static string? Strip(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var text = Tags.Replace(html, " ");
        text = text
            .Replace("&amp;", "&", StringComparison.Ordinal)
            .Replace("&lt;", "<", StringComparison.Ordinal)
            .Replace("&gt;", ">", StringComparison.Ordinal)
            .Replace("&quot;", "\"", StringComparison.Ordinal)
            .Replace("&#39;", "'", StringComparison.Ordinal)
            .Replace("&nbsp;", " ", StringComparison.Ordinal);

        return Whitespace.Replace(text, " ").Trim();
    }
}
