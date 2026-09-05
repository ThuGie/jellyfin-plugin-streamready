using Jellyfin.Plugin.StreamReady.Configuration;
using Jellyfin.Plugin.StreamReady.Models;

namespace Jellyfin.Plugin.StreamReady.Services;

public class EncodePlanner
{
    public static EncodeAction Decide(
        bool videoNeedsEncode,
        bool audioNeedsEncode,
        bool containerNeedsRemux,
        string videoCodec,
        string audioCodec,
        PluginConfiguration config)
    {
        if (videoNeedsEncode)
        {
            return EncodeAction.Full;
        }

        var dest = DestinationContainer(config);
        if (audioNeedsEncode || !ContainerSupportsAudio(dest, audioCodec))
        {
            return EncodeAction.AudioOnly;
        }

        if (containerNeedsRemux)
        {
            if (!ContainerSupportsVideo(dest, videoCodec))
            {
                return EncodeAction.Full;
            }

            return EncodeAction.Remux;
        }

        return EncodeAction.Remux;
    }

    public static string DestinationContainer(PluginConfiguration config)
    {
        return config.EncodingPreset.Equals("HevcCompact", StringComparison.OrdinalIgnoreCase) ? "mp4" : "mp4";
    }

    public static string DestinationVideoCodec(PluginConfiguration config)
    {
        return config.EncodingPreset.Equals("HevcCompact", StringComparison.OrdinalIgnoreCase) ? "hevc" : "h264";
    }

    public static int DestinationAudioChannels(PluginConfiguration config)
    {
        if (config.AudioChannels > 0)
        {
            return config.AudioChannels;
        }

        return config.EncodingPreset.Equals("MaxCompatibility", StringComparison.OrdinalIgnoreCase) ? 2 : 6;
    }

    public static int DestinationCrf(PluginConfiguration config)
    {
        if (config.Crf > 0)
        {
            return config.Crf;
        }

        return config.EncodingPreset.Equals("HevcCompact", StringComparison.OrdinalIgnoreCase) ? 20 : 18;
    }

    public static bool ContainerSupportsVideo(string container, string codec)
    {
        codec = CompatibilityAnalyzer.NormalizeCodec(codec);
        if (container is "mp4" or "m4v" or "mov")
        {
            return codec is "h264" or "hevc" or "av1" or "mpeg4";
        }

        return true;
    }

    public static bool ContainerSupportsAudio(string container, string codec)
    {
        codec = CompatibilityAnalyzer.NormalizeCodec(codec);
        if (container is "mp4" or "m4v" or "mov")
        {
            return codec is "aac" or "ac3" or "eac3" or "mp3" or "alac" or "opus";
        }

        return true;
    }
}
