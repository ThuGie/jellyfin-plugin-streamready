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
        IProgress<double> progress,
        CancellationToken cancellationToken,
        string? videoRange = null)
    {
        var ffmpeg = EncoderPath;
        if (!IsReady)
        {
            throw new InvalidOperationException("FFmpeg is not available. Check Dashboard → Playback → Transcoding.");
        }

        var args = BuildArgs(inputPath, outputPath, action, config, videoRange, forceSoftware: false);
        _logger.LogInformation("StreamReady ffmpeg ({Hw}): {Bin} {Args}", DescribeHardware(config), ffmpeg, args);

        var (exit, _, stderr) = await RunEncodeAsync(ffmpeg, args, durationSeconds, progress, cancellationToken)
            .ConfigureAwait(false);

        if (exit == 0)
        {
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
            var softArgs = BuildArgs(inputPath, outputPath, action, config, videoRange, forceSoftware: true);
            _logger.LogInformation("StreamReady ffmpeg (software fallback): {Bin} {Args}", ffmpeg, softArgs);
            (exit, _, stderr) = await RunEncodeAsync(ffmpeg, softArgs, durationSeconds, progress, cancellationToken)
                .ConfigureAwait(false);
            if (exit == 0)
            {
                return;
            }
        }

        throw new InvalidOperationException($"FFmpeg exited with code {exit}. {Truncate(stderr, 2000)}");
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunEncodeAsync(
        string ffmpeg,
        string args,
        double durationSeconds,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        return await RunProcessAsync(
            ffmpeg,
            args,
            line =>
            {
                var match = TimeRegex.Match(line);
                if (!match.Success || durationSeconds <= 0)
                {
                    return;
                }

                var hours = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                var minutes = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                var seconds = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
                var current = (hours * 3600) + (minutes * 60) + seconds;
                progress.Report(Math.Clamp(current / durationSeconds * 100d, 0, 99.5));
            },
            null,
            cancellationToken).ConfigureAwait(false);
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

    public string BuildArgs(
        string inputPath,
        string outputPath,
        EncodeAction action,
        PluginConfiguration config,
        string? videoRange = null,
        bool forceSoftware = false)
    {
        var sb = new StringBuilder();
        sb.Append("-y -hide_banner -loglevel error -stats ");

        var hw = forceSoftware || action != EncodeAction.Full
            ? HardwareAccelerationType.none
            : ResolveHardware(config);
        var vf = action == EncodeAction.Full ? BuildVideoFilter(config, videoRange) : string.Empty;
        var hasSoftwareFilters = !string.IsNullOrEmpty(vf);

        // Soft filters need system-memory frames. Keep -hwaccel only when encoding on GPU
        // without a soft filter graph (QSV/VAAPI + zscale/tonemap is unreliable on Synology).
        var (videoEncoder, extra) = action == EncodeAction.Full
            ? ResolveVideoEncoder(EncodePlanner.DestinationVideoCodec(config), config, hasSoftwareFilters, forceSoftware)
            : ("", "");

        var usingHwEncode = !forceSoftware
            && action == EncodeAction.Full
            && videoEncoder.Contains('_')
            && !videoEncoder.StartsWith("lib", StringComparison.OrdinalIgnoreCase);

        // NVENC/VAAPI can use -hwaccel for decode. QSV + Synology sc-ffmpeg7 often breaks
        // when -hwaccel qsv feeds soft format=nv12; prefer software decode → nv12 → h264_qsv.
        if (usingHwEncode
            && !hasSoftwareFilters
            && !videoEncoder.Contains("qsv", StringComparison.OrdinalIgnoreCase))
        {
            var hwaccel = HwaccelMethod(hw);
            if (!string.IsNullOrEmpty(hwaccel))
            {
                sb.Append("-hwaccel ").Append(hwaccel).Append(' ');
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
                        // Soft filters produce yuv420p; QSV needs nv12 without a full hwupload graph.
                        vf += ",format=nv12";
                    }

                    sb.Append("-vf ").Append(Quote(vf)).Append(' ');
                }
                else if (videoEncoder.Contains("qsv", StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append("-vf format=nv12 ");
                }
                else if (videoEncoder.Contains("vaapi", StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append("-vf format=nv12,hwupload ");
                }

                sb.Append("-c:v ").Append(videoEncoder).Append(' ').Append(extra);
                if (videoEncoder is "libx264" or "libx265")
                {
                    sb.Append("-crf ").Append(crf).Append(" -preset ").Append(preset).Append(' ');
                    sb.Append("-pix_fmt yuv420p ");
                }
                else if (videoEncoder.Contains("nvenc", StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append("-cq ").Append(Math.Max(crf, 16)).Append(" -preset p4 -pix_fmt yuv420p ");
                }
                else if (videoEncoder.Contains("qsv", StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append("-global_quality ").Append(crf).Append(" -pix_fmt nv12 ");
                }
                else if (videoEncoder.Contains("vaapi", StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append("-qp ").Append(crf).Append(' ');
                }
                else
                {
                    sb.Append("-pix_fmt yuv420p ");
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
        return sb.ToString();
    }

    private string BuildVideoFilter(PluginConfiguration config, string? videoRange)
    {
        var filters = new List<string>();
        // Only tone-map when the source is actually HDR (Pre-Transcode pattern).
        if (config.ToneMapHdr && IsHdrRange(videoRange))
        {
            filters.Add("zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv420p");
        }
        else if (config.ToneMapHdr && string.IsNullOrWhiteSpace(videoRange))
        {
            // Unknown range + tone-map enabled: keep previous behavior for safety on HDR files.
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

        // Soft filters (zscale/tonemap/scale) + QSV/VAAPI encode fail with auto_scale/ENOSYS
        // on many Intel/Synology builds. Prefer CPU encode; optional one-shot HW retry still exists.
        if (hasSoftwareFilters
            && (hw == HardwareAccelerationType.vaapi || hw == HardwareAccelerationType.qsv))
        {
            _logger.LogInformation(
                "StreamReady using software encode because {Hw} + tone-map/scale filters are unreliable together",
                hw);
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
