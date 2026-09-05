using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.StreamReady.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public bool Enabled { get; set; } = true;

    public bool AutoDirectPreTranscode { get; set; }

    public string SelectedLibraryIds { get; set; } = string.Empty;

    public bool IncludeExtras { get; set; }

    public string EncodingPreset { get; set; } = "Balanced";

    /// <summary>Per-library EncodingPreset overrides (empty/Inherit = use global).</summary>
    public List<LibraryOverride> LibraryOverrides { get; set; } = [];

    public string AllowedContainers { get; set; } = "mp4,m4v,mov";

    public string AllowedVideoCodecs { get; set; } = "h264";

    public string AllowedAudioCodecs { get; set; } = "aac,ac3,eac3,mp3";

    public string AllowedVideoRanges { get; set; } = "SDR";

    public double MaxFileSizeGiB { get; set; } = 25;

    public double MaxVideoBitrateMbps { get; set; } = 25;

    public int Crf { get; set; }

    public int ResolutionCap { get; set; }

    public string HardwareAccel { get; set; } = "FollowServer";

    public int AudioChannels { get; set; }

    public bool ToneMapHdr { get; set; } = true;

    /// <summary>Output container: mp4 or mkv.</summary>
    public string OutputContainer { get; set; } = "mp4";

    /// <summary>Map all audio + subtitle streams (preferred with mkv).</summary>
    public bool KeepAllAudioAndSubtitles { get; set; } = true;

    public string ReplacementPolicy { get; set; } = "Backup";

    public string BackupFolder { get; set; } = string.Empty;

    public int BackupRetentionDays { get; set; } = 30;

    public bool VerifyBeforeReplace { get; set; } = true;

    /// <summary>Skip replace when encoded file is larger than the source.</summary>
    public bool DiscardIfOutputLarger { get; set; } = true;

    public int ScanIntervalHours { get; set; } = 6;

    /// <summary>Wait after ItemAdded/Updated before analyzing (Radarr/Sonarr copies).</summary>
    public int ItemSettleDelaySeconds { get; set; } = 120;

    public int MaxConcurrentJobs { get; set; } = 1;

    public bool PauseDuringPlayback { get; set; } = true;

    /// <summary>When true, only start new encodes inside EncodeWindowStart–End on EncodeWindowDays.</summary>
    public bool EncodeWindowEnabled { get; set; }

    /// <summary>Local server time HH:mm (24h). Overnight wrap supported (e.g. 22:00–06:00).</summary>
    public string EncodeWindowStart { get; set; } = "22:00";

    /// <summary>Local server time HH:mm (24h).</summary>
    public string EncodeWindowEnd { get; set; } = "06:00";

    /// <summary>Comma-separated DayOfWeek ints (0=Sunday … 6=Saturday). Empty = every day.</summary>
    public string EncodeWindowDays { get; set; } = "0,1,2,3,4,5,6";

    /// <summary>Persisted worker pause (survives restart).</summary>
    public bool WorkerPaused { get; set; }

    public string FfmpegPreset { get; set; } = "medium";
}

public class LibraryOverride
{
    public string LibraryId { get; set; } = string.Empty;

    /// <summary>Empty or Inherit = use global EncodingPreset.</summary>
    public string EncodingPreset { get; set; } = string.Empty;
}
