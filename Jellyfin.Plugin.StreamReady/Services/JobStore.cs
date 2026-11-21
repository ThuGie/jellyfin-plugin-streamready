using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.StreamReady.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StreamReady.Services;

public class JobStore
{
    private readonly ILogger<JobStore> _logger;
    private readonly string? _explicitStorePath;
    private readonly object _gate = new();
    private JobStoreState _state = new();
    private readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private const int SchemaVersion = 1;
    private string? _committedJson;
    private bool _healthy = true;

    public bool IsHealthy
    {
        get { lock (_gate) { return _healthy; } }
    }

    public JobStore(ILogger<JobStore> logger) : this(logger, null)
    {
    }

    internal JobStore(ILogger<JobStore> logger, string? storePath)
    {
        _logger = logger;
        _explicitStorePath = storePath;
        Load();
    }

    public JobStoreState Snapshot()
    {
        lock (_gate)
        {
            var json = JsonSerializer.Serialize(_state, _json);
            return JsonSerializer.Deserialize<JobStoreState>(json, _json) ?? new JobStoreState();
        }
    }

    public IReadOnlyList<CandidateRecord> GetCandidates(bool includeIgnored = false)
    {
        lock (_gate)
        {
            return _state.Candidates
                .Where(c => includeIgnored || !c.Ignored)
                .Select(CloneCandidate)
                .ToList();
        }
    }

    public CandidateRecord? GetCandidate(string id)
    {
        lock (_gate)
        {
            var found = _state.Candidates.FirstOrDefault(c => c.Id == id);
            return found is null ? null : CloneCandidate(found);
        }
    }

    public void UpsertCandidates(IEnumerable<CandidateRecord> candidates)
    {
        lock (_gate)
        {
            foreach (var candidate in candidates)
            {
                UpsertCandidateLocked(candidate);
            }

            SaveLocked();
        }
    }

    public void ReconcileCandidates(IEnumerable<CandidateRecord> candidates, DateTime scanStarted)
    {
        lock (_gate)
        {
            var incoming = candidates.GroupBy(candidate => candidate.Id).ToDictionary(group => group.Key, group => group.First());
            _state.Candidates.RemoveAll(candidate => !candidate.Ignored && candidate.ObservedAt <= scanStarted
                && !incoming.ContainsKey(candidate.Id));
            var newer = _state.Candidates.Where(candidate => candidate.ObservedAt > scanStarted)
                .Select(candidate => candidate.Id).ToHashSet();
            foreach (var candidate in incoming.Values.Where(candidate => !newer.Contains(candidate.Id)))
            {
                candidate.ObservedAt = scanStarted;
                UpsertCandidateLocked(candidate);
            }

            SaveLocked();
        }
    }

    private void UpsertCandidateLocked(CandidateRecord incoming)
    {
        var index = _state.Candidates.FindIndex(candidate => candidate.Id == incoming.Id);
        var clone = CloneCandidate(incoming);
        if (index < 0)
        {
            _state.Candidates.Add(clone);
            return;
        }

        var existing = _state.Candidates[index];
        clone.Ignored = existing.Ignored;
        clone.AddedAt = existing.AddedAt;
        _state.Candidates[index] = clone;
    }

    public void RemoveCandidate(string id)
    {
        lock (_gate)
        {
            if (_state.Candidates.RemoveAll(c => c.Id == id && !c.Ignored) > 0)
            {
                SaveLocked();
            }
        }
    }

    public void IgnoreCandidate(string id)
    {
        lock (_gate)
        {
            var found = _state.Candidates.FirstOrDefault(c => c.Id == id);
            if (found is null)
            {
                return;
            }

            found.Ignored = true;
            SaveLocked();
        }
    }

    public bool IsProcessed(string itemId, string path, long sizeBytes, DateTime writeUtc)
    {
        lock (_gate)
        {
            return _state.Processed.Any(p =>
                p.ItemId == itemId &&
                string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase) &&
                p.SizeBytes == sizeBytes && p.WriteUtc == writeUtc);
        }
    }

    public bool IsQueued(string itemId)
    {
        lock (_gate)
        {
            return _state.Queue.Any(j =>
                j.ItemId == itemId &&
                (j.Status == JobStatus.Queued || j.Status == JobStatus.Running || j.Status == JobStatus.Paused));
        }
    }

    public EncodeJob Enqueue(CandidateRecord candidate)
    {
        lock (_gate)
        {
            if (_state.Candidates.Any(c => c.Id == candidate.Id && c.Ignored))
            {
                throw new InvalidOperationException("This candidate is ignored.");
            }

            var existing = _state.Queue.FirstOrDefault(j =>
                j.ItemId == candidate.ItemId &&
                (j.Status == JobStatus.Queued || j.Status == JobStatus.Running || j.Status == JobStatus.Paused));
            if (existing is not null)
            {
                return CloneJob(existing);
            }

            var job = new EncodeJob
            {
                CandidateId = candidate.Id,
                ItemId = candidate.ItemId,
                Name = candidate.Name,
                Path = candidate.Path,
                SourceWriteUtc = candidate.SourceWriteUtc,
                SourceSize = candidate.SizeBytes,
                Action = candidate.PlannedAction,
                Status = JobStatus.Queued,
                Reasons = [.. candidate.Reasons],
                VideoRange = candidate.VideoRange,
                QueuedAt = DateTime.UtcNow
            };
            _state.Queue.Insert(0, job);
            SaveLocked();
            return CloneJob(job);
        }
    }

    public List<EncodeJob> EnqueueMany(IEnumerable<CandidateRecord> candidates, bool automatic = false)
    {
        lock (_gate)
        {
            var created = new List<EncodeJob>();
            foreach (var candidate in candidates)
            {
                if (_state.Candidates.Any(c => c.Id == candidate.Id && c.Ignored)
                    || (automatic && _state.Queue.Any(j => j.ItemId == candidate.ItemId
                        && j.Path == candidate.Path && j.SourceWriteUtc == candidate.SourceWriteUtc
                        && j.Status is JobStatus.Failed or JobStatus.Skipped or JobStatus.Cancelled))
                    || _state.Queue.Any(j =>
                        j.ItemId == candidate.ItemId &&
                        (j.Status == JobStatus.Queued || j.Status == JobStatus.Running || j.Status == JobStatus.Paused)))
                {
                    continue;
                }

                var job = new EncodeJob
                {
                    CandidateId = candidate.Id,
                    ItemId = candidate.ItemId,
                    Name = candidate.Name,
                    Path = candidate.Path,
                    SourceWriteUtc = candidate.SourceWriteUtc,
                    SourceSize = candidate.SizeBytes,
                    Action = candidate.PlannedAction,
                    Status = JobStatus.Queued,
                    Reasons = [.. candidate.Reasons],
                    VideoRange = candidate.VideoRange,
                    QueuedAt = DateTime.UtcNow
                };
                _state.Queue.Insert(0, job);
                created.Add(CloneJob(job));
            }

            if (created.Count > 0)
            {
                SaveLocked();
            }

            return created;
        }
    }

    public EncodeJob? DequeueNext()
    {
        lock (_gate)
        {
            var job = _state.Queue
                .Where(j => j.Status == JobStatus.Queued && (j.EligibleAfterUtc is null || j.EligibleAfterUtc <= DateTime.UtcNow))
                .OrderBy(j => j.QueuedAt)
                .FirstOrDefault();
            if (job is null)
            {
                return null;
            }

            job.Status = JobStatus.Running;
            job.Phase = "Preparing";
            job.StartedAt = DateTime.UtcNow;
            job.Progress = 0;
            SaveLocked();
            return CloneJob(job);
        }
    }

    public IReadOnlyList<EncodeJob> GetQueue(int? limit = null)
    {
        lock (_gate)
        {
            return _state.Queue.Where(job => !job.HiddenFromQueue).Take(limit is int count ? Math.Clamp(count, 1, 500) : int.MaxValue).Select(CloneJob).ToList();
        }
    }

    public bool RemoveFromQueue(string id)
    {
        lock (_gate)
        {
            var job = _state.Queue.FirstOrDefault(job => job.Id == id);
            if (job is null || job.Status == JobStatus.Running)
            {
                return false;
            }

            if (job.HiddenFromQueue)
            {
                return true;
            }

            if (job.Status is JobStatus.Queued or JobStatus.Paused)
            {
                job.Status = JobStatus.Cancelled;
                job.FinishedAt = DateTime.UtcNow;
            }

            job.HiddenFromQueue = true;
            SaveLocked();
            return true;
        }
    }

    public EncodeJob? GetJob(string id)
    {
        lock (_gate)
        {
            var job = _state.Queue.FirstOrDefault(j => j.Id == id);
            return job is null ? null : CloneJob(job);
        }
    }

    public int RunningCount()
    {
        lock (_gate)
        {
            return _state.Queue.Count(j => j.Status == JobStatus.Running);
        }
    }

    public int QueuedCount()
    {
        lock (_gate)
        {
            return _state.Queue.Count(j => j.Status == JobStatus.Queued);
        }
    }

    public void UpdateProgress(
        string id,
        double progress,
        string? speed = null,
        string? eta = null,
        bool allowDecrease = false)
    {
        lock (_gate)
        {
            var job = _state.Queue.FirstOrDefault(j => j.Id == id);
            if (job is null || job.Status != JobStatus.Running)
            {
                return;
            }

            var next = Math.Clamp(progress, 0, 100);
            // Ignore noisy backwards ticks (e.g. parser glitches) unless this is an intentional reset.
            if (!allowDecrease && next < job.Progress && job.Progress < 100)
            {
                if (speed is not null)
                {
                    job.Speed = speed;
                }

                if (eta is not null)
                {
                    job.Eta = eta;
                }

                return;
            }

            job.Progress = next;
            if (speed is not null)
            {
                job.Speed = speed;
            }

            if (eta is not null)
            {
                job.Eta = eta;
            }
            else if (next <= 1 || next >= 100)
            {
                job.Eta = null;
            }
        }
    }

    public void ResetProgress(string id, double progress = 0)
    {
        UpdateProgress(id, progress, allowDecrease: true);
    }

    public void UpdatePhase(string id, string phase)
    {
        lock (_gate)
        {
            var job = _state.Queue.FirstOrDefault(j => j.Id == id && j.Status == JobStatus.Running);
            if (job is not null)
            {
                job.Phase = phase;
            }
        }
    }

    public void UpdateEncodePlan(string id, EncodePlan plan)
    {
        lock (_gate)
        {
            var job = _state.Queue.FirstOrDefault(j => j.Id == id);
            if (job is null)
            {
                return;
            }

            ApplyPlanToJob(job, plan);
            SaveLocked();
        }
    }

    private static void ApplyPlanToJob(EncodeJob job, EncodePlan plan)
    {
        job.StatusDetail = plan.Summary;
        job.VideoEncoder = plan.VideoEncoder;
        job.HardwarePath = plan.HardwareLabel + " · decode " + plan.DecodeMode;
        job.ToneMap = plan.ToneMap;
        job.Filters = plan.Filters;
        if (plan.SoftFallback)
        {
            job.StatusDetail = "[software fallback] " + plan.Summary;
        }
    }

    public void Complete(string id, string itemId, string path, long sizeBytes, ReplacementRecord? replacement = null)
    {
        lock (_gate)
        {
            var job = _state.Queue.FirstOrDefault(j => j.Id == id);
            if (job is not null)
            {
                job.Status = JobStatus.Done;
                job.Progress = 100;
                job.FinishedAt = DateTime.UtcNow;
                if (replacement is not null)
                {
                    job.OriginalPath = replacement.OriginalPath;
                    job.FinalPath = replacement.FinalPath;
                    job.BackupPath = replacement.BackupPath;
                    job.ReplacementPolicy = replacement.Policy;
                    job.ReplacementId = replacement.Id;
                }
            }

            _state.Candidates.RemoveAll(c => c.ItemId == itemId);
            _state.Processed.RemoveAll(p => p.ItemId == itemId);
            _state.Processed.Add(new ProcessedMarker
            {
                ItemId = itemId,
                Path = path,
                SizeBytes = sizeBytes,
                WriteUtc = File.GetLastWriteTimeUtc(path)
            });

            if (replacement is not null)
            {
                _state.Replacements ??= [];
                _state.Replacements.Insert(0, replacement);
            }

            SaveLocked();
        }
    }

    public IReadOnlyList<ReplacementRecord> GetReplacements(bool includeRestored = false, int? limit = null)
    {
        lock (_gate)
        {
            return _state.Replacements
                .Where(r => includeRestored || !r.Restored)
                .Take(limit is int count ? Math.Clamp(count, 1, 500) : int.MaxValue)
                .Select(CloneReplacement)
                .ToList();
        }
    }

    public ReplacementRecord? GetReplacement(string id)
    {
        lock (_gate)
        {
            var rec = _state.Replacements.FirstOrDefault(r => r.Id == id);
            return rec is null ? null : CloneReplacement(rec);
        }
    }

    public void MarkRestored(string id)
    {
        lock (_gate)
        {
            var rec = _state.Replacements.FirstOrDefault(r => r.Id == id);
            if (rec is null)
            {
                return;
            }

            rec.Restored = true;
            _state.Processed.RemoveAll(p => p.ItemId == rec.ItemId);
            if (File.Exists(rec.OriginalPath))
            {
                var source = new FileInfo(rec.OriginalPath);
                _state.Processed.Add(new ProcessedMarker
                {
                    ItemId = rec.ItemId,
                    Path = rec.OriginalPath,
                    SizeBytes = source.Length,
                    WriteUtc = source.LastWriteTimeUtc
                });
            }

            var job = _state.Queue.FirstOrDefault(j => j.ReplacementId == id);
            if (job is not null)
            {
                job.StatusDetail = (job.StatusDetail ?? string.Empty) + " · restored";
                job.BackupPath = null;
            }

            SaveLocked();
        }
    }

    public void Fail(string id, string error, string? errorDetail = null)
    {
        lock (_gate)
        {
            var job = _state.Queue.FirstOrDefault(j => j.Id == id);
            if (job is null)
            {
                return;
            }

            job.Status = JobStatus.Failed;
            job.Error = error;
            job.ErrorDetail = errorDetail;
            job.FinishedAt = DateTime.UtcNow;
            SaveLocked();
        }
    }

    public bool Requeue(string id, string? statusDetail = null, TimeSpan? delay = null)
    {
        lock (_gate)
        {
            var job = _state.Queue.FirstOrDefault(j => j.Id == id);
            if (job is null || job.HiddenFromQueue || job.Status is JobStatus.Queued or JobStatus.Done)
            {
                return false;
            }

            job.Status = JobStatus.Queued;
            job.Error = null;
            job.ErrorDetail = null;
            job.Progress = 0;
            job.Speed = null;
            job.Eta = null;
            job.StartedAt = null;
            job.FinishedAt = null;
            job.QueuedAt = DateTime.UtcNow;
            job.EligibleAfterUtc = delay is null ? null : job.QueuedAt + delay;
            if (!string.IsNullOrWhiteSpace(statusDetail))
            {
                job.StatusDetail = statusDetail;
            }

            SaveLocked();
            return true;
        }
    }

    public bool Cancel(string id)
    {
        lock (_gate)
        {
            var job = _state.Queue.FirstOrDefault(j => j.Id == id);
            if (job is null)
            {
                return false;
            }

            if (job.Status == JobStatus.Running)
            {
                job.Status = JobStatus.Cancelled;
                job.FinishedAt = DateTime.UtcNow;
                SaveLocked();
                return true;
            }

            if (job.Status == JobStatus.Queued)
            {
                job.Status = JobStatus.Cancelled;
                job.FinishedAt = DateTime.UtcNow;
                SaveLocked();
                return true;
            }

            return false;
        }
    }

    public bool Skip(string id)
    {
        lock (_gate)
        {
            var job = _state.Queue.FirstOrDefault(j => j.Id == id);
            if (job?.Status != JobStatus.Queued)
            {
                return false;
            }

            job.Status = JobStatus.Skipped;
            job.FinishedAt = DateTime.UtcNow;
            SaveLocked();
            return true;
        }
    }

    public bool IsCancelled(string id)
    {
        lock (_gate)
        {
            var job = _state.Queue.FirstOrDefault(j => j.Id == id);
            return job is not null && job.Status == JobStatus.Cancelled;
        }
    }

    public void Persist()
    {
        lock (_gate)
        {
            SaveLocked();
        }
    }

    private void Load()
    {
        try
        {
            var path = StorePath();
            foreach (var snapshot in new[] { path, path + ".previous" })
            {
                if (!File.Exists(snapshot))
                {
                    continue;
                }

                try
                {
                    var json = File.ReadAllText(snapshot);
                    var state = DeserializeState(json);
                    if (state.SchemaVersion == 0 && snapshot == path)
                    {
                        var legacy = path + ".legacy";
                        if (!File.Exists(legacy))
                        {
                            File.Copy(path, legacy, overwrite: false);
                        }
                    }

                    _state = state;
                    _committedJson = json;
                    foreach (var job in _state.Queue.Where(j => j.Status == JobStatus.Running))
                    {
                        job.Status = JobStatus.Queued;
                        job.Progress = 0;
                        job.StartedAt = null;
                    }

                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Invalid StreamReady snapshot {Path}", snapshot);
                }
            }

            _healthy = !File.Exists(path) && !File.Exists(path + ".previous");
        }
        catch (Exception ex)
        {
            _healthy = false;
            _logger.LogError(ex, "Unable to initialize StreamReady persistence");
        }
    }

    private JobStoreState DeserializeState(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty(nameof(JobStoreState.Queue), out var queue)
            || queue.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The job store has no valid queue.");
        }

        var state = JsonSerializer.Deserialize<JobStoreState>(json, _json)
            ?? throw new InvalidDataException("The job store is empty.");
        if (state.SchemaVersion < 0 || state.SchemaVersion > SchemaVersion)
        {
            throw new InvalidDataException("Unsupported job store version.");
        }

        state.Candidates ??= [];
        state.Queue ??= [];
        state.Processed ??= [];
        state.Replacements ??= [];
        return state;
    }

    private void SaveLocked()
    {
        try
        {
            if (!_healthy)
            {
                throw new InvalidOperationException("StreamReady persistence requires recovery.");
            }

            var path = StorePath();
            _state.SchemaVersion = SchemaVersion;
            var json = JsonSerializer.Serialize(_state, _json);
            if (_committedJson is not null)
            {
                WriteSnapshot(path + ".previous", _committedJson);
            }

            WriteSnapshot(path, json);
            _committedJson = json;
        }
        catch
        {
            _state = _committedJson is null ? new JobStoreState() : DeserializeState(_committedJson);
            _healthy = false;
            throw;
        }
    }

    internal static void WriteSnapshot(string path, string contents)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var writer = new StreamWriter(stream, leaveOpen: true);
                writer.Write(contents);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    internal static string DataDirectory()
    {
        var folder = Plugin.Instance?.DataFolderPath
            ?? throw new InvalidOperationException("The StreamReady plugin data directory is unavailable.");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private string StorePath() => _explicitStorePath ?? Path.Combine(DataDirectory(), "jobs.json");

    private static CandidateRecord CloneCandidate(CandidateRecord source)
    {
        return new CandidateRecord
        {
            Id = source.Id,
            ItemId = source.ItemId,
            Name = source.Name,
            SeriesName = source.SeriesName,
            ItemType = source.ItemType,
            LibraryId = source.LibraryId,
            LibraryName = source.LibraryName,
            Path = source.Path,
            SourceWriteUtc = source.SourceWriteUtc,
            ObservedAt = source.ObservedAt,
            SizeBytes = source.SizeBytes,
            Container = source.Container,
            VideoCodec = source.VideoCodec,
            AudioCodec = source.AudioCodec,
            VideoRange = source.VideoRange,
            Width = source.Width,
            Height = source.Height,
            Bitrate = source.Bitrate,
            RuntimeTicks = source.RuntimeTicks,
            Reasons = [.. source.Reasons],
            ReasonDetails = [.. source.ReasonDetails],
            PlannedAction = source.PlannedAction,
            Ignored = source.Ignored,
            AddedAt = source.AddedAt
        };
    }

    private static EncodeJob CloneJob(EncodeJob source)
    {
        return new EncodeJob
        {
            Id = source.Id,
            CandidateId = source.CandidateId,
            ItemId = source.ItemId,
            Name = source.Name,
            Path = source.Path,
            SourceWriteUtc = source.SourceWriteUtc,
            Action = source.Action,
            Status = source.Status,
            Progress = source.Progress,
            Error = source.Error,
            ErrorDetail = source.ErrorDetail,
            QueuedAt = source.QueuedAt,
            EligibleAfterUtc = source.EligibleAfterUtc,
            Phase = source.Status == JobStatus.Running ? source.Phase : null,
            SourceSize = source.SourceSize,
            HiddenFromQueue = source.HiddenFromQueue,
            StartedAt = source.StartedAt,
            FinishedAt = source.FinishedAt,
            Reasons = [.. source.Reasons],
            VideoRange = source.VideoRange,
            StatusDetail = source.StatusDetail,
            VideoEncoder = source.VideoEncoder,
            HardwarePath = source.HardwarePath,
            ToneMap = source.ToneMap,
            Filters = source.Filters,
            Speed = source.Speed,
            Eta = source.Eta,
            OriginalPath = source.OriginalPath,
            FinalPath = source.FinalPath,
            BackupPath = source.BackupPath,
            ReplacementPolicy = source.ReplacementPolicy,
            ReplacementId = source.ReplacementId
        };
    }

    private static ReplacementRecord CloneReplacement(ReplacementRecord source)
    {
        return new ReplacementRecord
        {
            Id = source.Id,
            ItemId = source.ItemId,
            Name = source.Name,
            OriginalPath = source.OriginalPath,
            FinalPath = source.FinalPath,
            BackupPath = source.BackupPath,
            Policy = source.Policy,
            ReplacedAt = source.ReplacedAt,
            BackupHash = source.BackupHash,
            FinalSize = source.FinalSize,
            FinalWriteUtc = source.FinalWriteUtc,
            Restored = source.Restored
        };
    }
}
