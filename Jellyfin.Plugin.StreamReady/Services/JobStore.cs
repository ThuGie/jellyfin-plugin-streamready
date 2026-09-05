using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.StreamReady.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StreamReady.Services;

public class JobStore
{
    private readonly ILogger<JobStore> _logger;
    private readonly object _gate = new();
    private JobStoreState _state = new();
    private readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public JobStore(ILogger<JobStore> logger)
    {
        _logger = logger;
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
            foreach (var incoming in candidates)
            {
                var existing = _state.Candidates.FirstOrDefault(c => c.Id == incoming.Id);
                if (existing is null)
                {
                    _state.Candidates.Add(incoming);
                    continue;
                }

                var ignored = existing.Ignored;
                incoming.Ignored = ignored;
                incoming.AddedAt = existing.AddedAt;
                var index = _state.Candidates.IndexOf(existing);
                _state.Candidates[index] = incoming;
            }

            SaveLocked();
        }
    }

    public void RemoveCandidatesNotIn(HashSet<string> ids)
    {
        lock (_gate)
        {
            _state.Candidates.RemoveAll(c => !c.Ignored && !ids.Contains(c.Id));
            SaveLocked();
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

    public bool IsProcessed(string itemId, string path, long sizeBytes)
    {
        lock (_gate)
        {
            return _state.Processed.Any(p =>
                p.ItemId == itemId &&
                string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase) &&
                p.SizeBytes == sizeBytes);
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

    public List<EncodeJob> EnqueueMany(IEnumerable<CandidateRecord> candidates)
    {
        lock (_gate)
        {
            var created = new List<EncodeJob>();
            foreach (var candidate in candidates)
            {
                if (_state.Queue.Any(j =>
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
                    Action = candidate.PlannedAction,
                    Status = JobStatus.Queued,
                    Reasons = [.. candidate.Reasons],
                    VideoRange = candidate.VideoRange,
                    QueuedAt = DateTime.UtcNow
                };
                _state.Queue.Insert(0, job);
                created.Add(CloneJob(job));
            }

            SaveLocked();
            return created;
        }
    }

    public EncodeJob? DequeueNext()
    {
        lock (_gate)
        {
            var job = _state.Queue
                .Where(j => j.Status == JobStatus.Queued)
                .OrderBy(j => j.QueuedAt)
                .FirstOrDefault();
            if (job is null)
            {
                return null;
            }

            job.Status = JobStatus.Running;
            job.StartedAt = DateTime.UtcNow;
            job.Progress = 0;
            SaveLocked();
            return CloneJob(job);
        }
    }

    public IReadOnlyList<EncodeJob> GetQueue()
    {
        lock (_gate)
        {
            return _state.Queue.Select(CloneJob).ToList();
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

    public void UpdateProgress(string id, double progress)
    {
        lock (_gate)
        {
            var job = _state.Queue.FirstOrDefault(j => j.Id == id);
            if (job is null)
            {
                return;
            }

            job.Progress = Math.Clamp(progress, 0, 100);
        }
    }

    public void Complete(string id, string itemId, string path, long sizeBytes)
    {
        lock (_gate)
        {
            var job = _state.Queue.FirstOrDefault(j => j.Id == id);
            if (job is not null)
            {
                job.Status = JobStatus.Done;
                job.Progress = 100;
                job.FinishedAt = DateTime.UtcNow;
            }

            _state.Candidates.RemoveAll(c => c.ItemId == itemId);
            _state.Processed.RemoveAll(p => p.ItemId == itemId);
            _state.Processed.Add(new ProcessedMarker
            {
                ItemId = itemId,
                Path = path,
                SizeBytes = sizeBytes
            });
            SaveLocked();
        }
    }

    public void Fail(string id, string error)
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
            job.FinishedAt = DateTime.UtcNow;
            SaveLocked();
        }
    }

    public void Requeue(string id)
    {
        lock (_gate)
        {
            var job = _state.Queue.FirstOrDefault(j => j.Id == id);
            if (job is null)
            {
                return;
            }

            job.Status = JobStatus.Queued;
            job.Error = null;
            job.Progress = 0;
            job.StartedAt = null;
            job.FinishedAt = null;
            job.QueuedAt = DateTime.UtcNow;
            SaveLocked();
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
            }

            return false;
        }
    }

    public void Skip(string id)
    {
        lock (_gate)
        {
            var job = _state.Queue.FirstOrDefault(j => j.Id == id);
            if (job is null)
            {
                return;
            }

            job.Status = JobStatus.Skipped;
            job.FinishedAt = DateTime.UtcNow;
            SaveLocked();
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
            if (!File.Exists(path))
            {
                return;
            }

            var json = File.ReadAllText(path);
            _state = JsonSerializer.Deserialize<JobStoreState>(json, _json) ?? new JobStoreState();
            foreach (var job in _state.Queue.Where(j => j.Status == JobStatus.Running))
            {
                job.Status = JobStatus.Queued;
                job.Progress = 0;
                job.StartedAt = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load StreamReady job store");
            _state = new JobStoreState();
        }
    }

    private void SaveLocked()
    {
        try
        {
            var path = StorePath();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(path, JsonSerializer.Serialize(_state, _json));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save StreamReady job store");
        }
    }

    private static string StorePath()
    {
        var folder = Plugin.Instance?.DataFolderPath ?? Path.Combine(Path.GetTempPath(), "streamready");
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, "jobs.json");
    }

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
            Action = source.Action,
            Status = source.Status,
            Progress = source.Progress,
            Error = source.Error,
            QueuedAt = source.QueuedAt,
            StartedAt = source.StartedAt,
            FinishedAt = source.FinishedAt,
            Reasons = [.. source.Reasons],
            VideoRange = source.VideoRange
        };
    }
}
