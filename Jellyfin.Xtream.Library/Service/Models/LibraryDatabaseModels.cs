using System;
using System.Collections.Generic;
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

/// <summary>
/// On-disk envelope of a library database file.
/// </summary>
/// <typeparam name="TEntry">The row type held by the file.</typeparam>
public class LibraryDatabaseFile<TEntry>
{
    /// <summary>
    /// Gets or sets the schema version, so that future layout changes can be migrated instead of
    /// discarded.
    /// </summary>
    [JsonProperty("schema_version")]
    public int SchemaVersion { get; set; } = LibraryDatabaseSchema.CurrentVersion;

    /// <summary>
    /// Gets or sets the moment the initial filesystem backfill completed in full. While this is
    /// null the database is considered incomplete and no destructive reconciliation may run
    /// against it.
    /// </summary>
    [JsonProperty("backfill_completed_at")]
    public DateTime? BackfillCompletedAt { get; set; }

    /// <summary>
    /// Gets or sets the rows of the database.
    /// </summary>
    [JsonProperty("entries")]
    public List<TEntry> Entries { get; set; } = new List<TEntry>();
}

/// <summary>
/// Schema constants shared by the library database files.
/// </summary>
public static class LibraryDatabaseSchema
{
    /// <summary>
    /// The schema version written by this build.
    /// </summary>
    public const int CurrentVersion = 1;
}
