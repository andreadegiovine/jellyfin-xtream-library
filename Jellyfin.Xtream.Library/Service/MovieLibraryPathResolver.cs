using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Decides where a movie's files go, using the library database as the only source of truth.
/// </summary>
/// <remarks>
/// <para>
/// This replaces the directory pre-scan the sync used to perform. Resolution is by identifier
/// first and by name only when the stream has never been seen, which is what stops the numbering
/// suffix from escalating: a name-based lookup would fail to recognise a directory that already
/// carries a <c>#2</c> and would keep inventing new ones on every run.
/// </para>
/// <para>
/// Names already assigned are never revised. A directory keeps the name it was given even when the
/// provider later starts or stops advertising a TMDB identifier, because renaming a directory
/// makes Jellyfin treat the content as a brand new item and lose watch state and artwork.
/// </para>
/// </remarks>
public static class MovieLibraryPathResolver
{
    /// <summary>
    /// Resolves the directory and file names for one movie inside one target folder.
    /// </summary>
    /// <param name="database">The library database state.</param>
    /// <param name="providerId">The provider the movie belongs to.</param>
    /// <param name="streamId">The Xtream stream identifier.</param>
    /// <param name="targetFolder">
    /// The folder the movie is mapped into, relative to the library root, for example
    /// <c>Movies</c> or <c>Movies/Action</c>.
    /// </param>
    /// <param name="candidateDirectoryName">
    /// The directory name the caller would like to use, without any path.
    /// </param>
    /// <param name="providerTmdbId">
    /// The TMDB identifier advertised by the provider, or null. Identifiers found by name lookup
    /// must not be passed here: they belong in the directory name, not in the grouping decision.
    /// </param>
    /// <param name="groupingName">
    /// The sanitized title used to group titles the provider left without an identifier.
    /// </param>
    /// <param name="candidateFileNames">The file names the caller would like to use.</param>
    /// <returns>The resolved plan.</returns>
    public static MoviePathPlan Resolve(
        LibraryDatabaseState database,
        string providerId,
        int streamId,
        string targetFolder,
        string candidateDirectoryName,
        int? providerTmdbId,
        string groupingName,
        IReadOnlyList<string> candidateFileNames)
    {
        ArgumentNullException.ThrowIfNull(candidateFileNames);

        string directory = ResolveDirectory(
            database, providerId, streamId, targetFolder, candidateDirectoryName, providerTmdbId,
            groupingName, out bool isExisting);

        return new MoviePathPlan(
            directory,
            ResolveFileNames(database, providerId, streamId, directory, candidateFileNames),
            isExisting);
    }

    /// <summary>
    /// Resolves only the directory, for callers that must know the directory before they can build
    /// the file names, because the file name is derived from the directory name.
    /// </summary>
    /// <param name="database">The library database state.</param>
    /// <param name="providerId">The provider the movie belongs to.</param>
    /// <param name="streamId">The Xtream stream identifier.</param>
    /// <param name="targetFolder">The folder the movie is mapped into.</param>
    /// <param name="candidateDirectoryName">The directory name the caller would like to use.</param>
    /// <param name="providerTmdbId">The TMDB identifier advertised by the provider, or null.</param>
    /// <param name="groupingName">The sanitized title used for grouping.</param>
    /// <param name="isExisting">Set to true when the database already knew this stream here.</param>
    /// <returns>The directory, relative to the library root.</returns>
    public static string ResolveDirectory(
        LibraryDatabaseState database,
        string providerId,
        int streamId,
        string targetFolder,
        string candidateDirectoryName,
        int? providerTmdbId,
        string groupingName,
        out bool isExisting)
    {
        ArgumentNullException.ThrowIfNull(database);

        string prefix = NormalizeFolder(targetFolder);

        string? known = database
            .GetMovieEntries(providerId, streamId)
            .Select(e => e.DirectoryName)
            .FirstOrDefault(d => IsInFolder(d, prefix));

        isExisting = known is not null;

        return known ?? database.ResolveMovieDirectory(
            LibraryDatabaseState.BuildMovieGroupKey(providerId, prefix, providerTmdbId, groupingName),
            Join(prefix, candidateDirectoryName));
    }

    /// <summary>
    /// Resolves the file names to use inside an already resolved directory.
    /// </summary>
    /// <param name="database">The library database state.</param>
    /// <param name="providerId">The provider the movie belongs to.</param>
    /// <param name="streamId">The Xtream stream identifier.</param>
    /// <param name="directory">The resolved directory, relative to the library root.</param>
    /// <param name="candidateFileNames">The file names the caller would like to use.</param>
    /// <returns>The resolved file names, in the order of the candidates.</returns>
    public static IReadOnlyList<string> ResolveFileNames(
        LibraryDatabaseState database,
        string providerId,
        int streamId,
        string directory,
        IReadOnlyList<string> candidateFileNames)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(candidateFileNames);

        string[] resolved = new string[candidateFileNames.Count];
        List<string> unclaimed = database
            .GetMovieEntries(providerId, streamId)
            .Where(e => string.Equals(e.DirectoryName, directory, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.FileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        // First pass: a candidate that matches a row of this same stream keeps that row's name,
        // so a re-sync is a no-op rather than a source of new numbering.
        for (int i = 0; i < candidateFileNames.Count; i++)
        {
            int match = unclaimed.FindIndex(
                name => string.Equals(name, candidateFileNames[i], StringComparison.OrdinalIgnoreCase));
            if (match >= 0)
            {
                resolved[i] = unclaimed[match];
                unclaimed.RemoveAt(match);
            }
        }

        // Second pass: rows of this stream that did not match by name are matched positionally.
        // This is what recovers a file that was numbered on a previous run, whose stored name no
        // longer equals the candidate the caller just built.
        for (int i = 0; i < candidateFileNames.Count && unclaimed.Count > 0; i++)
        {
            if (resolved[i] is null)
            {
                resolved[i] = unclaimed[0];
                unclaimed.RemoveAt(0);
            }
        }

        // Anything still unresolved is genuinely new and must claim a free name.
        for (int i = 0; i < candidateFileNames.Count; i++)
        {
            resolved[i] ??= database.ClaimMovieFileName(directory, candidateFileNames[i]);
        }

        return resolved;
    }

    /// <summary>
    /// Normalises a target folder to the form stored in the database.
    /// </summary>
    /// <param name="targetFolder">The target folder.</param>
    /// <returns>The normalised folder, without leading or trailing separators.</returns>
    public static string NormalizeFolder(string? targetFolder)
    {
        if (string.IsNullOrWhiteSpace(targetFolder))
        {
            return string.Empty;
        }

        return targetFolder.Replace('\\', '/').Trim('/');
    }

    /// <summary>
    /// Joins a folder and a name into a stored relative path.
    /// </summary>
    /// <param name="folder">The folder, already normalised.</param>
    /// <param name="name">The directory or file name.</param>
    /// <returns>The joined relative path.</returns>
    public static string Join(string folder, string name)
    {
        return folder.Length == 0 ? name : string.Concat(folder, "/", name);
    }

    /// <summary>
    /// Determines whether a stored directory is an immediate child of a folder.
    /// </summary>
    /// <param name="directory">The stored directory.</param>
    /// <param name="folder">The normalised folder.</param>
    /// <returns>True when the directory sits directly inside the folder.</returns>
    public static bool IsInFolder(string directory, string folder)
    {
        int slash = directory.LastIndexOf('/');
        string parent = slash < 0 ? string.Empty : directory[..slash];
        return string.Equals(parent, folder, StringComparison.OrdinalIgnoreCase);
    }
}
