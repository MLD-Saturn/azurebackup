using System.Diagnostics;
using AzureBackup.Core.Models;

namespace AzureBackup.Core.Services;

/// <summary>
/// Service for managing the chunk index, tracking chunk-to-file associations,
/// detecting orphans, and maintaining storage health.
/// </summary>
public partial class ChunkIndexService
{
    private readonly LocalDatabaseService _databaseService;
    private readonly IBlobStorageService _blobService;
    private readonly EncryptionService _encryptionService;
    
    private const string IndexBackupBlobName = "index/chunk-index-backup.enc";

    /// <summary>
    /// Event raised for diagnostic logging.
    /// </summary>
    public event EventHandler<string>? DiagnosticLog;

    [Conditional("DIAGNOSTICLOG")]
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

        const int maxParallelDeletes = 128;
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
}
