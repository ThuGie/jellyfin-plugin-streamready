using Jellyfin.Data.Enums;
using Jellyfin.Plugin.StreamReady.Configuration;
using Jellyfin.Plugin.StreamReady.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.StreamReady.Services;

public class CompatibilityAnalyzer
{
    public CompatibilityResult Analyze(BaseItem item, PluginConfiguration config, string libraryId, string libraryName)
    {
        var effective = EncodePlanner.WithLibraryPreset(config, libraryId);
        var result = new CompatibilityResult();
        var video = item.GetMediaStreams().FirstOrDefault(s => s.Type == MediaStreamType.Video);
        var audio = item.GetMediaStreams().FirstOrDefault(s => s.Type == MediaStreamType.Audio && s.IsDefault)
                    ?? item.GetMediaStreams().FirstOrDefault(s => s.Type == MediaStreamType.Audio);

        var container = NormalizeContainer(item.Container ?? Path.GetExtension(item.Path));
        var videoCodec = NormalizeCodec(video?.Codec);
        var audioCodec = NormalizeCodec(audio?.Codec);
        var range = video?.VideoRangeType.ToString() ?? "Unknown";
        var size = item.Size ?? SafeLength(item.Path);
        var bitrate = video?.BitRate ?? 0;

        var allowedContainers = Split(effective.AllowedContainers);
        var allowedVideo = Split(effective.AllowedVideoCodecs).Select(NormalizeCodec).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allowedAudio = Split(effective.AllowedAudioCodecs).Select(NormalizeCodec).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allowedRanges = Split(effective.AllowedVideoRanges).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var reasons = new List<string>();
        var details = new List<string>();
        var containerBad = allowedContainers.Count > 0 && !allowedContainers.Contains(container, StringComparer.OrdinalIgnoreCase);
        var videoBad = !string.IsNullOrEmpty(videoCodec) && allowedVideo.Count > 0 && !allowedVideo.Contains(videoCodec);
        var audioBad = !string.IsNullOrEmpty(audioCodec) && allowedAudio.Count > 0 && !allowedAudio.Contains(audioCodec);
        var rangeBad = !string.IsNullOrEmpty(range) && allowedRanges.Count > 0 && !RangeAllowed(range, allowedRanges);
        var sizeBad = effective.MaxFileSizeGiB > 0 && size > effective.MaxFileSizeGiB * 1024d * 1024d * 1024d;
        var bitrateBad = effective.MaxVideoBitrateMbps > 0 && bitrate > effective.MaxVideoBitrateMbps * 1_000_000d;

        if (containerBad)
        {
            reasons.Add("Container");
            details.Add($"Container: {container} not allowed (allowed: {string.Join(',', allowedContainers)})");
        }

        if (videoBad)
        {
            reasons.Add("Video");
            details.Add($"Video: {videoCodec} not allowed (allowed: {string.Join(',', allowedVideo)})");
        }

        if (rangeBad)
        {
            reasons.Add("VideoRange");
            details.Add($"VideoRange: {range} not allowed (allowed: {string.Join(',', allowedRanges)})");
        }

        if (audioBad)
        {
            reasons.Add("Audio");
            details.Add($"Audio: {audioCodec} not allowed (allowed: {string.Join(',', allowedAudio)})");
        }

        if (sizeBad)
        {
            reasons.Add("Size");
            var gib = size / (1024d * 1024d * 1024d);
            details.Add($"Size: {gib:0.0} GiB > {effective.MaxFileSizeGiB:0.#} GiB");
        }

        if (bitrateBad)
        {
            reasons.Add("Bitrate");
            var mbps = bitrate / 1_000_000d;
            details.Add($"Bitrate: {mbps:0.0} Mbps > {effective.MaxVideoBitrateMbps:0.#} Mbps");
        }

        result.NeedsWork = reasons.Count > 0;
        result.Reasons = reasons;
        result.PlannedAction = EncodePlanner.Decide(
            videoBad || rangeBad || sizeBad || bitrateBad,
            audioBad,
            containerBad,
            videoCodec,
            audioCodec,
            effective);
        result.Candidate = new CandidateRecord
        {
            Id = item.Id.ToString("N"),
            ItemId = item.Id.ToString("N"),
            Name = item.Name,
            SeriesName = item is Episode episode ? episode.SeriesName : null,
            ItemType = item is Episode ? "Episode" : "Movie",
            LibraryId = libraryId,
            LibraryName = libraryName,
            Path = item.Path,
            SizeBytes = size,
            Container = container,
            VideoCodec = videoCodec,
            AudioCodec = audioCodec,
            VideoRange = range,
            Width = video?.Width,
            Height = video?.Height,
            Bitrate = bitrate > 0 ? bitrate : null,
            RuntimeTicks = item.RunTimeTicks,
            Reasons = reasons,
            ReasonDetails = details,
            PlannedAction = result.PlannedAction,
            AddedAt = DateTime.UtcNow
        };

        return result;
    }

    public static string NormalizeCodec(string? codec)
    {
        if (string.IsNullOrWhiteSpace(codec))
        {
            return string.Empty;
        }

        var value = codec.Trim().ToLowerInvariant();
        return value switch
        {
            "h265" or "hev1" or "hvc1" => "hevc",
            "avc" or "avc1" or "x264" => "h264",
            "dca" => "dts",
            "dts-hd" or "dtshd_ma" or "dtsma" or "dts-hd ma" => "dtshd",
            "mp4a" => "aac",
            _ => value
        };
    }

    public static string NormalizeContainer(string? container)
    {
        if (string.IsNullOrWhiteSpace(container))
        {
            return string.Empty;
        }

        var value = container.Trim().Trim('.').ToLowerInvariant();
        return value switch
        {
            "matroska" => "mkv",
            "mpegts" => "ts",
            "mpeg4" => "mp4",
            _ => value
        };
    }

    public static List<string> Split(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => v.ToLowerInvariant())
            .Distinct()
            .ToList();
    }

    public static bool RangeAllowed(string range, HashSet<string> allowed)
    {
        return allowed.Contains(range, StringComparer.OrdinalIgnoreCase);
    }

    public static bool RangeAllowed(string range, string? allowedCsv)
    {
        var allowed = Split(allowedCsv).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (allowed.Count == 0)
        {
            return true;
        }

        return RangeAllowed(range, allowed);
    }

    private static long SafeLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch
        {
            return 0;
        }
    }
}
