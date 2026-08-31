using Newtonsoft.Json;

namespace Jellyfin.Xtream.Library.Service.Models;

/// <summary>
/// A single row of the series library database. One row exists for every episode <c>.strm</c>
/// file the plugin has written.
/// </summary>
public class SeriesDatabaseEntry
{
    /// <summary>
    /// Gets or sets the identifier of the provider this row belongs to.
    /// </summary>
    [JsonProperty("provider_id")]
    public string ProviderId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Xtream series identifier. Used to reconcile the database against the
    /// remote series list. Null for rows recovered from disk when no snapshot was available to
    /// resolve the series the episode belongs to.
    /// </summary>
    [JsonProperty("series_id")]
    public int? SeriesId { get; set; }

    /// <summary>
    /// Gets or sets the Xtream episode identifier, which is the natural key of the row because
    /// it is the identifier embedded in the stream URL of the file.
    /// </summary>
    [JsonProperty("episode_id")]
    public string? EpisodeId { get; set; }

    /// <summary>
    /// Gets or sets the TMDB identifier as supplied by the provider, or null.
    /// </summary>
    [JsonProperty("tmdb_id")]
    public int? TmdbId { get; set; }

    /// <summary>
    /// Gets or sets the directory of the row, relative to the library root and using forward
    /// slashes. For episodes this is the season directory, not the series directory.
    /// </summary>
    [JsonProperty("directory_name")]
    public string DirectoryName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the sanitized file name without extension.
    /// </summary>
    [JsonProperty("file_name")]
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the season number the episode belongs to. Zero denotes specials.
    /// </summary>
    [JsonProperty("season")]
    public int Season { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the last attempt to read the provider details
    /// failed.
    /// </summary>
    [JsonProperty("info_error")]
    public bool InfoError { get; set; }
}
