using System;
using System.Globalization;

namespace Shows.App.Avalonia.Services;

/// <summary>
/// Builds the external TVmaze / IMDb links for a show from its ids (SPEC P5 — the URL is a function of
/// the id, never a stored column). The URL <em>formats</em> live in <c>appsettings.json</c>
/// (<c>Shows:Links:Tvmaze</c> / <c>Shows:Links:Imdb</c>) and are handed to this class once from the
/// composition root, so the two heads share one source of truth and the format is configuration, not a
/// code constant (user-issues improvement 20).
/// </summary>
/// <remarks>
/// <para>
/// This lives in the View layer and names no Infrastructure type (ARCHITECTURE.md §2): the composition
/// root reads the formats out of <c>ShowsOptions</c> and passes them in as plain strings via
/// <see cref="Configure"/>, exactly as it hands <see cref="ImageBehavior"/> its cache directory.
/// </para>
/// <para>
/// A blank format disables that link — the returned URL is empty and the caller hides the button — rather
/// than producing a broken URL. The IMDb link is empty when the show has no <c>tt</c> id.
/// </para>
/// </remarks>
public static class ShowLinks
{
    private const string DefaultTvmazeFormat = "https://www.tvmaze.com/shows/{0}";
    private const string DefaultImdbFormat = "https://www.imdb.com/title/{0}/";

    private static string _tvmazeFormat = DefaultTvmazeFormat;
    private static string _imdbFormat = DefaultImdbFormat;

    /// <summary>
    /// Sets the URL formats. Called once from the composition root with the configured values before the
    /// UI starts. Idempotent; a null/blank argument falls back to the built-in default so a missing config
    /// section still produces working links.
    /// </summary>
    public static void Configure(string? tvmazeFormat, string? imdbFormat)
    {
        _tvmazeFormat = string.IsNullOrWhiteSpace(tvmazeFormat) ? DefaultTvmazeFormat : tvmazeFormat;
        _imdbFormat = string.IsNullOrWhiteSpace(imdbFormat) ? DefaultImdbFormat : imdbFormat;
    }

    /// <summary>The TVmaze show page for <paramref name="tvmazeId"/>, or empty when the format is blank.</summary>
    public static string Tvmaze(int tvmazeId) =>
        Format(_tvmazeFormat, tvmazeId.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// The IMDb title page for <paramref name="imdbId"/>, or empty when the show has no IMDb id or the
    /// format is blank.
    /// </summary>
    public static string Imdb(string? imdbId) =>
        string.IsNullOrWhiteSpace(imdbId) ? string.Empty : Format(_imdbFormat, imdbId);

    private static string Format(string format, string id)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            return string.Empty;
        }

        try
        {
            return string.Format(CultureInfo.InvariantCulture, format, id);
        }
        catch (FormatException)
        {
            // A malformed format string in appsettings must not crash a page; treat it as "no link" and
            // let the button hide. The composition root logs nothing here (this is the View layer), but a
            // broken link is visible as a missing button rather than an exception.
            return string.Empty;
        }
    }
}
