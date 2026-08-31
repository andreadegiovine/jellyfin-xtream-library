using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Library.Service.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Owns <c>movies.json</c> and <c>series.json</c>, the authoritative index of every <c>.strm</c>
/// file the plugin has written into a library.
/// </summary>
/// <remarks>
/// <para>
/// Every directory name and file name the plugin uses is resolved from this index rather than by
/// scanning the filesystem. The filesystem is still consulted to detect that something has been
/// deleted behind the plugin's back, but never to decide what a thing should be called.
/// </para>
/// <para>
/// The service is a singleton because it is read by API endpoints and by the failed-item retry
/// path, both of which run outside a sync. State is kept per library root, since two providers
/// that share a library root also share these files.
/// </para>
/// </remarks>
public class LibraryDatabaseService : IDisposable
{
    /// <summary>
    /// File name of the movie database inside the library root.
    /// </summary>
    public const string MoviesFileName = "movies.json";

    /// <summary>
    /// File name of the series database inside the library root.
    /// </summary>
    public const string SeriesFileName = "series.json";

    private readonly ILogger<LibraryDatabaseService> _logger;
    private readonly Dictionary<string, LibraryDatabaseState> _states =
        new Dictionary<string, LibraryDatabaseState>(StringComparer.OrdinalIgnoreCase);

    private readonly object _statesLock = new object();
    private readonly SemaphoreSlim _ioLock = new SemaphoreSlim(1, 1);
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryDatabaseService"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public LibraryDatabaseService(ILogger<LibraryDatabaseService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Loads the databases for a library root, reusing the cached state when it is already
    /// resident.
    /// </summary>
    /// <param name="libraryPath">The library root.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The state for the library root.</returns>
    public async Task<LibraryDatabaseState> GetOrLoadAsync(string libraryPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryPath);

        string key = Path.GetFullPath(libraryPath);
        lock (_statesLock)
        {
            if (_states.TryGetValue(key, out LibraryDatabaseState? cached))
            {
                return cached;
            }
        }

        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_statesLock)
            {
                if (_states.TryGetValue(key, out LibraryDatabaseState? cached))
                {
                    return cached;
                }
            }

            LibraryDatabaseFile<MovieDatabaseEntry> movies =
                ReadFile<MovieDatabaseEntry>(Path.Combine(key, MoviesFileName));
            LibraryDatabaseFile<SeriesDatabaseEntry> series =
                ReadFile<SeriesDatabaseEntry>(Path.Combine(key, SeriesFileName));

            LibraryDatabaseState state = new LibraryDatabaseState(key, movies, series);

            lock (_statesLock)
            {
                _states[key] = state;
            }

            _logger.LogInformation(
                "Loaded library database for {LibraryPath}: {MovieRows} movie rows, {SeriesRows} series rows",
                key,
                movies.Entries.Count,
                series.Entries.Count);

            return state;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <summary>
    /// Writes both databases of a library root to disk, atomically and only when something
    /// changed.
    /// </summary>
    /// <param name="state">The state to persist.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the write.</returns>
    public async Task SaveAsync(LibraryDatabaseState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (!state.IsDirty)
        {
            return;
        }

        // Deliberately not honouring cancellation past this point: the rows describe files that
        // already exist on disk, and dropping them would make those files look unknown, which the
        // reconciliation pass treats as orphans.
        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LibraryDatabaseFile<MovieDatabaseEntry> movies = state.SnapshotMovies();
            LibraryDatabaseFile<SeriesDatabaseEntry> series = state.SnapshotSeries();

            Directory.CreateDirectory(state.LibraryPath);
            WriteFile(Path.Combine(state.LibraryPath, MoviesFileName), movies);
            WriteFile(Path.Combine(state.LibraryPath, SeriesFileName), series);

            state.ClearDirty();

            _logger.LogDebug(
                "Saved library database for {LibraryPath}: {MovieRows} movie rows, {SeriesRows} series rows",
                state.LibraryPath,
                movies.Entries.Count,
                series.Entries.Count);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <summary>
    /// Drops the cached state for a library root, forcing the next access to read from disk.
    /// </summary>
    /// <param name="libraryPath">The library root, or null to drop every cached root.</param>
    public void Invalidate(string? libraryPath)
    {
        lock (_statesLock)
        {
            if (string.IsNullOrEmpty(libraryPath))
            {
                _states.Clear();
                return;
            }

            _states.Remove(Path.GetFullPath(libraryPath));
        }
    }

    private LibraryDatabaseFile<TEntry> ReadFile<TEntry>(string path)
    {
        if (!File.Exists(path))
        {
            return new LibraryDatabaseFile<TEntry>();
        }

        try
        {
            using StreamReader reader = new StreamReader(path);
            using JsonTextReader jsonReader = new JsonTextReader(reader);
            JsonSerializer serializer = new JsonSerializer();
            LibraryDatabaseFile<TEntry>? parsed = serializer.Deserialize<LibraryDatabaseFile<TEntry>>(jsonReader);

            if (parsed is null)
            {
                _logger.LogWarning("Library database {Path} was empty, starting from scratch", path);
                return new LibraryDatabaseFile<TEntry>();
            }

            if (parsed.SchemaVersion > LibraryDatabaseSchema.CurrentVersion)
            {
                _logger.LogWarning(
                    "Library database {Path} has schema version {Found}, this build understands {Known}. Refusing to use it",
                    path,
                    parsed.SchemaVersion,
                    LibraryDatabaseSchema.CurrentVersion);
                return new LibraryDatabaseFile<TEntry>();
            }

            parsed.Entries ??= new List<TEntry>();
            return parsed;
        }
        catch (JsonException ex)
        {
            // A corrupt database is recoverable: the backfill will rebuild it from disk, and the
            // missing completion marker keeps the reconciliation pass non-destructive until then.
            _logger.LogError(ex, "Library database {Path} is corrupt, it will be rebuilt from disk", path);
            return new LibraryDatabaseFile<TEntry>();
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Could not read library database {Path}, it will be rebuilt from disk", path);
            return new LibraryDatabaseFile<TEntry>();
        }
    }

    private static void WriteFile<TEntry>(string path, LibraryDatabaseFile<TEntry> content)
    {
        string tempPath = path + ".tmp";

        // Streamed rather than serialised to a string: a large library produces hundreds of
        // thousands of rows and the intermediate string would be a multi-hundred-megabyte
        // allocation on the large object heap.
        using (StreamWriter writer = new StreamWriter(tempPath, false))
        using (JsonTextWriter jsonWriter = new JsonTextWriter(writer) { Formatting = Formatting.None })
        {
            JsonSerializer serializer = new JsonSerializer { NullValueHandling = NullValueHandling.Include };
            serializer.Serialize(jsonWriter, content);
        }

        File.Move(tempPath, path, true);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases the unmanaged and optionally the managed resources.
    /// </summary>
    /// <param name="disposing">Whether managed resources should be released.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            _ioLock.Dispose();
        }

        _disposed = true;
    }
}

/// <summary>
/// In-memory state of the two database files belonging to one library root.
/// </summary>
/// <remarks>
/// All mutations are guarded by a single lock. The lock is only ever held for pure in-memory work,
/// never across an await, so contention stays negligible even with the sync running its item loop
/// in parallel.
/// </remarks>
public sealed class LibraryDatabaseState
{
    private readonly object _lock = new object();
    private readonly LibraryDatabaseFile<MovieDatabaseEntry> _movies;
    private readonly LibraryDatabaseFile<SeriesDatabaseEntry> _series;

    // Directory names in use, per kind, compared case-insensitively: two names differing only in
    // case cannot coexist on Windows or macOS, so they are treated as colliding everywhere.
    private readonly HashSet<string> _movieDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _seriesDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _movieFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _seriesFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // Group key to assigned directory. A group is the set of rows that deliberately share one
    // directory: same provider TMDB identifier, or same sanitized name when the provider has none.
    private readonly Dictionary<string, string> _movieGroups = new Dictionary<string, string>(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _seriesGroups = new Dictionary<string, string>(StringComparer.Ordinal);

    private bool _dirty;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryDatabaseState"/> class.
    /// </summary>
    /// <param name="libraryPath">The library root.</param>
    /// <param name="movies">The movie database.</param>
    /// <param name="series">The series database.</param>
    public LibraryDatabaseState(
        string libraryPath,
        LibraryDatabaseFile<MovieDatabaseEntry> movies,
        LibraryDatabaseFile<SeriesDatabaseEntry> series)
    {
        ArgumentNullException.ThrowIfNull(movies);
        ArgumentNullException.ThrowIfNull(series);

        LibraryPath = libraryPath;
        _movies = movies;
        _series = series;

        foreach (MovieDatabaseEntry entry in movies.Entries)
        {
            _movieDirectories.Add(entry.DirectoryName);
            _movieFiles.Add(CombineKey(entry.DirectoryName, entry.FileName));
        }

        foreach (SeriesDatabaseEntry entry in series.Entries)
        {
            _seriesDirectories.Add(SeriesDirectoryOf(entry.DirectoryName));
            _seriesFiles.Add(CombineKey(entry.DirectoryName, entry.FileName));
        }
    }

    /// <summary>
    /// Gets the library root these databases belong to.
    /// </summary>
    public string LibraryPath { get; }

    /// <summary>
    /// Gets a value indicating whether there are unsaved changes.
    /// </summary>
    public bool IsDirty
    {
        get
        {
            lock (_lock)
            {
                return _dirty;
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether the initial filesystem backfill has completed for the
    /// movie database. While false no row may be deleted and no unknown file may be removed.
    /// </summary>
    public bool IsMovieBackfillComplete
    {
        get
        {
            lock (_lock)
            {
                return _movies.BackfillCompletedAt.HasValue;
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether the initial filesystem backfill has completed for the
    /// series database.
    /// </summary>
    public bool IsSeriesBackfillComplete
    {
        get
        {
            lock (_lock)
            {
                return _series.BackfillCompletedAt.HasValue;
            }
        }
    }

    /// <summary>
    /// Marks the movie backfill as complete.
    /// </summary>
    public void MarkMovieBackfillComplete()
    {
        lock (_lock)
        {
            _movies.BackfillCompletedAt = DateTime.UtcNow;
            _dirty = true;
        }
    }

    /// <summary>
    /// Marks the series backfill as complete.
    /// </summary>
    public void MarkSeriesBackfillComplete()
    {
        lock (_lock)
        {
            _series.BackfillCompletedAt = DateTime.UtcNow;
            _dirty = true;
        }
    }

    /// <summary>
    /// Returns every movie row, whichever provider it belongs to.
    /// </summary>
    /// <returns>The rows.</returns>
    /// <remarks>
    /// Two providers may share a library root, so a file that one provider does not recognise may
    /// still be another's. Deciding that requires seeing all the rows, not just one provider's.
    /// </remarks>
    public IReadOnlyList<MovieDatabaseEntry> GetAllMovieEntries()
    {
        lock (_lock)
        {
            return _movies.Entries.ToList();
        }
    }

    /// <summary>
    /// Returns every series row, whichever provider it belongs to.
    /// </summary>
    /// <returns>The rows.</returns>
    public IReadOnlyList<SeriesDatabaseEntry> GetAllSeriesEntries()
    {
        lock (_lock)
        {
            return _series.Entries.ToList();
        }
    }

    /// <summary>
    /// Returns every movie row belonging to a provider.
    /// </summary>
    /// <param name="providerId">The provider identifier.</param>
    /// <returns>The matching rows.</returns>
    public IReadOnlyList<MovieDatabaseEntry> GetMovieEntries(string providerId)
    {
        lock (_lock)
        {
            return _movies.Entries
                .Where(e => string.Equals(e.ProviderId, providerId, StringComparison.Ordinal))
                .ToList();
        }
    }

    /// <summary>
    /// Returns every movie row for one stream of one provider.
    /// </summary>
    /// <param name="providerId">The provider identifier.</param>
    /// <param name="streamId">The stream identifier.</param>
    /// <returns>The matching rows.</returns>
    public IReadOnlyList<MovieDatabaseEntry> GetMovieEntries(string providerId, int streamId)
    {
        lock (_lock)
        {
            return _movies.Entries
                .Where(e => e.StreamId == streamId
                    && string.Equals(e.ProviderId, providerId, StringComparison.Ordinal))
                .ToList();
        }
    }

    /// <summary>
    /// Returns every series row belonging to a provider.
    /// </summary>
    /// <param name="providerId">The provider identifier.</param>
    /// <returns>The matching rows.</returns>
    public IReadOnlyList<SeriesDatabaseEntry> GetSeriesEntries(string providerId)
    {
        lock (_lock)
        {
            return _series.Entries
                .Where(e => string.Equals(e.ProviderId, providerId, StringComparison.Ordinal))
                .ToList();
        }
    }

    /// <summary>
    /// Returns every episode row of one series of one provider.
    /// </summary>
    /// <param name="providerId">The provider identifier.</param>
    /// <param name="seriesId">The series identifier.</param>
    /// <returns>The matching rows.</returns>
    public IReadOnlyList<SeriesDatabaseEntry> GetSeriesEntries(string providerId, int seriesId)
    {
        lock (_lock)
        {
            return _series.Entries
                .Where(e => e.SeriesId == seriesId
                    && string.Equals(e.ProviderId, providerId, StringComparison.Ordinal))
                .ToList();
        }
    }

    /// <summary>
    /// Resolves, or assigns, the directory shared by a movie group.
    /// </summary>
    /// <param name="groupKey">
    /// The group identity, as produced by <see cref="BuildMovieGroupKey"/>.
    /// </param>
    /// <param name="candidateDirectory">
    /// The directory the caller would like to use, relative to the library root.
    /// </param>
    /// <returns>
    /// The directory assigned to the group, which is the candidate when it was free and a
    /// numbered variant of it otherwise.
    /// </returns>
    public string ResolveMovieDirectory(string groupKey, string candidateDirectory)
    {
        lock (_lock)
        {
            if (_movieGroups.TryGetValue(groupKey, out string? assigned))
            {
                return assigned;
            }

            string resolved = FindFreeDirectory(_movieDirectories, candidateDirectory);
            _movieGroups[groupKey] = resolved;
            _movieDirectories.Add(resolved);
            return resolved;
        }
    }

    /// <summary>
    /// Resolves, or assigns, the directory of a series.
    /// </summary>
    /// <param name="groupKey">The group identity, as produced by <see cref="BuildSeriesGroupKey"/>.</param>
    /// <param name="candidateDirectory">The directory the caller would like to use.</param>
    /// <returns>The directory assigned to the series.</returns>
    public string ResolveSeriesDirectory(string groupKey, string candidateDirectory)
    {
        lock (_lock)
        {
            if (_seriesGroups.TryGetValue(groupKey, out string? assigned))
            {
                return assigned;
            }

            string resolved = FindFreeDirectory(_seriesDirectories, candidateDirectory);
            _seriesGroups[groupKey] = resolved;
            _seriesDirectories.Add(resolved);
            return resolved;
        }
    }

    /// <summary>
    /// Claims a file name inside a movie directory, numbering it when the name is already taken.
    /// </summary>
    /// <param name="directory">The directory, relative to the library root.</param>
    /// <param name="candidateFileName">The file name without extension.</param>
    /// <returns>The claimed file name without extension.</returns>
    public string ClaimMovieFileName(string directory, string candidateFileName)
    {
        lock (_lock)
        {
            return ClaimFileName(_movieFiles, directory, candidateFileName);
        }
    }

    /// <summary>
    /// Claims a file name inside a series season directory.
    /// </summary>
    /// <param name="directory">The directory, relative to the library root.</param>
    /// <param name="candidateFileName">The file name without extension.</param>
    /// <returns>The claimed file name without extension.</returns>
    public string ClaimSeriesFileName(string directory, string candidateFileName)
    {
        lock (_lock)
        {
            return ClaimFileName(_seriesFiles, directory, candidateFileName);
        }
    }

    /// <summary>
    /// Adds a movie row.
    /// </summary>
    /// <param name="entry">The row to add.</param>
    public void AddMovie(MovieDatabaseEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock (_lock)
        {
            _movies.Entries.Add(entry);
            _movieDirectories.Add(entry.DirectoryName);
            _movieFiles.Add(CombineKey(entry.DirectoryName, entry.FileName));
            _dirty = true;
        }
    }

    /// <summary>
    /// Adds a series row.
    /// </summary>
    /// <param name="entry">The row to add.</param>
    public void AddSeries(SeriesDatabaseEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock (_lock)
        {
            _series.Entries.Add(entry);
            _seriesDirectories.Add(SeriesDirectoryOf(entry.DirectoryName));
            _seriesFiles.Add(CombineKey(entry.DirectoryName, entry.FileName));
            _dirty = true;
        }
    }

    /// <summary>
    /// Attributes the rows of a directory that was written before the database existed to a
    /// series identifier, matching the directory by name.
    /// </summary>
    /// <param name="providerId">The provider identifier.</param>
    /// <param name="seriesId">The series identifier to write into the rows.</param>
    /// <param name="targetFolder">The normalised target folder the series is mapped into.</param>
    /// <param name="candidateDirectoryName">The directory name the sync would build for this series.</param>
    /// <param name="adopted">Set to the number of rows attributed.</param>
    /// <returns>The directory that was adopted, or null when nothing unambiguous matched.</returns>
    /// <remarks>
    /// <para>
    /// This is the one place a row is matched to a series by name rather than by identifier, and
    /// it runs once per row: a backfill cannot fill the column itself, because the URL inside an
    /// episode STRM carries the episode identifier and never the series one. Without it the first
    /// sync after an upgrade would find no rows for any series, hand every one of them a numbered
    /// directory and duplicate the entire library.
    /// </para>
    /// <para>
    /// The comparison ignores the metadata tag and the numbering suffix, so a directory that
    /// already carries <c>[tvdbid-N]</c> from an earlier version is still recognised. When more
    /// than one unattributed directory reduces to the same name nothing is adopted: picking one
    /// would attach a series to a directory that may hold a different show, and interleaving two
    /// shows in shared season folders is the one outcome that cannot be undone. The unadopted
    /// rows stay in the database, so their files keep their names and are not treated as orphans.
    /// </para>
    /// </remarks>
    public string? AdoptSeriesRowsByName(
        string providerId,
        int seriesId,
        string targetFolder,
        string candidateDirectoryName,
        out int adopted)
    {
        adopted = 0;
        string wanted = BaseLeaf(candidateDirectoryName);

        lock (_lock)
        {
            var attributed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (SeriesDatabaseEntry entry in _series.Entries)
            {
                if (!string.Equals(entry.ProviderId, providerId, StringComparison.Ordinal))
                {
                    continue;
                }

                string directory = SeriesDirectoryOf(entry.DirectoryName);
                if (entry.SeriesId.HasValue)
                {
                    attributed.Add(directory);
                    continue;
                }

                int slash = directory.LastIndexOf('/');
                string parent = slash < 0 ? string.Empty : directory[..slash];
                string leaf = slash < 0 ? directory : directory[(slash + 1)..];

                if (string.Equals(parent, targetFolder, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(BaseLeaf(leaf), wanted, StringComparison.OrdinalIgnoreCase))
                {
                    candidates.Add(directory);
                }
            }

            candidates.ExceptWith(attributed);
            if (candidates.Count != 1)
            {
                return null;
            }

            string resolved = candidates.First();
            string prefix = resolved + "/";

            foreach (SeriesDatabaseEntry entry in _series.Entries)
            {
                if (entry.SeriesId.HasValue
                    || !string.Equals(entry.ProviderId, providerId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (string.Equals(entry.DirectoryName, resolved, StringComparison.OrdinalIgnoreCase)
                    || entry.DirectoryName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    entry.SeriesId = seriesId;
                    adopted++;
                }
            }

            if (adopted > 0)
            {
                _dirty = true;
            }

            _seriesGroups[BuildSeriesGroupKey(providerId, targetFolder, seriesId)] = resolved;
            _seriesDirectories.Add(resolved);
            return resolved;
        }
    }

    /// <summary>
    /// Removes every movie row matching a predicate.
    /// </summary>
    /// <param name="predicate">The predicate.</param>    /// <returns>The number of rows removed.</returns>
    public int RemoveMovies(Func<MovieDatabaseEntry, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        lock (_lock)
        {
            int removed = _movies.Entries.RemoveAll(e => predicate(e));
            if (removed > 0)
            {
                RebuildMovieIndexes();
                _dirty = true;
            }

            return removed;
        }
    }

    /// <summary>
    /// Removes every series row matching a predicate.
    /// </summary>
    /// <param name="predicate">The predicate.</param>
    /// <returns>The number of rows removed.</returns>
    public int RemoveSeries(Func<SeriesDatabaseEntry, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        lock (_lock)
        {
            int removed = _series.Entries.RemoveAll(e => predicate(e));
            if (removed > 0)
            {
                RebuildSeriesIndexes();
                _dirty = true;
            }

            return removed;
        }
    }

    /// <summary>
    /// Marks the state as changed, for callers that mutate rows in place.
    /// </summary>
    public void MarkDirty()
    {
        lock (_lock)
        {
            _dirty = true;
        }
    }

    /// <summary>
    /// Clears the unsaved-changes flag.
    /// </summary>
    public void ClearDirty()
    {
        lock (_lock)
        {
            _dirty = false;
        }
    }

    /// <summary>
    /// Takes a consistent copy of the movie database for writing.
    /// </summary>
    /// <returns>The copy.</returns>
    public LibraryDatabaseFile<MovieDatabaseEntry> SnapshotMovies()
    {
        lock (_lock)
        {
            return new LibraryDatabaseFile<MovieDatabaseEntry>
            {
                SchemaVersion = LibraryDatabaseSchema.CurrentVersion,
                BackfillCompletedAt = _movies.BackfillCompletedAt,
                Entries = _movies.Entries.ToList()
            };
        }
    }

    /// <summary>
    /// Takes a consistent copy of the series database for writing.
    /// </summary>
    /// <returns>The copy.</returns>
    public LibraryDatabaseFile<SeriesDatabaseEntry> SnapshotSeries()
    {
        lock (_lock)
        {
            return new LibraryDatabaseFile<SeriesDatabaseEntry>
            {
                SchemaVersion = LibraryDatabaseSchema.CurrentVersion,
                BackfillCompletedAt = _series.BackfillCompletedAt,
                Entries = _series.Entries.ToList()
            };
        }
    }

    /// <summary>
    /// Builds the group identity of a movie.
    /// </summary>
    /// <param name="providerId">The provider identifier.</param>
    /// <param name="targetFolder">
    /// The mapped target folder, relative to the library root. Two folder mappings of the same
    /// title are separate groups, otherwise the second mapping would never be created.
    /// </param>
    /// <param name="tmdbId">The TMDB identifier supplied by the provider, or null.</param>
    /// <param name="sanitizedName">The sanitized title, used to group when there is no identifier.</param>
    /// <returns>The group key.</returns>
    public static string BuildMovieGroupKey(string providerId, string targetFolder, int? tmdbId, string sanitizedName)
    {
        string discriminator = tmdbId.HasValue
            ? "t:" + tmdbId.Value.ToString(CultureInfo.InvariantCulture)
            : "n:" + sanitizedName.ToUpperInvariant();

        return string.Concat(providerId, "\u001f", targetFolder.ToUpperInvariant(), "\u001f", discriminator);
    }

    /// <summary>
    /// Builds the group identity of a series. Series never share a directory, because episodes of
    /// different shows landing in one season folder cannot be told apart afterwards.
    /// </summary>
    /// <param name="providerId">The provider identifier.</param>
    /// <param name="targetFolder">The mapped target folder, relative to the library root.</param>
    /// <param name="seriesId">The series identifier.</param>
    /// <returns>The group key.</returns>
    public static string BuildSeriesGroupKey(string providerId, string targetFolder, int seriesId)
    {
        return string.Concat(
            providerId,
            "\u001f",
            targetFolder.ToUpperInvariant(),
            "\u001f",
            seriesId.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Normalises a path to the form stored in the database: relative to the library root and
    /// using forward slashes.
    /// </summary>
    /// <param name="libraryPath">The library root.</param>
    /// <param name="fullPath">The absolute path.</param>
    /// <returns>The normalised relative path.</returns>
    public static string ToRelativePath(string libraryPath, string fullPath)
    {
        string relative = Path.GetRelativePath(libraryPath, fullPath);
        return relative.Replace('\\', '/');
    }

    /// <summary>
    /// Rebuilds an absolute path from a stored relative path.
    /// </summary>
    /// <param name="libraryPath">The library root.</param>
    /// <param name="relativePath">The stored relative path.</param>
    /// <returns>The absolute path.</returns>
    public static string ToFullPath(string libraryPath, string relativePath)
    {
        return Path.GetFullPath(Path.Combine(libraryPath, relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static string FindFreeDirectory(HashSet<string> taken, string candidate)
    {
        if (!taken.Contains(candidate))
        {
            return candidate;
        }

        // The suffix goes before the metadata tag, because Jellyfin reads the tag from the end of
        // the name and would otherwise stop recognising it.
        (string head, string tag) = SplitMetadataTag(candidate);

        for (int counter = 2; ; counter++)
        {
            string numbered = string.Concat(head, " #", counter.ToString(CultureInfo.InvariantCulture), tag);
            if (!taken.Contains(numbered))
            {
                return numbered;
            }
        }
    }

    private static string ClaimFileName(HashSet<string> taken, string directory, string candidate)
    {
        string key = CombineKey(directory, candidate);
        if (taken.Add(key))
        {
            return candidate;
        }

        // Files use " - #2" rather than " #2" so that the result still matches the alternate
        // version naming Jellyfin expects inside a movie directory.
        for (int counter = 2; ; counter++)
        {
            string numbered = string.Concat(candidate, " - #", counter.ToString(CultureInfo.InvariantCulture));
            if (taken.Add(CombineKey(directory, numbered)))
            {
                return numbered;
            }
        }
    }

    private static (string Head, string Tag) SplitMetadataTag(string name)
    {
        if (name.Length > 0 && name[^1] == ']')
        {
            int open = name.LastIndexOf(" [", StringComparison.Ordinal);
            if (open > 0)
            {
                return (name[..open], name[open..]);
            }
        }

        return (name, string.Empty);
    }

    private static string CombineKey(string directory, string fileName)
    {
        return string.Concat(directory, "/", fileName);
    }

    /// <summary>
    /// Reduces the directory stored in a series row to the directory of the series itself.
    /// </summary>
    /// <param name="storedDirectory">The directory stored in the row, normally a season directory.</param>
    /// <returns>The series directory.</returns>
    /// <remarks>
    /// <para>
    /// The set of taken series directories has to hold series directories, because that is what
    /// the numbering suffix is applied to. Filling it with the season directories the rows carry
    /// would leave every series directory looking free: a second series sanitizing to the name of
    /// a series that exists but was not enumerated in this run — which is the normal case, since
    /// the snapshot skips unchanged series — would be handed that same directory, and the episodes
    /// of the two shows would end up interleaved in shared <c>Season N</c> folders.
    /// </para>
    /// <para>
    /// A row whose directory has no parent is left as-is rather than reduced to the library root.
    /// </para>
    /// </remarks>
    private static string SeriesDirectoryOf(string storedDirectory)
    {
        int slash = storedDirectory.LastIndexOf('/');
        return slash > 0 ? storedDirectory[..slash] : storedDirectory;
    }

    /// <summary>
    /// Reduces a directory leaf to the part that identifies the title, dropping the metadata tag
    /// and the numbering suffix.
    /// </summary>
    /// <param name="leaf">The directory leaf.</param>
    /// <returns>The reduced leaf.</returns>
    private static string BaseLeaf(string leaf)
    {
        string head = SplitMetadataTag(leaf).Head;

        int hash = head.LastIndexOf(" #", StringComparison.Ordinal);
        if (hash > 0 && head.Length > hash + 2)
        {
            bool allDigits = true;
            for (int i = hash + 2; i < head.Length; i++)
            {
                if (!char.IsAsciiDigit(head[i]))
                {
                    allDigits = false;
                    break;
                }
            }

            if (allDigits)
            {
                head = head[..hash];
            }
        }

        return head;
    }

    private void RebuildMovieIndexes()
    {
        _movieDirectories.Clear();
        _movieFiles.Clear();
        foreach (MovieDatabaseEntry entry in _movies.Entries)
        {
            _movieDirectories.Add(entry.DirectoryName);
            _movieFiles.Add(CombineKey(entry.DirectoryName, entry.FileName));
        }

        // A directory that no longer has rows must also lose its group reservation, otherwise the
        // name stays claimed for the rest of the process lifetime.
        foreach (string groupKey in _movieGroups
            .Where(pair => !_movieDirectories.Contains(pair.Value))
            .Select(pair => pair.Key)
            .ToList())
        {
            _movieGroups.Remove(groupKey);
        }
    }

    private void RebuildSeriesIndexes()
    {
        _seriesDirectories.Clear();
        _seriesFiles.Clear();
        foreach (SeriesDatabaseEntry entry in _series.Entries)
        {
            _seriesDirectories.Add(SeriesDirectoryOf(entry.DirectoryName));
            _seriesFiles.Add(CombineKey(entry.DirectoryName, entry.FileName));
        }

        foreach (string groupKey in _seriesGroups
            .Where(pair => !_seriesDirectories.Contains(pair.Value))
            .Select(pair => pair.Key)
            .ToList())
        {
            _seriesGroups.Remove(groupKey);
        }
    }
}
