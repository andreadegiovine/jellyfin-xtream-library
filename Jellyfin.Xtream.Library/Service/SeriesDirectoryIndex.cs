using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Jellyfin.Xtream.Library.Service.Models;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Maps a series to the directory that holds it, for the skip paths that need to know where a
/// series lives without resolving it.
/// </summary>
/// <remarks>
/// <para>
/// This replaces the lookup the sync used to build by scanning the disk and keying directories on
/// their sanitized name. That key could not tell two shows with the same name apart, and it missed
/// a directory whose name had drifted from what the sanitizer produces today. Here the key is the
/// series identifier, which is what actually distinguishes two shows.
/// </para>
/// <para>
/// The index says where a series is, never how many files it has: the counts still come from the
/// filesystem, because the point of the skip checks is to notice that something was deleted behind
/// the plugin's back. Rows recovered by a backfill carry no identifier and are therefore absent
/// here, which makes an un-upgraded library look like it has nothing to skip. That is the safe
/// direction to fail in: the sync does the full work once and the rows are attributed as it goes.
/// </para>
/// </remarks>
public sealed class SeriesDirectoryIndex
{
    private readonly Dictionary<string, string> _directories;

    private SeriesDirectoryIndex(Dictionary<string, string> directories)
    {
        _directories = directories;
    }

    /// <summary>
    /// Gets the number of distinct series directories known to the index.
    /// </summary>
    public int Count => _directories.Count;

    /// <summary>
    /// Builds the index from the rows of one provider.
    /// </summary>
    /// <param name="database">The library database state.</param>
    /// <param name="providerId">The provider identifier.</param>
    /// <param name="libraryPath">The library root, used to produce absolute directories.</param>
    /// <returns>The index.</returns>
    public static SeriesDirectoryIndex Build(LibraryDatabaseState database, string providerId, string libraryPath)
    {
        ArgumentNullException.ThrowIfNull(database);

        var directories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (SeriesDatabaseEntry entry in database.GetSeriesEntries(providerId))
        {
            if (!entry.SeriesId.HasValue)
            {
                continue;
            }

            int slash = entry.DirectoryName.LastIndexOf('/');
            if (slash <= 0)
            {
                // A row directly under the library root has no series directory to speak of.
                continue;
            }

            string relativeSeriesDirectory = entry.DirectoryName[..slash];
            int parentSlash = relativeSeriesDirectory.LastIndexOf('/');
            if (parentSlash <= 0)
            {
                continue;
            }

            string parent = LibraryDatabaseState.ToFullPath(libraryPath, relativeSeriesDirectory[..parentSlash]);
            string directory = LibraryDatabaseState.ToFullPath(libraryPath, relativeSeriesDirectory);

            directories.TryAdd(BuildKey(entry.SeriesId.Value, parent), directory);
        }

        return new SeriesDirectoryIndex(directories);
    }

    /// <summary>
    /// Finds the directory of a series inside one parent directory.
    /// </summary>
    /// <param name="seriesId">The Xtream series identifier.</param>
    /// <param name="parentDirectory">The absolute directory the series is mapped into.</param>
    /// <param name="directory">The absolute directory of the series, when known.</param>
    /// <returns>True when the database knows where this series is.</returns>
    public bool TryGetDirectory(int seriesId, string parentDirectory, out string directory)
    {
        return _directories.TryGetValue(BuildKey(seriesId, parentDirectory), out directory!);
    }

    private static string BuildKey(int seriesId, string parentDirectory)
    {
        return string.Concat(
            seriesId.ToString(CultureInfo.InvariantCulture),
            "\u001f",
            parentDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }
}
