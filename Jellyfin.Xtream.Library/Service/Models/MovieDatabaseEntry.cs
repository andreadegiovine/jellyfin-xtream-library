using Newtonsoft.Json;

namespace Jellyfin.Xtream.Library.Service.Models;

/// <summary>
/// A single row of the movie library database. One row exists for every <c>.strm</c> file the
/// plugin has written, so a movie that is mapped into several target folders, or that produces
/// several stream files inside one folder, owns several rows.
/// </summary>
public class MovieDatabaseEntry
{
    /// <summary>
    /// Gets or sets the identifier of the provider this row belongs to.
    /// </summary>
    [JsonProperty("provider_id")]
    public string ProviderId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Xtream stream identifier of the movie.
    /// Null only for rows recovered from disk whose stream URL could not be parsed.
    /// </summary>
    [JsonProperty("stream_id")]
    public int? StreamId { get; set; }

    /// <summary>
    /// Gets or sets the TMDB identifier as supplied by the provider. Null means the provider does
    /// not expose one; identifiers resolved by name lookup are deliberately not stored here.
    /// </summary>
    [JsonProperty("tmdb_id")]
    public int? TmdbId { get; set; }

    /// <summary>
    /// Gets or sets the directory of the row, relative to the library root and using forward
    /// slashes so the value is portable between operating systems.
    /// </summary>
    [JsonProperty("directory_name")]
    public string DirectoryName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the sanitized file name without extension.
    /// </summary>
    [JsonProperty("file_name")]
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the last attempt to read the provider details
    /// failed. False both when a TMDB identifier was obtained and when the provider answered
    /// correctly without one, so that only genuine transport failures are retried.
    /// </summary>
    [JsonProperty("info_error")]
    public bool InfoError { get; set; }
}
