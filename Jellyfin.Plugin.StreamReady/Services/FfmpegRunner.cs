using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.StreamReady.Configuration;
using Jellyfin.Plugin.StreamReady.Models;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StreamReady.Services;

public class FfmpegRunner
{
    private static readonly Regex TimeRegex = new(@"time=(\d+):(\d+):(\d+(?:\.\d+)?)", RegexOptions.Compiled);

    private readonly IMediaEncoder _mediaEncoder;
    private readonly IServerConfigurationManager _serverConfig;
    private readonly ILogger<FfmpegRunner> _logger;

    public FfmpegRunner(
        IMediaEncoder mediaEncoder,
        IServerConfigurationManager serverConfig,
        ILogger<FfmpegRunner> logger)
    {
        _mediaEncoder = mediaEncoder;
        _serverConfig = serverConfig;
        _logger = logger;
    }

    /// <summary>
    /// Same strategy as jellyfin-plugin-pre-transcode: prefer Jellyfin's path, else fall back to
    /// the bare <c>ffmpeg</c> command on PATH. Never treat a missing File.Exists as "not ready".
    /// </summary>
    public string EncoderPath
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_mediaEncoder.EncoderPath))
            {
                return _mediaEncoder.EncoderPath;
            }

            try
            {
                var options = _serverConfig.GetEncodingOptions();
                if (!string.IsNullOrWhiteSpace(options.EncoderAppPath))
                {
                    return options.EncoderAppPath;
                }

                if (!string.IsNullOrWhiteSpace(options.EncoderAppPathDisplay))
                {
                    return options.EncoderAppPathDisplay;
                }
            }
            catch
            {
                // ignored
            }

            return "ffmpeg";
        }
    }

    public string? EncoderVersion
    {
        get
        {
            try
            {
                var version = _mediaEncoder.EncoderVersion;
                return version is null || version.Major <= 0 ? null : version.ToString();
            }
            catch
            {
                return null;
            }
        }
    }

    public bool IsReady
    {
        get
        {
            try
            {
                // Jellyfin has already probed ffmpeg by the time admins open settings.
                if (!string.IsNullOrWhiteSpace(_mediaEncoder.EncoderPath))
                {
                    return true;
                }

                if (_mediaEncoder.EncoderVersion is not null && _mediaEncoder.EncoderVersion.Major > 0)
                {
                    return true;
                }

                if (_mediaEncoder.SupportsEncoder("libx264")
                    || _mediaEncoder.SupportsEncoder("h264_qsv")
                    || _mediaEncoder.SupportsEncoder("h264_vaapi")
                    || _mediaEncoder.SupportsHwaccel("qsv")
                    || _mediaEncoder.SupportsHwaccel("vaapi"))
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "StreamReady IsReady probe failed");
            }

            // Last resort: bare "ffmpeg" on PATH (Pre-Transcode strategy).
            return !string.IsNullOrWhiteSpace(EncoderPath);
        }
    }

    public string ProbePath
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_mediaEncoder.ProbePath))
            {
                return _mediaEncoder.ProbePath;
            }

            var ffmpeg = EncoderPath;
            var file = Path.GetFileName(ffmpeg);
            var probe = string.IsNullOrEmpty(file)
                ? "ffprobe"
                : file.Replace("ffmpeg", "ffprobe", StringComparison.OrdinalIgnoreCase);
            var dir = Path.GetDirectoryName(ffmpeg);
            return string.IsNullOrEmpty(dir) ? probe : Path.Combine(dir, probe);
        }
    }

    public string DescribeHardware(PluginConfiguration? config)
    {
        var hw = ResolveHardware(config ?? new PluginConfiguration());
        return hw switch
        {
            HardwareAccelerationType.qsv => "Intel QSV",
            HardwareAccelerationType.vaapi => "VAAPI",
            HardwareAccelerationType.nvenc => "NVIDIA NVENC",
            HardwareAccelerationType.amf => "AMD AMF",
            HardwareAccelerationType.videotoolbox => "VideoToolbox",
            _ => "Software (CPU)"
        };
    }

    public async Task<double> ProbeDurationAsync(string path, CancellationToken cancellationToken)
    {
        var probe = ProbePath;
        if (string.IsNullOrWhiteSpace(probe) || !File.Exists(path))
        {
            return 0;
        }

        var args = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 {Quote(path)}";
        var (exit, stdout, _) = await RunProcessAsync(probe, args, null, null, cancellationToken).ConfigureAwait(false);
        if (exit != 0)
        {
            return 0;
        }

        return double.TryParse(stdout.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var duration)
            ? duration
            : 0;
    }

    public async Task EncodeAsync(
        string inputPath,
        string outputPath,
        EncodeAction action,
        PluginConfiguration config,
        double durationSeconds,
        IProgress<EncodeProgressUpdate> progress,
        CancellationToken cancellationToken,
        string? videoRange = null,
        Action<EncodePlan>? onPlan = null)
    {
        var ffmpeg = EncoderPath;
        if (!IsReady)
        {
            throw new InvalidOperationException("FFmpeg is not available. Check Dashboard → Playback → Transcoding.");
        }

        var plan = BuildEncodePlan(inputPath, outputPath, action, config, videoRange, forceSoftware: false);
        onPlan?.Invoke(plan);
        _logger.LogInformation(
            "StreamReady encode plan: {Summary} | encoder={Encoder} tonemap={ToneMap} filters={Filters}",
            plan.Summary,
            plan.VideoEncoder,
            plan.ToneMap,
            string.IsNullOrEmpty(plan.Filters) ? "(none)" : plan.Filters);
        _logger.LogInformation("StreamReady ffmpeg: {Bin} {Args}", ffmpeg, plan.Args);

        ReportProgress(progress, 0.5, null, null);
        var (exit, _, stderr) = await RunEncodeAsync(ffmpeg, plan.Args, durationSeconds, progress, cancellationToken)
            .ConfigureAwait(false);

        if (exit == 0)
        {
            ReportProgress(progress, 100, null, "done");
            return;
        }

        // QSV + soft filters often fails with auto_scale / ENOSYS on Synology sc-ffmpeg7.
        // Retry once with software decode+encode (libx264/libx265).
        if (action == EncodeAction.Full
            && ResolveHardware(config) != HardwareAccelerationType.none
            && LooksLikeHwFilterFailure(stderr))
        {
            _logger.LogWarning(
                "StreamReady hardware encode failed (exit {Exit}); retrying with software encode. Tail: {Tail}",
                exit,
                Truncate(stderr, 500));
            plan = BuildEncodePlan(inputPath, outputPath, action, config, videoRange, forceSoftware: true);
            onPlan?.Invoke(plan);
            _logger.LogInformation("StreamReady ffmpeg (software fallback): {Bin} {Args}", ffmpeg, plan.Args);
            ReportProgress(progress, 0.5, null, "retrying…");
            (exit, _, stderr) = await RunEncodeAsync(ffmpeg, plan.Args, durationSeconds, progress, cancellationToken)
                .ConfigureAwait(false);
            if (exit == 0)
            {
                ReportProgress(progress, 100, null, "done");
                return;
            }
        }

        throw new InvalidOperationException($"FFmpeg exited with code {exit}. {Truncate(stderr, 2000)}");
    }

    private static void ReportProgress(
        IProgress<EncodeProgressUpdate> progress,
        double percent,
        string? speed,
        string? eta)
    {
        progress.Report(new EncodeProgressUpdate
        {
            Percent = percent,
            Speed = speed,
            Eta = eta
        });
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunEncodeAsync(
        string ffmpeg,
        string args,
        double durationSeconds,
        IProgress<EncodeProgressUpdate> progress,
        CancellationToken cancellationToken)
    {
        var tracker = new ProgressTracker(durationSeconds);
        void HandleLine(string line)
        {
            if (tracker.TryHandle(line, out var update))
            {
                progress.Report(update);
            }
        }

        return await RunProcessAsync(
            ffmpeg,
            args,
            HandleLine,
            HandleLine,
            cancellationToken).ConfigureAwait(false);
    }

    private sealed class ProgressTracker
    {
        private readonly double _durationSeconds;
        private readonly DateTime _startedUtc = DateTime.UtcNow;
        private double _lastEncodedSeconds;
        private double _speedFactor;
        private string? _speedLabel;

        public ProgressTracker(double durationSeconds)
        {
            _durationSeconds = durationSeconds;
        }

        public bool TryHandle(string line, out EncodeProgressUpdate update)
        {
            update = new EncodeProgressUpdate();
            if (string.IsNullOrWhiteSpace(line) || _durationSeconds <= 0)
            {
                return false;
            }

            if (line.StartsWith("speed=", StringComparison.OrdinalIgnoreCase))
            {
                var raw = line["speed=".Length..].Trim().TrimEnd('x', 'X');
                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed) && speed > 0)
                {
                    _speedFactor = speed;
                    _speedLabel = speed.ToString("0.00", CultureInfo.InvariantCulture) + "x";
                }

                return false;
            }

            double? encodedSeconds = null;

            if (line.StartsWith("out_time=", StringComparison.OrdinalIgnoreCase))
            {
                var raw = line["out_time=".Length..].Trim();
                if (raw is not ("N/A" or "") && TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out var ts))
                {
                    encodedSeconds = ts.TotalSeconds;
                }
            }
            else if (line.StartsWith("out_time_us=", StringComparison.OrdinalIgnoreCase))
            {
                var raw = line["out_time_us=".Length..].Trim();
                if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) && us >= 0)
                {
                    encodedSeconds = us / 1_000_000d;
                }
            }
            else if (line.StartsWith("out_time_ms=", StringComparison.OrdinalIgnoreCase))
            {
                // Misnamed: ffmpeg reports microseconds here.
                var raw = line["out_time_ms=".Length..].Trim();
                if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var misnamedUs) && misnamedUs >= 0)
                {
                    encodedSeconds = misnamedUs / 1_000_000d;
                }
            }
            else
            {
                var match = TimeRegex.Match(line);
                if (match.Success)
                {
                    var hours = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                    var minutes = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                    var seconds = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
                    encodedSeconds = (hours * 3600) + (minutes * 60) + seconds;
                }
            }

            if (encodedSeconds is null)
            {
                return false;
            }

            _lastEncodedSeconds = encodedSeconds.Value;
            var percent = Math.Clamp(_lastEncodedSeconds / _durationSeconds * 100d, 0, 99.5);
            update.Percent = percent;
            update.Speed = _speedLabel;
            update.Eta = FormatEta(_durationSeconds, _lastEncodedSeconds, _speedFactor, _startedUtc);
            return true;
        }

        private static string? FormatEta(
            double durationSeconds,
            double encodedSeconds,
            double speedFactor,
            DateTime startedUtc)
        {
            var remainingMedia = Math.Max(0, durationSeconds - encodedSeconds);
            double? etaSeconds = null;

            if (speedFactor > 0.05)
            {
                etaSeconds = remainingMedia / speedFactor;
            }
            else if (encodedSeconds > 2)
            {
                // Wall-clock fallback when ffmpeg speed is missing/N/A.
                var wall = (DateTime.UtcNow - startedUtc).TotalSeconds;
                if (wall > 1 && encodedSeconds > 0)
                {
                    var effective = encodedSeconds / wall;
                    if (effective > 0.05)
                    {
                        etaSeconds = remainingMedia / effective;
                    }
                }
            }

            if (etaSeconds is null || double.IsNaN(etaSeconds.Value) || double.IsInfinity(etaSeconds.Value))
            {
                return null;
            }

            var sec = (int)Math.Ceiling(Math.Max(0, etaSeconds.Value));
            if (sec < 60)
            {
                return $"~{sec}s left";
            }

            if (sec < 3600)
            {
                var m = sec / 60;
                var s = sec % 60;
                return s > 0 ? $"~{m}m {s}s left" : $"~{m}m left";
            }

            var h = sec / 3600;
            var rm = (sec % 3600) / 60;
            return rm > 0 ? $"~{h}h {rm}m left" : $"~{h}h left";
        }
    }

    private static bool LooksLikeHwFilterFailure(string stderr)
    {
        if (string.IsNullOrEmpty(stderr))
        {
            return false;
        }

        return stderr.Contains("auto_scale", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("Impossible to convert between the formats", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("Function not implemented", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("Could not open encoder", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("Error reinitializing filters", StringComparison.OrdinalIgnoreCase);
    }

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max)
        {
            return value ?? string.Empty;
        }

        return value[^max..];
    }

    public EncodePlan BuildEncodePlan(
        string inputPath,
        string outputPath,
        EncodeAction action,
        PluginConfiguration config,
        string? videoRange = null,
        bool forceSoftware = false)
    {
        var sb = new StringBuilder();
        // -progress pipe:1 is reliable; -loglevel error used to hide classic time= stats (0% forever).
        sb.Append("-y -hide_banner -loglevel warning -progress pipe:1 -nostats ");

        var hw = forceSoftware || action != EncodeAction.Full
            ? HardwareAccelerationType.none
            : ResolveHardware(config);
        var vf = action == EncodeAction.Full ? BuildVideoFilter(config, videoRange) : string.Empty;
        var hasSoftwareFilters = !string.IsNullOrEmpty(vf);
        var toneMap = hasSoftwareFilters && vf.Contains("tonemap", StringComparison.OrdinalIgnoreCase);

        var (videoEncoder, extra) = action == EncodeAction.Full
            ? ResolveVideoEncoder(EncodePlanner.DestinationVideoCodec(config), config, hasSoftwareFilters, forceSoftware)
            : (action == EncodeAction.Remux ? "copy" : "copy (video)", string.Empty);

        var usingHwEncode = !forceSoftware
            && action == EncodeAction.Full
            && videoEncoder.Contains('_')
            && !videoEncoder.StartsWith("lib", StringComparison.OrdinalIgnoreCase);

        var decodeMode = "software";
        if (usingHwEncode
            && !hasSoftwareFilters
            && !videoEncoder.Contains("qsv", StringComparison.OrdinalIgnoreCase))
        {
            var hwaccel = HwaccelMethod(hw);
            if (!string.IsNullOrEmpty(hwaccel))
            {
                sb.Append("-hwaccel ").Append(hwaccel).Append(' ');
                decodeMode = hwaccel;
            }
        }

        sb.Append("-i ").Append(Quote(inputPath)).Append(' ');
        if (action == EncodeAction.Remux)
        {
            sb.Append("-map 0 ");
        }
        else
        {
            sb.Append("-map 0:v:0 -map 0:a:0? ");
        }

        var channels = EncodePlanner.DestinationAudioChannels(config);
        var crf = EncodePlanner.DestinationCrf(config);
        var audioBitrate = channels <= 2 ? "192k" : "384k";
        var preset = string.IsNullOrWhiteSpace(config.FfmpegPreset) ? "medium" : config.FfmpegPreset;
        var pixelFormat = string.Empty;

        switch (action)
        {
            case EncodeAction.Remux:
                sb.Append("-c copy ");
                break;
            case EncodeAction.AudioOnly:
                sb.Append("-c:v copy -c:a aac -b:a ").Append(audioBitrate)
                    .Append(" -ac ").Append(channels).Append(' ');
                break;
            default:
                if (hasSoftwareFilters)
                {
                    if (videoEncoder.Contains("vaapi", StringComparison.OrdinalIgnoreCase))
                    {
                        vf += ",format=nv12,hwupload";
                    }
                    else if (videoEncoder.Contains("qsv", StringComparison.OrdinalIgnoreCase))
                    {
                        vf += ",format=nv12";
                    }

                    sb.Append("-vf ").Append(Quote(vf)).Append(' ');
                }
                else if (videoEncoder.Contains("qsv", StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append("-vf format=nv12 ");
                    vf = "format=nv12";
                }
                else if (videoEncoder.Contains("vaapi", StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append("-vf format=nv12,hwupload ");
                    vf = "format=nv12,hwupload";
                }

                sb.Append("-c:v ").Append(videoEncoder).Append(' ').Append(extra);
                if (videoEncoder is "libx264" or "libx265")
                {
                    sb.Append("-crf ").Append(crf).Append(" -preset ").Append(preset).Append(' ');
                    sb.Append("-pix_fmt yuv420p ");
                    pixelFormat = "yuv420p";
                }
                else if (videoEncoder.Contains("nvenc", StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append("-cq ").Append(Math.Max(crf, 16)).Append(" -preset p4 -pix_fmt yuv420p ");
                    pixelFormat = "yuv420p";
                }
                else if (videoEncoder.Contains("qsv", StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append("-global_quality ").Append(crf).Append(" -pix_fmt nv12 ");
                    pixelFormat = "nv12";
                }
                else if (videoEncoder.Contains("vaapi", StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append("-qp ").Append(crf).Append(' ');
                }
                else
                {
                    sb.Append("-pix_fmt yuv420p ");
                    pixelFormat = "yuv420p";
                }

                sb.Append("-c:a aac -b:a ").Append(audioBitrate)
                    .Append(" -ac ").Append(channels).Append(' ');
                break;
        }

        if (Path.GetExtension(outputPath).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            if (action == EncodeAction.Remux)
            {
                sb.Append("-c:s mov_text -movflags +faststart ");
            }
            else
            {
                sb.Append("-sn -movflags +faststart ");
            }
        }

        sb.Append(Quote(outputPath));

        var hardwareLabel = forceSoftware || videoEncoder.StartsWith("lib", StringComparison.OrdinalIgnoreCase)
            ? "Software (CPU)"
            : DescribeHardware(config);

        var summaryParts = new List<string>
        {
            action.ToString(),
            videoEncoder,
            hardwareLabel
        };
        if (forceSoftware)
        {
            summaryParts.Add("fallback");
        }

        summaryParts.Add(toneMap ? "tone-map ON" : "tone-map OFF");
        if (!string.IsNullOrEmpty(vf))
        {
            summaryParts.Add("vf=" + (vf.Length > 60 ? vf[..60] + "…" : vf));
        }
        else
        {
            summaryParts.Add("no filters");
        }

        summaryParts.Add("decode " + decodeMode);

        return new EncodePlan
        {
            Action = action.ToString(),
            VideoEncoder = videoEncoder,
            HardwareLabel = hardwareLabel,
            DecodeMode = decodeMode,
            ToneMap = toneMap,
            Filters = vf,
            PixelFormat = pixelFormat,
            SoftFallback = forceSoftware,
            Summary = string.Join(" · ", summaryParts),
            Args = sb.ToString()
        };
    }

    /// <summary>Kept for callers/tests that only need the argument string.</summary>
    public string BuildArgs(
        string inputPath,
        string outputPath,
        EncodeAction action,
        PluginConfiguration config,
        string? videoRange = null,
        bool forceSoftware = false)
    {
        return BuildEncodePlan(inputPath, outputPath, action, config, videoRange, forceSoftware).Args;
    }

    private string BuildVideoFilter(PluginConfiguration config, string? videoRange)
    {
        var filters = new List<string>();
        // Tone-map only for known HDR/DV. Never invent tonemap for unknown/SDR — that forced
        // soft filters and previously knocked QSV down to libx264 (CPU).
        if (config.ToneMapHdr && IsHdrRange(videoRange))
        {
            filters.Add("zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv420p");
        }

        if (config.ResolutionCap > 0)
        {
            filters.Add($"scale=-2:'min(ih,{config.ResolutionCap})'");
        }

        return string.Join(',', filters);
    }

    private static bool IsHdrRange(string? videoRange)
    {
        if (string.IsNullOrWhiteSpace(videoRange))
        {
            return false;
        }

        return videoRange.Contains("HDR", StringComparison.OrdinalIgnoreCase)
            || videoRange.Contains("DOVI", StringComparison.OrdinalIgnoreCase)
            || videoRange.Contains("Dolby", StringComparison.OrdinalIgnoreCase)
            || videoRange.Contains("HLG", StringComparison.OrdinalIgnoreCase);
    }

    private (string Encoder, string Extra) ResolveVideoEncoder(
        string destCodec,
        PluginConfiguration config,
        bool hasSoftwareFilters,
        bool forceSoftware)
    {
        var hevc = destCodec.Equals("hevc", StringComparison.OrdinalIgnoreCase);
        var extra = string.Empty;

        if (forceSoftware)
        {
            return (hevc ? "libx265" : "libx264", extra);
        }

        var hw = ResolveHardware(config);

        // VAAPI + soft filters still needs CPU encode (hwupload after tonemap is brittle).
        // QSV: keep h264_qsv/hevc_qsv — soft decode + filters ending in format=nv12 works on Synology.
        if (hasSoftwareFilters && hw == HardwareAccelerationType.vaapi)
        {
            _logger.LogInformation("StreamReady using software encode because VAAPI + tone-map/scale filters are unreliable together");
            return (hevc ? "libx265" : "libx264", extra);
        }

        switch (hw)
        {
            case HardwareAccelerationType.nvenc:
                return (hevc ? "hevc_nvenc" : "h264_nvenc", extra);
            case HardwareAccelerationType.qsv:
                return (hevc ? "hevc_qsv" : "h264_qsv", extra);
            case HardwareAccelerationType.vaapi:
                extra = "-vaapi_device /dev/dri/renderD128 ";
                return (hevc ? "hevc_vaapi" : "h264_vaapi", extra);
            case HardwareAccelerationType.amf:
                return (hevc ? "hevc_amf" : "h264_amf", extra);
            case HardwareAccelerationType.videotoolbox:
                return (hevc ? "hevc_videotoolbox" : "h264_videotoolbox", extra);
            default:
                return (hevc ? "libx265" : "libx264", extra);
        }
    }

    private static string? HwaccelMethod(HardwareAccelerationType hw)
    {
        return hw switch
        {
            HardwareAccelerationType.nvenc => "cuda",
            HardwareAccelerationType.qsv => "qsv",
            HardwareAccelerationType.vaapi => "vaapi",
            HardwareAccelerationType.videotoolbox => "videotoolbox",
            HardwareAccelerationType.amf => "d3d11va",
            _ => null
        };
    }

    public HardwareAccelerationType ResolveHardware(PluginConfiguration config)
    {
        var choice = config.HardwareAccel ?? "FollowServer";
        if (choice.Equals("Software", StringComparison.OrdinalIgnoreCase))
        {
            return HardwareAccelerationType.none;
        }

        EncodingOptions? options = null;
        try
        {
            options = _serverConfig.GetEncodingOptions();
        }
        catch
        {
            options = null;
        }

        HardwareAccelerationType requested;
        if (choice.Equals("FollowServer", StringComparison.OrdinalIgnoreCase)
            || choice.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            if (options is not null
                && options.EnableHardwareEncoding
                && options.HardwareAccelerationType != HardwareAccelerationType.none
                && EncoderSupports(options.HardwareAccelerationType))
            {
                requested = options.HardwareAccelerationType;
            }
            else
            {
                requested = DetectBestHardware();
            }
        }
        else if (Enum.TryParse(choice, true, out HardwareAccelerationType parsed))
        {
            requested = parsed;
        }
        else
        {
            requested = DetectBestHardware();
        }

        if (requested != HardwareAccelerationType.none && !EncoderSupports(requested))
        {
            _logger.LogWarning("StreamReady requested {Hw} but ffmpeg lacks that encoder; auto-detecting", requested);
            requested = DetectBestHardware();
        }

        return requested;
    }

    private HardwareAccelerationType DetectBestHardware()
    {
        // Prefer hwaccel methods Jellyfin already enumerated (Synology/sc-ffmpeg7: qsv + vaapi).
        try
        {
            if (_mediaEncoder.SupportsHwaccel("qsv")
                || _mediaEncoder.SupportsEncoder("h264_qsv")
                || _mediaEncoder.SupportsEncoder("hevc_qsv"))
            {
                return HardwareAccelerationType.qsv;
            }

            if (_mediaEncoder.SupportsHwaccel("cuda")
                || _mediaEncoder.SupportsEncoder("h264_nvenc")
                || _mediaEncoder.SupportsEncoder("hevc_nvenc"))
            {
                return HardwareAccelerationType.nvenc;
            }

            if (_mediaEncoder.SupportsEncoder("h264_amf") || _mediaEncoder.SupportsEncoder("hevc_amf"))
            {
                return HardwareAccelerationType.amf;
            }

            if (_mediaEncoder.SupportsHwaccel("videotoolbox")
                || _mediaEncoder.SupportsEncoder("h264_videotoolbox"))
            {
                return HardwareAccelerationType.videotoolbox;
            }

            if (_mediaEncoder.SupportsHwaccel("vaapi")
                || _mediaEncoder.SupportsEncoder("h264_vaapi")
                || _mediaEncoder.SupportsEncoder("hevc_vaapi"))
            {
                return HardwareAccelerationType.vaapi;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StreamReady hardware detection failed");
        }

        return HardwareAccelerationType.none;
    }

    private bool EncoderSupports(HardwareAccelerationType hw)
    {
        try
        {
            // Synology/sc-ffmpeg7 often lists hwaccel (qsv/vaapi) even when SupportsEncoder is flaky —
            // treat either as usable, same as DetectBestHardware.
            return hw switch
            {
                HardwareAccelerationType.qsv =>
                    _mediaEncoder.SupportsHwaccel("qsv")
                    || _mediaEncoder.SupportsEncoder("h264_qsv")
                    || _mediaEncoder.SupportsEncoder("hevc_qsv"),
                HardwareAccelerationType.nvenc =>
                    _mediaEncoder.SupportsHwaccel("cuda")
                    || _mediaEncoder.SupportsEncoder("h264_nvenc")
                    || _mediaEncoder.SupportsEncoder("hevc_nvenc"),
                HardwareAccelerationType.vaapi =>
                    _mediaEncoder.SupportsHwaccel("vaapi")
                    || _mediaEncoder.SupportsEncoder("h264_vaapi")
                    || _mediaEncoder.SupportsEncoder("hevc_vaapi"),
                HardwareAccelerationType.amf =>
                    _mediaEncoder.SupportsEncoder("h264_amf") || _mediaEncoder.SupportsEncoder("hevc_amf"),
                HardwareAccelerationType.videotoolbox =>
                    _mediaEncoder.SupportsHwaccel("videotoolbox")
                    || _mediaEncoder.SupportsEncoder("h264_videotoolbox")
                    || _mediaEncoder.SupportsEncoder("hevc_videotoolbox"),
                _ => false
            };
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Pre-Transcode-style snapshot for the UI: path + version + hwaccel names Jellyfin already knows.
    /// </summary>
    public object GetCapabilitiesSnapshot(PluginConfiguration? config)
    {
        var hwaccels = new List<string>();
        try
        {
            foreach (var name in new[] { "qsv", "vaapi", "cuda", "nvenc", "opencl", "vulkan", "d3d11va", "videotoolbox", "drm" })
            {
                if (_mediaEncoder.SupportsHwaccel(name))
                {
                    hwaccels.Add(name);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "StreamReady hwaccel enumeration failed");
        }

        return new
        {
            FfmpegPath = EncoderPath,
            FfmpegVersion = EncoderVersion ?? string.Empty,
            FfmpegReady = IsReady,
            HardwareAccel = DescribeHardware(config),
            HardwareAccelerators = hwaccels
        };
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        string fileName,
        string arguments,
        Action<string>? onStdErrLine,
        Action<string>? onStdOutLine,
        CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo.FileName = fileName;
        process.StartInfo.Arguments = arguments;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.EnableRaisingEvents = true;

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            stdout.AppendLine(e.Data);
            onStdOutLine?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            stderr.AppendLine(e.Data);
            onStdErrLine?.Invoke(e.Data);
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start " + fileName);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        return (process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // ignored
        }
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }
}
