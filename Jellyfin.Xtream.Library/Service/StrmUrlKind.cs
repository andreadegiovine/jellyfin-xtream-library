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
