using Jellyfin.Plugin.StreamReady.Configuration;
using Jellyfin.Plugin.StreamReady.Models;
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

    public async Task<CommitResult> CommitAsync(
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
        string? backupPath = null;

        if (policy.Equals("Sidecar", StringComparison.OrdinalIgnoreCase))
        {
            destPath = Path.Combine(originalDir, originalName + ".streamready" + destExt);
            File.Move(tempPath, destPath, overwrite: true);
            Refresh(item);
            return new CommitResult
            {
                FinalPath = destPath,
                OriginalPath = originalPath,
                BackupPath = null,
                Policy = "Sidecar"
            };
        }

        if (policy.Equals("Backup", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(policy))
        {
            backupPath = await BackupOriginalAsync(originalPath, config, cancellationToken).ConfigureAwait(false);
            policy = "Backup";
        }
        else
        {
            policy = "Replace";
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
        return new CommitResult
        {
            FinalPath = destPath,
            OriginalPath = originalPath,
            BackupPath = backupPath,
            Policy = policy
        };
    }

    public async Task RestoreAsync(ReplacementRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Restored)
        {
            throw new InvalidOperationException("This replacement was already restored.");
        }

        var policy = record.Policy ?? "Backup";
        if (policy.Equals("Sidecar", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(record.FinalPath) && File.Exists(record.FinalPath))
            {
                File.Delete(record.FinalPath);
                _logger.LogInformation("Removed StreamReady sidecar {Path}", record.FinalPath);
            }

            RefreshByItemId(record.ItemId);
            return;
        }

        if (!policy.Equals("Backup", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(record.BackupPath)
            || !File.Exists(record.BackupPath))
        {
            throw new InvalidOperationException("No backup file is available to restore.");
        }

        var restoreTarget = string.IsNullOrWhiteSpace(record.OriginalPath)
            ? record.FinalPath
            : record.OriginalPath;
        if (string.IsNullOrWhiteSpace(restoreTarget))
        {
            throw new InvalidOperationException("Original path is unknown.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(restoreTarget) ?? ".");

        // Remove the encoded file when it lives at a different path than the original.
        if (!string.IsNullOrWhiteSpace(record.FinalPath)
            && File.Exists(record.FinalPath)
            && !string.Equals(record.FinalPath, restoreTarget, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(record.FinalPath);
        }

        var tempRestore = restoreTarget + ".streamready.restore";
        await using (var source = File.OpenRead(record.BackupPath))
        await using (var target = File.Create(tempRestore))
        {
            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        }

        if (File.Exists(restoreTarget))
        {
            File.Delete(restoreTarget);
        }

        File.Move(tempRestore, restoreTarget, overwrite: true);
        _logger.LogInformation(
            "Restored {Target} from backup {Backup}",
            restoreTarget,
            record.BackupPath);
        RefreshByItemId(record.ItemId);
    }

    private async Task<string> BackupOriginalAsync(string originalPath, PluginConfiguration config, CancellationToken cancellationToken)
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
        return dest;
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

    private void RefreshByItemId(string itemId)
    {
        if (!Guid.TryParse(itemId, out var id))
        {
            return;
        }

        var item = _libraryManager.GetItemById(id);
        if (item is not null)
        {
            Refresh(item);
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
