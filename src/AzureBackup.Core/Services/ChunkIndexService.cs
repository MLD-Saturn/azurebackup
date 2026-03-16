using System.Text.Json;
using AzureBackup.Core.Models;

namespace AzureBackup.Core.Services;

/// <summary>
/// Service for managing the chunk index, tracking chunk-to-file associations,
/// detecting orphans, and maintaining storage health.
/// </summary>
public class ChunkIndexService
{
    private readonly LocalDatabaseService _databaseService;
    private readonly IBlobStorageService _blobService;
    private readonly EncryptionService _encryptionService;
    
    private const string IndexBackupBlobName = "index/chunk-index-backup.enc";

    /// <summary>
    /// Event raised for diagnostic logging.
    /// </summary>
    public event EventHandler<string>? DiagnosticLog;

    private void Log(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        DiagnosticLog?.Invoke(this, $"[{timestamp}] [ChunkIndex] {message}");
    }

    public ChunkIndexService(LocalDatabaseService databaseService, IBlobStorageService blobService, EncryptionService encryptionService)
    {
        ArgumentNullException.ThrowIfNull(databaseService);
        ArgumentNullException.ThrowIfNull(blobService);
        ArgumentNullException.ThrowIfNull(encryptionService);
        _databaseService = databaseService;
        _blobService = blobService;
        _encryptionService = encryptionService;
    }

    #region Reference Management

    /// <summary>
    /// Adds a reference from a file to a chunk.
    /// Creates the chunk entry if it doesn't exist.
    /// </summary>
    /// <param name="chunkHash">The chunk hash</param>
    /// <param name="filePath">The file path referencing this chunk</param>
    /// <param name="chunkIndex">The index of this chunk within the file</param>
    /// <param name="sizeBytes">Size of the chunk in bytes</param>
    /// <param name="tier">Storage tier of the chunk</param>
    /// <param name="isNewChunk">Whether this chunk was just uploaded (vs. deduplicated)</param>
    public void AddReference(string chunkHash, string filePath, int chunkIndex, 
        long sizeBytes, StorageTier tier, bool isNewChunk)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var entry = _databaseService.GetChunkIndexEntry(chunkHash);
        
        if (entry == null)
        {
            // New chunk - create entry
            entry = new ChunkIndexEntry
            {
                ChunkHash = chunkHash,
                FirstUploadedAt = DateTime.UtcNow,
                OriginalUploaderPath = filePath,
                CurrentTier = tier,
                SizeBytes = sizeBytes,
                LastVerifiedAt = DateTime.UtcNow,
                ReferenceCount = 0,
                ReferencingFiles = []
            };
            Log($"Created new chunk index entry for {chunkHash[..8]}...");
        }

        // Check if this file already references this chunk
        var existingRef = entry.ReferencingFiles.Find(r => 
            r.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase) && 
            r.ChunkIndex == chunkIndex);

        if (existingRef == null)
        {
            // Add new reference
            entry.ReferencingFiles.Add(new ChunkFileReference
            {
                FilePath = filePath,
                ChunkIndex = chunkIndex,
                ReferencedAt = DateTime.UtcNow
            });
            entry.ReferenceCount = entry.ReferencingFiles.Count;
            Log($"Added reference: {filePath} -> chunk {chunkHash[..8]}... (ref count: {entry.ReferenceCount})");
        }

        // Update tier if this was a new upload
        if (isNewChunk)
        {
            entry.CurrentTier = tier;
            entry.LastVerifiedAt = DateTime.UtcNow;
        }

        _databaseService.SaveChunkIndexEntry(entry);
    }

    /// <summary>
    /// Removes all references from a file to its chunks.
    /// Deletes chunks that reach reference count 0.
    /// </summary>
    /// <param name="filePath">The file path to remove references for</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of orphaned chunks deleted</returns>
    public async Task<int> RemoveFileReferencesAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        Log($"Removing all chunk references for file: {filePath}");

        var entries = _databaseService.GetChunkEntriesForFile(filePath);
        var deletedCount = 0;

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Remove references from this file
            entry.ReferencingFiles.RemoveAll(r => 
                r.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));
            entry.ReferenceCount = entry.ReferencingFiles.Count;

            if (entry.ReferenceCount == 0)
            {
                // Delete the orphaned chunk immediately
                Log($"Chunk {entry.ChunkHash[..8]}... has no references, deleting from Azure...");
                try
                {
                    await _blobService.DeleteBlobAsync($"chunks/{entry.ChunkHash}", cancellationToken);
                    _databaseService.DeleteChunkIndexEntry(entry.ChunkHash);
                    deletedCount++;
                    Log($"Deleted orphaned chunk {entry.ChunkHash[..8]}...");
                }
                catch (Exception ex)
                {
                    Log($"Failed to delete chunk {entry.ChunkHash[..8]}...: {ex.Message}");
                    // Keep the entry marked as orphan for later cleanup
                    _databaseService.SaveChunkIndexEntry(entry);
                }
            }
            else
            {
                _databaseService.SaveChunkIndexEntry(entry);
                Log($"Updated chunk {entry.ChunkHash[..8]}... ref count to {entry.ReferenceCount}");
            }
        }

        return deletedCount;
    }

    /// <summary>
    /// Updates chunk references when a file is modified.
    /// Removes references to old chunks and adds references to new chunks.
    /// </summary>
    /// <param name="filePath">The file path</param>
    /// <param name="oldChunkHashes">Hashes of chunks from previous version</param>
    /// <param name="newChunks">New chunk information</param>
    /// <param name="tier">Storage tier for new chunks</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task UpdateFileChunksAsync(
        string filePath,
        IList<string> oldChunkHashes,
        IList<(string hash, int index, long size, bool isNew)> newChunks,
        StorageTier tier,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        Log($"Updating chunk references for modified file: {filePath}");

        // Find chunks that are no longer used by this file
        var newHashSet = new HashSet<string>(newChunks.Select(c => c.hash), StringComparer.Ordinal);
        var removedHashes = oldChunkHashes.Where(h => !newHashSet.Contains(h)).ToList();

        // Remove references to old chunks
        foreach (var hash in removedHashes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = _databaseService.GetChunkIndexEntry(hash);
            if (entry != null)
            {
                entry.ReferencingFiles.RemoveAll(r => 
                    r.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));
                entry.ReferenceCount = entry.ReferencingFiles.Count;

                if (entry.ReferenceCount == 0)
                {
                    // Delete immediately
                    try
                    {
                        await _blobService.DeleteBlobAsync($"chunks/{hash}", cancellationToken);
                        _databaseService.DeleteChunkIndexEntry(hash);
                        Log($"Deleted orphaned chunk {hash[..8]}... (removed from modified file)");
                    }
                    catch (Exception ex)
                    {
                        Log($"Failed to delete chunk {hash[..8]}...: {ex.Message}");
                        _databaseService.SaveChunkIndexEntry(entry);
                    }
                }
                else
                {
                    _databaseService.SaveChunkIndexEntry(entry);
                }
            }
        }

        // Add references to new chunks
        foreach (var (hash, index, size, isNew) in newChunks)
        {
            AddReference(hash, filePath, index, size, tier, isNew);
        }
    }

    #endregion

    #region Orphan Detection and Cleanup

    /// <summary>
    /// Scans for orphaned chunks in Azure that aren't referenced by any file.
    /// Uses a lightweight index summary (hash + refcount only) to minimize memory,
    /// then parallel Azure property queries for orphan details.
    /// </summary>
    /// <param name="progress">Progress reporter</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Scan result with orphan details</returns>
    public async Task<OrphanScanResult> ScanForOrphansAsync(
        IProgress<(int scanned, int total, string currentChunk)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Log("Starting orphan scan...");
        var startTime = DateTime.UtcNow;
        var result = new OrphanScanResult { ScannedAt = startTime };

        // Get all chunk blobs from Azure
        var azureChunks = await ListAzureChunksAsync(cancellationToken);
        var totalChunks = azureChunks.Count;
        Log($"Found {totalChunks} chunks in Azure");

        // Bulk-load lightweight chunk index summary for fast lookups
        // Only loads hash + refcount + size + tier — not the full ReferencingFiles list
        // At 1M chunks this uses ~80 MB vs ~1.5 GB for full ChunkIndexEntry objects
        var indexSummary = _databaseService.GetChunkIndexSummaryMap();
        Log($"Loaded lightweight index summary for {indexSummary.Count} chunks");

        // Phase 1: Identify orphans using local lookups only (no HTTP)
        var orphanHashes = new List<(string hash, long cachedSize, StorageTier cachedTier)>();
        var scanned = 0;
        foreach (var chunkHash in azureChunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            scanned++;

            if (scanned % 500 == 0 || scanned == totalChunks)
            {
                progress?.Report((scanned, totalChunks, chunkHash));
            }

            if (indexSummary.TryGetValue(chunkHash, out var summary))
            {
                if (summary.ReferenceCount == 0)
                {
                    orphanHashes.Add((chunkHash, summary.SizeBytes, summary.Tier));
                }
            }
            else
            {
                // Not in index at all — orphan with no cached info
                orphanHashes.Add((chunkHash, 0, StorageTier.Hot));
            }
        }

        Log($"Identified {orphanHashes.Count} potential orphans. Fetching details in parallel...");

        // Phase 2: Fetch size/tier for orphans in parallel from Azure
        const int maxParallelQueries = 32;
        int queried = 0;
        object resultLock = new();

        await Parallel.ForEachAsync(
            orphanHashes,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = maxParallelQueries,
                CancellationToken = cancellationToken
            },
            async (item, ct) =>
            {
                var (chunkHash, cachedSize, cachedTier) = item;
                long sizeBytes = cachedSize;
                StorageTier tier = cachedTier;

                try
                {
                    var (size, blobTier) = await GetChunkInfoFromAzureAsync(chunkHash, ct);
                    sizeBytes = size;
                    tier = blobTier;
                }
                catch
                {
                    // Use cached values from index summary
                }

                var orphanEntry = new ChunkIndexEntry
                {
                    ChunkHash = chunkHash,
                    SizeBytes = sizeBytes,
                    CurrentTier = tier,
                    ReferenceCount = 0,
                    ReferencingFiles = []
                };

                lock (resultLock)
                {
                    result.OrphanedChunks.Add(orphanEntry);
                    result.TotalOrphanSizeBytes += sizeBytes;
                }

                var count = Interlocked.Increment(ref queried);
                if (count % 50 == 0 || count == orphanHashes.Count)
                {
                    progress?.Report((scanned, totalChunks, $"Querying orphan details... {count}/{orphanHashes.Count}"));
                }
            });

        result.ChunksScanned = scanned;
        result.ScanDuration = DateTime.UtcNow - startTime;

        Log($"Orphan scan complete: {result.OrphanedChunks.Count} orphans found, " +
            $"{FormatHelper.FormatBytes(result.TotalOrphanSizeBytes)} total");

        return result;
    }

    /// <summary>
    /// Deletes orphaned chunks from Azure using parallel blob deletions.
    /// </summary>
    /// <param name="orphans">List of orphaned chunks to delete</param>
    /// <param name="progress">Progress reporter</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Cleanup result</returns>
    public async Task<CleanupResult> CleanupOrphansAsync(
        IList<ChunkIndexEntry> orphans,
        IProgress<(int deleted, int total, string currentChunk)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Log($"Starting parallel cleanup of {orphans.Count} orphaned chunks...");
        var result = new CleanupResult { CleanedAt = DateTime.UtcNow };

        const int maxParallelDeletes = 16;
        int deleted = 0;
        object resultLock = new();

        await Parallel.ForEachAsync(
            orphans,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = maxParallelDeletes,
                CancellationToken = cancellationToken
            },
            async (orphan, ct) =>
            {
                try
                {
                    await _blobService.DeleteBlobAsync($"chunks/{orphan.ChunkHash}", ct);
                    _databaseService.DeleteChunkIndexEntry(orphan.ChunkHash);

                    lock (resultLock)
                    {
                        result.ChunksDeleted++;
                        result.BytesFreed += orphan.SizeBytes;
                    }

                    var count = Interlocked.Increment(ref deleted);
                    if (count % 20 == 0 || count == orphans.Count)
                    {
                        progress?.Report((count, orphans.Count, orphan.ChunkHash));
                        Log($"Cleanup progress: {count}/{orphans.Count} orphans deleted");
                    }
                }
                catch (Exception ex)
                {
                    lock (resultLock)
                    {
                        result.FailedDeletions++;
                        result.Errors.Add($"Failed to delete {orphan.ChunkHash[..8]}...: {ex.Message}");
                    }
                    Log($"Failed to delete orphan {orphan.ChunkHash[..8]}...: {ex.Message}");
                }
            });

        Log($"Cleanup complete: {result.ChunksDeleted} deleted, {result.FailedDeletions} failed, " +
            $"{FormatHelper.FormatBytes(result.BytesFreed)} freed");

        return result;
    }

    /// <summary>
    /// Performs a lightweight verification after backup to ensure chunk references are consistent.
    /// </summary>
    /// <param name="filePath">The file that was just backed up</param>
    /// <param name="chunkHashes">The chunk hashes for this file</param>
    public void VerifyBackupConsistency(string filePath, IList<string> chunkHashes)
    {
        Log($"Verifying backup consistency for {filePath}...");
        
        foreach (var hash in chunkHashes)
        {
            var entry = _databaseService.GetChunkIndexEntry(hash);
            if (entry == null)
            {
                Log($"WARNING: Chunk {hash[..8]}... not found in index after backup!");
            }
            else if (!entry.ReferencingFiles.Any(r => 
                r.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase)))
            {
                Log($"WARNING: File {filePath} not found in references for chunk {hash[..8]}...");
            }
        }
    }

    #endregion

    #region Index Statistics

    /// <summary>
    /// Gets summary statistics for the chunk index.
    /// </summary>
    public ChunkIndexSummary GetIndexSummary()
    {
        var entries = _databaseService.GetAllChunkIndexEntries();
        
        var summary = new ChunkIndexSummary
        {
            TotalChunks = entries.Count,
            TotalSizeBytes = entries.Sum(e => e.SizeBytes),
            OrphanCount = entries.Count(e => e.ReferenceCount == 0),
            OrphanSizeBytes = entries.Where(e => e.ReferenceCount == 0).Sum(e => e.SizeBytes),
            SharedChunks = entries.Count(e => e.ReferenceCount > 1),
            TierBreakdown = []
        };

        // Calculate deduplication savings
        foreach (var entry in entries.Where(e => e.ReferenceCount > 1))
        {
            summary.DeduplicationSavingsBytes += entry.SizeBytes * (entry.ReferenceCount - 1);
        }

        // Calculate tier breakdown
        foreach (StorageTier tier in Enum.GetValues<StorageTier>())
        {
            var tierEntries = entries.Where(e => e.CurrentTier == tier).ToList();
            var tierSize = tierEntries.Sum(e => e.SizeBytes);
            
            summary.TierBreakdown[tier] = new TierStatistics
            {
                ChunkCount = tierEntries.Count,
                TotalSizeBytes = tierSize
            };
        }

        summary.LastFullRebuildAt = _databaseService.GetIndexMetadata("LastFullRebuildAt");
        summary.LastAzureSyncAt = _databaseService.GetIndexMetadata("LastAzureSyncAt");

        return summary;
    }

    #endregion

    #region Azure Backup/Restore

    /// <summary>
    /// Backs up the chunk index to Azure storage.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task BackupIndexToAzureAsync(CancellationToken cancellationToken = default)
    {
        Log("Backing up chunk index to Azure...");

        var backup = new ChunkIndexBackup
        {
            Version = 1,
            CreatedAt = DateTime.UtcNow,
            Entries = _databaseService.GetAllChunkIndexEntries(),
            Summary = GetIndexSummary()
        };

        var json = JsonSerializer.Serialize(backup, new JsonSerializerOptions { WriteIndented = false });
        var plaintext = System.Text.Encoding.UTF8.GetBytes(json);
        var data = _encryptionService.Encrypt(plaintext);

        await _blobService.UploadBlobAsync(IndexBackupBlobName, data, StorageTier.Cool, cancellationToken);

        // Delete legacy unencrypted backup if it exists
        try
        {
            await _blobService.DeleteBlobAsync("index/chunk-index-backup.json", cancellationToken);
        }
        catch { /* Ignore if legacy blob doesn't exist */ }

        _databaseService.SetIndexMetadata("LastAzureSyncAt", DateTime.UtcNow);
        Log($"Index backup complete: {backup.Entries.Count} entries, {FormatHelper.FormatBytes(data.Length)} stored (encrypted)");
    }

    /// <summary>
    /// Restores the chunk index from Azure storage.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if restore was successful</returns>
    public async Task<bool> RestoreIndexFromAzureAsync(CancellationToken cancellationToken = default)
    {
        Log("Restoring chunk index from Azure...");

        try
        {
            var data = await _blobService.DownloadBlobAsync(IndexBackupBlobName, cancellationToken);
            var plaintext = _encryptionService.Decrypt(data);
            var json = System.Text.Encoding.UTF8.GetString(plaintext);
            var backup = JsonSerializer.Deserialize<ChunkIndexBackup>(json);

            if (backup == null)
            {
                Log("Failed to deserialize index backup");
                return false;
            }

            // Clear existing index and restore from backup
            _databaseService.ClearChunkIndex();
            foreach (var entry in backup.Entries)
            {
                _databaseService.SaveChunkIndexEntry(entry);
            }

            Log($"Index restored: {backup.Entries.Count} entries from {backup.CreatedAt}");
            return true;
        }
        catch (Exception ex)
        {
            Log($"Failed to restore index from Azure: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Rebuilds the chunk index by scanning all metadata blobs in Azure.
    /// Uses parallel metadata downloads and batched chunk info lookups.
    /// </summary>
    /// <param name="progress">Progress reporter</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task RebuildIndexFromAzureAsync(
        IProgress<(int processed, int total, string currentFile)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Log("Rebuilding chunk index from Azure metadata...");

        // Clear existing index
        _databaseService.ClearChunkIndex();

        // Get all metadata blobs
        var metadataBlobs = await _blobService.ListMetadataBlobsAsync(cancellationToken);
        var total = metadataBlobs.Count;
        Log($"Found {total} metadata blobs to process");

        // Phase 1: Download all metadata in parallel (same pattern as ListRestorableFilesAsync)
        const int maxParallelMetadataDownloads = 32;
        var allMetadata = new System.Collections.Concurrent.ConcurrentBag<BackedUpFile>();
        int downloaded = 0;

        await Parallel.ForEachAsync(
            metadataBlobs,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = maxParallelMetadataDownloads,
                CancellationToken = cancellationToken
            },
            async (blobName, ct) =>
            {
                try
                {
                    var metadata = await _blobService.DownloadFileMetadataAsync(blobName, ct);
                    if (metadata != null)
                    {
                        allMetadata.Add(metadata);
                    }
                }
                catch (Exception ex)
                {
                    Log($"Error downloading metadata {blobName}: {ex.Message}");
                }

                var count = Interlocked.Increment(ref downloaded);
                if (count % 50 == 0 || count == total)
                {
                    progress?.Report((count, total, $"Downloading metadata... {count}/{total}"));
                }
            });

        Log($"Downloaded {allMetadata.Count} metadata entries. Collecting unique chunk hashes...");

        // Phase 2: Collect all unique chunk hashes and fetch their properties in parallel
        // This replaces N*M sequential HTTP calls with one parallel batch
        var uniqueChunkHashes = allMetadata
            .SelectMany(m => m.Chunks.Select(c => c.Hash))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Log($"Found {uniqueChunkHashes.Count} unique chunks to query");

        const int maxParallelChunkQueries = 32;
        var chunkInfoCache = new System.Collections.Concurrent.ConcurrentDictionary<string, (long size, StorageTier tier)>(StringComparer.Ordinal);
        int queriedChunks = 0;

        await Parallel.ForEachAsync(
            uniqueChunkHashes,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = maxParallelChunkQueries,
                CancellationToken = cancellationToken
            },
            async (chunkHash, ct) =>
            {
                try
                {
                    var info = await GetChunkInfoFromAzureAsync(chunkHash, ct);
                    chunkInfoCache.TryAdd(chunkHash, info);
                }
                catch
                {
                    // Will fall back to chunk.Length when processing
                }

                var count = Interlocked.Increment(ref queriedChunks);
                if (count % 100 == 0 || count == uniqueChunkHashes.Count)
                {
                    progress?.Report((downloaded, total, $"Querying chunk info... {count}/{uniqueChunkHashes.Count}"));
                }
            });

        Log($"Cached info for {chunkInfoCache.Count} chunks. Building index...");

        // Phase 3: Build the index from cached data (all local, no HTTP)
        var processed = 0;
        foreach (var metadata in allMetadata)
        {
            cancellationToken.ThrowIfCancellationRequested();
            processed++;
            progress?.Report((processed, allMetadata.Count, metadata.LocalPath));

            foreach (var chunk in metadata.Chunks)
            {
                var size = (long)chunk.Length;
                var tier = StorageTier.Hot;

                if (chunkInfoCache.TryGetValue(chunk.Hash, out var cached))
                {
                    size = cached.size;
                    tier = cached.tier;
                }

                AddReference(chunk.Hash, metadata.LocalPath, chunk.Index, size, tier, false);
            }
        }

        _databaseService.SetIndexMetadata("LastFullRebuildAt", DateTime.UtcNow);
        Log($"Index rebuild complete: processed {processed} files, {uniqueChunkHashes.Count} unique chunks");

        // Backup the newly rebuilt index
        await BackupIndexToAzureAsync(cancellationToken);
    }

    #endregion

    #region Tier Mismatch Detection

    /// <summary>
    /// Checks if a chunk exists in a different tier than intended and logs a warning.
    /// </summary>
    /// <param name="chunkHash">The chunk hash</param>
    /// <param name="intendedTier">The tier the file is configured for</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The actual tier of the existing chunk, or null if chunk doesn't exist</returns>
    public async Task<StorageTier?> CheckTierMismatchAsync(
        string chunkHash, 
        StorageTier intendedTier,
        CancellationToken cancellationToken = default)
    {
        var entry = _databaseService.GetChunkIndexEntry(chunkHash);
        if (entry == null) return null;

        // Verify against Azure
        try
        {
            var (_, actualTier) = await GetChunkInfoFromAzureAsync(chunkHash, cancellationToken);
            
            if (actualTier != intendedTier)
            {
                Log($"WARNING: Chunk {chunkHash[..8]}... exists in {actualTier} tier, " +
                    $"but file is configured for {intendedTier} tier. " +
                    $"Referenced by: {string.Join(", ", entry.ReferencingFiles.Select(r => r.FilePath))}");
            }

            // Update cached tier if different
            if (entry.CurrentTier != actualTier)
            {
                entry.CurrentTier = actualTier;
                entry.LastVerifiedAt = DateTime.UtcNow;
                _databaseService.SaveChunkIndexEntry(entry);
            }

            return actualTier;
        }
        catch
        {
            return entry.CurrentTier;
        }
    }

    #endregion

    #region Private Helpers

    private async Task<List<string>> ListAzureChunksAsync(CancellationToken cancellationToken)
    {
        // Use the blob service to list all chunks from Azure
        return await _blobService.ListChunkBlobsAsync(cancellationToken);
    }

    private async Task<(long size, StorageTier tier)> GetChunkInfoFromAzureAsync(
        string chunkHash, CancellationToken cancellationToken)
    {
        var blobName = $"chunks/{chunkHash}";
        return await _blobService.GetBlobPropertiesAsync(blobName, cancellationToken);
    }

    #endregion
}
