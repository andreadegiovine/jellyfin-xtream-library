// Copyright (C) 2024  Roland Breitschaft
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Xtream.Library.Client;
using Jellyfin.Xtream.Library.Client.Models;
using Jellyfin.Xtream.Library.Service;
using Jellyfin.Xtream.Library.Tests.Helpers;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service;

/// <summary>
/// Two provider streams that share a title and a recognised quality tag want the same STRM file
/// name, because the name is built from the folder name and the version label alone. The original
/// guard refused the second write and counted it, which kept the library consistent but dropped
/// one of the two streams. With the library database in place the names are assigned before
/// anything is written, so the second claimant is numbered apart instead of refused: both streams
/// reach the library and neither can overwrite the other. These tests pin that numbering down.
/// The refusal counter is still wired up as a last resort for a path that assigns the same name
/// twice inside one run, so every scenario here also asserts that it stays at zero.
/// </summary>
[Collection("PluginSingletonTests")]
public class StrmNameCollisionTests : IDisposable
{
    private readonly string _libraryPath;
    private readonly Mock<IXtreamClient> _client = new();
    private readonly List<string> _log = [];

    private sealed class ListLogger(List<string> sink) : Microsoft.Extensions.Logging.ILogger<StrmSyncService>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => sink.Add($"[{logLevel}] {formatter(state, exception)}");
    }

    public StrmNameCollisionTests()
    {
        _libraryPath = Path.Combine(Path.GetTempPath(), "xtream-collision-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_libraryPath);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_libraryPath))
            {
                Directory.Delete(_libraryPath, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort; a leftover temp directory must not fail the suite.
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task TwoStreamsSharingTitleAndQualityTag_AreNumberedApartInsideOneFolder()
    {
        // Both resolve to "Duplicate Movie (2024)" with version label "FHD", so both want
        // "Duplicate Movie (2024) - FHD.strm". Neither carries a provider TMDB id, so they group
        // by sanitized name and share one directory; only the second file name is numbered.
        var result = await RunMovieSyncAsync(
            new StreamInfo { StreamId = 100, Name = "Duplicate Movie (2024) - [FHD]", ContainerExtension = "mp4" },
            new StreamInfo { StreamId = 200, Name = "Duplicate Movie (2024) - [FHD]", ContainerExtension = "mp4" })
            .ConfigureAwait(true);

        var movieFolder = Path.Combine(_libraryPath, "Movies", "Duplicate Movie (2024)");
        var written = Directory.Exists(movieFolder)
            ? Directory.GetFiles(movieFolder, "*.strm")
            : Array.Empty<string>();

        written.Select(Path.GetFileName).Should().BeEquivalentTo(
            ["Duplicate Movie (2024) - FHD.strm", "Duplicate Movie (2024) - FHD - #2.strm"],
            "the second claimant is numbered rather than dropped");

        // The numbering suffix goes after the version label and keeps the " - " separator, which
        // is what makes Jellyfin read the two files as alternate versions of one movie.
        result.MovieNameCollisions.Should().Be(0, "a numbered name is not a refused write");
        result.MoviesCreated.Should().Be(2);
        result.Errors.Should().Be(0, "a duplicate provider title is a quirk, not a sync failure");
        _log.Should().NotContain(l => l.StartsWith("[Warning] STRM name collision for movie", StringComparison.Ordinal));

        // Each file points at its own stream: neither was overwritten by the other.
        var contents = new List<string>();
        foreach (var file in written)
        {
            contents.Add(await File.ReadAllTextAsync(file).ConfigureAwait(true));
        }

        contents.Should().Contain(c => c.EndsWith("/100.mp4", StringComparison.Ordinal));
        contents.Should().Contain(c => c.EndsWith("/200.mp4", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TwoStreamsWithDifferentQualityTags_BothWriteAndNothingCollides()
    {
        // The regression guard for #74's actual feature: distinct tags still group into one
        // folder as separate versions, which is what the version dropdown is built on.
        var result = await RunMovieSyncAsync(
            new StreamInfo { StreamId = 100, Name = "Variant Movie (2024) - [FHD]", ContainerExtension = "mp4" },
            new StreamInfo { StreamId = 200, Name = "Variant Movie (2024) - [4K]", ContainerExtension = "mp4" })
            .ConfigureAwait(true);

        var movieFolder = Path.Combine(_libraryPath, "Movies", "Variant Movie (2024)");
        Directory.GetFiles(movieFolder, "*.strm").Should().HaveCount(2);
        result.MovieNameCollisions.Should().Be(0);
        result.MoviesCreated.Should().Be(2);
    }

    [Fact]
    public async Task TwoStreamsSharingAVersionTag_AreNumberedTheSameWayAsAQualityTag()
    {
        // V1/V2 became version labels in #75, so they land on one name exactly like FHD does and
        // must be numbered apart by the same rule.
        var result = await RunMovieSyncAsync(
            new StreamInfo { StreamId = 100, Name = "Tagged Movie (2024) - [V1]", ContainerExtension = "mp4" },
            new StreamInfo { StreamId = 200, Name = "Tagged Movie (2024) - [V1]", ContainerExtension = "mp4" })
            .ConfigureAwait(true);

        result.MovieNameCollisions.Should().Be(0);
        Directory.GetFiles(Path.Combine(_libraryPath, "Movies", "Tagged Movie (2024)"), "*.strm")
            .Select(Path.GetFileName)
            .Should().BeEquivalentTo(["Tagged Movie (2024) - V1.strm", "Tagged Movie (2024) - V1 - #2.strm"]);
    }

    // syncedFiles doubles as the orphan-protection set: the incremental and smart-skip paths bulk-add
    // existing .strm files to it so cleanup leaves them alone. Both default to on, and the first
    // version of this guard reused that dictionary as its collision detector, which would have made
    // "this file exists" indistinguishable from "another stream wrote this". A stale URL would then
    // never be refreshed. These runs use the shipped defaults, unlike the tests above.
    [Fact]
    public async Task DefaultConfigWithIncrementalAndSmartSkip_ReportsNoCollisionsForDistinctMovies()
    {
        StreamInfo[] Streams() =>
        [
            new StreamInfo { StreamId = 100, Name = "Alpha Movie (2024)", ContainerExtension = "mp4" },
            new StreamInfo { StreamId = 200, Name = "Beta Movie (2024)", ContainerExtension = "mp4" },
        ];

        var first = await RunMovieSyncAsync(Streams(), useShippedDefaults: true).ConfigureAwait(true);
        first.MovieNameCollisions.Should().Be(0);
        first.MoviesCreated.Should().Be(2);

        // Second run over the same catalogue: the files now exist, so the skip and orphan-protection
        // paths are the ones doing the work. Nothing here is a collision.
        var second = await RunMovieSyncAsync(Streams(), useShippedDefaults: true).ConfigureAwait(true);
        second.MovieNameCollisions.Should().Be(0, "an existing file is not another stream's claim");
        second.MoviesCreated.Should().Be(0);

        Directory.GetFiles(Path.Combine(_libraryPath, "Movies"), "*.strm", SearchOption.AllDirectories)
            .Should().HaveCount(2);
    }

    // The guard sits in the write loop, immediately before the branch that rewrites a STRM whose
    // URL no longer matches. If it treated "this file exists" as a collision, that rewrite would
    // never happen and a provider URL change would leave the library pointing at a dead host.
    [Fact]
    public async Task DefaultConfig_ChangedProviderUrlIsStillRewritten()
    {
        var first = await RunMovieSyncAsync(
            [new StreamInfo { StreamId = 100, Name = "Refresh Movie (2024)", ContainerExtension = "mp4" }],
            useShippedDefaults: true).ConfigureAwait(true);
        first.MoviesCreated.Should().Be(1);

        var strm = Directory.GetFiles(Path.Combine(_libraryPath, "Movies"), "*.strm", SearchOption.AllDirectories).Single();
        (await File.ReadAllTextAsync(strm).ConfigureAwait(true)).Should().EndWith("/100.mp4");

        // Same movie, same name, different container. The URL changes, so the stream counts as
        // modified and reaches the write loop, where the file already exists.
        var second = await RunMovieSyncAsync(
            [new StreamInfo { StreamId = 100, Name = "Refresh Movie (2024)", ContainerExtension = "mkv" }],
            useShippedDefaults: true).ConfigureAwait(true);

        second.MovieNameCollisions.Should().Be(0, "an existing file is not another stream's claim");
        second.MoviesUpdated.Should().Be(1, "a changed provider URL still has to be rewritten");
        (await File.ReadAllTextAsync(strm).ConfigureAwait(true)).Should().EndWith("/100.mkv");
    }

    // Episodes collide on series name plus season and episode number. The wiring is the same two
    // hops as the movie counter (local -> provider result -> global result), and the movie one was
    // missing its second hop while the guard itself worked, so this path needs its own run.
    [Fact]
    public async Task TwoEpisodesSharingSeasonAndEpisodeNumber_WriteOneFileAndCountTheCollision()
    {
        // The episode title is part of the file name, so a collision needs that to match too.
        // Providers routinely send no title at all, which collapses both to "<series> - S01E01.strm".
        var result = await RunSeriesSyncAsync(
            new Episode { EpisodeId = 11, EpisodeNum = 1, Season = 1, Title = string.Empty, ContainerExtension = "mkv" },
            new Episode { EpisodeId = 22, EpisodeNum = 1, Season = 1, Title = string.Empty, ContainerExtension = "mkv" })
            .ConfigureAwait(true);

        result.EpisodeNameCollisions.Should().Be(1);
        result.Errors.Should().Be(0);
        Directory.GetFiles(Path.Combine(_libraryPath, "Series"), "*.strm", SearchOption.AllDirectories)
            .Should().HaveCount(1);
        _log.Should().Contain(l => l.StartsWith("[Warning] STRM name collision for series", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EpisodesWithDistinctNumbers_BothWriteAndNothingCollides()
    {
        var result = await RunSeriesSyncAsync(
            new Episode { EpisodeId = 11, EpisodeNum = 1, Season = 1, Title = "First", ContainerExtension = "mkv" },
            new Episode { EpisodeId = 22, EpisodeNum = 2, Season = 1, Title = "Second", ContainerExtension = "mkv" })
            .ConfigureAwait(true);

        result.EpisodeNameCollisions.Should().Be(0);
        result.EpisodesCreated.Should().Be(2);
        Directory.GetFiles(Path.Combine(_libraryPath, "Series"), "*.strm", SearchOption.AllDirectories)
            .Should().HaveCount(2);
    }

    private async Task<SyncResult> RunSeriesSyncAsync(params Episode[] episodes)
    {
        _client.Setup(c => c.GetSeriesCategoryAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Category> { new() { CategoryId = 1, CategoryName = "Shows" } });
        _client.Setup(c => c.GetSeriesByCategoryAsync(It.IsAny<ConnectionInfo>(), 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Series> { new() { SeriesId = 7, Name = "Collide Show (2024)" } });
        _client.Setup(c => c.GetSeriesStreamsBySeriesAsync(It.IsAny<ConnectionInfo>(), 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesStreamInfo
            {
                Seasons = new List<Season> { new() { SeasonNumber = 1 } },
                Episodes = new Dictionary<int, ICollection<Episode>> { [1] = new List<Episode>(episodes) },
            });

        return await RunSyncAsync(syncMovies: false, syncSeries: true, useShippedDefaults: false).ConfigureAwait(true);
    }

    // "Case Movie" and "CASE MOVIE" are two paths on Linux and one on Windows and macOS, so a
    // filesystem-driven decision here produces a library whose shape depends on the host. The
    // grouping key folds case, which takes the filesystem out of it: the two titles are one group
    // everywhere, share one directory, and the second file is numbered. Which of the two spellings
    // ends up on the directory is not pinned down: the streams of a batch are drained from a
    // ConcurrentBag, so the one that claims the group first is an ordering detail, not a
    // guarantee. What has to hold on every platform is that there is exactly one directory and
    // that the file names follow whichever spelling won.
    [Fact]
    public async Task TitlesDifferingOnlyInCase_ShareOneDirectoryOnEveryPlatform()
    {
        var result = await RunMovieSyncAsync(
            new StreamInfo { StreamId = 100, Name = "Case Movie (2024)", ContainerExtension = "mp4" },
            new StreamInfo { StreamId = 200, Name = "CASE MOVIE (2024)", ContainerExtension = "mp4" })
            .ConfigureAwait(true);

        result.Errors.Should().Be(0);
        result.MovieNameCollisions.Should().Be(0);
        result.MoviesCreated.Should().Be(2);

        var directories = Directory.GetDirectories(Path.Combine(_libraryPath, "Movies"));
        directories.Should().HaveCount(1, "folding case in the group key makes the two titles one group");

        var leaf = Path.GetFileName(directories[0]);
        leaf.Should().BeOneOf("Case Movie (2024)", "CASE MOVIE (2024)");

        Directory.GetFiles(Path.Combine(_libraryPath, "Movies"), "*.strm", SearchOption.AllDirectories)
            .Select(Path.GetFileName)
            .Should().BeEquivalentTo(
                [leaf + ".strm", leaf + " - #2.strm"],
                "the file name follows the directory, whichever spelling claimed it");
    }

    // Providers are allowed to share a LibraryPath: PluginConfiguration records
    // HasDuplicateLibraryPaths but nothing refuses the config. The provider id is part of the
    // grouping key, so the same title coming from two providers is two groups and gets two
    // directories. That is deliberate: merging them would tie one directory to two provider URLs,
    // and dropping one would make the surviving library depend on which provider synced first.
    [Fact]
    public async Task TwoProvidersSharingALibraryPath_GetSeparateNumberedDirectories()
    {
        _client.Setup(c => c.GetVodCategoryAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Category> { new() { CategoryId = 1, CategoryName = "Movies" } });
        _client.Setup(c => c.GetVodStreamsByCategoryAsync(It.IsAny<ConnectionInfo>(), 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<StreamInfo>
            {
                new() { StreamId = 100, Name = "Shared Movie (2024)", ContainerExtension = "mp4" },
            });

        var result = await RunSyncAsync(syncMovies: true, syncSeries: false, useShippedDefaults: false, providerCount: 2)
            .ConfigureAwait(true);

        result.MovieNameCollisions.Should().Be(0, "each provider gets its own directory instead of being refused");

        var written = Directory.GetFiles(Path.Combine(_libraryPath, "Movies"), "*.strm", SearchOption.AllDirectories);
        written.Select(f => Path.GetRelativePath(Path.Combine(_libraryPath, "Movies"), f).Replace('\\', '/'))
            .Should().BeEquivalentTo(
            [
                "Shared Movie (2024)/Shared Movie (2024).strm",
                "Shared Movie (2024) #2/Shared Movie (2024) #2.strm",
            ]);

        // The suffix sits on the directory name and the file follows it, so the second provider's
        // movie is a self-consistent folder rather than a numbered file in the first one's.
        result.MoviesCreated.Should().Be(2);
    }

    private Task<SyncResult> RunMovieSyncAsync(params StreamInfo[] streams)
        => RunMovieSyncAsync(streams, useShippedDefaults: false);

    private async Task<SyncResult> RunMovieSyncAsync(StreamInfo[] streams, bool useShippedDefaults)
    {
        _client.Setup(c => c.GetVodCategoryAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Category> { new() { CategoryId = 1, CategoryName = "Movies" } });
        _client.Setup(c => c.GetVodStreamsByCategoryAsync(It.IsAny<ConnectionInfo>(), 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<StreamInfo>(streams));

        return await RunSyncAsync(syncMovies: true, syncSeries: false, useShippedDefaults).ConfigureAwait(true);
    }

    private Task<SyncResult> RunSyncAsync(bool syncMovies, bool syncSeries, bool useShippedDefaults)
        => RunSyncAsync(syncMovies, syncSeries, useShippedDefaults, providerCount: 1);

    private async Task<SyncResult> RunSyncAsync(bool syncMovies, bool syncSeries, bool useShippedDefaults, int providerCount)
    {
        var appPaths = new Mock<IServerApplicationPaths>();
        appPaths.Setup(p => p.PluginConfigurationsPath).Returns(_libraryPath);
        appPaths.Setup(p => p.DataPath).Returns(_libraryPath);
        appPaths.Setup(p => p.ProgramDataPath).Returns(_libraryPath);
        appPaths.Setup(p => p.CachePath).Returns(_libraryPath);
        appPaths.Setup(p => p.TempDirectory).Returns(_libraryPath);
        appPaths.Setup(p => p.PluginsPath).Returns(_libraryPath);

        // Constructing the plugin publishes Plugin.Instance, which SyncAsync reads.
        var plugin = new Plugin(appPaths.Object, new RealXmlSerializer());
        plugin.Configuration.Providers = Enumerable.Range(0, providerCount).Select(i => new ProviderConfig
        {
            Name = $"test{i}",
            BaseUrl = $"http://provider{i}.test",
            Username = "u",
            Password = "p",
            LibraryPath = _libraryPath,
            SyncMovies = syncMovies,
            SyncSeries = syncSeries,
            CleanupOrphans = false,
            EnableIncrementalSync = useShippedDefaults,
            SmartSkipExisting = useShippedDefaults,
            DownloadArtworkForUnmatched = false,
            SyncParallelism = 1,
        }).ToList();
        plugin.Configuration.EnableLiveTv = false;
        plugin.Configuration.EnableMetadataLookup = false;

        var service = new StrmSyncService(
            _client.Object,
            new Mock<IDispatcharrClient>().Object,
            new Mock<ILibraryManager>().Object,
            new Mock<IMetadataLookupService>().Object,
            new SnapshotService(appPaths.Object, NullLogger<SnapshotService>.Instance),
            new LibraryDatabaseService(NullLogger<LibraryDatabaseService>.Instance),
            new LibraryBackfillService(NullLogger<LibraryBackfillService>.Instance),
            new DeltaCalculator(NullLogger<DeltaCalculator>.Instance),
            new LiveTvService(_client.Object, appPaths.Object, MockAppHost(), NullLogger<LiveTvService>.Instance),
            appPaths.Object,
            new ListLogger(_log));

        return await service.SyncAsync(CancellationToken.None).ConfigureAwait(true);
    }

    private static IServerApplicationHost MockAppHost()
    {
        var host = new Mock<IServerApplicationHost>();
        host.Setup(h => h.GetApiUrlForLocalAccess(It.IsAny<System.Net.IPAddress>(), It.IsAny<bool>()))
            .Returns("http://127.0.0.1:8096");
        return host.Object;
    }
}
