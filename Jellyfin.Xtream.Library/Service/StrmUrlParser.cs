using System;
using System.Globalization;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// The kind of item a stream URL points at.
/// </summary>
public enum StrmUrlKind
{
    /// <summary>
    /// A movie stream.
    /// </summary>
    Movie,

    /// <summary>
    /// A single series episode.
    /// </summary>
    Episode
}

/// <summary>
/// The parts of a stream URL that identify what a <c>.strm</c> file points at.
/// </summary>
/// <param name="Kind">Whether the URL addresses a movie or an episode.</param>
/// <param name="BaseUrl">The provider base URL, without a trailing slash.</param>
/// <param name="ItemId">The identifier as it appears in the URL.</param>
/// <param name="NumericItemId">The identifier as a number, when it is one.</param>
public sealed record ParsedStrmUrl(StrmUrlKind Kind, string BaseUrl, string ItemId, int? NumericItemId);

/// <summary>
/// Recovers the provider and the item identifier from the URL stored inside a <c>.strm</c> file.
/// </summary>
/// <remarks>
/// This is what makes the filesystem backfill exact rather than heuristic: the file content
/// carries both the identifier and the provider base URL, so rows can be rebuilt without guessing
/// from directory names. Three layouts are produced by the plugin and all three are understood
/// here: <c>{base}/movie/{user}/{pass}/{id}.{ext}</c>, <c>{base}/series/{user}/{pass}/{id}.{ext}</c>
/// and the Dispatcharr proxy form <c>{base}/proxy/vod/movie/{uuid}?stream_id={id}</c>.
/// </remarks>
public static class StrmUrlParser
{
    /// <summary>
    /// Parses the contents of a <c>.strm</c> file.
    /// </summary>
    /// <param name="content">The raw file contents.</param>
    /// <returns>The parsed parts, or null when the URL is not one the plugin writes.</returns>
    public static ParsedStrmUrl? Parse(string? content)
    {
        string? line = FirstMeaningfulLine(content);
        if (line is null)
        {
            return null;
        }

        if (!Uri.TryCreate(line, UriKind.Absolute, out Uri? uri))
        {
            return null;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        string[] segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        ParsedStrmUrl? dispatcharr = TryParseDispatcharr(uri, segments);
        if (dispatcharr is not null)
        {
            return dispatcharr;
        }

        return TryParseXtream(uri, segments);
    }

    private static ParsedStrmUrl? TryParseDispatcharr(Uri uri, string[] segments)
    {
        // {base}/proxy/vod/movie/{uuid}?stream_id={id}
        int proxyIndex = IndexOfSequence(segments, "proxy", "vod", "movie");
        if (proxyIndex < 0)
        {
            return null;
        }

        string? streamId = GetQueryValue(uri.Query, "stream_id");
        if (streamId is null)
        {
            return null;
        }

        return new ParsedStrmUrl(
            StrmUrlKind.Movie,
            BuildBaseUrl(uri, segments, proxyIndex),
            streamId,
            ParseInt(streamId));
    }

    private static ParsedStrmUrl? TryParseXtream(Uri uri, string[] segments)
    {
        // {base}/movie|series/{user}/{pass}/{id}.{ext}
        // Scanned from the end so that a provider hosted under a path prefix that happens to
        // contain "movie" does not derail the match.
        for (int i = segments.Length - 4; i >= 0; i--)
        {
            StrmUrlKind kind;
            if (string.Equals(segments[i], "movie", StringComparison.OrdinalIgnoreCase))
            {
                kind = StrmUrlKind.Movie;
            }
            else if (string.Equals(segments[i], "series", StringComparison.OrdinalIgnoreCase))
            {
                kind = StrmUrlKind.Episode;
            }
            else
            {
                continue;
            }

            if (i + 3 != segments.Length - 1)
            {
                continue;
            }

            string last = segments[^1];
            int dot = last.LastIndexOf('.');
            string id = dot > 0 ? last[..dot] : last;
            if (id.Length == 0)
            {
                continue;
            }

            return new ParsedStrmUrl(kind, BuildBaseUrl(uri, segments, i), id, ParseInt(id));
        }

        return null;
    }

    private static string BuildBaseUrl(Uri uri, string[] segments, int stopIndex)
    {
        string authority = uri.GetLeftPart(UriPartial.Authority);
        if (stopIndex == 0)
        {
            return authority;
        }

        return authority + "/" + string.Join('/', System.Linq.Enumerable.Take(segments, stopIndex));
    }

    private static int IndexOfSequence(string[] segments, string a, string b, string c)
    {
        for (int i = 0; i + 2 < segments.Length; i++)
        {
            if (string.Equals(segments[i], a, StringComparison.OrdinalIgnoreCase)
                && string.Equals(segments[i + 1], b, StringComparison.OrdinalIgnoreCase)
                && string.Equals(segments[i + 2], c, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static string? GetQueryValue(string query, string key)
    {
        if (string.IsNullOrEmpty(query))
        {
            return null;
        }

        foreach (string pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                continue;
            }

            if (string.Equals(pair[..equals], key, StringComparison.OrdinalIgnoreCase))
            {
                string value = pair[(equals + 1)..];
                return value.Length == 0 ? null : Uri.UnescapeDataString(value);
            }
        }

        return null;
    }

    private static int? ParseInt(string value)
    {
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : null;
    }

    private static string? FirstMeaningfulLine(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        foreach (string line in content.Split('\n'))
        {
            string trimmed = line.Trim().Trim('\uFEFF');
            if (trimmed.Length > 0 && !trimmed.StartsWith('#'))
            {
                return trimmed;
            }
        }

        return null;
    }

    /// <summary>
    /// Normalises a base URL for comparison, so that a trailing slash or a difference in casing of
    /// the host does not stop a file from being attributed to its provider.
    /// </summary>
    /// <param name="baseUrl">The base URL.</param>
    /// <returns>The normalised form.</returns>
    public static string NormalizeBaseUrl(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return string.Empty;
        }

        string trimmed = baseUrl.Trim().TrimEnd('/');
        return Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri)
            ? uri.GetLeftPart(UriPartial.Authority).ToUpperInvariant() + uri.AbsolutePath.TrimEnd('/')
            : trimmed.ToUpperInvariant();
    }
}
