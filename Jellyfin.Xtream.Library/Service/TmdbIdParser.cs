using System;
using System.Globalization;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Parses the TMDB identifier field returned by Xtream panels.
/// </summary>
/// <remarks>
/// Panels are wildly inconsistent about this field: it may be absent, an empty string, the string
/// <c>0</c>, a placeholder such as <c>N/A</c>, or an IMDB identifier that has been dropped into
/// the wrong column. Anything that is not a positive decimal integer is treated as "the provider
/// does not know", because storing a wrong identifier is far more damaging than storing none.
/// </remarks>
public static class TmdbIdParser
{
    /// <summary>
    /// The largest value accepted as a TMDB identifier. TMDB identifiers are far below this
    /// bound, so anything above it is a foreign identifier that landed in the field by mistake.
    /// </summary>
    private const int MaxPlausibleId = 100_000_000;

    /// <summary>
    /// Converts a raw provider value into a TMDB identifier.
    /// </summary>
    /// <param name="value">The raw value as returned by the provider.</param>
    /// <returns>The identifier, or null when the value carries no usable identifier.</returns>
    public static int? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim().Trim('"', '\'');
        if (trimmed.Length == 0)
        {
            return null;
        }

        // Reject anything non-numeric outright rather than letting int.TryParse accept signs,
        // thousands separators or an IMDB "tt" prefix that happens to parse in some cultures.
        for (int i = 0; i < trimmed.Length; i++)
        {
            if (trimmed[i] < '0' || trimmed[i] > '9')
            {
                return null;
            }
        }

        if (!int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed))
        {
            return null;
        }

        if (parsed <= 0 || parsed > MaxPlausibleId)
        {
            return null;
        }

        return parsed;
    }

    /// <summary>
    /// Resolves the identifier to store, preferring the value obtained from the item details over
    /// the value obtained from the listing when the two disagree.
    /// </summary>
    /// <param name="fromList">The raw value taken from the remote listing.</param>
    /// <param name="fromDetails">The raw value taken from the item details, if they were read.</param>
    /// <param name="conflict">Set to true when both values are usable and differ.</param>
    /// <returns>The identifier to store, or null when neither value is usable.</returns>
    public static int? Reconcile(string? fromList, string? fromDetails, out bool conflict)
    {
        int? list = Parse(fromList);
        int? details = Parse(fromDetails);

        conflict = list.HasValue && details.HasValue && list.Value != details.Value;

        return details ?? list;
    }
}
