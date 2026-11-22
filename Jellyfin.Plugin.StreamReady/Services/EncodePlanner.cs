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
        if (!ContainerSupportsVideo(dest, videoCodec))
        {
            return EncodeAction.Full;
        }

        if (audioNeedsEncode || (audioCodec.Length > 0 && !ContainerSupportsAudio(dest, audioCodec)))
        {
            return EncodeAction.AudioOnly;
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

    internal static string SelectOutputContainer(PluginConfiguration config, IReadOnlyList<ProbedStream> subtitles)
    {
        var container = DestinationContainer(config);
        var allowed = CompatibilityAnalyzer.Split(config.AllowedContainers)
            .Select(CompatibilityAnalyzer.NormalizeContainer).ToHashSet();
        if (container == "mp4" && subtitles.Any(stream => stream.Codec is not ("mov_text" or "subrip")))
        {
            if (allowed.Count > 0 && !allowed.Contains("mkv"))
            {
                throw new InvalidOperationException("Selected subtitle tracks require MKV, which the compatibility profile excludes.");
            }

            return "mkv";
        }

        if (allowed.Count > 0 && !allowed.Contains(container))
        {
            throw new InvalidOperationException("The output container is outside the compatibility profile.");
        }

        return container;
    }

    internal sealed record AudioOutput(bool Copy, string Codec, long Bitrate, int? Channels);

    internal static AudioOutput PlanAudio(ProbedStream source, string container, PluginConfiguration config)
    {
        var codec = CompatibilityAnalyzer.NormalizeCodec(source.Codec);
        var allowed = CompatibilityAnalyzer.Split(config.AllowedAudioCodecs)
            .Select(CompatibilityAnalyzer.NormalizeCodec).ToHashSet();
        var copy = ContainerSupportsAudio(container, codec) && (allowed.Count == 0 || allowed.Contains(codec));
        if (copy)
        {
            return new(true, codec, source.Bitrate ?? 384_000, source.Channels);
        }

        var channels = Math.Min(source.Channels ?? 2, DestinationAudioChannels(config));
        var ceiling = channels <= 2 ? 192_000 : 384_000;
        var bitrate = Math.Min(source.Bitrate ?? ceiling, ceiling);
        return new(false, "aac", bitrate, channels);
    }

    internal static long? VideoBitrateBudget(PluginConfiguration config, long sourceBytes, double duration,
        long? sourceVideoBitrate, IReadOnlyList<AudioOutput> audio)
    {
        var sizeLimit = config.MaxFileSizeGiB > 0 ? config.MaxFileSizeGiB * 1073741824d : double.PositiveInfinity;
        if (config.DiscardIfOutputLarger)
        {
            sizeLimit = Math.Min(sizeLimit, sourceBytes);
        }

        double budget = sourceVideoBitrate ?? long.MaxValue;
        if (double.IsFinite(sizeLimit))
        {
            budget = Math.Min(budget, sizeLimit * 8 * 0.95 / duration - audio.Sum(track => track.Bitrate));
        }

        if (config.MaxVideoBitrateMbps > 0)
        {
            budget = Math.Min(budget, config.MaxVideoBitrateMbps * 950_000d);
        }

        if (budget < 100_000)
        {
            throw new InvalidOperationException("The source or configured size limit leaves too little bitrate for video.");
        }

        return budget >= long.MaxValue ? null : (long)budget;
    }

    internal static string DestinationSubtitleCodec(string container, string inputCodec) =>
        container == "mp4" ? "mov_text" : inputCodec == "mov_text" ? "subrip" : inputCodec;

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
