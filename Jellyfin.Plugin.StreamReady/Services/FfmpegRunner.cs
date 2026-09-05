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
        CancellationToken cancellationToken)
    {
        var ffmpeg = EncoderPath;
        if (!IsReady)
        {
            throw new InvalidOperationException("FFmpeg is not available. Check Dashboard → Playback → Transcoding.");
        }

        var args = BuildArgs(inputPath, outputPath, action, config);
        _logger.LogInformation("StreamReady ffmpeg ({Hw}): {Bin} {Args}", DescribeHardware(config), ffmpeg, args);

        var (exit, _, stderr) = await RunProcessAsync(
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

        if (exit != 0)
        {
            var tail = stderr.Length > 2000 ? stderr[^2000..] : stderr;
            throw new InvalidOperationException($"FFmpeg exited with code {exit}. {tail}");
        }
    }

    public string BuildArgs(string inputPath, string outputPath, EncodeAction action, PluginConfiguration config)
    {
        var sb = new StringBuilder();
        sb.Append("-y -hide_banner -loglevel error -stats ");

        var hw = action == EncodeAction.Full ? ResolveHardware(config) : HardwareAccelerationType.none;
        var hwaccel = HwaccelMethod(hw);
        // Decode on GPU when encoding on GPU. Omit -hwaccel_output_format so software filters
        // (tone-map / scale) still work — same approach as jellyfin-plugin-pre-transcode.
        if (!string.IsNullOrEmpty(hwaccel))
        {
            sb.Append("-hwaccel ").Append(hwaccel).Append(' ');
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

        var destCodec = EncodePlanner.DestinationVideoCodec(config);
        var channels = EncodePlanner.DestinationAudioChannels(config);
        var crf = EncodePlanner.DestinationCrf(config);
        var audioBitrate = channels <= 2 ? "192k" : "384k";
        var preset = string.IsNullOrWhiteSpace(config.FfmpegPreset) ? "medium" : config.FfmpegPreset;
        var vf = action == EncodeAction.Full ? BuildVideoFilter(config) : string.Empty;

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
                // VAAPI encode + software tone-map is fragile; prefer QSV/NVENC or fall back to software.
                var (videoEncoder, extra) = ResolveVideoEncoder(destCodec, config, !string.IsNullOrEmpty(vf));
                if (!string.IsNullOrEmpty(vf))
                {
                    if (videoEncoder.Contains("vaapi", StringComparison.OrdinalIgnoreCase))
                    {
                        vf += ",format=nv12,hwupload";
                    }

                    sb.Append("-vf ").Append(Quote(vf)).Append(' ');
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
                    sb.Append("-global_quality ").Append(crf).Append(' ');
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

    private string BuildVideoFilter(PluginConfiguration config)
    {
        var filters = new List<string>();
        if (config.ToneMapHdr)
        {
            filters.Add("zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv420p");
        }

        if (config.ResolutionCap > 0)
        {
            filters.Add($"scale=-2:'min(ih,{config.ResolutionCap})'");
        }

        return string.Join(',', filters);
    }

    private (string Encoder, string Extra) ResolveVideoEncoder(string destCodec, PluginConfiguration config, bool hasSoftwareFilters)
    {
        var hw = ResolveHardware(config);
        var hevc = destCodec.Equals("hevc", StringComparison.OrdinalIgnoreCase);
        var extra = string.Empty;

        // Soft filters + VAAPI encode often fails; fall back to software encode (decode can still be HW).
        if (hw == HardwareAccelerationType.vaapi && hasSoftwareFilters)
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
