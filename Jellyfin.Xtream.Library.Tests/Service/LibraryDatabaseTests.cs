// Copyright (C) 2024  Roland Breitschaft
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Xtream.Library.Service;
using Jellyfin.Xtream.Library.Service.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service;

/// <summary>
/// The database is what decides where every file goes, so these are the rules the whole library
/// rests on. Every one of them is a decision that cannot be walked back later: a name assigned
/// here is never revised, because renaming a folder makes Jellyfin treat it as a new item and drop
/// its watched status and artwork.
/// </summary>
public class LibraryDatabaseTests : IDisposable
{
    private readonly string _libraryPath;
    private readonly LibraryDatabaseService _service;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryDatabaseTests"/> class.
    /// </summary>
    public LibraryDatabaseTests()
    {
        _libraryPath = Path.Combine(Path.GetTempPath(), "xtream-db-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_libraryPath);
        _service = new LibraryDatabaseService(NullLogger<LibraryDatabaseService>.Instance);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    #region Movie directories and files

    [Fact]
    public async Task TwoMoviesSharingATitleWithNoProviderTmdbId_ShareOneDirectory()
    {
        var state = await LoadAsync().ConfigureAwait(true);

        string first = ResolveMovie(state, 100, "Duplicate (2024)");
        string second = ResolveMovie(state, 200, "Duplicate (2024)");

        second.Should().Be(first, "titles that sanitize alike are versions of one movie");
    }

    [Fact]
    public async Task TwoMoviesWithDifferentProviderTmdbIds_GetSeparateNumberedDirectories()
    {
        var state = await LoadAsync().ConfigureAwait(true);

        string first = MovieLibraryPathResolver.ResolveDirectory(
            state, "prov0001", 100, "Movies", "Clash (2024)", 111, "Clash (2024)", out _);
        string second = MovieLibraryPathResolver.ResolveDirectory(
            state, "prov0001", 200, "Movies", "Clash (2024)", 222, "Clash (2024)", out _);

        first.Should().Be("Movies/Clash (2024)");
        second.Should().Be("Movies/Clash (2024) #2", "two TMDB ids are two different films");
    }

    // The suffix goes before the tag because Jellyfin reads the tag from the end of the name.
    // Putting it after would leave Jellyfin parsing "1778 #2" as the identifier.
    [Fact]
    public async Task ANumberedDirectoryKeepsItsMetadataTagAtTheEnd()
    {
        var state = await LoadAsync().ConfigureAwait(true);

        MovieLibraryPathResolver.ResolveDirectory(
            state, "prov0001", 100, "Movies", "Tagged (2024) [tmdbid-1778]", 111, "Tagged (2024)", out _);
        string second = MovieLibraryPathResolver.ResolveDirectory(
            state, "prov0001", 200, "Movies", "Tagged (2024) [tmdbid-1778]", 222, "Tagged (2024)", out _);

        second.Should().Be("Movies/Tagged (2024) #2 [tmdbid-1778]");
    }

    // On files the separator is " - #2" rather than " #2", which is what makes Jellyfin read the
    // two files as alternate versions of one movie instead of as two unrelated titles.
    [Fact]
    public async Task ASecondFileInOneDirectoryIsNumberedWithTheVersionSeparator()
    {
        var state = await LoadAsync().ConfigureAwait(true);

        state.ClaimMovieFileName("Movies/Duplicate (2024)", "Duplicate (2024)")
            .Should().Be("Duplicate (2024)");
        state.ClaimMovieFileName("Movies/Duplicate (2024)", "Duplicate (2024)")
            .Should().Be("Duplicate (2024) - #2");
        state.ClaimMovieFileName("Movies/Duplicate (2024)", "Duplicate (2024)")
            .Should().Be("Duplicate (2024) - #3");
    }

    [Fact]
    public async Task TwoProvidersWithTheSameTitle_GetSeparateDirectories()
    {
        var state = await LoadAsync().ConfigureAwait(true);

        ResolveMovie(state, 100, "Shared (2024)", "prov0001").Should().Be("Movies/Shared (2024)");
        ResolveMovie(state, 100, "Shared (2024)", "prov0002").Should().Be("Movies/Shared (2024) #2");
    }

    [Fact]
    public async Task AMovieAlreadyRecorded_KeepsItsDirectoryEvenWhenTheProposedNameChanges()
    {
        var state = await LoadAsync().ConfigureAwait(true);
        state.AddMovie(new MovieDatabaseEntry
        {
            ProviderId = "prov0001",
            StreamId = 100,
            DirectoryName = "Movies/Old Name (2024) #2",
            FileName = "Old Name (2024) #2",
        });

        var reloaded = await ReloadAsync(state).ConfigureAwait(true);

        string resolved = MovieLibraryPathResolver.ResolveDirectory(
            reloaded, "prov0001", 100, "Movies", "New Name (2024) [tmdbid-9]", 9, "New Name (2024)", out bool known);

        known.Should().BeTrue();
        resolved.Should().Be(
            "Movies/Old Name (2024) #2",
            "renaming would cost the item its watched status and artwork in Jellyfin");
    }

    #endregion

    #region Series directories

    // Series are never grouped. Merging two shows that sanitize alike would drop the episodes of
    // both into shared "Season N" folders, and no later run could tell which episode was whose.
    [Fact]
    public async Task TwoSeriesSharingATitle_AreNeverMerged()
    {
        var state = await LoadAsync().ConfigureAwait(true);

        string first = ResolveSeries(state, 7, "Collide (2024)");
        WriteEpisode(state, 7, "11", first, 1, "Collide - S01E01");

        string second = ResolveSeries(state, 9, "Collide (2024)");

        second.Should().Be("Series/Collide (2024) #2");
        first.Should().Be("Series/Collide (2024)");
    }

    // The rows record season directories, so the set of taken series directories has to be derived
    // from them. Filling it with the season directories instead leaves every series directory
    // looking free, and the second show is handed the first one's folder. This only shows up after
    // a reload, which is the normal case: the snapshot skips unchanged series, so the first show
    // is usually not enumerated in the run that adds the second.
    [Fact]
    public async Task ASeriesDirectoryStaysTakenAcrossAReload()
    {
        var state = await LoadAsync().ConfigureAwait(true);
        string first = ResolveSeries(state, 7, "Collide (2024)");
        WriteEpisode(state, 7, "11", first, 1, "Collide - S01E01");

        var reloaded = await ReloadAsync(state).ConfigureAwait(true);

        ResolveSeries(reloaded, 9, "Collide (2024)").Should().Be("Series/Collide (2024) #2");
        ResolveSeries(reloaded, 7, "Collide (2024)").Should().Be("Series/Collide (2024)");
    }

    [Fact]
    public async Task SeasonFoldersAreNumberedWithoutPadding()
    {
        var state = await LoadAsync().ConfigureAwait(true);
        string directory = ResolveSeries(state, 7, "Show (2024)");

        SeriesLibraryPathResolver.SeasonDirectory(directory, 2)
            .Should().Be("Series/Show (2024)/Season 2", "Season 02 would rename every existing folder");
    }

    [Fact]
    public async Task AKnownEpisodeKeepsItsFileNameIncludingTheNumbering()
    {
        var state = await LoadAsync().ConfigureAwait(true);
        string directory = ResolveSeries(state, 7, "Show (2024)");
        string season = SeriesLibraryPathResolver.SeasonDirectory(directory, 1);

        string first = WriteEpisode(state, 7, "11", directory, 1, "Show - S01E01");
        string second = WriteEpisode(state, 7, "22", directory, 1, "Show - S01E01");

        first.Should().Be("Show - S01E01");
        second.Should().Be("Show - S01E01 - #2");

        SeriesLibraryPathResolver.ResolveFileName(state, "prov0001", 7, "22", season, "Show - S01E01")
            .Should().Be("Show - S01E01 - #2", "an episode is recognised by its id, not by position");
    }

    #endregion

    #region Adopting rows recovered from disk

    // A backfill cannot fill series_id: the URL inside an episode STRM names the episode and never
    // the show. Without this match the first sync after an upgrade would find no rows for any
    // series, hand every one a numbered directory, and duplicate the whole library.
    [Fact]
    public async Task RowsRecoveredFromDisk_AreAttributedToTheSeriesByName()
    {
        var state = await LoadAsync().ConfigureAwait(true);
        AddRecoveredEpisode(state, "Series/Old Show (2024)/Season 1", "Old Show - S01E01");

        var reloaded = await ReloadAsync(state).ConfigureAwait(true);

        string? adopted = SeriesLibraryPathResolver.AdoptBackfilledRows(
            reloaded, "prov0001", 7, "Series", "Old Show (2024)", out int rows);

        adopted.Should().Be("Series/Old Show (2024)");
        rows.Should().Be(1);

        ResolveSeries(reloaded, 7, "Old Show (2024)").Should().Be("Series/Old Show (2024)");
        SeriesLibraryPathResolver.ResolveFileName(
            reloaded, "prov0001", 7, "11", "Series/Old Show (2024)/Season 1", "Old Show - S01E01")
            .Should().Be("Old Show - S01E01", "the recovered file keeps the name it already has");
    }

    [Fact]
    public async Task AdoptionRecognisesADirectoryCarryingAMetadataTag()
    {
        var state = await LoadAsync().ConfigureAwait(true);
        AddRecoveredEpisode(state, "Series/Tagged Show (2024) [tvdbid-99]/Season 1", "Tagged Show - S01E01");

        SeriesLibraryPathResolver.AdoptBackfilledRows(state, "prov0001", 7, "Series", "Tagged Show (2024)", out int rows)
            .Should().Be("Series/Tagged Show (2024) [tvdbid-99]");
        rows.Should().Be(1);
    }

    // Two unattributed directories reducing to one name is the case that must not be guessed at:
    // attaching a series to a folder that may hold a different show interleaves both in shared
    // season folders, which is the one outcome that cannot be undone. The rows stay in the
    // database, so the files keep their names and are not treated as orphans.
    [Fact]
    public async Task AdoptionRefusesWhenTwoDirectoriesReduceToTheSameName()
    {
        var state = await LoadAsync().ConfigureAwait(true);
        AddRecoveredEpisode(state, "Series/Twin Show (2024)/Season 1", "a");
        AddRecoveredEpisode(state, "Series/Twin Show (2024) #2/Season 1", "b");

        SeriesLibraryPathResolver.AdoptBackfilledRows(state, "prov0001", 7, "Series", "Twin Show (2024)", out int rows)
            .Should().BeNull();
        rows.Should().Be(0);
    }

    [Fact]
    public async Task AdoptionNeverTakesADirectoryThatNamesAnotherSeries()
    {
        var state = await LoadAsync().ConfigureAwait(true);
        string directory = ResolveSeries(state, 5, "Show (2024)");
        WriteEpisode(state, 5, "11", directory, 1, "Show - S01E01");

        SeriesLibraryPathResolver.AdoptBackfilledRows(state, "prov0001", 7, "Series", "Show (2024)", out int rows)
            .Should().BeNull();
        rows.Should().Be(0);
    }

    [Fact]
    public async Task AdoptionDoesNotMatchOnAPrefixOfTheName()
    {
        var state = await LoadAsync().ConfigureAwait(true);
        AddRecoveredEpisode(state, "Series/Show 2/Season 1", "a");

        SeriesLibraryPathResolver.AdoptBackfilledRows(state, "prov0001", 7, "Series", "Show", out int rows)
            .Should().BeNull();
        rows.Should().Be(0);
    }

    #endregion

    #region Concurrency

    // Movies and series are written from several threads at once, so two callers must never be
    // handed the same name. A collision here is silent: both write, one file survives.
    [Fact]
    public async Task ConcurrentClaimsNeverHandOutTheSameName()
    {
        var state = await LoadAsync().ConfigureAwait(true);
        var claimed = new System.Collections.Concurrent.ConcurrentBag<string>();

        await Parallel.ForEachAsync(
            Enumerable.Range(0, 200),
            (_, _) =>
            {
                claimed.Add(state.ClaimMovieFileName("Movies/Race (2024)", "Race (2024)"));
                return ValueTask.CompletedTask;
            }).ConfigureAwait(true);

        claimed.Should().OnlyHaveUniqueItems();
        claimed.Should().HaveCount(200);
    }

    #endregion

    /// <summary>
    /// Releases the resources held by the fixture.
    /// </summary>
    /// <param name="disposing">Whether managed resources should be released.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing && Directory.Exists(_libraryPath))
        {
            try
            {
                Directory.Delete(_libraryPath, true);
            }
            catch (IOException)
            {
                // A leftover temp directory is not worth failing a test run over.
            }
        }

        _disposed = true;
    }

    private Task<LibraryDatabaseState> LoadAsync()
    {
        return _service.GetOrLoadAsync(_libraryPath, CancellationToken.None);
    }

    // Writes the rows out and reads them back through a service of its own, which is what a
    // restart does: the rows survive, the in-memory reservations do not.
    private async Task<LibraryDatabaseState> ReloadAsync(LibraryDatabaseState state)
    {
        await _service.SaveAsync(state, CancellationToken.None).ConfigureAwait(false);
        var fresh = new LibraryDatabaseService(NullLogger<LibraryDatabaseService>.Instance);
        return await fresh.GetOrLoadAsync(_libraryPath, CancellationToken.None).ConfigureAwait(false);
    }

    private static string ResolveMovie(
        LibraryDatabaseState state,
        int streamId,
        string folderName,
        string providerId = "prov0001")
    {
        return MovieLibraryPathResolver.ResolveDirectory(
            state, providerId, streamId, "Movies", folderName, null, folderName, out _);
    }

    private static string ResolveSeries(LibraryDatabaseState state, int seriesId, string folderName)
    {
        return SeriesLibraryPathResolver.ResolveDirectory(
            state, "prov0001", seriesId, "Series", folderName, out _);
    }

    private static string WriteEpisode(
        LibraryDatabaseState state,
        int seriesId,
        string episodeId,
        string seriesDirectory,
        int season,
        string candidateName)
    {
        string seasonDirectory = SeriesLibraryPathResolver.SeasonDirectory(seriesDirectory, season);
        string fileName = SeriesLibraryPathResolver.ResolveFileName(
            state, "prov0001", seriesId, episodeId, seasonDirectory, candidateName);

        state.AddSeries(new SeriesDatabaseEntry
        {
            ProviderId = "prov0001",
            SeriesId = seriesId,
            EpisodeId = episodeId,
            DirectoryName = seasonDirectory,
            FileName = fileName,
            Season = season,
        });

        return fileName;
    }

    private static void AddRecoveredEpisode(LibraryDatabaseState state, string seasonDirectory, string fileName)
    {
        state.AddSeries(new SeriesDatabaseEntry
        {
            ProviderId = "prov0001",
            SeriesId = null,
            EpisodeId = "11",
            DirectoryName = seasonDirectory,
            FileName = fileName,
            Season = 1,
        });
    }
}
