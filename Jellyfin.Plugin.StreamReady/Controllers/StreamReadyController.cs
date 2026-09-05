using Jellyfin.Plugin.StreamReady.Models;
using Jellyfin.Plugin.StreamReady.Services;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.StreamReady.Controllers;

[ApiController]
[Authorize]
[Route("StreamReady")]
public class StreamReadyController : ControllerBase
{
    private readonly JobStore _store;
    private readonly LibraryScanner _scanner;
    private readonly EncodeWorker _worker;
    private readonly FfmpegRunner _ffmpeg;
    private readonly ILibraryManager _libraryManager;

    public StreamReadyController(
        JobStore store,
        LibraryScanner scanner,
        EncodeWorker worker,
        FfmpegRunner ffmpeg,
        ILibraryManager libraryManager)
    {
        _store = store;
        _scanner = scanner;
        _worker = worker;
        _ffmpeg = ffmpeg;
        _libraryManager = libraryManager;
    }

    [HttpGet("Configuration/configPage.css")]
    public ActionResult GetCss()
    {
        var resource = typeof(Plugin).Namespace + ".Configuration.configPage.css";
        var stream = typeof(Plugin).Assembly.GetManifestResourceStream(resource);
        if (stream is null)
        {
            return NotFound();
        }

        return File(stream, "text/css");
    }

    [HttpGet("status")]
    public ActionResult<object> GetStatus()
    {
        var config = Plugin.Instance?.Configuration;
        var current = _worker.CurrentJob;
        var ffmpegPath = _ffmpeg.EncoderPath;
        return new
        {
            enabled = config?.Enabled ?? false,
            autoDirectPreTranscode = config?.AutoDirectPreTranscode ?? false,
            configured = !string.IsNullOrWhiteSpace(config?.SelectedLibraryIds),
            scanning = _scanner.IsScanning,
            lastScanUtc = _scanner.LastScanUtc,
            lastFound = _scanner.LastFound,
            paused = _worker.IsPaused,
            ffmpegPath,
            ffmpegReady = !string.IsNullOrWhiteSpace(ffmpegPath) && System.IO.File.Exists(ffmpegPath),
            candidateCount = _store.GetCandidates().Count,
            queuedCount = _store.QueuedCount(),
            runningCount = _store.RunningCount(),
            currentJob = current is null
                ? null
                : new
                {
                    current.Id,
                    current.Name,
                    current.Progress,
                    current.Status,
                    Action = current.Action.ToString()
                }
        };
    }

    [HttpGet("libraries")]
    public ActionResult<object> GetLibraries()
    {
        var folders = _libraryManager.GetVirtualFolders()
            .Select(f => new
            {
                id = f.ItemId,
                name = f.Name,
                collectionType = f.CollectionType?.ToString()
            })
            .ToList();
        return folders;
    }

    [HttpPost("scan")]
    public async Task<ActionResult<object>> Scan(CancellationToken cancellationToken)
    {
        var count = await _scanner.ScanAsync(null, cancellationToken).ConfigureAwait(false);
        return new { found = count };
    }

    [HttpGet("candidates")]
    public ActionResult<object> GetCandidates([FromQuery] string? reason, [FromQuery] string? itemType)
    {
        var list = _store.GetCandidates().AsEnumerable();
        if (!string.IsNullOrWhiteSpace(reason))
        {
            list = list.Where(c => c.Reasons.Contains(reason, StringComparer.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(itemType))
        {
            list = list.Where(c => c.ItemType.Equals(itemType, StringComparison.OrdinalIgnoreCase));
        }

        return list.Select(MapCandidate).ToList();
    }

    [HttpPost("candidates/{id}/encode")]
    public ActionResult<object> EncodeOne(string id)
    {
        var candidate = _store.GetCandidate(id);
        if (candidate is null)
        {
            return NotFound();
        }

        var job = _store.Enqueue(candidate);
        _worker.Resume();
        return MapJob(job);
    }

    [HttpPost("candidates/encode")]
    public ActionResult<object> EncodeMany([FromBody] IdListRequest request)
    {
        var ids = request.Ids ?? [];
        var candidates = ids
            .Select(id => _store.GetCandidate(id))
            .Where(c => c is not null)
            .Cast<CandidateRecord>()
            .ToList();
        var jobs = _store.EnqueueMany(candidates);
        _worker.Resume();
        return new { queued = jobs.Count };
    }

    [HttpPost("candidates/{id}/ignore")]
    public ActionResult Ignore(string id)
    {
        _store.IgnoreCandidate(id);
        return Ok();
    }

    [HttpGet("queue")]
    public ActionResult<object> GetQueue()
    {
        return _store.GetQueue().Select(MapJob).ToList();
    }

    [HttpPost("queue/{id}/cancel")]
    public ActionResult Cancel(string id)
    {
        var running = _store.Cancel(id);
        if (running && _worker.CurrentJob?.Id == id)
        {
            _worker.CancelCurrent();
        }

        return Ok();
    }

    [HttpPost("queue/{id}/retry")]
    public ActionResult Retry(string id)
    {
        _store.Requeue(id);
        _worker.Resume();
        return Ok();
    }

    [HttpPost("queue/{id}/skip")]
    public ActionResult Skip(string id)
    {
        _store.Skip(id);
        return Ok();
    }

    [HttpPost("worker/pause")]
    public ActionResult Pause()
    {
        _worker.Pause();
        return Ok();
    }

    [HttpPost("worker/resume")]
    public ActionResult Resume()
    {
        _worker.Resume();
        return Ok();
    }

    private static object MapCandidate(CandidateRecord c)
    {
        return new
        {
            c.Id,
            c.ItemId,
            c.Name,
            c.SeriesName,
            c.ItemType,
            c.LibraryId,
            c.LibraryName,
            c.Path,
            c.SizeBytes,
            sizeLabel = FormatSize(c.SizeBytes),
            c.Container,
            c.VideoCodec,
            c.AudioCodec,
            c.VideoRange,
            c.Width,
            c.Height,
            c.Bitrate,
            reasons = c.Reasons,
            plannedAction = c.PlannedAction.ToString(),
            c.Ignored,
            c.AddedAt
        };
    }

    private static object MapJob(EncodeJob j)
    {
        return new
        {
            j.Id,
            j.CandidateId,
            j.ItemId,
            j.Name,
            j.Path,
            action = j.Action.ToString(),
            status = j.Status.ToString(),
            j.Progress,
            j.Error,
            j.QueuedAt,
            j.StartedAt,
            j.FinishedAt,
            reasons = j.Reasons
        };
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

    public class IdListRequest
    {
        public List<string>? Ids { get; set; }
    }
}
