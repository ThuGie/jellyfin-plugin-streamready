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

            return string.Empty;
        }
    }

    public string? EncoderVersion
    {
        get
        {
            try
            {
                var version = _mediaEncoder.EncoderVersion;
                return version is null ? null : version.ToString();
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
                if (_mediaEncoder.EncoderVersion is not null && _mediaEncoder.EncoderVersion.Major > 0)
                {
                    return true;
                }
            }
            catch
            {
                // ignored
            }

            var path = EncoderPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            // Absolute path that exists, or a bare command Jellyfin resolved on PATH
            // (File.Exists("ffmpeg") is false even when Process.Start works).
            return File.Exists(path) || path.IndexOf(Path.DirectorySeparatorChar) < 0;
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

            var encoder = _mediaEncoder.EncoderPath;
            if (string.IsNullOrWhiteSpace(encoder))
            {
                return string.Empty;
            }

            var file = Path.GetFileName(encoder);
            var probe = file.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase)
                ? file.Replace("ffmpeg", "ffprobe", StringComparison.OrdinalIgnoreCase)
                : "ffprobe";
            var dir = Path.GetDirectoryName(encoder);
            return string.IsNullOrEmpty(dir) ? probe : Path.Combine(dir, probe);
        }
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
        if (string.IsNullOrWhiteSpace(ffmpeg) || !IsReady)
        {
            throw new InvalidOperationException("FFmpeg path is not configured in Jellyfin.");
        }

        var args = BuildArgs(inputPath, outputPath, action, config);
        _logger.LogInformation("StreamReady ffmpeg: {Args}", args);

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
                var vf = BuildVideoFilter(config);
                if (!string.IsNullOrEmpty(vf))
                {
                    sb.Append("-vf ").Append(Quote(vf)).Append(' ');
                }

                var (videoEncoder, extra) = ResolveVideoEncoder(destCodec, config);
                sb.Append("-c:v ").Append(videoEncoder).Append(' ').Append(extra);
                if (videoEncoder is "libx264" or "libx265")
                {
                    sb.Append("-crf ").Append(crf).Append(" -preset ").Append(preset).Append(' ');
                }
                else if (videoEncoder.Contains("nvenc", StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append("-cq ").Append(Math.Max(crf, 16)).Append(" -preset p4 ");
                }
                else if (videoEncoder.Contains("qsv", StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append("-global_quality ").Append(crf).Append(' ');
                }
                else if (videoEncoder.Contains("vaapi", StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append("-qp ").Append(crf).Append(' ');
                }

                sb.Append("-pix_fmt yuv420p -c:a aac -b:a ").Append(audioBitrate)
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

    private (string Encoder, string Extra) ResolveVideoEncoder(string destCodec, PluginConfiguration config)
    {
        var hw = ResolveHardware(config);
        var hevc = destCodec.Equals("hevc", StringComparison.OrdinalIgnoreCase);
        var extra = string.Empty;

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

    private HardwareAccelerationType ResolveHardware(PluginConfiguration config)
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

        if (choice.Equals("FollowServer", StringComparison.OrdinalIgnoreCase))
        {
            if (options is null || !options.EnableHardwareEncoding)
            {
                return HardwareAccelerationType.none;
            }

            return options.HardwareAccelerationType;
        }

        return Enum.TryParse(choice, true, out HardwareAccelerationType parsed)
            ? parsed
            : HardwareAccelerationType.none;
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
