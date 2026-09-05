namespace Jellyfin.Plugin.StreamReady.Models;

public enum EncodeAction
{
    Remux,
    AudioOnly,
    Full
}

public enum JobStatus
{
    Queued,
    Running,
    Paused,
    Failed,
    Done,
    Cancelled,
    Skipped
}

public class CandidateRecord
{
    public string Id { get; set; } = string.Empty;

    public string ItemId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? SeriesName { get; set; }

    public string ItemType { get; set; } = "Movie";

    public string LibraryId { get; set; } = string.Empty;

    public string LibraryName { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public string Container { get; set; } = string.Empty;

    public string VideoCodec { get; set; } = string.Empty;

    public string AudioCodec { get; set; } = string.Empty;

    public string VideoRange { get; set; } = string.Empty;

    public int? Width { get; set; }

    public int? Height { get; set; }

    public int? Bitrate { get; set; }

    public long? RuntimeTicks { get; set; }

    public List<string> Reasons { get; set; } = [];

    public EncodeAction PlannedAction { get; set; } = EncodeAction.Full;

    public bool Ignored { get; set; }

    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}

public class EncodeJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string CandidateId { get; set; } = string.Empty;

    public string ItemId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public EncodeAction Action { get; set; } = EncodeAction.Full;

    public JobStatus Status { get; set; } = JobStatus.Queued;

    public double Progress { get; set; }

    public string? Error { get; set; }

    public DateTime QueuedAt { get; set; } = DateTime.UtcNow;

    public DateTime? StartedAt { get; set; }

    public DateTime? FinishedAt { get; set; }

    public List<string> Reasons { get; set; } = [];
}

public class ProcessedMarker
{
    public string ItemId { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public long SizeBytes { get; set; }
}

public class JobStoreState
{
    public List<CandidateRecord> Candidates { get; set; } = [];

    public List<EncodeJob> Queue { get; set; } = [];

    public List<ProcessedMarker> Processed { get; set; } = [];
}

public class CompatibilityResult
{
    public bool NeedsWork { get; set; }

    public List<string> Reasons { get; set; } = [];

    public EncodeAction PlannedAction { get; set; } = EncodeAction.Full;

    public CandidateRecord Candidate { get; set; } = new();
}
