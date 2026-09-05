using Jellyfin.Plugin.StreamReady.Services;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.StreamReady.ScheduledTasks;

public class ScanLibraryTask : IScheduledTask
{
    private readonly LibraryScanner _scanner;

    public ScanLibraryTask(LibraryScanner scanner)
    {
        _scanner = scanner;
    }

    public string Name => "StreamReady library scan";

    public string Key => "StreamReadyLibraryScan";

    public string Description => "Finds movies and episodes that are oversized or incompatible. In Manual mode they are listed; in Auto Direct Pre-Transcode they are also queued.";

    public string Category => "StreamReady";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await _scanner.ScanAsync(progress, cancellationToken).ConfigureAwait(false);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(6).Ticks
            }
        ];
    }
}
