using System.Collections.Concurrent;
using Jellyfin.Plugin.StreamReady.Configuration;
using Jellyfin.Plugin.StreamReady.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StreamReady.Services;

public class EncodeWorker : BackgroundService
{
    private readonly JobStore _store;
    private readonly LibraryScanner _scanner;
    private readonly FfmpegRunner _ffmpeg;
    private readonly ReplacementService _replacement;
    private readonly ILibraryManager _libraryManager;
    private readonly ISessionManager _sessionManager;
    private readonly ILogger<EncodeWorker> _logger;
    private readonly object _pauseGate = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _settleTimers = new();
    private readonly Dictionary<string, Attempt> _attempts = new();
    private DateTime _lastScanUtc = DateTime.MinValue;
    private CancellationToken _lifetimeToken;

    private sealed class Attempt(EncodeJob job, CancellationTokenSource cancellation)
    {
        public EncodeJob Job { get; } = job;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task Task { get; set; } = Task.CompletedTask;
        public bool Requeue { get; set; }
        public bool Committing { get; set; }
    }

    public EncodeWorker(
        JobStore store,
        LibraryScanner scanner,
        FfmpegRunner ffmpeg,
        ReplacementService replacement,
        ILibraryManager libraryManager,
        ISessionManager sessionManager,
        ILogger<EncodeWorker> logger)
    {
        _store = store;
        _scanner = scanner;
        _ffmpeg = ffmpeg;
        _replacement = replacement;
        _libraryManager = libraryManager;
        _sessionManager = sessionManager;
        _logger = logger;
    }

    public bool IsPaused { get; private set; }

    public IReadOnlyList<EncodeJob> ActiveJobs
    {
        get
        {
            lock (_pauseGate)
            {
                return _attempts.Keys.Select(id => _store.GetJob(id))
                    .OfType<EncodeJob>().ToArray();
            }
        }
    }

    public EncodeJob? CurrentJob => ActiveJobs.FirstOrDefault();

    public void Pause()
    {
        lock (_pauseGate)
        {
            PersistWorkerPaused(true);
            IsPaused = true;
            foreach (var attempt in _attempts.Values.Where(a => !a.Committing && !a.Cancellation.IsCancellationRequested))
            {
                attempt.Requeue = true;
                attempt.Cancellation.Cancel();
            }
        }
    }

    public void Resume()
    {
        lock (_pauseGate)
        {
            PersistWorkerPaused(false);
            IsPaused = false;
        }
    }

    public bool Cancel(string id)
    {
        lock (_pauseGate)
        {
            if (_attempts.TryGetValue(id, out var attempt))
            {
                if (attempt.Committing)
                {
                    return false;
                }

                attempt.Requeue = false;
                attempt.Cancellation.Cancel();
                return true;
            }

            return _store.Cancel(id);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _lifetimeToken = stoppingToken;
        IsPaused = Plugin.Instance?.Configuration.WorkerPaused ?? false;
        if (_store.IsHealthy)
        {
            await _replacement.RecoverAsync(stoppingToken).ConfigureAwait(false);
        }
        _libraryManager.ItemAdded += OnItemChanged;
        _libraryManager.ItemUpdated += OnItemChanged;
        _libraryManager.ItemRemoved += OnItemRemoved;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await TickAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "StreamReady worker loop error");
                }

                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _libraryManager.ItemAdded -= OnItemChanged;
            _libraryManager.ItemUpdated -= OnItemChanged;
            _libraryManager.ItemRemoved -= OnItemRemoved;
            foreach (var cts in _settleTimers.Values)
            {
                try
                {
                    cts.Cancel();
                    cts.Dispose();
                }
                catch
                {
                    // ignored
                }
            }

            _settleTimers.Clear();
            Task[] attempts;
            lock (_pauseGate)
            {
                attempts = _attempts.Values.Select(a => a.Task).ToArray();
            }

            await Task.WhenAll(attempts).ConfigureAwait(false);
        }
    }

    private async Task TickAsync(CancellationToken stoppingToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.Enabled || !_store.IsHealthy || _replacement.PendingOperation is not null)
        {
            return;
        }

        if (config.ScanIntervalHours > 0 &&
            DateTime.UtcNow - _lastScanUtc > TimeSpan.FromHours(config.ScanIntervalHours) &&
            !string.IsNullOrWhiteSpace(config.SelectedLibraryIds))
        {
            _lastScanUtc = DateTime.UtcNow;
            await _scanner.ScanAsync(null, stoppingToken).ConfigureAwait(false);
        }

        if (IsPaused)
        {
            return;
        }

        if (config.PauseDuringPlayback && HasActivePlayback())
        {
            return;
        }

        if (!EncodeWindow.IsOpen(config))
        {
            return;
        }

        lock (_pauseGate)
        {
            var max = Math.Clamp(config.MaxConcurrentJobs, 1, 2);
            if (IsPaused || _attempts.Count >= max)
            {
                return;
            }

            var job = _store.DequeueNext();
            if (job is null)
            {
                return;
            }

            var attempt = new Attempt(job, CancellationTokenSource.CreateLinkedTokenSource(stoppingToken));
            _attempts.Add(job.Id, attempt);
            attempt.Task = Task.Run(() => ProcessJobAsync(attempt, stoppingToken));
        }
    }

    private async Task ProcessJobAsync(Attempt attempt, CancellationToken stoppingToken)
    {
        var job = attempt.Job;
        using var jobCts = attempt.Cancellation;
        string? tempPath = null;
        try
        {
            if (!Guid.TryParse(job.ItemId, out var itemId))
            {
                _store.Fail(job.Id, "Invalid item id");
                return;
            }

            var item = _libraryManager.GetItemById(itemId);
            if (item is null)
            {
                _store.Fail(job.Id, "Item no longer exists");
                return;
            }

            if (IsPlaying(item.Id))
            {
                _store.Requeue(job.Id, "Waiting for playback", TimeSpan.FromSeconds(30));
                return;
            }

            var sourcePath = item.Path;
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                _store.Fail(job.Id, "Source file is missing");
                return;
            }

            var config = Plugin.Instance!.Configuration;
            if (!double.IsFinite(config.MaxFileSizeGiB) || config.MaxFileSizeGiB < 0
                || !double.IsFinite(config.MaxVideoBitrateMbps) || config.MaxVideoBitrateMbps < 0
                || config.Crf is < 0 or > 51 || config.ResolutionCap < 0 || config.AudioChannels is not (0 or 2 or 6)
                || config.EncodingPreset is not ("Balanced" or "MaxCompatibility" or "HevcCompact"))
            {
                throw new InvalidOperationException("The encoding settings contain invalid limits or an unsupported preset.");
            }

            var library = _scanner.ResolveItemLibrary(item, config)
                ?? throw new InvalidOperationException("The item's library is no longer selected.");
            if (!string.Equals(job.Path, sourcePath,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                || (job.SourceWriteUtc is not null && job.SourceWriteUtc != File.GetLastWriteTimeUtc(sourcePath)))
            {
                throw new InvalidOperationException("The queued source changed. Scan again before enqueuing it.");
            }

            config = EncodePlanner.WithLibraryPreset(config, library.Id.ToString("N"));

            var sourceFile = new FileInfo(sourcePath);
            var sourceLength = sourceFile.Length;
            if (job.SourceSize is long expectedSize && expectedSize != sourceLength)
            {
                throw new InvalidOperationException("The queued source size changed. Scan again before enqueuing it.");
            }
            var sourceWrite = sourceFile.LastWriteTimeUtc;
            var sourceProbe = await _ffmpeg.ProbeMediaAsync(sourcePath, jobCts.Token).ConfigureAwait(false);
            if (!sourceProbe.IsValid)
            {
                throw new InvalidOperationException("Source media could not be verified by FFprobe.");
            }

            var subtitles = config.KeepAllAudioAndSubtitles
                ? sourceProbe.Streams.Where(s => s.Type == "subtitle").ToArray() : [];
            var container = EncodePlanner.SelectOutputContainer(config, subtitles);

            tempPath = sourcePath + "." + Guid.NewGuid().ToString("N") + ".streamready.tmp." + container;
            var duration = sourceProbe.Duration;
            var video = sourceProbe.Streams.First(s => s.Type == "video" && !s.AttachedPicture);
            var audio = FfmpegRunner.SelectAudio(sourceProbe, config.KeepAllAudioAndSubtitles);
            var fresh = _scanner.Analyze(item, config, library.Id, library.Name);
            if (!fresh.NeedsWork)
            {
                throw new InvalidOperationException("The source no longer requires encoding under the current profile.");
            }

            var action = fresh.PlannedAction;
            long? videoBudget = null;
            if (fresh.Reasons.Contains("Size"))
            {
                var audioOverhead = audio.Count * (EncodePlanner.DestinationAudioChannels(config) <= 2 ? 192_000 : 384_000);
                videoBudget = (long)(config.MaxFileSizeGiB * 1073741824d * 8 * 0.95 / duration) - audioOverhead;
                if (videoBudget < 100_000)
                {
                    throw new InvalidOperationException("The file-size cap leaves an infeasible video bitrate budget.");
                }
            }

            if (fresh.Reasons.Contains("Bitrate") || (config.MaxVideoBitrateMbps > 0 && action == EncodeAction.Full))
            {
                var cap = (long)(config.MaxVideoBitrateMbps * 950_000d);
                videoBudget = Math.Min(videoBudget ?? cap, cap);
            }
            if (!EncodePlanner.ContainerSupportsVideo(container, video.Codec))
            {
                action = EncodeAction.Full;
            }
            else if (action == EncodeAction.Remux && audio.Any(s => !EncodePlanner.ContainerSupportsAudio(container, s.Codec)))
            {
                action = EncodeAction.AudioOnly;
            }

            var progress = new Progress<EncodeProgressUpdate>(update =>
                _store.UpdateProgress(job.Id, Math.Min(95, update.Percent), update.Speed, update.Eta,
                    allowDecrease: update.Percent <= 1));
            void ApplyPlan(EncodePlan plan) => _store.UpdateEncodePlan(job.Id, plan);

            var videoRange = fresh.Candidate.VideoRange;
            if (string.IsNullOrWhiteSpace(videoRange)
                && job.Reasons.Any(r => r.Equals("VideoRange", StringComparison.OrdinalIgnoreCase)))
            {
                videoRange = "HDR";
            }

            var allowedVideo = CompatibilityAnalyzer.Split(config.AllowedVideoCodecs)
                .Select(CompatibilityAnalyzer.NormalizeCodec).ToArray();
            var allowedAudio = CompatibilityAnalyzer.Split(config.AllowedAudioCodecs)
                .Select(CompatibilityAnalyzer.NormalizeCodec).ToArray();
            var expectedVideo = action == EncodeAction.Full ? EncodePlanner.DestinationVideoCodec(config) : video.Codec;
            if ((allowedVideo.Length > 0 && !allowedVideo.Contains(expectedVideo))
                || (allowedAudio.Length > 0 && audio.Any(track => !allowedAudio.Contains(
                    action == EncodeAction.Remux ? track.Codec : "aac"))))
            {
                throw new InvalidOperationException("The selected output codecs conflict with the compatibility profile.");
            }

            if (action == EncodeAction.Full
                && (sourceProbe.ColorTransfer is "smpte2084" or "arib-std-b67"
                    || fresh.Reasons.Contains("VideoRange")))
            {
                var range = fresh.Candidate.VideoRange;
                if (range.Contains("DOVI", StringComparison.OrdinalIgnoreCase)
                    || range.Contains("Dolby", StringComparison.OrdinalIgnoreCase)
                    || !config.ToneMapHdr || CompatibilityAnalyzer.RangeAllowed(range, config.AllowedVideoRanges)
                    || !CompatibilityAnalyzer.RangeAllowed("SDR", config.AllowedVideoRanges))
                {
                    throw new InvalidOperationException("The requested HDR or Dolby Vision conversion could not be validated.");
                }
            }

            _store.UpdatePhase(job.Id, "Encoding");
            await _ffmpeg.EncodeAsync(
                    sourcePath,
                    tempPath,
                    action,
                    config,
                    duration,
                    progress,
                    jobCts.Token,
                    videoRange,
                    ApplyPlan,
                    sourceProbe,
                    videoBudget)
                .ConfigureAwait(false);

            jobCts.Token.ThrowIfCancellationRequested();
            _store.UpdatePhase(job.Id, "Verifying");

            {
                var probe = await _ffmpeg.ProbeMediaAsync(tempPath, jobCts.Token).ConfigureAwait(false);
                if (!File.Exists(tempPath) || probe.SizeBytes < 1024)
                {
                    TryDelete(tempPath);
                    _store.Fail(job.Id, "Output file is missing or too small");
                    return;
                }

                if (!probe.IsValid || probe.Streams.Count(s => s.Type == "audio") != audio.Count
                    || probe.Streams.Count(s => s.Type == "subtitle") != subtitles.Length)
                {
                    TryDelete(tempPath);
                    _store.Fail(
                        job.Id,
                        "Output duration or selected stream counts could not be verified",
                        $"probe size={probe.SizeBytes} duration={probe.Duration:0.###} hasAudio={probe.HasAudio}");
                    return;
                }

                var outputAudio = probe.Streams.Where(stream => stream.Type == "audio").ToArray();
                var outputSubs = probe.Streams.Where(stream => stream.Type == "subtitle").ToArray();
                static string? Language(ProbedStream stream) => stream.Language is null or "und" ? null : stream.Language;

                if (audio.Zip(outputAudio).Any(pair =>
                        pair.Second.Codec != (action == EncodeAction.Remux ? pair.First.Codec : "aac")
                        || Language(pair.First) != Language(pair.Second)
                        || (audio.Count > 1 && pair.First.Default != pair.Second.Default))
                    || subtitles.Zip(outputSubs).Any(pair =>
                        pair.Second.Codec != (container == "mp4" ? "mov_text" : pair.First.Codec)
                        || Language(pair.First) != Language(pair.Second) || pair.First.Forced != pair.Second.Forced))
                {
                    throw new InvalidOperationException("The encoded track metadata does not match the selected tracks.");
                }

                if (probe.VideoCodec != expectedVideo
                    || (action == EncodeAction.Full && probe.ColorTransfer is "smpte2084" or "arib-std-b67")
                    || (action == EncodeAction.Full && (sourceProbe.ColorTransfer is "smpte2084" or "arib-std-b67")
                        && probe.ColorTransfer != "bt709")
                    || (config.ResolutionCap > 0 && probe.Height > config.ResolutionCap)
                    || (config.MaxFileSizeGiB > 0 && probe.SizeBytes > config.MaxFileSizeGiB * 1073741824d)
                    || (config.MaxVideoBitrateMbps > 0 && (probe.VideoBitrate
                        ?? (long)Math.Ceiling(probe.SizeBytes * 8d / probe.Duration)) > config.MaxVideoBitrateMbps * 1_000_000d))
                {
                    throw new InvalidOperationException("The output does not satisfy the configured codec, resolution, size, or bitrate limits.");
                }

                if (probe.Width <= 0 || probe.Height <= 0)
                {
                    TryDelete(tempPath);
                    _store.Fail(
                        job.Id,
                        "Output video has invalid dimensions",
                        $"codec={probe.VideoCodec} {probe.Width}x{probe.Height}");
                    return;
                }

                if (duration > 0 && probe.Duration > 0)
                {
                    var delta = Math.Abs(probe.Duration - duration);
                    if (delta > Math.Max(2, duration * 0.02))
                    {
                        TryDelete(tempPath);
                        _store.Fail(
                            job.Id,
                            $"Duration mismatch (source {duration:0}s, output {probe.Duration:0}s)",
                            $"video={probe.VideoCodec} {probe.Width}x{probe.Height} size={probe.SizeBytes}");
                        return;
                    }
                }
            }
            if (!string.Equals(config.ReplacementPolicy, "Sidecar", StringComparison.OrdinalIgnoreCase))
            {
                await _ffmpeg.VerifyDecodeAsync(tempPath, duration, jobCts.Token).ConfigureAwait(false);
            }

            if (config.DiscardIfOutputLarger && File.Exists(tempPath) && File.Exists(sourcePath))
            {
                var outSize = new FileInfo(tempPath).Length;
                var inSize = new FileInfo(sourcePath).Length;
                if (outSize >= inSize)
                {
                    TryDelete(tempPath);
                    _store.Fail(
                        job.Id,
                        $"Output larger than source ({FormatSize(outSize)} >= {FormatSize(inSize)}); discarded");
                    return;
                }
            }

            if (IsPlaying(item.Id))
            {
                TryDelete(tempPath);
                _store.Requeue(job.Id, "Waiting for playback", TimeSpan.FromSeconds(30));
                return;
            }

            await _replacement.CommitAsync(item, job.Id, sourcePath, tempPath, config, () =>
            {
                lock (_pauseGate)
                {
                    jobCts.Token.ThrowIfCancellationRequested();
                    attempt.Committing = true;
                    _store.UpdatePhase(job.Id, "Committing");
                }

                var source = new FileInfo(sourcePath);
                if (Plugin.Instance?.Configuration is not { Enabled: true } currentConfig
                    || _scanner.ResolveItemLibrary(item, currentConfig)?.Id != library.Id
                    || IsPlaying(item.Id) || !source.Exists || source.Length != sourceLength
                    || source.LastWriteTimeUtc != sourceWrite)
                {
                    throw new InvalidOperationException("The source changed or playback began before promotion.");
                }
            }, replacement => _store.Complete(job.Id, job.ItemId,
                replacement.Policy == "Sidecar" ? sourcePath : replacement.FinalPath,
                replacement.Policy == "Sidecar" ? sourceLength : replacement.FinalSize!.Value,
                replacement), jobCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (attempt.Requeue || stoppingToken.IsCancellationRequested)
            {
                _store.Requeue(job.Id, "Interrupted before promotion");
            }
            else
            {
                _store.Cancel(job.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StreamReady encode failed for {Name}", job.Name);
            var shortMsg = ex.Message;
            if (shortMsg.Length > 280)
            {
                shortMsg = shortMsg[..280] + "…";
            }

            if (_store.IsHealthy)
            {
                _store.Fail(job.Id, shortMsg, TruncateDetail(ex.Message, 8000));
            }
        }
        finally
        {
            try
            {
                if (tempPath is not null && _replacement.PendingOperation is null)
                {
                    TryDelete(tempPath);
                }
            }
            finally
            {
                lock (_pauseGate)
                {
                    _attempts.Remove(job.Id);
                }
            }
        }
    }

    private void OnItemRemoved(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is Video && _store.IsHealthy)
        {
            try
            {
                _store.RemoveCandidate(e.Item.Id.ToString("N"));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not remove a deleted StreamReady candidate");
            }
        }
    }

    private void OnItemChanged(object? sender, ItemChangeEventArgs e)
    {
        try
        {
            if (e.Item is not Video video)
            {
                return;
            }

            var delay = Plugin.Instance?.Configuration.ItemSettleDelaySeconds ?? 120;
            if (delay <= 0)
            {
                _scanner.AnalyzeItem(video);
                return;
            }

            var id = video.Id;
            if (_settleTimers.TryRemove(id, out var old))
            {
                try
                {
                    old.Cancel();
                    old.Dispose();
                }
                catch
                {
                    // ignored
                }
            }

            var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
            var token = cts.Token;
            _settleTimers[id] = cts;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(delay), token).ConfigureAwait(false);
                    if (cts.IsCancellationRequested)
                    {
                        return;
                    }

                    var latest = _libraryManager.GetItemById(id);
                    if (latest is Video)
                    {
                        _scanner.AnalyzeItem(latest);
                    }
                }
                catch (OperationCanceledException)
                {
                    // superseded
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "StreamReady settle analysis failed");
                }
                finally
                {
                    _settleTimers.TryRemove(new KeyValuePair<Guid, CancellationTokenSource>(id, cts));
                    cts.Dispose();
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "StreamReady item-change analysis failed");
        }
    }

    private static void PersistWorkerPaused(bool paused)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return;
        }

        plugin.Configuration.WorkerPaused = paused;
        plugin.SaveConfiguration();
    }

    private bool HasActivePlayback()
    {
        return _sessionManager.Sessions.Any(s => s.NowPlayingItem is not null);
    }

    private bool IsPlaying(Guid itemId)
    {
        return _sessionManager.Sessions.Any(s => s.NowPlayingItem?.Id == itemId);
    }

    private static string TruncateDetail(string value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max)
        {
            return value;
        }

        return value[^max..];
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024L * 1024L)
        {
            return $"{bytes / (1024d * 1024d * 1024d):0.0} GiB";
        }

        if (bytes >= 1024L * 1024L)
        {
            return $"{bytes / (1024d * 1024d):0} MiB";
        }

        return $"{bytes} B";
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignored
        }
    }
}
