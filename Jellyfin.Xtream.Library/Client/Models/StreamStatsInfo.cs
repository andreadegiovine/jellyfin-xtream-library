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

using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

#pragma warning disable CS1591
namespace Jellyfin.Xtream.Library.Client.Models;

/// <summary>
/// Stream statistics from Dispatcharr/StreamFlow probing.
/// </summary>
public class StreamStatsInfo
{
    [JsonProperty("resolution")]
    public string? Resolution { get; set; }

    [JsonProperty("video_codec")]
    public string? VideoCodec { get; set; }

    [JsonProperty("audio_codec")]
    public string? AudioCodec { get; set; }

    [JsonProperty("source_fps")]
    public double? SourceFps { get; set; }

    // In kbit/s. A double because Dispatcharr's proxy stores it rounded to one decimal; an int here
    // would fail the whole stats object on the first fractional value.
    // Raw tokens, read through the helpers below, so an unexpected type in a field this plugin
    // only uses as a hint cannot fail the whole stats object (or a whole channel list).
    [JsonProperty("audio_channels")]
    public JToken? AudioChannelsRaw { get; set; }

    [JsonProperty("audio_sample_rate")]
    public JToken? AudioSampleRateRaw { get; set; }

    [JsonProperty("audio_bitrate")]
    public JToken? AudioBitrateRaw { get; set; }

    // Recorded by Dispatcharr's channel listing, in ffprobe's own terms ("High", "Main 10"; level
    // 42 for H.264 4.2, 123 for HEVC 4.1), which is how Jellyfin stores a probed stream too.
    [JsonProperty("video_profile")]
    public JToken? VideoProfileRaw { get; set; }

    [JsonProperty("video_level")]
    public JToken? VideoLevelRaw { get; set; }

    [JsonIgnore]
    public int? AudioChannels => ReadInt(AudioChannelsRaw);

    [JsonIgnore]
    public int? AudioSampleRate => ReadInt(AudioSampleRateRaw);

    /// <summary>Gets the audio bitrate in kbit/s.</summary>
    [JsonIgnore]
    public int? AudioBitrate => ReadInt(AudioBitrateRaw);

    /// <summary>Gets the video profile, such as "High" or "Main 10", or null.</summary>
    [JsonIgnore]
    public string? VideoProfile => VideoProfileRaw is { Type: JTokenType.String } t && !string.IsNullOrWhiteSpace(t.Value<string>())
        ? t.Value<string>()!.Trim()
        : null;

    /// <summary>Gets the video level as ffprobe reports it, or null.</summary>
    [JsonIgnore]
    public int? VideoLevel => ReadInt(VideoLevelRaw);

    [JsonProperty("ffmpeg_output_bitrate")]
    public double? Bitrate { get; set; }

    private static int? ReadInt(JToken? token)
    {
        if (token is null)
        {
            return null;
        }

        if (token.Type is JTokenType.Integer or JTokenType.Float)
        {
            var value = token.Value<double>();
            return value > 0 && value < int.MaxValue ? (int)value : null;
        }

        return int.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 ? parsed : null;
    }
}
