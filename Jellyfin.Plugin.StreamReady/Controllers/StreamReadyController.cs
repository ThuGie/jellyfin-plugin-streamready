using Jellyfin.Plugin.StreamReady.Models;
using Jellyfin.Plugin.StreamReady.Services;
using MediaBrowser.Common.Api;
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

    [Authorize(Policy = Policies.RequiresElevation)]
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
            storeHealthy = _store.IsHealthy,
            pendingMediaOperation = _replacement.PendingOperation,
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
            activeJobs = _worker.ActiveJobs.Select(MapJob),
            currentJob = current is null
                ? null
                : new
                {
                    id = current.Id,
                    name = current.Name,
                    progress = current.Progress,
                    status = current.Status.ToString(),
                    phase = current.Phase,
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
    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("Capabilities")]
    public ActionResult<object> GetCapabilities()
    {
        return _ffmpeg.GetCapabilitiesSnapshot(Plugin.Instance?.Configuration);
    }

    [Authorize(Policy = Policies.RequiresElevation)]
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

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("scan")]
    public async Task<ActionResult<object>> Scan(CancellationToken cancellationToken)
    {
        var result = await _scanner.ScanAsync(null, cancellationToken).ConfigureAwait(false);
        return new { status = result.Status, found = result.Found };
    }

    [Authorize(Policy = Policies.RequiresElevation)]
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
        _logger.LogDebug(
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

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("candidates/{id}/encode")]
    public ActionResult<object> EncodeOne(string id)
    {
        var candidate = _store.GetCandidate(id);
        if (candidate is null)
        {
            return NotFound();
        }

        if (candidate.Ignored)
        {
            return Conflict(new { error = "This candidate is ignored." });
        }

        var job = _store.Enqueue(candidate);
        return MapJob(job);
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("candidates/encode")]
    public ActionResult<object> EncodeMany([FromBody] IdListRequest request)
    {
        var ids = request.Ids ?? [];
        if (ids.Count > 2000)
        {
            return BadRequest(new { error = "At most 2000 candidates may be queued at once." });
        }
        var candidates = ids
            .Select(id => _store.GetCandidate(id))
            .Where(c => c is not null)
            .Cast<CandidateRecord>()
            .ToList();
        var jobs = _store.EnqueueMany(candidates);
        return new { queued = jobs.Count };
    }

    [Authorize(Policy = Policies.RequiresElevation)]
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
        return new { queued = jobs.Count, matched = matching.Count, capped = matching.Count >= 2000 };
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("candidates/{id}/ignore")]
    public ActionResult Ignore(string id)
    {
        _store.IgnoreCandidate(id);
        return Ok();
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("queue")]
    public ActionResult<object> GetQueue([FromQuery] int? limit = null)
    {
        return _store.GetQueue(limit).Select(MapJob).ToList();
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpDelete("queue/{id}")]
    public ActionResult RemoveQueueEntry(string id)
    {
        if (_store.GetJob(id) is null)
        {
            return NotFound();
        }

        return _store.RemoveFromQueue(id) ? Ok(new { removed = true })
            : Conflict(new { error = "Cancel the active job and wait for it to stop before removing it." });
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("queue/{id}/cancel")]
    public ActionResult Cancel(string id)
    {
        if (_store.GetJob(id) is null) return NotFound();
        return _worker.Cancel(id) ? Ok() : Conflict(new { error = "Job cannot be cancelled." });
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("queue/{id}/retry")]
    public ActionResult Retry(string id)
    {
        var job = _store.GetJob(id);
        if (job is null)
        {
            return NotFound();
        }

        return job.Status is JobStatus.Failed or JobStatus.Cancelled or JobStatus.Skipped && _store.Requeue(id)
            ? Ok() : Conflict(new { error = "Job cannot be retried." });
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("queue/{id}/skip")]
    public ActionResult Skip(string id)
    {
        if (_store.GetJob(id) is null) return NotFound();
        return _store.Skip(id) ? Ok() : Conflict(new { error = "Only queued jobs can be skipped." });
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("worker/pause")]
    public ActionResult Pause()
    {
        _worker.Pause();
        return Ok();
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("worker/resume")]
    public ActionResult Resume()
    {
        _worker.Resume();
        return Ok();
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("replacements")]
    public ActionResult<object> GetReplacements([FromQuery] int? limit = null)
    {
        return _store.GetReplacements(limit: limit).Select(MapReplacement).ToList();
    }

    [Authorize(Policy = Policies.RequiresElevation)]
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
            return Ok(new { restored = true, id });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StreamReady restore failed for {Id}", id);
            return Conflict(new { error = ex.Message });
        }
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("replacements/{id}/preview")]
    public async Task<ActionResult> PreviewReplacement(string id, [FromQuery] string version,
        [FromQuery] int startSeconds, CancellationToken cancellationToken)
    {
        var record = _store.GetReplacement(id);
        if (record is null)
        {
            return NotFound();
        }

        if (version is not ("original" or "encoded") || startSeconds < 0 || startSeconds > 86_400)
        {
            return BadRequest(new { error = "Choose a version and a valid time within the video." });
        }

        try
        {
            var path = _replacement.ResolvePreviewPath(record, original: version == "original");
            var clip = await _ffmpeg.CreatePreviewAsync(path, startSeconds, cancellationToken).ConfigureAwait(false);
            Response.Headers.CacheControl = "no-store";
            return File(clip, "video/mp4");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpDelete("replacements/{id}/original")]
    public async Task<ActionResult> DeleteOriginal(string id, CancellationToken cancellationToken)
    {
        if (_store.GetReplacement(id) is null)
        {
            return NotFound();
        }

        try
        {
            await _replacement.DeleteOriginalAsync(id, cancellationToken).ConfigureAwait(false);
            return Ok(new { deleted = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return Conflict(new { error = ex.Message });
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
            phase = j.Phase,
            eligibleAfterUtc = j.EligibleAfterUtc,
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
            originalSize = r.OriginalSize ?? ExistingLength(r.Policy == "Sidecar" ? r.OriginalPath : r.BackupPath),
            encodedSize = r.FinalSize ?? ExistingLength(r.FinalPath),
            originalDeletedAt = r.OriginalDeletedAt,
            canPreviewEncoded = !r.Restored && r.FinalSize is not null && System.IO.File.Exists(r.FinalPath),
            canPreviewOriginal = !r.Restored && r.FinalSize is not null && r.OriginalDeletedAt is null
                && System.IO.File.Exists(r.Policy == "Sidecar" ? r.OriginalPath : r.BackupPath),
            canDeleteOriginal = !r.Restored && r.Policy == "Backup" && r.BackupHash is not null
                && r.OriginalDeletedAt is null && System.IO.File.Exists(r.BackupPath),
            canRestore = !r.Restored && r.OriginalDeletedAt is null && r.FinalSize is not null && r.FinalWriteUtc is not null && (
                string.Equals(r.Policy, "Sidecar", StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(r.BackupPath) && System.IO.File.Exists(r.BackupPath)))
        };
    }

    private static long? ExistingLength(string? path)
    {
        try
        {
            return System.IO.File.Exists(path) ? new FileInfo(path!).Length : null;
        }
        catch (IOException)
        {
            return null;
        }
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
