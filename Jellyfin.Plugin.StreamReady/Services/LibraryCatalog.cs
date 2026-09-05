using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.StreamReady.Services;

public static class LibraryCatalog
{
    public sealed record LibraryInfo(string Id, string Name, string CollectionType);

    public static List<LibraryInfo> ListLibraries(ILibraryManager libraryManager)
    {
        var byId = new Dictionary<string, LibraryInfo>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var root = libraryManager.GetUserRootFolder();
            foreach (var child in root.Children)
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
        catch
        {
            // Fall through to virtual-folder listing.
        }

        if (byId.Count == 0)
        {
            try
            {
                foreach (var folder in libraryManager.GetVirtualFolders(true))
                {
                    if (string.IsNullOrWhiteSpace(folder.ItemId) || !Guid.TryParse(folder.ItemId, out var guid))
                    {
                        continue;
                    }

                    var id = guid.ToString("N");
                    byId[id] = new LibraryInfo(
                        id,
                        folder.Name,
                        folder.CollectionType?.ToString() ?? string.Empty);
                }
            }
            catch
            {
                // ignored
            }
        }

        if (byId.Count == 0)
        {
            try
            {
                var folders = libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = [BaseItemKind.CollectionFolder],
                    Recursive = false
                });

                foreach (var item in folders)
                {
                    var id = item.Id.ToString("N");
                    var collection = string.Empty;
                    if (item is CollectionFolder cf)
                    {
                        collection = cf.CollectionType?.ToString() ?? string.Empty;
                    }

                    byId[id] = new LibraryInfo(id, item.Name, collection);
                }
            }
            catch
            {
                // ignored
            }
        }

        return byId.Values
            .OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
