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
    private CancellationTokenSource? _jobCts;
    private DateTime _lastScanUtc = DateTime.MinValue;
    private string? _pauseRequeueJobId;

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

    public EncodeJob? CurrentJob { get; private set; }

    public void Pause()
    {
        IsPaused = true;
        PersistWorkerPaused(true);
        string? runningId;
        lock (_pauseGate)
        {
            runningId = CurrentJob?.Id;
            _pauseRequeueJobId = runningId;
            _jobCts?.Cancel();
        }

        if (!string.IsNullOrEmpty(runningId))
        {
            _store.Requeue(runningId, "Paused by admin");
        }
    }

    public void Resume()
    {
        IsPaused = false;
        PersistWorkerPaused(false);
        _pauseRequeueJobId = null;
    }

    public void CancelCurrent()
    {
        lock (_pauseGate)
        {
            _jobCts?.Cancel();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IsPaused = Plugin.Instance?.Configuration.WorkerPaused ?? false;
        _libraryManager.ItemAdded += OnItemChanged;
        _libraryManager.ItemUpdated += OnItemChanged;
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
        }
    }

    private async Task TickAsync(CancellationToken stoppingToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.Enabled)
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

        var max = Math.Clamp(config.MaxConcurrentJobs, 1, 2);
        if (_store.RunningCount() >= max)
        {
            return;
        }

        var job = _store.DequeueNext();
        if (job is null)
        {
            return;
        }

        _ = ProcessJobAsync(job, stoppingToken);
    }

    private async Task ProcessJobAsync(EncodeJob job, CancellationToken stoppingToken)
    {
        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        lock (_pauseGate)
        {
            _jobCts = jobCts;
            CurrentJob = job;
        }

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
                _store.Fail(job.Id, "Skipped because the item is currently playing");
                _store.Requeue(job.Id);
                return;
            }

            var sourcePath = item.Path;
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                _store.Fail(job.Id, "Source file is missing");
                return;
            }

            var config = Plugin.Instance!.Configuration;
            var folders = _libraryManager.GetCollectionFolders(item);
            var libraryId = folders.FirstOrDefault()?.Id.ToString("N") ?? string.Empty;
            config = EncodePlanner.WithLibraryPreset(config, libraryId);

            var destExt = "." + EncodePlanner.DestinationContainer(config);
            var tempPath = sourcePath + ".streamready.tmp" + destExt;
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            var duration = item.RunTimeTicks.HasValue
                ? TimeSpan.FromTicks(item.RunTimeTicks.Value).TotalSeconds
                : await _ffmpeg.ProbeDurationAsync(sourcePath, jobCts.Token).ConfigureAwait(false);

            var progress = new Progress<EncodeProgressUpdate>(update =>
            {
                _store.UpdateProgress(
                    job.Id,
                    update.Percent,
                    update.Speed,
                    update.Eta,
                    allowDecrease: update.Percent <= 1);
                if (CurrentJob?.Id != job.Id)
                {
                    return;
                }

                if (update.Percent <= 1 || update.Percent >= CurrentJob.Progress)
                {
                    CurrentJob.Progress = update.Percent;
                }

                if (update.Speed is not null)
                {
                    CurrentJob.Speed = update.Speed;
                }

                if (update.Eta is not null || update.Percent <= 1 || update.Percent >= 100)
                {
                    CurrentJob.Eta = update.Eta;
                }
            });

            void ApplyPlan(EncodePlan plan)
            {
                _store.UpdateEncodePlan(job.Id, plan);
                if (CurrentJob?.Id != job.Id)
                {
                    return;
                }

                CurrentJob.StatusDetail = plan.SoftFallback ? "[software fallback] " + plan.Summary : plan.Summary;
                CurrentJob.VideoEncoder = plan.VideoEncoder;
                CurrentJob.HardwarePath = plan.HardwareLabel + " · decode " + plan.DecodeMode;
                CurrentJob.ToneMap = plan.ToneMap;
                CurrentJob.Filters = plan.Filters;
            }

            var videoRange = job.VideoRange;
            if (string.IsNullOrWhiteSpace(videoRange)
                && job.Reasons.Any(r => r.Equals("VideoRange", StringComparison.OrdinalIgnoreCase)))
            {
                videoRange = "HDR";
            }

            await _ffmpeg.EncodeAsync(
                    sourcePath,
                    tempPath,
                    job.Action,
                    config,
                    duration,
                    progress,
                    jobCts.Token,
                    videoRange,
                    ApplyPlan)
                .ConfigureAwait(false);

            if (_store.IsCancelled(job.Id) || jobCts.IsCancellationRequested)
            {
                TryDelete(tempPath);
                if (_pauseRequeueJobId == job.Id)
                {
                    _pauseRequeueJobId = null;
                }

                return;
            }

            if (config.VerifyBeforeReplace)
            {
                var probe = await _ffmpeg.ProbeMediaAsync(tempPath, jobCts.Token).ConfigureAwait(false);
                if (!File.Exists(tempPath) || probe.SizeBytes < 1024)
                {
                    TryDelete(tempPath);
                    _store.Fail(job.Id, "Output file is missing or too small");
                    return;
                }

                if (!probe.HasVideo)
                {
                    TryDelete(tempPath);
                    _store.Fail(
                        job.Id,
                        "Output has no video stream",
                        $"probe size={probe.SizeBytes} duration={probe.Duration:0.###} hasAudio={probe.HasAudio}");
                    return;
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
            else if (!File.Exists(tempPath) || new FileInfo(tempPath).Length < 1024)
            {
                TryDelete(tempPath);
                _store.Fail(job.Id, "Output file is missing or too small");
                return;
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
                _store.Fail(job.Id, "Playback started during encode; file was not replaced");
                _store.Requeue(job.Id);
                return;
            }

            var commit = await _replacement.CommitAsync(item, sourcePath, tempPath, config, jobCts.Token)
                .ConfigureAwait(false);
            var finalPath = commit.FinalPath;
            var finalSize = File.Exists(finalPath) ? new FileInfo(finalPath).Length : 0;
            var replacement = new ReplacementRecord
            {
                ItemId = job.ItemId,
                Name = job.Name,
                OriginalPath = commit.OriginalPath,
                FinalPath = commit.FinalPath,
                BackupPath = commit.BackupPath,
                Policy = commit.Policy,
                ReplacedAt = DateTime.UtcNow
            };
            _store.Complete(job.Id, job.ItemId, finalPath, finalSize, replacement);
            _logger.LogInformation(
                "StreamReady finished {Name} -> {Path} (policy={Policy}, backup={Backup})",
                item.Name,
                finalPath,
                commit.Policy,
                commit.BackupPath ?? "(none)");
        }
        catch (OperationCanceledException)
        {
            if (_pauseRequeueJobId == job.Id)
            {
                _pauseRequeueJobId = null;
                // Already requeued in Pause().
            }
            else
            {
                _store.Fail(job.Id, "Cancelled");
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

            _store.Fail(job.Id, shortMsg, TruncateDetail(ex.Message, 8000));
        }
        finally
        {
            lock (_pauseGate)
            {
                if (CurrentJob?.Id == job.Id)
                {
                    CurrentJob = null;
                }

                if (_jobCts == jobCts)
                {
                    _jobCts = null;
                }
            }

            _store.Persist();
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

            var cts = new CancellationTokenSource();
            _settleTimers[id] = cts;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(delay), cts.Token).ConfigureAwait(false);
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
                    _settleTimers.TryRemove(id, out _);
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
