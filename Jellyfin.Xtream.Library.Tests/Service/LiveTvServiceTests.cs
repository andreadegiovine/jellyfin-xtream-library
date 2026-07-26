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

#pragma warning disable CS0618 // Legacy config fields exercised in regression tests for BUG-008
using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Jellyfin.Xtream.Library;
using Jellyfin.Xtream.Library.Client;
using Jellyfin.Xtream.Library.Client.Models;
using Jellyfin.Xtream.Library.Service;
using Jellyfin.Xtream.Library.Service.Models;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service;

public class LiveTvServiceTests
{
    private readonly LiveTvService _service;

    public LiveTvServiceTests()
    {
        var clientMock = new Mock<IXtreamClient>();
        var appPathsMock = new Mock<IServerApplicationPaths>();
        appPathsMock.Setup(p => p.DataPath).Returns("/tmp");
        var appHostMock = new Mock<IServerApplicationHost>();
        appHostMock.Setup(h => h.GetApiUrlForLocalAccess(It.IsAny<System.Net.IPAddress>(), It.IsAny<bool>()))
            .Returns("http://127.0.0.1:8096");
        _service = new LiveTvService(clientMock.Object, appPathsMock.Object, appHostMock.Object, NullLogger<LiveTvService>.Instance);
    }

    [Fact]
    public void ResolveChannelLogoUrl_LocalPath_ReturnsProxyUrl()
    {
        _service.ResolveChannelLogoUrl("/share/logo.png", 7)
            .Should().Be("http://127.0.0.1:8096/XtreamLibrary/ChannelLogo/7");
    }

    [Fact]
    public void ResolveChannelLogoUrl_HttpUrl_Unchanged()
    {
        _service.ResolveChannelLogoUrl("http://x/y.png", 7).Should().Be("http://x/y.png");
    }

    private static List<LiveStreamInfo> MakeChannels(params int[] streamIds) =>
        streamIds.Select(id => new LiveStreamInfo
        {
            StreamId = id,
            Name = "Channel " + id,
            Num = id,
        }).ToList();

    [Fact]
    public void FilterExcludedChannels_EmptyExclusionList_ReturnsAllChannels()
    {
        var channels = MakeChannels(1, 2, 3);

        var result = LiveTvService.FilterExcludedChannels(channels, Array.Empty<int>());

        result.Should().HaveCount(3);
        result.Select(c => c.StreamId).Should().BeEquivalentTo(new[] { 1, 2, 3 });
    }

    [Fact]
    public void FilterExcludedChannels_NullExclusionList_ReturnsAllChannels()
    {
        var channels = MakeChannels(1, 2, 3);

        var result = LiveTvService.FilterExcludedChannels(channels, null);

        result.Should().BeSameAs(channels);
    }

    [Fact]
    public void FilterExcludedChannels_ExcludesSpecifiedStreamIds()
    {
        var channels = MakeChannels(1, 2, 3, 4, 5);

        var result = LiveTvService.FilterExcludedChannels(channels, new[] { 2, 4 });

        result.Select(c => c.StreamId).Should().BeEquivalentTo(new[] { 1, 3, 5 });
    }

    [Fact]
    public void FilterExcludedChannels_StaleExclusionIds_DoesNotThrow()
    {
        // Channel 99 doesn't exist in the source list — must not error or affect output.
        var channels = MakeChannels(1, 2, 3);

        var result = LiveTvService.FilterExcludedChannels(channels, new[] { 99 });

        result.Should().HaveCount(3);
    }

    [Fact]
    public void FilterExcludedChannels_AllExcluded_ReturnsEmpty()
    {
        var channels = MakeChannels(1, 2);

        var result = LiveTvService.FilterExcludedChannels(channels, new[] { 1, 2 });

        result.Should().BeEmpty();
    }

    [Fact]
    public void FilterExcludedChannels_DoesNotMutateInput()
    {
        var channels = MakeChannels(1, 2, 3);

        LiveTvService.FilterExcludedChannels(channels, new[] { 2 });

        channels.Should().HaveCount(3);
    }

    [Fact]
    public void ChooseCategoryFetchStrategy_IncludeAllMode_AlwaysAllFromProvider()
    {
        LiveTvService.ChooseCategoryFetchStrategy(LiveChannelSelectionMode.IncludeAll, selectedCategoryCount: 0)
            .Should().Be(LiveTvService.CategoryFetchStrategy.AllFromProvider);

        LiveTvService.ChooseCategoryFetchStrategy(LiveChannelSelectionMode.IncludeAll, selectedCategoryCount: 5)
            .Should().Be(LiveTvService.CategoryFetchStrategy.AllFromProvider);
    }

    [Fact]
    public void ChooseCategoryFetchStrategy_CustomMode_EmptySelection_None()
    {
        // The headline regression-guard test: pre-v1.35 this same input (empty selection)
        // ended up fetching every channel from the provider. Custom mode must now mean "none".
        LiveTvService.ChooseCategoryFetchStrategy(LiveChannelSelectionMode.Custom, selectedCategoryCount: 0)
            .Should().Be(LiveTvService.CategoryFetchStrategy.None);
    }

    [Fact]
    public void ChooseCategoryFetchStrategy_CustomMode_NonEmptySelection_BySelectedCategories()
    {
        LiveTvService.ChooseCategoryFetchStrategy(LiveChannelSelectionMode.Custom, selectedCategoryCount: 1)
            .Should().Be(LiveTvService.CategoryFetchStrategy.BySelectedCategories);

        LiveTvService.ChooseCategoryFetchStrategy(LiveChannelSelectionMode.Custom, selectedCategoryCount: 47)
            .Should().Be(LiveTvService.CategoryFetchStrategy.BySelectedCategories);
    }

    // BUG-008: multi-provider configs (Providers[0] populated, legacy fields empty) used to
    // produce m3u stream URLs shaped like "/live///{streamId}.ts" because BuildStreamUrl
    // read the legacy single-provider fields directly. These tests pin the resolver and the
    // two URL builders to the multi-provider data model.

    [Fact]
    public void ResolveLiveTvProvider_ProvidersPopulated_ReturnsProviderCredentials()
    {
        var config = new PluginConfiguration();
        config.Providers.Add(new ProviderConfig
        {
            BaseUrl = "http://multi.example.com:5656",
            Username = "multiuser",
            Password = "multipass",
        });

        // Legacy fields left empty — represents a fresh v1.32+ install configured via the
        // multi-provider UI.
        var result = LiveTvService.ResolveLiveTvProvider(config);

        result.BaseUrl.Should().Be("http://multi.example.com:5656");
        result.Username.Should().Be("multiuser");
        result.Password.Should().Be("multipass");
    }

    [Fact]
    public void ResolveLiveTvProvider_LegacyOnly_FallsBackToLegacyFields()
    {
        var config = new PluginConfiguration
        {
            BaseUrl = "http://legacy.example.com",
            Username = "legacyuser",
            Password = "legacypass",
        };

        // Providers list deliberately empty — represents a config caught mid-migration.
        var result = LiveTvService.ResolveLiveTvProvider(config);

        result.BaseUrl.Should().Be("http://legacy.example.com");
        result.Username.Should().Be("legacyuser");
        result.Password.Should().Be("legacypass");
    }

    [Fact]
    public void ResolveLiveTvProvider_BothPopulated_PrefersProviders()
    {
        var config = new PluginConfiguration
        {
            BaseUrl = "http://legacy.example.com",
            Username = "legacyuser",
            Password = "legacypass",
        };
        config.Providers.Add(new ProviderConfig
        {
            BaseUrl = "http://multi.example.com:5656",
            Username = "multiuser",
            Password = "multipass",
        });

        var result = LiveTvService.ResolveLiveTvProvider(config);

        result.BaseUrl.Should().Be("http://multi.example.com:5656");
        result.Username.Should().Be("multiuser");
    }

    [Fact]
    public void BuildStreamUrl_MultiProviderOnly_UsesProviderCredentials()
    {
        var config = new PluginConfiguration { LiveTvOutputFormat = "ts" };
        config.Providers.Add(new ProviderConfig
        {
            BaseUrl = "http://multi.example.com:5656",
            Username = "multiuser",
            Password = "multipass",
        });

        var channel = new LiveStreamInfo { StreamId = 2420044, Name = "X", Num = 1 };
        var url = LiveTvService.BuildStreamUrl(config, channel);

        url.Should().Be("http://multi.example.com:5656/live/multiuser/multipass/2420044.ts");
        url.Should().NotContain("///");
    }

    [Fact]
    public void BuildStreamUrl_MultipleLiveTvProviders_UsesChannelProviderCredentials()
    {
        var config = new PluginConfiguration { LiveTvOutputFormat = "ts" };
        config.Providers.Add(new ProviderConfig
        {
            BaseUrl = "http://provider-a.example.com",
            Username = "user-a",
            Password = "pass-a",
        });
        config.Providers.Add(new ProviderConfig
        {
            BaseUrl = "http://provider-b.example.com",
            Username = "user-b",
            Password = "pass-b",
        });

        var channel = new LiveStreamInfo { ProviderIndex = 1, StreamId = 2420044, Name = "X", Num = 1 };
        var url = LiveTvService.BuildStreamUrl(config, channel);

        url.Should().Be("http://provider-b.example.com/live/user-b/pass-b/2420044.ts");
    }

    [Fact]
    public void BuildChannelId_ProviderZero_KeepsLegacyShape()
    {
        XtreamTunerHost.BuildChannelId(providerIndex: 0, streamId: 100).Should().Be("xtream_100");
    }

    [Fact]
    public void BuildChannelId_AdditionalProvider_IncludesProviderIndex()
    {
        XtreamTunerHost.BuildChannelId(providerIndex: 1, streamId: 100).Should().Be("xtream_1_100");
    }

    // BUG-011: Live TV channels appeared as one flat list because GenerateM3U never
    // emitted group-title. These pin the category grouping behaviour.

    private static PluginConfiguration MakeM3UConfig()
    {
        var config = new PluginConfiguration { LiveTvOutputFormat = "ts" };
        config.Providers.Add(new ProviderConfig
        {
            BaseUrl = "http://multi.example.com:5656",
            Username = "multiuser",
            Password = "multipass",
        });
        return config;
    }

    [Fact]
    public void GenerateM3U_ChannelWithKnownCategory_EmitsGroupTitle()
    {
        var channels = new List<LiveStreamInfo>
        {
            new() { StreamId = 1, Name = "Channel 1", Num = 1, CategoryId = 10 },
        };
        var categoryNames = new Dictionary<int, string> { [10] = "Sports" };

        var m3u = LiveTvService.GenerateM3U(channels, MakeM3UConfig(), catchupOnly: false, "http://127.0.0.1:8096", categoryNames);

        m3u.Should().Contain("group-title=\"Sports\"");
    }

    // Cold-cache handling renders the M3U from the persisted snapshot instead of doing a full
    // upstream fetch inside the request, because Jellyfin only allows its own M3U fetch 100
    // seconds. That only holds up if a snapshot round trip reproduces the same output.

    [Fact]
    public void GenerateM3U_FromSnapshotRestoredChannels_ByteForByteIdentical()
    {
        var config = MakeM3UConfig();
        config.EnableCatchup = true;
        config.CatchupDays = 7;
        var channels = new List<LiveStreamInfo>
        {
            new()
            {
                StreamId = 1, Name = "BBC One", Num = 1, EpgChannelId = "bbc.one",
                StreamIcon = "http://logos.example.com/bbc.png", CategoryId = 10,
            },
            new()
            {
                StreamId = 2, Name = "Sky Sports", Num = 2, CategoryId = 20,
                TvArchive = true, TvArchiveDuration = 5,
            },
        };
        var categoryNames = new Dictionary<int, string> { [10] = "General", [20] = "Sports" };
        const string BaseUrl = "http://127.0.0.1:8096";

        var fromLiveFetch = LiveTvService.GenerateM3U(channels, config, catchupOnly: false, BaseUrl, categoryNames);

        var snapshot = LiveChannelSnapshot.FromChannels(channels, categoryNames);
        var restored = snapshot.ToChannels();
        restored.Should().NotBeNull();
        var fromSnapshot = LiveTvService.GenerateM3U(restored!, config, catchupOnly: false, BaseUrl, snapshot.Categories);

        fromSnapshot.Should().Be(fromLiveFetch);
    }

    [Fact]
    public void TryBeginChannelRefresh_SecondCallerBlocked_UntilTheFirstEnds()
    {
        // Without this guard every request arriving during a slow refresh would kick off
        // another one, which is the stampede the old lock-the-whole-request design avoided.
        _service.TryBeginChannelRefresh().Should().BeTrue();
        _service.TryBeginChannelRefresh().Should().BeFalse();

        _service.EndChannelRefresh();

        _service.TryBeginChannelRefresh().Should().BeTrue();
    }

    // A successful refresh calls InvalidateCache, so the next tuner poll finds a cold cache
    // again. Starting a refresh on every cold poll therefore loops forever and re-downloads
    // the whole catalogue at poll frequency. Only refresh when the snapshot is actually due.

    // Fetch-time filters are baked into the snapshot, so turning a filter ON after a snapshot
    // was taken would keep serving the filtered-out channels until the next refresh completes.
    // For adult content that is not an acceptable staleness window, so the filters are
    // re-applied when rendering from a snapshot.

    [Fact]
    public void ApplyRenderTimeFilters_AdultChannelsDroppedWhenNotIncluded()
    {
        var config = MakeM3UConfig();
        config.IncludeAdultChannels = false;
        var channels = new List<LiveStreamInfo>
        {
            new() { StreamId = 1, Name = "BBC One", Num = 1 },
            new() { StreamId = 2, Name = "Adult Channel", Num = 2, IsAdult = true },
        };

        var result = LiveTvService.ApplyRenderTimeFilters(channels, config);

        result.Select(c => c.StreamId).Should().BeEquivalentTo(new[] { 1 });
    }

    [Fact]
    public void ApplyRenderTimeFilters_AdultChannelsKeptWhenIncluded()
    {
        var config = MakeM3UConfig();
        config.IncludeAdultChannels = true;
        var channels = new List<LiveStreamInfo>
        {
            new() { StreamId = 1, Name = "BBC One", Num = 1 },
            new() { StreamId = 2, Name = "Adult Channel", Num = 2, IsAdult = true },
        };

        var result = LiveTvService.ApplyRenderTimeFilters(channels, config);

        result.Should().HaveCount(2);
    }

    [Fact]
    public void ApplyRenderTimeFilters_ExclusionsReappliedInCustomMode()
    {
        var config = MakeM3UConfig();
        config.LiveChannelMode = LiveChannelSelectionMode.Custom;
        config.ExcludedLiveStreamIds = new[] { 2 };
        var channels = new List<LiveStreamInfo>
        {
            new() { StreamId = 1, Name = "BBC One", Num = 1 },
            new() { StreamId = 2, Name = "Newly Excluded", Num = 2 },
        };

        var result = LiveTvService.ApplyRenderTimeFilters(channels, config);

        result.Select(c => c.StreamId).Should().BeEquivalentTo(new[] { 1 });
    }

    [Fact]
    public void ApplyRenderTimeFilters_ExclusionsIgnoredInIncludeAllMode()
    {
        // Matches the fetch path, where IncludeAll deliberately ignores per-channel exclusions.
        var config = MakeM3UConfig();
        config.LiveChannelMode = LiveChannelSelectionMode.IncludeAll;
        config.ExcludedLiveStreamIds = new[] { 2 };
        var channels = new List<LiveStreamInfo>
        {
            new() { StreamId = 1, Name = "BBC One", Num = 1 },
            new() { StreamId = 2, Name = "Excluded But IncludeAll", Num = 2 },
        };

        var result = LiveTvService.ApplyRenderTimeFilters(channels, config);

        result.Should().HaveCount(2);
    }

    // The stampede guard only covers the background refresh. The scheduled sync calls
    // RefreshChannelsAsync directly, and both fetch outside the snapshot lock, so a slow fetch
    // that finishes last would overwrite a newer snapshot with older data.

    [Fact]
    public void ShouldWriteSnapshot_NoExistingSnapshot_Writes()
    {
        var started = new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);

        LiveTvService.ShouldWriteSnapshot(started, existingCreatedAtUtc: null).Should().BeTrue();
    }

    [Fact]
    public void ShouldWriteSnapshot_ExistingIsOlderThanOurFetch_Writes()
    {
        var started = new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);

        LiveTvService.ShouldWriteSnapshot(started, started.AddMinutes(-5)).Should().BeTrue();
    }

    [Fact]
    public void ShouldWriteSnapshot_NewerSnapshotLandedWhileWeFetched_DoesNotOverwrite()
    {
        // Our fetch started at 12:00 and something wrote a fresher snapshot at 12:02 while we
        // were still downloading. Writing ours now would move the data backwards.
        var started = new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);

        LiveTvService.ShouldWriteSnapshot(started, started.AddMinutes(2)).Should().BeFalse();
    }

    [Fact]
    public void ShouldRefreshSnapshot_JustRefreshed_DoesNotRefreshAgain()
    {
        var now = new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);

        LiveTvService.ShouldRefreshSnapshot(now.AddMinutes(-1), now, cacheMinutes: 15)
            .Should().BeFalse();
    }

    [Fact]
    public void ShouldRefreshSnapshot_OlderThanCacheWindow_Refreshes()
    {
        var now = new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);

        LiveTvService.ShouldRefreshSnapshot(now.AddMinutes(-16), now, cacheMinutes: 15)
            .Should().BeTrue();
    }

    [Fact]
    public void ShouldRefreshSnapshot_NoTimestamp_Refreshes()
    {
        // A snapshot written before CreatedAt was populated, or a corrupt one, must not pin
        // the plugin to stale data forever.
        var now = new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);

        LiveTvService.ShouldRefreshSnapshot(default, now, cacheMinutes: 15).Should().BeTrue();
    }

    [Fact]
    public void ShouldRefreshSnapshot_ClockWentBackwards_DoesNotRefreshEveryPoll()
    {
        // A snapshot stamped in the future (clock skew, timezone bug) must not read as
        // infinitely stale, which would reintroduce the refresh-per-poll loop.
        var now = new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);

        LiveTvService.ShouldRefreshSnapshot(now.AddHours(1), now, cacheMinutes: 15)
            .Should().BeFalse();
    }

    [Fact]
    public void GenerateM3U_ChannelWithUnknownCategoryId_OmitsGroupTitle()
    {
        var channels = new List<LiveStreamInfo>
        {
            new() { StreamId = 1, Name = "Channel 1", Num = 1, CategoryId = 99 },
        };
        var categoryNames = new Dictionary<int, string> { [10] = "Sports" };

        var m3u = LiveTvService.GenerateM3U(channels, MakeM3UConfig(), catchupOnly: false, "http://127.0.0.1:8096", categoryNames);

        m3u.Should().NotContain("group-title");
    }

    [Fact]
    public void GenerateM3U_ChannelWithNullCategoryId_OmitsGroupTitle()
    {
        var channels = new List<LiveStreamInfo>
        {
            new() { StreamId = 1, Name = "Channel 1", Num = 1, CategoryId = null },
        };
        var categoryNames = new Dictionary<int, string> { [10] = "Sports" };

        var m3u = LiveTvService.GenerateM3U(channels, MakeM3UConfig(), catchupOnly: false, "http://127.0.0.1:8096", categoryNames);

        m3u.Should().NotContain("group-title");
    }

    [Fact]
    public void GenerateM3U_CategoryNameWithAmpersand_IsEscaped()
    {
        var channels = new List<LiveStreamInfo>
        {
            new() { StreamId = 1, Name = "Channel 1", Num = 1, CategoryId = 10 },
        };
        var categoryNames = new Dictionary<int, string> { [10] = "Sports & News" };

        var m3u = LiveTvService.GenerateM3U(channels, MakeM3UConfig(), catchupOnly: false, "http://127.0.0.1:8096", categoryNames);

        m3u.Should().Contain("group-title=\"Sports &amp; News\"");
    }
}
