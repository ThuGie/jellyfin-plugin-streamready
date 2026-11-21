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
    private readonly JobStore _store;
    private readonly string? _journalDirectory;
    private readonly MediaBrowser.Controller.Session.ISessionManager _sessions;

    public ReplacementService(
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        ILogger<ReplacementService> logger,
        JobStore store,
        MediaBrowser.Controller.Session.ISessionManager sessions)
        : this(libraryManager, providerManager, fileSystem, logger, store, sessions, null)
    {
    }

    internal ReplacementService(
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        ILogger<ReplacementService> logger,
        JobStore store,
        MediaBrowser.Controller.Session.ISessionManager sessions,
        string? journalDirectory)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _logger = logger;
        _store = store;
        _sessions = sessions;
        _journalDirectory = journalDirectory;
    }

    private readonly SemaphoreSlim _mutation = new(1, 1);

    private sealed record Operation(
        string Id, string Original, string Output, string Staging, string? Backup, string? Rollback,
        long StagingLength, DateTime StagingWriteUtc, long SourceLength, DateTime SourceWriteUtc,
        string? JobId = null, string? ItemId = null, string? Name = null, string? Policy = null,
        string? BackupHash = null, string? RestoreId = null);

    private string JournalPath => Path.Combine(_journalDirectory ?? JobStore.DataDirectory(), "media-operation.json");

    public string? PendingOperation =>
        File.Exists(JournalPath) ? File.ReadAllText(JournalPath) : null;

    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        await _mutation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_store.IsHealthy || !File.Exists(JournalPath))
            {
                return;
            }

            var operation = System.Text.Json.JsonSerializer.Deserialize<Operation>(File.ReadAllText(JournalPath));
            if (operation is null)
            {
                return;
            }

            var history = _store.GetReplacement(operation.RestoreId ?? operation.Id);
            var finalized = history is not null && (operation.RestoreId is null
                ? Matches(operation.Output, history.FinalSize, history.FinalWriteUtc)
                : history.Restored && (history.Policy == "Sidecar"
                    ? !File.Exists(operation.Output)
                    : Matches(operation.Original, operation.StagingLength, operation.StagingWriteUtc)));
            if (!finalized && operation.JobId is string jobId && operation.ItemId is string itemId
                && operation.Policy is string policy && Matches(operation.Output, operation.StagingLength, operation.StagingWriteUtc)
                && (policy == "Sidecar"
                    ? Matches(operation.Original, operation.SourceLength, operation.SourceWriteUtc)
                    : operation.Rollback is not null && Matches(operation.Rollback, operation.SourceLength, operation.SourceWriteUtc)
                        && (SamePath(operation.Original, operation.Output) || !File.Exists(operation.Original))))
            {
                var markerPath = policy == "Sidecar" ? operation.Original : operation.Output;
                _store.Complete(jobId, itemId, markerPath, new FileInfo(markerPath).Length, new ReplacementRecord
                {
                    Id = operation.Id,
                    ItemId = itemId,
                    Name = operation.Name ?? "",
                    OriginalPath = operation.Original,
                    FinalPath = operation.Output,
                    BackupPath = operation.Backup,
                    BackupHash = operation.BackupHash,
                    Policy = policy,
                    FinalSize = operation.StagingLength,
                    FinalWriteUtc = operation.StagingWriteUtc
                });
                finalized = true;
                RefreshByItemId(itemId);
            }

            if (!finalized && operation.RestoreId is null && operation.JobId is not null
                && Matches(operation.Original, operation.SourceLength, operation.SourceWriteUtc)
                && (SamePath(operation.Original, operation.Output) || !File.Exists(operation.Output))
                && (operation.Rollback is null || !File.Exists(operation.Rollback)))
            {
                File.Delete(JournalPath);
                _logger.LogWarning("Recovered original media for interrupted StreamReady operation {Id}", operation.Id);
                return;
            }

            if (finalized)
            {
                // Keep any rollback copy for an administrator after an interrupted commit.
                File.Delete(JournalPath);
                _logger.LogWarning("Recovered completed StreamReady operation {Id}; rollback path: {Path}",
                    operation.Id, operation.Rollback);
                return;
            }

            _logger.LogError("StreamReady operation {Id} needs recovery. Journal: {Path}", operation.Id, JournalPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to reconcile the StreamReady media journal");
        }
        finally
        {
            _mutation.Release();
        }
    }

    private static bool Matches(string path, long? size, DateTime? writeUtc)
    {
        var file = new FileInfo(path);
        return file.Exists && file.Length == size && file.LastWriteTimeUtc == writeUtc;
    }

    private void EnsureReady()
    {
        if (!_store.IsHealthy || File.Exists(JournalPath))
        {
            throw new InvalidOperationException("StreamReady media changes are blocked pending recovery.");
        }
    }

    public async Task CommitAsync(
        BaseItem item,
        string jobId,
        string originalPath,
        string tempPath,
        PluginConfiguration config,
        Action beforePromotion,
        Action<ReplacementRecord> complete,
        CancellationToken cancellationToken)
    {
        await _mutation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureReady();
            var id = Guid.NewGuid().ToString("N");
            var policy = (config.ReplacementPolicy ?? "Backup").Trim();
            policy = policy.ToLowerInvariant() switch
            {
                "" or "backup" => "Backup",
                "replace" => "Replace",
                "sidecar" => "Sidecar",
                _ => policy
            };
            if (policy is not ("Backup" or "Replace" or "Sidecar"))
            {
                throw new InvalidOperationException("Invalid replacement policy.");
            }

            var extension = Path.GetExtension(tempPath);
            var stem = Path.Combine(Path.GetDirectoryName(originalPath)!, Path.GetFileNameWithoutExtension(originalPath));
            var destination = stem + (policy == "Sidecar" ? ".streamready" : "") + extension;
            var samePath = SamePath(originalPath, destination);
            if (!samePath && File.Exists(destination))
            {
                throw new IOException("The output destination already exists.");
            }

            string? backup = null;
            string? backupHash = null;
            if (policy == "Backup")
            {
                var folder = string.IsNullOrWhiteSpace(config.BackupFolder)
                    ? Path.Combine(Path.GetDirectoryName(originalPath)!, ".streamready-backup")
                    : config.BackupFolder;
                Directory.CreateDirectory(folder);
                backup = Path.Combine(folder, id + Path.GetExtension(originalPath));
                backupHash = await CopyVerifiedAsync(originalPath, backup, cancellationToken).ConfigureAwait(false);
            }

            beforePromotion();
            var rollback = policy == "Sidecar" ? null : stem + "." + id + ".streamready-rollback";
            var stagingInfo = new FileInfo(tempPath);
            var originalInfo = new FileInfo(originalPath);
            var operation = new Operation(id, originalPath, destination, tempPath, backup, rollback,
                stagingInfo.Length, stagingInfo.LastWriteTimeUtc, originalInfo.Length, originalInfo.LastWriteTimeUtc,
                jobId, item.Id.ToString("N"), item.Name, policy, backupHash);
            JobStore.WriteSnapshot(JournalPath, System.Text.Json.JsonSerializer.Serialize(operation));
            if (samePath)
            {
                File.Replace(tempPath, originalPath, rollback);
            }
            else
            {
                File.Move(tempPath, destination);
                if (rollback is not null)
                {
                    File.Move(originalPath, rollback);
                }
            }

            var output = new FileInfo(destination);
            complete(new ReplacementRecord
            {
                Id = id,
                ItemId = item.Id.ToString("N"),
                Name = item.Name,
                OriginalPath = originalPath,
                FinalPath = destination,
                BackupPath = backup,
                BackupHash = backupHash,
                Policy = policy,
                FinalSize = output.Length,
                FinalWriteUtc = output.LastWriteTimeUtc
            });

            if (rollback is not null)
            {
                File.Delete(rollback);
            }

            File.Delete(JournalPath);
            PruneOwnedBackups(config.BackupRetentionDays);
            Refresh(item);
        }
        finally
        {
            _mutation.Release();
        }
    }

    public async Task RestoreAsync(ReplacementRecord record, CancellationToken cancellationToken)
    {
        await _mutation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureReady();
            record = _store.GetReplacement(record.Id)
                ?? throw new InvalidOperationException("Replacement history is unavailable.");
            if (record.Restored)
            {
                return;
            }

            var output = new FileInfo(record.FinalPath);
            if (!output.Exists || output.Length != record.FinalSize || output.LastWriteTimeUtc != record.FinalWriteUtc)
            {
                throw new InvalidOperationException("The output identity cannot be verified. Restore the backup manually.");
            }

            var id = Guid.NewGuid().ToString("N");
            var staging = record.OriginalPath + "." + id + ".streamready-restore";
            var rollback = record.FinalPath + "." + id + ".streamready-rollback";
            var samePath = SamePath(record.OriginalPath, record.FinalPath);
            if (record.Policy != "Sidecar")
            {
                if (record.Policy != "Backup" || record.BackupPath is null || !File.Exists(record.BackupPath))
                {
                    throw new InvalidOperationException("No backup file is available to restore.");
                }

                if (!samePath && File.Exists(record.OriginalPath))
                {
                    throw new IOException("The restore destination already exists.");
                }

                var hash = await CopyVerifiedAsync(record.BackupPath, staging, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(hash, record.BackupHash, StringComparison.Ordinal))
                {
                    File.Delete(staging);
                    throw new InvalidOperationException("The backup no longer matches replacement history.");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            output.Refresh();
            if (!output.Exists || output.Length != record.FinalSize || output.LastWriteTimeUtc != record.FinalWriteUtc
                || _sessions.Sessions.Any(s => s.NowPlayingItem?.Id.ToString("N") == record.ItemId
                    || SamePath(s.NowPlayingItem?.Path, record.FinalPath)))
            {
                throw new InvalidOperationException("The output changed or is playing.");
            }

            var staged = new FileInfo(staging);
            JobStore.WriteSnapshot(JournalPath, System.Text.Json.JsonSerializer.Serialize(
                new Operation(id, record.OriginalPath, record.FinalPath, staging, record.BackupPath, rollback,
                    staged.Exists ? staged.Length : 0, staged.LastWriteTimeUtc,
                    output.Length, output.LastWriteTimeUtc, RestoreId: record.Id)));
            if (record.Policy == "Sidecar")
            {
                File.Move(record.FinalPath, rollback);
            }
            else if (samePath)
            {
                File.Replace(staging, record.OriginalPath, rollback);
            }
            else
            {
                File.Move(staging, record.OriginalPath);
                File.Move(record.FinalPath, rollback);
            }

            _store.MarkRestored(record.Id);
            File.Delete(rollback);
            File.Delete(JournalPath);
            RefreshByItemId(record.ItemId);
        }
        finally
        {
            _mutation.Release();
        }
    }

    private static bool SamePath(string? first, string second) =>
        !string.IsNullOrWhiteSpace(first) && string.Equals(Path.GetFullPath(first), Path.GetFullPath(second),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private void PruneOwnedBackups(int retentionDays)
    {
        if (retentionDays <= 0)
        {
            return;
        }

        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        foreach (var record in _store.GetReplacements(includeRestored: true))
        {
            var path = record.BackupPath;
            if (path is null || record.BackupHash is null || record.ReplacedAt >= cutoff
                || Path.GetFileNameWithoutExtension(path) != record.Id || !File.Exists(path))
            {
                continue;
            }

            try
            {
                string hash;
                using (var stream = File.OpenRead(path))
                {
                    hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
                }
                if (hash == record.BackupHash)
                {
                    File.Delete(path);
                }
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Could not prune StreamReady backup {Path}", path);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Could not prune StreamReady backup {Path}", path);
            }
        }
    }

    private static async Task<string> CopyVerifiedAsync(string sourcePath, string destination, CancellationToken cancellationToken)
    {
        var created = false;
        try
        {
            await using var source = File.OpenRead(sourcePath);
            await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            created = true;
            var length = source.Length;
            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            target.Flush(flushToDisk: true);
            source.Position = 0;
            target.Position = 0;
            var sourceHash = await System.Security.Cryptography.SHA256.HashDataAsync(source, cancellationToken).ConfigureAwait(false);
            var targetHash = await System.Security.Cryptography.SHA256.HashDataAsync(target, cancellationToken).ConfigureAwait(false);
            if (target.Length != length || !sourceHash.AsSpan().SequenceEqual(targetHash))
            {
                throw new IOException("The media copy could not be verified.");
            }

            return Convert.ToHexString(sourceHash);
        }
        catch
        {
            if (created)
            {
                File.Delete(destination);
            }

            throw;
        }
    }

    private void RefreshByItemId(string itemId)
    {
        if (!Guid.TryParse(itemId, out var id))
        {
            return;
        }

        try
        {
            var item = _libraryManager.GetItemById(id);
            if (item is not null)
            {
                Refresh(item);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to locate item {Id} for metadata refresh", id);
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
