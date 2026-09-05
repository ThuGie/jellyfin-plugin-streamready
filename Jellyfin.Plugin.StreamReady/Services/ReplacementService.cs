using Jellyfin.Plugin.StreamReady.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StreamReady.Services;

public class ReplacementService
{
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<ReplacementService> _logger;

    public ReplacementService(
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        ILogger<ReplacementService> logger)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    public async Task<string> CommitAsync(
        BaseItem item,
        string originalPath,
        string tempPath,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        var destExt = Path.GetExtension(tempPath);
        var originalDir = Path.GetDirectoryName(originalPath) ?? string.Empty;
        var originalName = Path.GetFileNameWithoutExtension(originalPath);
        var destPath = Path.Combine(originalDir, originalName + destExt);
        var policy = config.ReplacementPolicy ?? "Backup";

        if (policy.Equals("Sidecar", StringComparison.OrdinalIgnoreCase))
        {
            destPath = Path.Combine(originalDir, originalName + ".streamready" + destExt);
            File.Move(tempPath, destPath, overwrite: true);
            Refresh(item);
            return destPath;
        }

        if (policy.Equals("Backup", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(policy))
        {
            await BackupOriginalAsync(originalPath, config, cancellationToken).ConfigureAwait(false);
        }

        if (string.Equals(originalPath, destPath, StringComparison.OrdinalIgnoreCase))
        {
            var swap = destPath + ".swap";
            File.Move(tempPath, swap, overwrite: true);
            File.Delete(originalPath);
            File.Move(swap, destPath, overwrite: true);
        }
        else
        {
            File.Move(tempPath, destPath, overwrite: true);
            if (File.Exists(originalPath))
            {
                File.Delete(originalPath);
            }
        }

        Refresh(item);
        return destPath;
    }

    private async Task BackupOriginalAsync(string originalPath, PluginConfiguration config, CancellationToken cancellationToken)
    {
        var folder = config.BackupFolder;
        if (string.IsNullOrWhiteSpace(folder))
        {
            folder = Path.Combine(Path.GetDirectoryName(originalPath) ?? string.Empty, ".streamready-backup");
        }

        Directory.CreateDirectory(folder);
        var dest = Path.Combine(folder, Path.GetFileName(originalPath));
        if (File.Exists(dest))
        {
            dest = Path.Combine(
                folder,
                Path.GetFileNameWithoutExtension(originalPath) + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + Path.GetExtension(originalPath));
        }

        await using var source = File.OpenRead(originalPath);
        await using var target = File.Create(dest);
        await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Backed up {Original} to {Backup}", originalPath, dest);

        PruneBackups(folder, config.BackupRetentionDays);
    }

    private static void PruneBackups(string folder, int retentionDays)
    {
        if (retentionDays <= 0 || !Directory.Exists(folder))
        {
            return;
        }

        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
            catch
            {
                // ignored
            }
        }
    }

    private void Refresh(BaseItem item)
    {
        try
        {
            var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
            {
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ImageRefreshMode = MetadataRefreshMode.None,
                ReplaceAllMetadata = false,
                ForceSave = true
            };
            _providerManager.QueueRefresh(item.Id, options, RefreshPriority.High);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to queue metadata refresh for {Name}", item.Name);
        }
    }
}
