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

    public static PluginConfiguration WithLibraryPreset(PluginConfiguration config, string? libraryId)
    {
        if (string.IsNullOrWhiteSpace(libraryId) || config.LibraryOverrides is null || config.LibraryOverrides.Count == 0)
        {
            return config;
        }

        var normalized = libraryId.Replace("-", string.Empty, StringComparison.Ordinal);
        var ov = config.LibraryOverrides.FirstOrDefault(o =>
            !string.IsNullOrWhiteSpace(o.LibraryId)
            && (o.LibraryId.Equals(libraryId, StringComparison.OrdinalIgnoreCase)
                || o.LibraryId.Replace("-", string.Empty, StringComparison.Ordinal)
                    .Equals(normalized, StringComparison.OrdinalIgnoreCase)));
        if (ov is null
            || string.IsNullOrWhiteSpace(ov.EncodingPreset)
            || ov.EncodingPreset.Equals("Inherit", StringComparison.OrdinalIgnoreCase))
        {
            return config;
        }

        return new PluginConfiguration
        {
            Enabled = config.Enabled,
            AutoDirectPreTranscode = config.AutoDirectPreTranscode,
            SelectedLibraryIds = config.SelectedLibraryIds,
            IncludeExtras = config.IncludeExtras,
            EncodingPreset = ov.EncodingPreset,
            LibraryOverrides = config.LibraryOverrides,
            AllowedContainers = config.AllowedContainers,
            AllowedVideoCodecs = config.AllowedVideoCodecs,
            AllowedAudioCodecs = config.AllowedAudioCodecs,
            AllowedVideoRanges = config.AllowedVideoRanges,
            MaxFileSizeGiB = config.MaxFileSizeGiB,
            MaxVideoBitrateMbps = config.MaxVideoBitrateMbps,
            Crf = config.Crf,
            ResolutionCap = config.ResolutionCap,
            HardwareAccel = config.HardwareAccel,
            AudioChannels = config.AudioChannels,
            ToneMapHdr = config.ToneMapHdr,
            OutputContainer = config.OutputContainer,
            KeepAllAudioAndSubtitles = config.KeepAllAudioAndSubtitles,
            ReplacementPolicy = config.ReplacementPolicy,
            BackupFolder = config.BackupFolder,
            BackupRetentionDays = config.BackupRetentionDays,
            VerifyBeforeReplace = config.VerifyBeforeReplace,
            DiscardIfOutputLarger = config.DiscardIfOutputLarger,
            ScanIntervalHours = config.ScanIntervalHours,
            ItemSettleDelaySeconds = config.ItemSettleDelaySeconds,
            MaxConcurrentJobs = config.MaxConcurrentJobs,
            PauseDuringPlayback = config.PauseDuringPlayback,
            EncodeWindowEnabled = config.EncodeWindowEnabled,
            EncodeWindowStart = config.EncodeWindowStart,
            EncodeWindowEnd = config.EncodeWindowEnd,
            EncodeWindowDays = config.EncodeWindowDays,
            WorkerPaused = config.WorkerPaused,
            FfmpegPreset = config.FfmpegPreset
        };
    }

    public static string DestinationContainer(PluginConfiguration config)
    {
        var c = (config.OutputContainer ?? "mp4").Trim().ToLowerInvariant();
        return c is "mkv" or "mp4" ? c : "mp4";
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
