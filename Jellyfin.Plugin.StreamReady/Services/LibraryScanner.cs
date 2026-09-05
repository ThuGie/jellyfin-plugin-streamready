using Jellyfin.Data.Enums;
using Jellyfin.Plugin.StreamReady.Configuration;
using Jellyfin.Plugin.StreamReady.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StreamReady.Services;

public class LibraryScanner
{
    private readonly ILibraryManager _libraryManager;
    private readonly CompatibilityAnalyzer _analyzer;
    private readonly JobStore _store;
    private readonly ILogger<LibraryScanner> _logger;
    private readonly SemaphoreSlim _scanLock = new(1, 1);

    public LibraryScanner(
        ILibraryManager libraryManager,
        CompatibilityAnalyzer analyzer,
        JobStore store,
        ILogger<LibraryScanner> logger)
    {
        _libraryManager = libraryManager;
        _analyzer = analyzer;
        _store = store;
        _logger = logger;
    }

    public bool IsScanning { get; private set; }

    public DateTime? LastScanUtc { get; private set; }

    public int LastFound { get; private set; }

    public async Task<int> ScanAsync(IProgress<double>? progress, CancellationToken cancellationToken, bool enqueueIfAuto = true)
    {
        await _scanLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        IsScanning = true;
        try
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null || !config.Enabled)
            {
                return 0;
            }

            var libraries = ResolveLibraries(config);
            if (libraries.Count == 0)
            {
                _logger.LogInformation("StreamReady scan skipped: no libraries selected");
                return 0;
            }

            var foundIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var newCandidates = new List<CandidateRecord>();
            var totalLibraries = libraries.Count;
            var index = 0;

            foreach (var library in libraries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var items = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    Recursive = true,
                    IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
                    AncestorIds = [library.Id],
                    IsVirtualItem = false
                });

                foreach (var item in items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!item.IsFileProtocol || string.IsNullOrWhiteSpace(item.Path) || !File.Exists(item.Path))
                    {
                        continue;
                    }

                    if (!config.IncludeExtras && item.ExtraType is not null)
                    {
                        continue;
                    }

                    var itemId = item.Id.ToString("N");
                    var size = item.Size ?? new FileInfo(item.Path).Length;
                    if (_store.IsProcessed(itemId, item.Path, size))
                    {
                        continue;
                    }

                    var analysis = _analyzer.Analyze(item, config, library.Id.ToString("N"), library.Name);
                    if (!analysis.NeedsWork)
                    {
                        continue;
                    }

                    foundIds.Add(analysis.Candidate.Id);
                    newCandidates.Add(analysis.Candidate);
                }

                index++;
                progress?.Report(index * 100d / totalLibraries);
            }

            _store.UpsertCandidates(newCandidates);
            _store.RemoveCandidatesNotIn(foundIds);

            if (enqueueIfAuto && config.AutoDirectPreTranscode)
            {
                var toQueue = _store.GetCandidates()
                    .Where(c => foundIds.Contains(c.Id) && !_store.IsQueued(c.ItemId))
                    .ToList();
                _store.EnqueueMany(toQueue);
            }

            LastScanUtc = DateTime.UtcNow;
            LastFound = foundIds.Count;
            _logger.LogInformation("StreamReady scan found {Count} items that need encoding", foundIds.Count);
            progress?.Report(100);
            return foundIds.Count;
        }
        finally
        {
            IsScanning = false;
            _scanLock.Release();
        }
    }

    public void AnalyzeItem(BaseItem item)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.Enabled || item is null || !item.IsFileProtocol)
        {
            return;
        }

        if (!config.IncludeExtras && item.ExtraType is not null)
        {
            return;
        }

        var libraries = ResolveLibraries(config);
        var match = libraries.FirstOrDefault(l => item.GetParents().Any(p => p.Id == l.Id) || item.Id == l.Id);
        if (match.Id == Guid.Empty && libraries.Count > 0)
        {
            match = libraries.FirstOrDefault(l =>
                item.Path is not null &&
                _libraryManager.GetItemById(l.Id) is BaseItem folder &&
                item.Path.StartsWith(folder.Path ?? "\0", StringComparison.OrdinalIgnoreCase));
        }

        if (match.Id == Guid.Empty)
        {
            return;
        }

        var itemId = item.Id.ToString("N");
        var size = item.Size ?? (File.Exists(item.Path) ? new FileInfo(item.Path).Length : 0);
        if (_store.IsProcessed(itemId, item.Path, size))
        {
            return;
        }

        var analysis = _analyzer.Analyze(item, config, match.Id.ToString("N"), match.Name);
        if (!analysis.NeedsWork)
        {
            return;
        }

        _store.UpsertCandidates([analysis.Candidate]);
        if (config.AutoDirectPreTranscode)
        {
            _store.Enqueue(analysis.Candidate);
        }
    }

    public List<(Guid Id, string Name, string CollectionType)> ResolveLibraries(PluginConfiguration config)
    {
        var selected = CompatibilityAnalyzer.Split(config.SelectedLibraryIds).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selected.Count == 0)
        {
            return [];
        }

        var result = new List<(Guid Id, string Name, string CollectionType)>();
        foreach (var folder in LibraryCatalog.ListLibraries(_libraryManager))
        {
            if (!Guid.TryParse(folder.Id, out var id))
            {
                continue;
            }

            if (!selected.Contains(folder.Id)
                && !selected.Contains(id.ToString("N"))
                && !selected.Contains(id.ToString("D")))
            {
                continue;
            }

            result.Add((id, folder.Name, folder.CollectionType));
        }

        return result;
    }
}
