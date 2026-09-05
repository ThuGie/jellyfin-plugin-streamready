using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StreamReady.Services;

public static class LibraryCatalog
{
    public sealed record LibraryInfo(string Id, string Name, string CollectionType);

    public static List<LibraryInfo> ListLibraries(ILibraryManager libraryManager, ILogger? logger = null)
    {
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

        logger?.LogInformation("StreamReady discovered {Count} libraries", byId.Count);
        return byId.Values
            .OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
