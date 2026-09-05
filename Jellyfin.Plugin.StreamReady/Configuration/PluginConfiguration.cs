using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.StreamReady.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public bool Enabled { get; set; } = true;

    public bool AutoDirectPreTranscode { get; set; }

    public string SelectedLibraryIds { get; set; } = string.Empty;

    public bool IncludeExtras { get; set; }

    public string EncodingPreset { get; set; } = "Balanced";

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

    public string ReplacementPolicy { get; set; } = "Backup";

    public string BackupFolder { get; set; } = string.Empty;

    public int BackupRetentionDays { get; set; } = 30;

    public bool VerifyBeforeReplace { get; set; } = true;

    public int ScanIntervalHours { get; set; } = 6;

    public int MaxConcurrentJobs { get; set; } = 1;

    public bool PauseDuringPlayback { get; set; } = true;

    public string FfmpegPreset { get; set; } = "medium";
}
