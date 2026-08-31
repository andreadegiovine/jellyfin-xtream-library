using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Jellyfin.Xtream.Library.Service.Models;

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
