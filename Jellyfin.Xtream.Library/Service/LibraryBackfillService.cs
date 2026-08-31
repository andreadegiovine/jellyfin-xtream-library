using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Library.Service.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Outcome of a backfill pass.
/// </summary>
/// <param name="FilesSeen">How many <c>.strm</c> files were found.</param>
/// <param name="RowsCreated">How many rows were written.</param>
/// <param name="Unattributed">
/// How many files could not be attributed to a provider, either because the URL was unreadable or
/// because no configured provider matches it. Rows are still created for these so that their names
/// stay reserved.
/// </param>
/// <param name="Failed">How many files could not be read at all.</param>
public sealed record BackfillResult(int FilesSeen, int RowsCreated, int Unattributed, int Failed)
{
    /// <summary>
    /// Gets a value indicating whether the pass covered the whole tree without losing a file. Only
    /// a complete pass may mark the database as backfilled, because a partial one would make the
    /// files it missed look like orphans on the next reconciliation.
    /// </summary>
    public bool IsComplete => Failed == 0;
}

/// <summary>
/// Rebuilds the library database from the files already on disk, for the first run after the
/// database was introduced and for recovery after the file is lost or corrupted.
/// </summary>
public partial class LibraryBackfillService
{
    private readonly ILogger<LibraryBackfillService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryBackfillService"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public LibraryBackfillService(ILogger<LibraryBackfillService> logger)
    {
        _logger = logger;
    }

    [GeneratedRegex(@"\[tmdbid-(\d{1,9})\]", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex TmdbTagRegex();

    [GeneratedRegex(@"^Season\s+(\d{1,4})$", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex SeasonFolderRegex();

    /// <summary>
    /// Rebuilds the movie rows of a library from disk.
    /// </summary>
    /// <param name="state">The database state to populate.</param>
    /// <param name="moviesRoot">The absolute root of the movie tree.</param>
    /// <param name="providersByBaseUrl">
    /// Provider identifiers keyed by the normalised base URL, as produced by
    /// <see cref="StrmUrlParser.NormalizeBaseUrl"/>.
    /// </param>
    /// <param name="parallelism">How many files to read at a time.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The outcome of the pass.</returns>
    public async Task<BackfillResult> BackfillMoviesAsync(
        LibraryDatabaseState state,
        string moviesRoot,
        IReadOnlyDictionary<string, string> providersByBaseUrl,
        int parallelism,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(providersByBaseUrl);

        ConcurrentBag<MovieDatabaseEntry> rows = new ConcurrentBag<MovieDatabaseEntry>();
        int unattributed = 0;
        int failed = 0;

        int seen = await ForEachStrmFileAsync(
            moviesRoot,
            parallelism,
            (path, content) =>
            {
                if (content is null)
                {
                    Interlocked.Increment(ref failed);
                    return;
                }

                string directory = LibraryDatabaseState.ToRelativePath(
                    state.LibraryPath,
                    Path.GetDirectoryName(path) ?? state.LibraryPath);

                ParsedStrmUrl? parsed = StrmUrlParser.Parse(content);
                string providerId = ResolveProvider(parsed, providersByBaseUrl);
                if (providerId.Length == 0)
                {
                    Interlocked.Increment(ref unattributed);
                }

                rows.Add(new MovieDatabaseEntry
                {
                    ProviderId = providerId,
                    StreamId = parsed?.Kind == StrmUrlKind.Movie ? parsed.NumericItemId : null,
                    TmdbId = ExtractTmdbId(directory),
                    DirectoryName = directory,
                    FileName = Path.GetFileNameWithoutExtension(path),
                    InfoError = false
                });
            },
            cancellationToken).ConfigureAwait(false);

        foreach (MovieDatabaseEntry row in rows)
        {
            state.AddMovie(row);
        }

        BackfillResult result = new BackfillResult(seen, rows.Count, unattributed, failed);
        LogOutcome("movie", moviesRoot, result);

        if (result.IsComplete)
        {
            state.MarkMovieBackfillComplete();
        }

        return result;
    }

    /// <summary>
    /// Rebuilds the episode rows of a library from disk.
    /// </summary>
    /// <param name="state">The database state to populate.</param>
    /// <param name="seriesRoot">The absolute root of the series tree.</param>
    /// <param name="providersByBaseUrl">Provider identifiers keyed by the normalised base URL.</param>
    /// <param name="parallelism">How many files to read at a time.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The outcome of the pass.</returns>
    public async Task<BackfillResult> BackfillSeriesAsync(
        LibraryDatabaseState state,
        string seriesRoot,
        IReadOnlyDictionary<string, string> providersByBaseUrl,
        int parallelism,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(providersByBaseUrl);

        ConcurrentBag<SeriesDatabaseEntry> rows = new ConcurrentBag<SeriesDatabaseEntry>();
        int unattributed = 0;
        int failed = 0;

        int seen = await ForEachStrmFileAsync(
            seriesRoot,
            parallelism,
            (path, content) =>
            {
                if (content is null)
                {
                    Interlocked.Increment(ref failed);
                    return;
                }

                string absoluteDirectory = Path.GetDirectoryName(path) ?? state.LibraryPath;
                string directory = LibraryDatabaseState.ToRelativePath(state.LibraryPath, absoluteDirectory);

                ParsedStrmUrl? parsed = StrmUrlParser.Parse(content);
                string providerId = ResolveProvider(parsed, providersByBaseUrl);
                if (providerId.Length == 0)
                {
                    Interlocked.Increment(ref unattributed);
                }

                // The episode URL carries the episode identifier, never the series identifier, so
                // series_id stays null here and is filled in by the first sync that matches the
                // series directory against the provider listing.
                rows.Add(new SeriesDatabaseEntry
                {
                    ProviderId = providerId,
                    SeriesId = null,
                    EpisodeId = parsed?.Kind == StrmUrlKind.Episode ? parsed.ItemId : null,
                    TmdbId = ExtractTmdbId(directory),
                    DirectoryName = directory,
                    FileName = Path.GetFileNameWithoutExtension(path),
                    Season = ExtractSeason(absoluteDirectory),
                    InfoError = false
                });
            },
            cancellationToken).ConfigureAwait(false);

        foreach (SeriesDatabaseEntry row in rows)
        {
            state.AddSeries(row);
        }

        BackfillResult result = new BackfillResult(seen, rows.Count, unattributed, failed);
        LogOutcome("series", seriesRoot, result);

        if (result.IsComplete)
        {
            state.MarkSeriesBackfillComplete();
        }

        return result;
    }

    /// <summary>
    /// Reads the TMDB identifier out of a directory name. At backfill time the provenance of the
    /// identifier cannot be recovered, and does not matter: the directory already exists and its
    /// name is immutable from here on.
    /// </summary>
    /// <param name="directory">The directory name or relative path.</param>
    /// <returns>The identifier, or null.</returns>
    internal static int? ExtractTmdbId(string directory)
    {
        Match match = TmdbTagRegex().Match(directory);
        return match.Success ? TmdbIdParser.Parse(match.Groups[1].Value) : null;
    }

    /// <summary>
    /// Derives the season number from the directory holding an episode.
    /// </summary>
    /// <param name="absoluteDirectory">The absolute directory of the episode file.</param>
    /// <returns>The season number, zero when the directory is not a season directory.</returns>
    internal static int ExtractSeason(string absoluteDirectory)
    {
        string name = Path.GetFileName(absoluteDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));

        Match match = SeasonFolderRegex().Match(name);
        return match.Success
            && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int season)
                ? season
                : 0;
    }

    private static string ResolveProvider(
        ParsedStrmUrl? parsed,
        IReadOnlyDictionary<string, string> providersByBaseUrl)
    {
        if (parsed is null)
        {
            return string.Empty;
        }

        return providersByBaseUrl.TryGetValue(
            StrmUrlParser.NormalizeBaseUrl(parsed.BaseUrl),
            out string? providerId)
                ? providerId
                : string.Empty;
    }

    private async Task<int> ForEachStrmFileAsync(
        string root,
        int parallelism,
        Action<string, string?> handler,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root))
        {
            return 0;
        }

        int seen = 0;
        ParallelOptions options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, parallelism),
            CancellationToken = cancellationToken
        };

        IEnumerable<string> files = Directory.EnumerateFiles(root, "*.strm", SearchOption.AllDirectories);

        await Parallel.ForEachAsync(files, options, async (path, token) =>
        {
            Interlocked.Increment(ref seen);
            string? content = null;
            try
            {
                content = await File.ReadAllTextAsync(path, token).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Could not read {Path} during backfill", path);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Could not read {Path} during backfill", path);
            }

            handler(path, content);
        }).ConfigureAwait(false);

        return seen;
    }

    private void LogOutcome(string kind, string root, BackfillResult result)
    {
        _logger.LogInformation(
            "Backfilled {Kind} database from {Root}: {Rows} rows from {Files} files, {Unattributed} without a provider",
            kind,
            root,
            result.RowsCreated,
            result.FilesSeen,
            result.Unattributed);

        if (result.Failed > 0)
        {
            // Leaving the marker unset keeps every later pass non-destructive, which is the whole
            // point: a partial backfill must never be allowed to authorise deletions.
            _logger.LogError(
                "Backfill of the {Kind} database did not complete: {Failed} files could not be read. "
                    + "The database stays marked as incomplete and nothing will be deleted until a clean pass runs",
                kind,
                result.Failed);
        }
    }
}
