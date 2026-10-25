// Copyright (C) 2024  Roland Breitschaft

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.

// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Xtream.Library.Service;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service;

/// <summary>
/// GitHub #152: Jellyfin shows Live TV only once a tuner is listed under Tuner Devices, so the
/// Enable Native Tuner checkbox has to list one itself.
/// </summary>
public class NativeTunerRegistrationTests
{
    private readonly Mock<ITunerHostManager> _tunerHosts = new();
    private readonly Mock<ITaskManager> _tasks = new();
    private readonly Mock<IServerConfigurationManager> _serverConfig = new();
    private readonly Mock<IServerApplicationHost> _appHost = new();
    private readonly LiveTvOptions _liveTv = new() { TunerHosts = [], ListingProviders = [] };

    public NativeTunerRegistrationTests()
    {
        _serverConfig.Setup(c => c.GetConfiguration("livetv")).Returns(() => _liveTv);
        _appHost.Setup(h => h.GetApiUrlForLocalAccess(It.IsAny<IPAddress>(), It.IsAny<bool>())).Returns("http://127.0.0.1:8096");
        _tunerHosts.Setup(t => t.SaveTunerHost(It.IsAny<TunerHostInfo>(), It.IsAny<bool>()))
            .ReturnsAsync((TunerHostInfo info, bool _) => info);
    }

    [Fact]
    public async Task NativeTunerOn_NoTunerListed_AddsOne()
    {
        await SyncAsync(liveTv: true, nativeTuner: true);

        _tunerHosts.Verify(
            t => t.SaveTunerHost(
                It.Is<TunerHostInfo>(i => i.Type == XtreamTunerHost.TunerType && i.FriendlyName == NativeTunerRegistration.FriendlyName),
                It.IsAny<bool>()),
            Times.Once);
    }

    [Fact]
    public async Task NativeTunerOn_TunerAddedByHand_AddsNothing()
    {
        // Everyone who got the native tuner working before this fix has one of these.
        _liveTv.TunerHosts = [new TunerHostInfo { Id = "manual", Type = XtreamTunerHost.TunerType, FriendlyName = "Xtream Library" }];

        await SyncAsync(liveTv: true, nativeTuner: true);

        _tunerHosts.Verify(t => t.SaveTunerHost(It.IsAny<TunerHostInfo>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task NativeTunerOn_OnlyAnM3uTunerListed_StillAddsOurs()
    {
        _liveTv.TunerHosts = [new TunerHostInfo { Id = "m3u", Type = "m3u", FriendlyName = "Playlist" }];

        await SyncAsync(liveTv: true, nativeTuner: true);

        _tunerHosts.Verify(t => t.SaveTunerHost(It.Is<TunerHostInfo>(i => i.Type == XtreamTunerHost.TunerType), It.IsAny<bool>()), Times.Once);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task NativeTunerNotInUse_RemovesOnlyTheEntryThePluginAdded(bool liveTv, bool nativeTuner)
    {
        _liveTv.TunerHosts =
        [
            new TunerHostInfo { Id = "ours", Type = XtreamTunerHost.TunerType, FriendlyName = NativeTunerRegistration.FriendlyName },
            new TunerHostInfo { Id = "manual", Type = XtreamTunerHost.TunerType, FriendlyName = "Xtream Library" },
            new TunerHostInfo { Id = "m3u", Type = "m3u", FriendlyName = NativeTunerRegistration.FriendlyName },
        ];

        await SyncAsync(liveTv, nativeTuner);

        _tunerHosts.Verify(t => t.DeleteTunerHost("ours"), Times.Once);
        _tunerHosts.Verify(t => t.DeleteTunerHost("manual"), Times.Never);
        _tunerHosts.Verify(t => t.DeleteTunerHost("m3u"), Times.Never);
        _tunerHosts.Verify(t => t.SaveTunerHost(It.IsAny<TunerHostInfo>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task NativeTunerOff_NothingListed_DoesNothing()
    {
        await SyncAsync(liveTv: true, nativeTuner: false);

        _tunerHosts.Verify(t => t.DeleteTunerHost(It.IsAny<string>()), Times.Never);
        _tunerHosts.Verify(t => t.SaveTunerHost(It.IsAny<TunerHostInfo>(), It.IsAny<bool>()), Times.Never);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task AddingAtStartup_DoesNotAskJellyfinToRefreshTheGuide(bool atStartup, bool expectedRefresh)
    {
        // During startup Jellyfin has not loaded its tasks, and SaveTunerHost throws while
        // queueing the refresh, after the entry is already saved. Measured on Jellyfin 12.
        var added = await SyncAsync(liveTv: true, nativeTuner: true, atStartup);

        added.Should().BeTrue();
        _tunerHosts.Verify(t => t.SaveTunerHost(It.IsAny<TunerHostInfo>(), expectedRefresh), Times.Once);
    }

    [Fact]
    public async Task GuideRefresh_IsQueuedOnceJellyfinHasLoadedTheTask()
    {
        var worker = new Mock<IScheduledTaskWorker>();
        var task = new Mock<IScheduledTask>();
        task.Setup(t => t.Key).Returns("RefreshGuide");
        worker.Setup(w => w.ScheduledTask).Returns(task.Object);
        var calls = 0;
        _tasks.Setup(t => t.ScheduledTasks).Returns(() => ++calls < 3 ? [] : [worker.Object]);

        using var registration = NewRegistration();
        var queued = await registration.QueueGuideRefreshWhenReadyAsync(TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(5));

        queued.Should().BeTrue();
        _tasks.Verify(t => t.QueueScheduledTask(task.Object, It.IsAny<TaskOptions>()), Times.Once);
    }

    [Fact]
    public async Task GuideRefresh_GivesUpWhenTheTaskNeverAppears()
    {
        _tasks.Setup(t => t.ScheduledTasks).Returns([]);

        using var registration = NewRegistration();
        var queued = await registration.QueueGuideRefreshWhenReadyAsync(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(20));

        queued.Should().BeFalse();
        _tasks.Verify(t => t.QueueScheduledTask(It.IsAny<IScheduledTask>(), It.IsAny<TaskOptions>()), Times.Never);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task SwitchingTheNativeTunerWithAHandAddedEntry_RefreshesTheGuide(bool before, bool after)
    {
        // A settings save does not refresh the guide, and with an entry already there nothing is
        // added, so nothing else would.
        _liveTv.TunerHosts = [new TunerHostInfo { Id = "manual", Type = XtreamTunerHost.TunerType, FriendlyName = "Xtream Library" }];
        var refresh = SetUpRefreshGuideTask();

        using var registration = NewRegistration();
        await registration.SyncAsync(new PluginConfiguration { EnableLiveTv = true, EnableNativeTuner = before }, atStartup: true);
        await registration.SyncAsync(new PluginConfiguration { EnableLiveTv = true, EnableNativeTuner = after }, atStartup: false);

        _tasks.Verify(t => t.QueueScheduledTask(refresh, It.IsAny<TaskOptions>()), Times.Once);
    }

    [Fact]
    public async Task SavingWithoutAChange_DoesNotRefreshTheGuide()
    {
        _liveTv.TunerHosts = [new TunerHostInfo { Id = "manual", Type = XtreamTunerHost.TunerType, FriendlyName = "Xtream Library" }];
        SetUpRefreshGuideTask();

        using var registration = NewRegistration();
        await registration.SyncAsync(new PluginConfiguration { EnableLiveTv = true, EnableNativeTuner = true }, atStartup: true);
        await registration.SyncAsync(new PluginConfiguration { EnableLiveTv = true, EnableNativeTuner = true }, atStartup: false);

        _tasks.Verify(t => t.QueueScheduledTask(It.IsAny<IScheduledTask>(), It.IsAny<TaskOptions>()), Times.Never);
    }

    private IScheduledTask SetUpRefreshGuideTask()
    {
        var worker = new Mock<IScheduledTaskWorker>();
        var task = new Mock<IScheduledTask>();
        task.Setup(t => t.Key).Returns("RefreshGuide");
        worker.Setup(w => w.ScheduledTask).Returns(task.Object);
        _tasks.Setup(t => t.ScheduledTasks).Returns([worker.Object]);
        return task.Object;
    }

    private NativeTunerRegistration NewRegistration()
        => new(_tunerHosts.Object, _tasks.Object, _serverConfig.Object, _appHost.Object, NullLogger<NativeTunerRegistration>.Instance);

    private async Task<bool> SyncAsync(bool liveTv, bool nativeTuner, bool atStartup = false, bool epg = false)
    {
        using var registration = NewRegistration();
        return await registration.SyncAsync(new PluginConfiguration { EnableLiveTv = liveTv, EnableNativeTuner = nativeTuner, EnableEpg = epg }, atStartup);
    }

    // GitHub #165: the native tuner brings channels, the guide comes from an XMLTV source.

    [Fact]
    public async Task NativeTunerAndEpgOn_NoGuideSource_AddsTheEpgXmlOnLoopback()
    {
        await SyncAsync(liveTv: true, nativeTuner: true, epg: true);

        var source = _liveTv.ListingProviders.Should().ContainSingle().Subject;
        source.Type.Should().Be("xmltv");
        source.Path.Should().Be("http://127.0.0.1:8096/XtreamLibrary/Epg.xml?" + NativeTunerRegistration.GuideSourceMarker);
        source.EnableAllTuners.Should().BeTrue();
        source.Id.Should().NotBeNullOrEmpty();
        _serverConfig.Verify(c => c.SaveConfiguration("livetv", _liveTv), Times.Once);
    }

    [Fact]
    public async Task NativeTunerAndEpgOn_EpgXmlAddedByHand_AddsNothing()
    {
        // Everyone who set the guide up before this has one, often on localhost instead of 127.0.0.1.
        _liveTv.ListingProviders = [new ListingsProviderInfo { Id = "manual", Type = "xmltv", Path = "http://localhost:8096/XtreamLibrary/Epg.xml" }];

        await SyncAsync(liveTv: true, nativeTuner: true, epg: true);

        _liveTv.ListingProviders.Select(p => p.Id).Should().Equal("manual");
        _serverConfig.Verify(c => c.SaveConfiguration("livetv", It.IsAny<object>()), Times.Never);
    }

    [Fact]
    public async Task NativeTunerAndEpgOn_OnlyAnotherXmltvSource_StillAddsOurs()
    {
        _liveTv.ListingProviders = [new ListingsProviderInfo { Id = "other", Type = "xmltv", Path = "https://epg.example/guide.xml" }];

        await SyncAsync(liveTv: true, nativeTuner: true, epg: true);

        _liveTv.ListingProviders.Should().HaveCount(2);
        _liveTv.ListingProviders[0].Id.Should().Be("other");
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public async Task GuideNotWanted_RemovesOnlyTheSourceThePluginAdded(bool liveTv, bool nativeTuner, bool epg)
    {
        _liveTv.ListingProviders =
        [
            new ListingsProviderInfo { Id = "ours", Type = "xmltv", Path = "http://127.0.0.1:8096/XtreamLibrary/Epg.xml?" + NativeTunerRegistration.GuideSourceMarker },
            new ListingsProviderInfo { Id = "manual", Type = "xmltv", Path = "http://localhost:8096/XtreamLibrary/Epg.xml" },
            new ListingsProviderInfo { Id = "other", Type = "xmltv", Path = "https://epg.example/guide.xml" },
        ];

        await SyncAsync(liveTv, nativeTuner, epg: epg);

        _liveTv.ListingProviders.Select(p => p.Id).Should().Equal("manual", "other");
    }

    [Fact]
    public async Task EpgOff_NoGuideSource_SavesNothing()
    {
        await SyncAsync(liveTv: true, nativeTuner: true, epg: false);

        _liveTv.ListingProviders.Should().BeEmpty();
        _serverConfig.Verify(c => c.SaveConfiguration("livetv", It.IsAny<object>()), Times.Never);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChangingOnlyTheGuideSource_RefreshesTheGuideOnce(bool epgOn)
    {
        // With the tuner already listed nothing else asks for a refresh, so the programmes would
        // only appear, or go, after the daily run.
        _liveTv.TunerHosts = [new TunerHostInfo { Id = "manual", Type = XtreamTunerHost.TunerType, FriendlyName = "Xtream Library" }];
        if (!epgOn)
        {
            _liveTv.ListingProviders = [new ListingsProviderInfo { Id = "ours", Type = "xmltv", Path = "http://127.0.0.1:8096/XtreamLibrary/Epg.xml?" + NativeTunerRegistration.GuideSourceMarker }];
        }

        var refresh = SetUpRefreshGuideTask();

        await SyncAsync(liveTv: true, nativeTuner: true, epg: epgOn);

        _tasks.Verify(t => t.QueueScheduledTask(refresh, It.IsAny<TaskOptions>()), Times.Once);
    }

    [Fact]
    public async Task AddingOnlyTheGuideSource_StillReportsAnAddition()
    {
        // The tuner is already listed; the caller then queues the guide refresh at startup.
        _liveTv.TunerHosts = [new TunerHostInfo { Id = "manual", Type = XtreamTunerHost.TunerType, FriendlyName = "Xtream Library" }];

        var added = await SyncAsync(liveTv: true, nativeTuner: true, atStartup: true, epg: true);

        added.Should().BeTrue();
        _liveTv.ListingProviders.Should().ContainSingle();
    }
}
