using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StreamReady.Services;

public static class LibraryCatalog
{
    public sealed record LibraryInfo(string Id, string Name, string CollectionType);

    private static readonly object CacheGate = new();
    private static List<LibraryInfo>? _cache;
    private static DateTime _cacheUtc = DateTime.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    public static List<LibraryInfo> ListLibraries(ILibraryManager libraryManager, ILogger? logger = null)
    {
        lock (CacheGate)
        {
            if (_cache is not null && DateTime.UtcNow - _cacheUtc < CacheTtl)
            {
                return _cache;
            }
        }

        var byId = new Dictionary<string, LibraryInfo>(StringComparer.OrdinalIgnoreCase);

        // 1) Same source Jellyfin Dashboard uses: virtual folder dirs + ItemId from user root.
        try
        {
            foreach (var folder in libraryManager.GetVirtualFolders(true))
            {
                if (string.IsNullOrWhiteSpace(folder.ItemId) || !Guid.TryParse(folder.ItemId, out var guid))
                {
                    logger?.LogDebug("StreamReady skipped virtual folder without ItemId: {Name}", folder.Name);
                    continue;
                }

                var id = guid.ToString("N");
                byId[id] = new LibraryInfo(
                    id,
                    folder.Name ?? id,
                    folder.CollectionType?.ToString() ?? string.Empty);
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "StreamReady GetVirtualFolders failed");
        }

        // 2) Collection folders on the user root (covers cases where virtual-folder ItemId is blank).
        try
        {
            foreach (var child in libraryManager.GetUserRootFolder().Children)
            {
                if (child is not CollectionFolder folder)
                {
                    continue;
                }

                var id = folder.Id.ToString("N");
                byId[id] = new LibraryInfo(
                    id,
                    folder.Name,
                    folder.CollectionType?.ToString() ?? string.Empty);
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "StreamReady GetUserRootFolder failed");
        }

        // 3) Direct query for collection folders.
        if (byId.Count == 0)
        {
            try
            {
                var folders = libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = [BaseItemKind.CollectionFolder],
                    Recursive = true
                });

                foreach (var item in folders)
                {
                    var id = item.Id.ToString("N");
                    var collection = item is CollectionFolder cf
                        ? cf.CollectionType?.ToString() ?? string.Empty
                        : string.Empty;
                    byId[id] = new LibraryInfo(id, item.Name, collection);
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "StreamReady CollectionFolder query failed");
            }
        }

        var list = byId.Values
            .OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Only log when the catalog is (re)built — not on every AnalyzeItem hit.
        logger?.LogDebug("StreamReady discovered {Count} libraries", list.Count);

        lock (CacheGate)
        {
            _cache = list;
            _cacheUtc = DateTime.UtcNow;
        }

        return list;
    }

    public static void Invalidate()
    {
        lock (CacheGate)
        {
            _cache = null;
            _cacheUtc = DateTime.MinValue;
        }
    }
}
