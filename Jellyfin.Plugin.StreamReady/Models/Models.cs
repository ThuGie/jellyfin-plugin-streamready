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

    public List<string> ReasonDetails { get; set; } = [];

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

    /// <summary>Longer ffmpeg stderr / failure detail for the queue UI.</summary>
    public string? ErrorDetail { get; set; }

    public DateTime QueuedAt { get; set; } = DateTime.UtcNow;

    public DateTime? StartedAt { get; set; }

    public DateTime? FinishedAt { get; set; }

    public List<string> Reasons { get; set; } = [];

    public string VideoRange { get; set; } = string.Empty;

    /// <summary>Human-readable encode plan (encoder, tone-map, filters, HW path).</summary>
    public string StatusDetail { get; set; } = string.Empty;

    public string VideoEncoder { get; set; } = string.Empty;

    public string HardwarePath { get; set; } = string.Empty;

    public bool ToneMap { get; set; }

    public string Filters { get; set; } = string.Empty;

    public string? Speed { get; set; }

    /// <summary>Human ETA like "~12m left".</summary>
    public string? Eta { get; set; }

    public string? OriginalPath { get; set; }

    public string? FinalPath { get; set; }

    public string? BackupPath { get; set; }

    public string? ReplacementPolicy { get; set; }

    public string? ReplacementId { get; set; }
}

public class ProbeInfo
{
    public double Duration { get; set; }

    public bool HasVideo { get; set; }

    public bool HasAudio { get; set; }

    public string? VideoCodec { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public long SizeBytes { get; set; }
}

public class CommitResult
{
    public string FinalPath { get; set; } = string.Empty;

    public string OriginalPath { get; set; } = string.Empty;

    public string? BackupPath { get; set; }

    public string Policy { get; set; } = "Backup";
}

public class ReplacementRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string ItemId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string OriginalPath { get; set; } = string.Empty;

    public string FinalPath { get; set; } = string.Empty;

    public string? BackupPath { get; set; }

    public string Policy { get; set; } = "Backup";

    public DateTime ReplacedAt { get; set; } = DateTime.UtcNow;

    public bool Restored { get; set; }
}

public class EncodeProgressUpdate
{
    public double Percent { get; set; }

    public string? Speed { get; set; }

    public string? Eta { get; set; }
}

public class EncodePlan
{
    public string Action { get; set; } = string.Empty;

    public string VideoEncoder { get; set; } = string.Empty;

    public string HardwareLabel { get; set; } = "Software (CPU)";

    public string DecodeMode { get; set; } = "software";

    public bool ToneMap { get; set; }

    public string Filters { get; set; } = string.Empty;

    public string PixelFormat { get; set; } = string.Empty;

    public bool SoftFallback { get; set; }

    public string Summary { get; set; } = string.Empty;

    public string Args { get; set; } = string.Empty;
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

    public List<ReplacementRecord> Replacements { get; set; } = [];
}

public class CompatibilityResult
{
    public bool NeedsWork { get; set; }

    public List<string> Reasons { get; set; } = [];

    public EncodeAction PlannedAction { get; set; } = EncodeAction.Full;

    public CandidateRecord Candidate { get; set; } = new();
}
