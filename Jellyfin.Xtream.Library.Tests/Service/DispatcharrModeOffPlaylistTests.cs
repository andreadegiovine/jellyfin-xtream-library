// Copyright (C) 2024  Roland Breitschaft

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// An upstream XMLTV that breaks off part way. The reader cannot move past broken markup, so the
// document has to go to the JSON fallback as a whole, and quickly: this build runs under the lock
// every Epg.xml request waits on.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Xtream.Library.Client;
using Jellyfin.Xtream.Library.Client.Models;
using Jellyfin.Xtream.Library.Service;
using Jellyfin.Xtream.Library.Tests.Helpers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service;

/// <summary>
/// BUG-038: switching Dispatcharr Mode off must take the proxy URLs out of the playlist, also
/// while a playlist rendered before the switch is still cached.
/// </summary>
[Collection("PluginSingletonTests")]
public sealed class DispatcharrModeOffPlaylistTests : IDisposable
{
    private readonly string _tempPath = Path.Combine(Path.GetTempPath(), "test-dispatcharr-off-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<IXtreamClient> _client = new();
    private readonly Mock<IDispatcharrClient> _dispatcharr = new();
    private readonly LiveTvService _liveTvService;

    public DispatcharrModeOffPlaylistTests()
    {
        var appPaths = new Mock<IApplicationPaths>();
        appPaths.Setup(p => p.PluginConfigurationsPath).Returns(_tempPath);
        appPaths.Setup(p => p.DataPath).Returns(_tempPath);
        appPaths.Setup(p => p.ProgramDataPath).Returns(_tempPath);
        appPaths.Setup(p => p.CachePath).Returns(_tempPath);
        appPaths.Setup(p => p.LogDirectoryPath).Returns(_tempPath);
        appPaths.Setup(p => p.ConfigurationDirectoryPath).Returns(_tempPath);
        appPaths.Setup(p => p.TempDirectory).Returns(_tempPath);
        appPaths.Setup(p => p.PluginsPath).Returns(_tempPath);
        appPaths.Setup(p => p.WebPath).Returns(_tempPath);
        appPaths.Setup(p => p.ProgramSystemPath).Returns(_tempPath);
        var xmlSerializer = new Mock<IXmlSerializer>();
        xmlSerializer
            .Setup(s => s.DeserializeFromFile(It.IsAny<Type>(), It.IsAny<string>()))
            .Returns(new PluginConfiguration());
        _ = new Plugin(appPaths.Object, xmlSerializer.Object);

        var config = Plugin.Instance.Configuration;
        var provider = TestDataBuilder.CreateProviderConfig();
        provider.EnableDispatcharrMode = true;
        provider.DispatcharrApiUser = "api";
        provider.DispatcharrBaseUrl = "http://dispatcharr.test:9191";
        config.Providers.Add(provider);
        config.EnableLiveTv = true;
        config.EnableChannelNameCleaning = false;
        config.M3UCacheMinutes = 60;

        _client
            .Setup(c => c.GetAllLiveStreamsAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new List<LiveStreamInfo> { new() { StreamId = 100, Num = 1, Name = "NPO 1", EpgChannelId = "npo1" } });
        _dispatcharr
            .Setup(d => d.GetChannelsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DispatcharrChannel>
            {
                new() { Id = 1, Uuid = "086aab97-3c36-41f0-a417-b97f39a69f0f", Streams = [new DispatcharrChannelSource { StreamId = 100 }] },
            });

        var serverAppPaths = new Mock<IServerApplicationPaths>();
        serverAppPaths.Setup(p => p.DataPath).Returns(_tempPath);
        var appHost = new Mock<IServerApplicationHost>();
        appHost.Setup(h => h.GetApiUrlForLocalAccess(It.IsAny<System.Net.IPAddress>(), It.IsAny<bool>()))
            .Returns("http://127.0.0.1:8096");
        _liveTvService = new LiveTvService(_client.Object, _dispatcharr.Object, serverAppPaths.Object, appHost.Object, NullLogger<LiveTvService>.Instance);
    }

    public void Dispose()
    {
        _liveTvService.Dispose();
        if (Directory.Exists(_tempPath))
        {
            Directory.Delete(_tempPath, recursive: true);
        }
    }

    [Fact]
    public async Task SwitchingDispatcharrModeOff_TakesTheProxyUrlsOutOfACachedPlaylist()
    {
        var before = await _liveTvService.GetM3UPlaylistAsync(CancellationToken.None);
        before.Should().Contain("/proxy/ts/stream/086aab97-3c36-41f0-a417-b97f39a69f0f");

        Plugin.Instance.Configuration.Providers[0].EnableDispatcharrMode = false;
        var after = await _liveTvService.GetM3UPlaylistAsync(CancellationToken.None);

        after.Should().NotContain("/proxy/ts/stream/");
        after.Should().Contain("/live/");
    }
}
