using Jellyfin.Plugin.StreamReady.Models;
using Jellyfin.Plugin.StreamReady.Services;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StreamReady.Controllers;

[ApiController]
[Route("StreamReady")]
public class StreamReadyController : ControllerBase
{
    private readonly JobStore _store;
    private readonly LibraryScanner _scanner;
    private readonly EncodeWorker _worker;
    private readonly FfmpegRunner _ffmpeg;
    private readonly ReplacementService _replacement;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<StreamReadyController> _logger;

    public StreamReadyController(
        JobStore store,
        LibraryScanner scanner,
        EncodeWorker worker,
        FfmpegRunner ffmpeg,
        ReplacementService replacement,
        ILibraryManager libraryManager,
        ILogger<StreamReadyController> logger)
    {
        _store = store;
        _scanner = scanner;
        _worker = worker;
        _ffmpeg = ffmpeg;
        _replacement = replacement;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    [HttpGet("Configuration/configPage.css")]
    public ActionResult GetConfigPageStylesheet()
    {
        var stream = typeof(Plugin).Assembly.GetManifestResourceStream(
            typeof(Plugin).Namespace + ".Configuration.configPage.css");
        if (stream is null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "no-store";
        return new FileStreamResult(stream, "text/css");
    }

    [HttpGet("thumb.png")]
    public ActionResult GetThumb()
    {
        var stream = typeof(Plugin).Assembly.GetManifestResourceStream(
            typeof(Plugin).Namespace + ".thumb.png");
        if (stream is null)
        {
            return NotFound();
        }

        return File(stream, "image/png");
    }

    [Authorize]
    [HttpGet("status")]
    public ActionResult<object> GetStatus()
    {
        var config = Plugin.Instance?.Configuration;
        var current = _worker.CurrentJob;
        var ffmpegPath = _ffmpeg.EncoderPath;
        var ffmpegVersion = _ffmpeg.EncoderVersion;
        var ffmpegReady = _ffmpeg.IsReady;
        var hwLabel = _ffmpeg.DescribeHardware(config);
        var candidateCount = _store.GetCandidates().Count;
        _logger.LogDebug(
            "StreamReady status: ready={Ready} path={Path} version={Version} hw={Hw} candidates={Count} lastFound={LastFound}",
            ffmpegReady,
            ffmpegPath,
            ffmpegVersion,
            hwLabel,
            candidateCount,
            _scanner.LastFound);
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
            ffmpegVersion,
            ffmpegReady,
            hardwareAccel = hwLabel,
            candidateCount,
            queuedCount = _store.QueuedCount(),
            runningCount = _store.RunningCount(),
            encodeWindowEnabled = config?.EncodeWindowEnabled ?? false,
            encodeWindowOpen = config is null || EncodeWindow.IsOpen(config),
            encodeWindowLabel = config is null ? "Any time" : EncodeWindow.Describe(config),
            currentJob = current is null
                ? null
                : new
                {
                    id = current.Id,
                    name = current.Name,
                    progress = current.Progress,
                    status = current.Status.ToString(),
                    action = current.Action.ToString(),
                    statusDetail = current.StatusDetail,
                    videoEncoder = current.VideoEncoder,
                    hardwarePath = current.HardwarePath,
                    toneMap = current.ToneMap,
                    filters = current.Filters,
                    videoRange = current.VideoRange,
                    speed = current.Speed,
                    eta = current.Eta
                }
        };
    }

    /// <summary>
    /// Pre-Transcode-style capabilities (PascalCase) for UI debugging / readiness.
    /// </summary>
    [Authorize]
    [HttpGet("Capabilities")]
    public ActionResult<object> GetCapabilities()
    {
        return _ffmpeg.GetCapabilitiesSnapshot(Plugin.Instance?.Configuration);
    }

    [Authorize]
    [HttpGet("libraries")]
    public ActionResult<object> GetLibraries()
    {
        var folders = LibraryCatalog.ListLibraries(_libraryManager, _logger)
            .Select(f => new
            {
                id = f.Id,
                name = f.Name,
                collectionType = f.CollectionType
            })
            .ToList();
        return folders;
    }

    [Authorize]
    [HttpPost("scan")]
    public async Task<ActionResult<object>> Scan(CancellationToken cancellationToken)
    {
        var count = await _scanner.ScanAsync(null, cancellationToken).ConfigureAwait(false);
        return new { found = count };
    }

    [Authorize]
    [HttpGet("candidates")]
    public ActionResult<object> GetCandidates(
        [FromQuery] string? reason,
        [FromQuery] string? itemType,
        [FromQuery] string? q,
        [FromQuery] string? sortBy,
        [FromQuery] string? sortDir,
        [FromQuery] int skip = 0,
        [FromQuery] int limit = 200)
    {
        if (limit <= 0 || limit > 500)
        {
            limit = 200;
        }

        if (skip < 0)
        {
            skip = 0;
        }

        var filtered = FilterCandidates(reason, itemType, q).ToList();
        filtered = SortCandidates(filtered, sortBy, sortDir);
        var page = filtered.Skip(skip).Take(limit).Select(MapCandidate).ToList();
        _logger.LogInformation(
            "StreamReady candidates: total={Total} skip={Skip} limit={Limit} returned={Returned} reason={Reason} itemType={ItemType} q={Q}",
            filtered.Count,
            skip,
            limit,
            page.Count,
            reason ?? "",
            itemType ?? "",
            q ?? "");
        return new
        {
            total = filtered.Count,
            skip,
            limit,
            items = page
        };
    }

    [Authorize]
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

    [Authorize]
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

    [Authorize]
    [HttpPost("candidates/encodeMatching")]
    public ActionResult<object> EncodeMatching(
        [FromQuery] string? reason,
        [FromQuery] string? itemType,
        [FromQuery] string? q)
    {
        var matching = SortCandidates(FilterCandidates(reason, itemType, q).ToList(), "size", "desc")
            .Take(2000)
            .ToList();
        var jobs = _store.EnqueueMany(matching);
        _worker.Resume();
        return new { queued = jobs.Count, matched = matching.Count, capped = matching.Count >= 2000 };
    }

    [Authorize]
    [HttpPost("candidates/{id}/ignore")]
    public ActionResult Ignore(string id)
    {
        _store.IgnoreCandidate(id);
        return Ok();
    }

    [Authorize]
    [HttpGet("queue")]
    public ActionResult<object> GetQueue()
    {
        return _store.GetQueue().Select(MapJob).ToList();
    }

    [Authorize]
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

    [Authorize]
    [HttpPost("queue/{id}/retry")]
    public ActionResult Retry(string id)
    {
        _store.Requeue(id);
        _worker.Resume();
        return Ok();
    }

    [Authorize]
    [HttpPost("queue/{id}/skip")]
    public ActionResult Skip(string id)
    {
        _store.Skip(id);
        return Ok();
    }

    [Authorize]
    [HttpPost("worker/pause")]
    public ActionResult Pause()
    {
        _worker.Pause();
        return Ok();
    }

    [Authorize]
    [HttpPost("worker/resume")]
    public ActionResult Resume()
    {
        _worker.Resume();
        return Ok();
    }

    [Authorize]
    [HttpGet("replacements")]
    public ActionResult<object> GetReplacements()
    {
        return _store.GetReplacements().Select(MapReplacement).ToList();
    }

    [Authorize]
    [HttpPost("replacements/{id}/restore")]
    public async Task<ActionResult> RestoreReplacement(string id, CancellationToken cancellationToken)
    {
        var record = _store.GetReplacement(id);
        if (record is null)
        {
            return NotFound();
        }

        try
        {
            await _replacement.RestoreAsync(record, cancellationToken).ConfigureAwait(false);
            _store.MarkRestored(id);
            return Ok(new { restored = true, id });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StreamReady restore failed for {Id}", id);
            return BadRequest(new { error = ex.Message });
        }
    }

    private IEnumerable<CandidateRecord> FilterCandidates(string? reason, string? itemType, string? q)
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

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            list = list.Where(c =>
                (c.Name?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                || (c.SeriesName?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                || (c.LibraryName?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                || (c.Path?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                || (c.Container?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                || (c.VideoCodec?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                || (c.AudioCodec?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                || c.Reasons.Any(r => r.Contains(term, StringComparison.OrdinalIgnoreCase))
                || c.ReasonDetails.Any(r => r.Contains(term, StringComparison.OrdinalIgnoreCase)));
        }

        return list;
    }

    private static List<CandidateRecord> SortCandidates(List<CandidateRecord> list, string? sortBy, string? sortDir)
    {
        var asc = string.Equals(sortDir, "asc", StringComparison.OrdinalIgnoreCase);
        return (sortBy ?? "size").Trim().ToLowerInvariant() switch
        {
            "name" => (asc
                ? list.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                : list.OrderByDescending(c => c.Name, StringComparer.OrdinalIgnoreCase)).ToList(),
            "added" => (asc
                ? list.OrderBy(c => c.AddedAt)
                : list.OrderByDescending(c => c.AddedAt)).ToList(),
            "library" => (asc
                ? list.OrderBy(c => c.LibraryName, StringComparer.OrdinalIgnoreCase)
                : list.OrderByDescending(c => c.LibraryName, StringComparer.OrdinalIgnoreCase)).ToList(),
            _ => (asc
                ? list.OrderBy(c => c.SizeBytes)
                : list.OrderByDescending(c => c.SizeBytes)).ToList()
        };
    }

    private static object MapCandidate(CandidateRecord c)
    {
        return new
        {
            id = c.Id,
            itemId = c.ItemId,
            name = c.Name,
            seriesName = c.SeriesName,
            itemType = c.ItemType,
            libraryId = c.LibraryId,
            libraryName = c.LibraryName,
            path = c.Path,
            sizeBytes = c.SizeBytes,
            sizeLabel = FormatSize(c.SizeBytes),
            container = c.Container,
            videoCodec = c.VideoCodec,
            audioCodec = c.AudioCodec,
            videoRange = c.VideoRange,
            width = c.Width,
            height = c.Height,
            bitrate = c.Bitrate,
            reasons = c.Reasons,
            reasonDetails = c.ReasonDetails,
            plannedAction = c.PlannedAction.ToString(),
            ignored = c.Ignored,
            addedAt = c.AddedAt
        };
    }

    private static object MapJob(EncodeJob j)
    {
        return new
        {
            id = j.Id,
            candidateId = j.CandidateId,
            itemId = j.ItemId,
            name = j.Name,
            path = j.Path,
            action = j.Action.ToString(),
            status = j.Status.ToString(),
            progress = j.Progress,
            error = j.Error,
            errorDetail = j.ErrorDetail,
            queuedAt = j.QueuedAt,
            startedAt = j.StartedAt,
            finishedAt = j.FinishedAt,
            reasons = j.Reasons,
            statusDetail = j.StatusDetail,
            videoEncoder = j.VideoEncoder,
            hardwarePath = j.HardwarePath,
            toneMap = j.ToneMap,
            filters = j.Filters,
            videoRange = j.VideoRange,
            speed = j.Speed,
            eta = j.Eta,
            originalPath = j.OriginalPath,
            finalPath = j.FinalPath,
            backupPath = j.BackupPath,
            replacementPolicy = j.ReplacementPolicy,
            replacementId = j.ReplacementId,
            canRestore = CanRestore(j)
        };
    }

    private static object MapReplacement(ReplacementRecord r)
    {
        return new
        {
            id = r.Id,
            itemId = r.ItemId,
            name = r.Name,
            originalPath = r.OriginalPath,
            finalPath = r.FinalPath,
            backupPath = r.BackupPath,
            policy = r.Policy,
            replacedAt = r.ReplacedAt,
            restored = r.Restored,
            canRestore = !r.Restored && (
                string.Equals(r.Policy, "Sidecar", StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(r.BackupPath) && System.IO.File.Exists(r.BackupPath)))
        };
    }

    private static bool CanRestore(EncodeJob j)
    {
        if (j.Status != JobStatus.Done || string.IsNullOrWhiteSpace(j.ReplacementId))
        {
            return false;
        }

        if (string.Equals(j.ReplacementPolicy, "Sidecar", StringComparison.OrdinalIgnoreCase))
        {
            return !string.IsNullOrWhiteSpace(j.FinalPath) && System.IO.File.Exists(j.FinalPath);
        }

        return string.Equals(j.ReplacementPolicy, "Backup", StringComparison.OrdinalIgnoreCase)
               && !string.IsNullOrWhiteSpace(j.BackupPath)
               && System.IO.File.Exists(j.BackupPath);
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
