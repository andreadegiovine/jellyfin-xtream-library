using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Xtream.Library.Service.Models;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Decides where the episodes of a series go, using the library database as the only source of
/// truth. This is the series counterpart of <see cref="MovieLibraryPathResolver"/>.
/// </summary>
/// <remarks>
/// <para>
/// Series are never grouped. Two shows that sanitize to the same name get two directories, the
/// second one numbered, because merging them would drop the episodes of both into the same
/// <c>Season N</c> folders with no way to tell afterwards which episode belonged to which show.
/// The grouping key is therefore the series identifier alone, never the name.
/// </para>
/// <para>
/// A row stores the season directory, not the series directory, because that is where the file
/// actually sits. The series directory is recovered as the parent of the stored directory, which
/// is why the two are resolved in that order: series directory first, season directory composed
/// underneath it, file name claimed inside the season directory.
/// </para>
/// </remarks>
public static class SeriesLibraryPathResolver
{
    /// <summary>
    /// Resolves, or assigns, the directory of a series inside one target folder.
    /// </summary>
    /// <param name="database">The library database state.</param>
    /// <param name="providerId">The provider the series belongs to.</param>
    /// <param name="seriesId">The Xtream series identifier.</param>
    /// <param name="targetFolder">
    /// The folder the series is mapped into, relative to the library root, for example
    /// <c>Series</c> or <c>Series/Drama</c>.
    /// </param>
    /// <param name="candidateDirectoryName">
    /// The directory name the caller would like to use, without any path.
    /// </param>
    /// <param name="isExisting">Set to true when the database already knew this series here.</param>
    /// <returns>The directory, relative to the library root.</returns>
    public static string ResolveDirectory(
        LibraryDatabaseState database,
        string providerId,
        int seriesId,
        string targetFolder,
        string candidateDirectoryName,
        out bool isExisting)
    {
        ArgumentNullException.ThrowIfNull(database);

        string prefix = MovieLibraryPathResolver.NormalizeFolder(targetFolder);

        string? known = database
            .GetSeriesEntries(providerId, seriesId)
            .Select(e => SeriesDirectoryOf(e.DirectoryName, prefix))
            .FirstOrDefault(d => d is not null);

        isExisting = known is not null;

        return known ?? database.ResolveSeriesDirectory(
            LibraryDatabaseState.BuildSeriesGroupKey(providerId, prefix, seriesId),
            MovieLibraryPathResolver.Join(prefix, candidateDirectoryName));
    }

    /// <summary>
    /// Composes the season directory of a resolved series directory.
    /// </summary>
    /// <param name="seriesDirectory">The resolved series directory, relative to the library root.</param>
    /// <param name="seasonNumber">The season number. Zero denotes specials.</param>
    /// <returns>The season directory, relative to the library root.</returns>
    /// <remarks>
    /// The season number is written without padding, matching the folders the plugin has always
    /// produced. Changing this would rename every season folder of every library.
    /// </remarks>
    public static string SeasonDirectory(string seriesDirectory, int seasonNumber)
    {
        return MovieLibraryPathResolver.Join(
            seriesDirectory,
            "Season " + seasonNumber.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Resolves the file name of one episode inside an already resolved season directory.
    /// </summary>
    /// <param name="database">The library database state.</param>
    /// <param name="providerId">The provider the series belongs to.</param>
    /// <param name="seriesId">The Xtream series identifier.</param>
    /// <param name="episodeId">The Xtream episode identifier.</param>
    /// <param name="seasonDirectory">The season directory, relative to the library root.</param>
    /// <param name="candidateFileName">The file name the caller would like to use, without extension.</param>
    /// <returns>The file name to use, without extension.</returns>
    /// <remarks>
    /// Unlike movies, an episode has an identifier of its own in the row, so a known episode is
    /// recognised exactly rather than positionally. A row that already exists wins over the
    /// candidate: the stored name may carry a numbering suffix the caller cannot reconstruct, and
    /// rebuilding the name would leave the old file behind as an orphan.
    /// </remarks>
    public static string ResolveFileName(
        LibraryDatabaseState database,
        string providerId,
        int seriesId,
        string? episodeId,
        string seasonDirectory,
        string candidateFileName)
    {
        ArgumentNullException.ThrowIfNull(database);

        IReadOnlyList<SeriesDatabaseEntry> rows = database.GetSeriesEntries(providerId, seriesId);

        if (!string.IsNullOrEmpty(episodeId))
        {
            string? known = rows
                .Where(e => string.Equals(e.EpisodeId, episodeId, StringComparison.Ordinal)
                    && string.Equals(e.DirectoryName, seasonDirectory, StringComparison.OrdinalIgnoreCase))
                .Select(e => e.FileName)
                .FirstOrDefault();

            if (known is not null)
            {
                return known;
            }
        }

        return database.ClaimSeriesFileName(seasonDirectory, candidateFileName);
    }

    /// <summary>
    /// Attributes to a series the rows a backfill recovered from disk without an identifier.
    /// </summary>
    /// <param name="database">The library database state.</param>
    /// <param name="providerId">The provider the series belongs to.</param>
    /// <param name="seriesId">The Xtream series identifier.</param>
    /// <param name="targetFolder">The folder the series is mapped into.</param>
    /// <param name="candidateDirectoryName">The directory name the caller would build for this series.</param>
    /// <param name="adopted">Set to the number of rows attributed.</param>
    /// <returns>The directory that was adopted, or null when nothing unambiguous matched.</returns>
    /// <remarks>
    /// This must run before <see cref="ResolveDirectory"/>, not after. Rows with no identifier are
    /// invisible to the identifier lookup but their directory is already taken, so resolving first
    /// would hand the series a numbered directory beside the one that holds its own episodes, and
    /// the immutability rule would then keep it there forever.
    /// </remarks>
    public static string? AdoptBackfilledRows(
        LibraryDatabaseState database,
        string providerId,
        int seriesId,
        string targetFolder,
        string candidateDirectoryName,
        out int adopted)
    {
        ArgumentNullException.ThrowIfNull(database);

        return database.AdoptSeriesRowsByName(
            providerId,
            seriesId,
            MovieLibraryPathResolver.NormalizeFolder(targetFolder),
            candidateDirectoryName,
            out adopted);
    }

    /// <summary>
    /// Recovers the series directory a stored row belongs to.
    /// </summary>
    /// <param name="storedDirectory">The directory stored in the row.</param>
    /// <param name="folder">The normalised target folder.</param>
    /// <returns>The series directory, or null when the row belongs to another target folder.</returns>
    private static string? SeriesDirectoryOf(string storedDirectory, string folder)
    {
        int slash = storedDirectory.LastIndexOf('/');
        if (slash > 0)
        {
            string parent = storedDirectory[..slash];
            if (MovieLibraryPathResolver.IsInFolder(parent, folder))
            {
                return parent;
            }
        }

        // A row written directly into the series directory, which a backfill can produce for a
        // library whose episodes were never filed into season folders.
        return MovieLibraryPathResolver.IsInFolder(storedDirectory, folder) ? storedDirectory : null;
    }
}
