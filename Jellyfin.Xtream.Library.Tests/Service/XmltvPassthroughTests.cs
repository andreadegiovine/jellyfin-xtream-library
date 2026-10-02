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
using System.Text.RegularExpressions;
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

[Collection("PluginSingletonTests")]
public sealed class XmltvPassthroughTests : IDisposable
{
    private readonly string _tempPath = Path.Combine(Path.GetTempPath(), "test-xmltv-passthrough-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<IXtreamClient> _client = new();
    private readonly LiveTvService _liveTvService;

    public XmltvPassthroughTests()
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
        config.Providers.Add(TestDataBuilder.CreateProviderConfig());
        config.EnableLiveTv = true;
        config.EnableEpg = true;
        config.EnableChannelNameCleaning = false;

        var serverAppPaths = new Mock<IServerApplicationPaths>();
        serverAppPaths.Setup(p => p.DataPath).Returns(_tempPath);
        var appHost = new Mock<IServerApplicationHost>();
        appHost.Setup(h => h.GetApiUrlForLocalAccess(It.IsAny<System.Net.IPAddress>(), It.IsAny<bool>()))
            .Returns("http://127.0.0.1:8096");
        _liveTvService = new LiveTvService(_client.Object, new Mock<IDispatcharrClient>().Object, serverAppPaths.Object, appHost.Object, NullLogger<LiveTvService>.Instance);
    }

    public void Dispose()
    {
        _liveTvService.Dispose();
        if (Directory.Exists(_tempPath))
        {
            Directory.Delete(_tempPath, recursive: true);
        }
    }

    private static string XmltvTime(DateTimeOffset t) => t.UtcDateTime.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture) + " +0000";

    [Fact]
    public async Task ACutOffXmltvFallsBackToTheJsonGuideInsteadOfHanging()
    {
        var now = DateTimeOffset.UtcNow;
        _client
            .Setup(c => c.GetAllLiveStreamsAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LiveStreamInfo> { new() { StreamId = 1, Num = 1, Name = "A", EpgChannelId = "a" } });
        _client
            .Setup(c => c.GetXmltvAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("<tv>"
                + $"<programme start=\"{XmltvTime(now.AddMinutes(-30))}\" stop=\"{XmltvTime(now.AddMinutes(30))}\" channel=\"a\"><title>On now</title></programme>"
                + $"<programme start=\"{XmltvTime(now.AddMinutes(30))}\" stop=\"{XmltvTime(now.AddMinutes(90))}\" channel=\"a\"><title>Up next</title></programme>"
                + "<programme start=");
        _client
            .Setup(c => c.GetSimpleDataTableAsync(It.IsAny<ConnectionInfo>(), 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EpgListings
            {
                Listings = new List<EpgProgram>
                {
                    new()
                    {
                        StartTimestamp = now.AddMinutes(-30).ToUnixTimeSeconds(),
                        StopTimestamp = now.AddMinutes(30).ToUnixTimeSeconds(),
                        Title = "T24gbm93",
                        Description = string.Empty,
                    },
                },
            });

        var build = Task.Run(() => _liveTvService.GetXmltvEpgAsync(CancellationToken.None));
        var finished = await Task.WhenAny(build, Task.Delay(TimeSpan.FromSeconds(20))) == build;

        finished.Should().BeTrue("a broken document must not keep the EPG lock forever");
        var xml = await build;
        Regex.Matches(xml, "On now").Should().HaveCount(1, "what the broken pass appended is taken out again before the fallback adds its own");
    }
}
