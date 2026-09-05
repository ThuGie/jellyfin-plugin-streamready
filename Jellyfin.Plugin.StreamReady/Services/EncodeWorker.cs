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
    private CancellationTokenSource? _jobCts;
    private DateTime _lastScanUtc = DateTime.MinValue;

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
    }

    public void Resume()
    {
        IsPaused = false;
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
            var destExt = "." + EncodePlanner.DestinationContainer(config);
            var tempPath = sourcePath + ".streamready.tmp" + destExt;
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            var duration = item.RunTimeTicks.HasValue
                ? TimeSpan.FromTicks(item.RunTimeTicks.Value).TotalSeconds
                : await _ffmpeg.ProbeDurationAsync(sourcePath, jobCts.Token).ConfigureAwait(false);

            var progress = new Progress<double>(value =>
            {
                _store.UpdateProgress(job.Id, value);
                if (CurrentJob?.Id == job.Id)
                {
                    CurrentJob.Progress = value;
                }
            });

            await _ffmpeg.EncodeAsync(sourcePath, tempPath, job.Action, config, duration, progress, jobCts.Token)
                .ConfigureAwait(false);

            if (_store.IsCancelled(job.Id) || jobCts.IsCancellationRequested)
            {
                TryDelete(tempPath);
                return;
            }

            if (config.VerifyBeforeReplace)
            {
                var outDuration = await _ffmpeg.ProbeDurationAsync(tempPath, jobCts.Token).ConfigureAwait(false);
                if (duration > 0 && outDuration > 0)
                {
                    var delta = Math.Abs(outDuration - duration);
                    if (delta > Math.Max(2, duration * 0.02))
                    {
                        TryDelete(tempPath);
                        _store.Fail(job.Id, $"Duration mismatch (source {duration:0}s, output {outDuration:0}s)");
                        return;
                    }
                }

                if (!File.Exists(tempPath) || new FileInfo(tempPath).Length < 1024)
                {
                    TryDelete(tempPath);
                    _store.Fail(job.Id, "Output file is missing or too small");
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

            var finalPath = await _replacement.CommitAsync(item, sourcePath, tempPath, config, jobCts.Token)
                .ConfigureAwait(false);
            var finalSize = File.Exists(finalPath) ? new FileInfo(finalPath).Length : 0;
            _store.Complete(job.Id, job.ItemId, finalPath, finalSize);
            _logger.LogInformation("StreamReady finished {Name} -> {Path}", item.Name, finalPath);
        }
        catch (OperationCanceledException)
        {
            _store.Fail(job.Id, "Cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StreamReady encode failed for {Name}", job.Name);
            _store.Fail(job.Id, ex.Message);
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
            if (e.Item is not Video)
            {
                return;
            }

            _scanner.AnalyzeItem(e.Item);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "StreamReady item-change analysis failed");
        }
    }

    private bool HasActivePlayback()
    {
        return _sessionManager.Sessions.Any(s => s.NowPlayingItem is not null);
    }

    private bool IsPlaying(Guid itemId)
    {
        return _sessionManager.Sessions.Any(s => s.NowPlayingItem?.Id == itemId);
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
