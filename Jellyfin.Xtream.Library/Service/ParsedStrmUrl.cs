namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// The parts of a stream URL that identify what a <c>.strm</c> file points at.
/// </summary>
/// <param name="Kind">Whether the URL addresses a movie or an episode.</param>
/// <param name="BaseUrl">The provider base URL, without a trailing slash.</param>
/// <param name="ItemId">The identifier as it appears in the URL.</param>
/// <param name="NumericItemId">The identifier as a number, when it is one.</param>
public sealed record ParsedStrmUrl(StrmUrlKind Kind, string BaseUrl, string ItemId, int? NumericItemId);
