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
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Adds the native tuner to Jellyfin's Live TV tuner list while Enable Native Tuner is on
/// (GitHub #152).
/// <para>
/// Jellyfin asks every registered tuner type for channels, but it treats Live TV as switched off
/// for every user, and hides the Refresh Guide task, until at least one tuner is listed under
/// Tuner Devices. Registering the tuner type alone is therefore not enough: without a listed
/// tuner nobody sees Live TV.
/// </para>
/// <para>
/// The entry this class adds carries <see cref="FriendlyName"/>, so it can tell its own entry
/// from one a user added by hand. Switching the native tuner off removes only its own.
/// </para>
/// <para>
/// With EPG on as well, it also adds the plugin's Epg.xml as an XMLTV source under TV Guide Data
/// Providers (GitHub #165), since the tuner brings channels but no programmes. That entry is
/// marked by <see cref="GuideSourceMarker"/> in its address, and the same rules apply: an XMLTV
/// source already pointing at Epg.xml, added by hand, is reused and never removed.
/// </para>
/// </summary>
public sealed class NativeTunerRegistration : IHostedService, IDisposable
{
    /// <summary>The name shown under Tuner Devices for the entry this class adds.</summary>
    internal const string FriendlyName = "Xtream Library (added by the plugin)";

    /// <summary>Query string on the Epg.xml address of the guide source this class adds. Epg.xml
    /// ignores it; it is only there to tell that entry from one added by hand.</summary>
    internal const string GuideSourceMarker = "added-by=xtream-library";

    private const string LiveTvConfigKey = "livetv";

    // XmlTvListingsProvider.Type in Jellyfin.LiveTv, which plugins cannot reference.
    private const string XmlTvType = "xmltv";

    private const string EpgPath = "/XtreamLibrary/Epg.xml";

    // RefreshGuideScheduledTask.Key in Jellyfin.LiveTv, which plugins cannot reference.
    private const string RefreshGuideTaskKey = "RefreshGuide";

    private readonly ITunerHostManager _tunerHostManager;
    private readonly ITaskManager _taskManager;
    private readonly IServerConfigurationManager _serverConfig;
    private readonly IServerApplicationHost _appHost;
    private readonly ILogger<NativeTunerRegistration> _logger;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private EventHandler<BasePluginConfiguration>? _subscribedHandler;
    private bool? _lastWanted;

    /// <summary>
    /// Initializes a new instance of the <see cref="NativeTunerRegistration"/> class.
    /// </summary>
    /// <param name="tunerHostManager">Jellyfin's tuner host manager.</param>
    /// <param name="taskManager">Jellyfin's scheduled task manager.</param>
    /// <param name="serverConfig">The Jellyfin server configuration manager.</param>
    /// <param name="appHost">The Jellyfin server, for the address it reaches itself on.</param>
    /// <param name="logger">The logger.</param>
    public NativeTunerRegistration(
        ITunerHostManager tunerHostManager,
        ITaskManager taskManager,
        IServerConfigurationManager serverConfig,
        IServerApplicationHost appHost,
        ILogger<NativeTunerRegistration> logger)
    {
        _tunerHostManager = tunerHostManager ?? throw new ArgumentNullException(nameof(tunerHostManager));
        _taskManager = taskManager ?? throw new ArgumentNullException(nameof(taskManager));
        _serverConfig = serverConfig ?? throw new ArgumentNullException(nameof(serverConfig));
        _appHost = appHost ?? throw new ArgumentNullException(nameof(appHost));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (await SyncSafelyAsync(atStartup: true).ConfigureAwait(false))
        {
            _ = QueueGuideRefreshWhenReadyAsync(TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(3));
        }

        var plugin = Plugin.Instance;
        if (plugin is not null)
        {
            _subscribedHandler = (_, _) => _ = SyncSafelyAsync(atStartup: false);
            plugin.ConfigurationChanged += _subscribedHandler;
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        if (_subscribedHandler is not null && plugin is not null)
        {
            plugin.ConfigurationChanged -= _subscribedHandler;
        }

        _subscribedHandler = null;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Adds or removes the plugin's own tuner entry to match the plugin settings.
    /// </summary>
    /// <param name="config">The plugin configuration, or null to read the current one. Read
    /// inside the lock, so of two overlapping saves the later settings win.</param>
    /// <param name="atStartup">True while the server starts. Jellyfin has not loaded its
    /// scheduled tasks yet then, and saving a tuner with a guide refresh throws after the entry is
    /// already saved, so the refresh is left to the caller.</param>
    /// <returns>True when an entry was added.</returns>
    internal async Task<bool> SyncAsync(PluginConfiguration? config, bool atStartup)
    {
        await _sync.WaitAsync().ConfigureAwait(false);
        try
        {
            config ??= Plugin.Instance?.Configuration;
            if (config is null)
            {
                return false;
            }

            // The guide source first, so the refresh that adding the tuner starts already reads it.
            var (guideAdded, guideRemoved) = SyncGuideSource(config);
            var (tunerAdded, tunerSwitched) = await SyncTunerAsync(config, atStartup).ConfigureAwait(false);

            // Switching the native tuner on or off changes which channels it reports, and a new
            // guide source brings programmes, but a settings save does not refresh the guide by
            // itself. Adding the tuner already asks for one through SaveTunerHost, and at startup
            // the caller queues it once the tasks are loaded. One refresh covers both.
            if ((tunerSwitched || guideAdded || guideRemoved) && !tunerAdded && !atStartup)
            {
                await QueueGuideRefreshWhenReadyAsync(TimeSpan.Zero, TimeSpan.Zero).ConfigureAwait(false);
            }

            return tunerAdded || guideAdded;
        }
        finally
        {
            _sync.Release();
        }
    }

    private async Task<(bool Added, bool Switched)> SyncTunerAsync(PluginConfiguration config, bool atStartup)
    {
        var tuners = _serverConfig.GetConfiguration<LiveTvOptions>(LiveTvConfigKey).TunerHosts ?? Array.Empty<TunerHostInfo>();
        bool wanted = config.EnableLiveTv && config.EnableNativeTuner;

        // Not at startup: nothing has changed since the last run.
        bool switched = !atStartup && _lastWanted.HasValue && _lastWanted.Value != wanted;
        _lastWanted = wanted;

        if (wanted)
        {
            // Any entry of this type will do, including one added by hand before this existed.
            if (!tuners.Any(t => string.Equals(t.Type, XtreamTunerHost.TunerType, StringComparison.OrdinalIgnoreCase)))
            {
                await _tunerHostManager.SaveTunerHost(
                    new TunerHostInfo
                    {
                        Type = XtreamTunerHost.TunerType,
                        FriendlyName = FriendlyName,
                    },
                    dataSourceChanged: !atStartup).ConfigureAwait(false);
                _logger.LogInformation("Added the Xtream Library native tuner under Live TV Tuner Devices");
                return (true, switched);
            }

            return (false, switched);
        }

        foreach (var entry in tuners.Where(IsOwnEntry).ToList())
        {
            _tunerHostManager.DeleteTunerHost(entry.Id);
            _logger.LogInformation("Removed the Xtream Library native tuner from Live TV Tuner Devices, since Native Tuner is off");
        }

        return (false, switched);
    }

    /// <summary>
    /// Adds or removes the plugin's own XMLTV guide source to match the plugin settings.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <returns>Whether a source was added, and whether one was removed.</returns>
    private (bool Added, bool Removed) SyncGuideSource(PluginConfiguration config)
    {
        var options = _serverConfig.GetConfiguration<LiveTvOptions>(LiveTvConfigKey);
        var sources = options.ListingProviders ?? Array.Empty<ListingsProviderInfo>();
        bool wanted = config.EnableLiveTv && config.EnableNativeTuner && config.EnableEpg;

        if (wanted)
        {
            // Any XMLTV source on Epg.xml will do, including one added by hand before this existed.
            if (sources.Any(IsPluginGuideSource))
            {
                return (false, false);
            }

            string baseUrl;
            try
            {
                baseUrl = _appHost.GetApiUrlForLocalAccess(IPAddress.Loopback, false) ?? string.Empty;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not work out the address Jellyfin reaches itself on; add the EPG URL under TV Guide Data Providers by hand");
                return (false, false);
            }

            if (string.IsNullOrEmpty(baseUrl))
            {
                return (false, false);
            }

            options.ListingProviders =
            [
                .. sources,
                new ListingsProviderInfo
                {
                    Id = Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture),
                    Type = XmlTvType,
                    Path = baseUrl.TrimEnd('/') + EpgPath + "?" + GuideSourceMarker,
                    EnableAllTuners = true,
                },
            ];
            _serverConfig.SaveConfiguration(LiveTvConfigKey, options);
            _logger.LogInformation("Added the plugin's Epg.xml under Live TV TV Guide Data Providers");

            // Jellyfin fetches the guide from its own loopback address. An allow-list without it
            // would answer that fetch with 404 and leave the guide empty.
            if (IpAllowListParser.HasEntries(config.LiveTvEndpointAllowedIps)
                && !IpAllowListParser.IsAllowed(IPAddress.Loopback, IpAllowListParser.Parse(config.LiveTvEndpointAllowedIps)))
            {
                _logger.LogWarning("The Live TV allow-list does not include 127.0.0.1, so Jellyfin cannot read the guide it was given; add 127.0.0.1 to it");
            }

            return (true, false);
        }

        var own = sources.Where(IsOwnGuideSource).ToList();
        if (own.Count > 0)
        {
            options.ListingProviders = sources.Except(own).ToArray();
            _serverConfig.SaveConfiguration(LiveTvConfigKey, options);
            _logger.LogInformation("Removed the plugin's Epg.xml from Live TV TV Guide Data Providers, since the native tuner or EPG is off");
            return (false, true);
        }

        return (false, false);
    }

    private static bool IsPluginGuideSource(ListingsProviderInfo info)
        => string.Equals(info.Type, XmlTvType, StringComparison.OrdinalIgnoreCase)
            && info.Path is not null
            && info.Path.Contains(EpgPath, StringComparison.OrdinalIgnoreCase);

    private static bool IsOwnGuideSource(ListingsProviderInfo info)
        => IsPluginGuideSource(info) && info.Path!.Contains(GuideSourceMarker, StringComparison.Ordinal);

    /// <inheritdoc />
    public void Dispose() => _sync.Dispose();

    private static bool IsOwnEntry(TunerHostInfo info)
        => string.Equals(info.Type, XtreamTunerHost.TunerType, StringComparison.OrdinalIgnoreCase)
            && string.Equals(info.FriendlyName, FriendlyName, StringComparison.Ordinal);

    /// <summary>
    /// Queues Jellyfin's guide refresh once its scheduled tasks are loaded, so the channels of a
    /// tuner added during startup show without waiting for the daily run.
    /// </summary>
    /// <param name="interval">How often to look for the task.</param>
    /// <param name="timeout">How long to keep looking.</param>
    /// <returns>True when the refresh was queued.</returns>
    internal async Task<bool> QueueGuideRefreshWhenReadyAsync(TimeSpan interval, TimeSpan timeout)
    {
        try
        {
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                var refresh = _taskManager.ScheduledTasks
                    .FirstOrDefault(t => string.Equals(t.ScheduledTask.Key, RefreshGuideTaskKey, StringComparison.Ordinal));
                if (refresh is not null)
                {
                    _taskManager.QueueScheduledTask(refresh.ScheduledTask, new TaskOptions());
                    _logger.LogInformation("Queued a guide refresh for the native tuner");
                    return true;
                }

                if (DateTime.UtcNow >= deadline)
                {
                    _logger.LogInformation("Jellyfin's Refresh Guide task did not appear; the native tuner's channels show after its next scheduled run");
                    return false;
                }

                await Task.Delay(interval).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not queue a guide refresh for the native tuner");
            return false;
        }
    }

    private async Task<bool> SyncSafelyAsync(bool atStartup)
    {
        try
        {
            return await SyncAsync(null, atStartup).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Never let this stop the server from starting or a settings save from completing;
            // adding the tuner by hand under Tuner Devices still works.
            _logger.LogWarning(ex, "Could not update the Xtream Library entry under Live TV Tuner Devices");
            return false;
        }
    }
}
