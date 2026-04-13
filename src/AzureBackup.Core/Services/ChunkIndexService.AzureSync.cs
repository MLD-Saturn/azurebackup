using System.Text.Json;
using AzureBackup.Core.Models;

namespace AzureBackup.Core.Services;

/// <summary>
/// Azure backup/restore, index rebuild, and tier mismatch detection.
/// </summary>
public partial class ChunkIndexService
{
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
    /// Detects and cleans up incomplete metadata entries (files with missing chunks)
    /// and deletes orphaned chunks that were only referenced by those entries.
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

        // Phase 1 & 2: Download metadata and list chunk blobs concurrently.
        // These query different prefixes (metadata/ vs chunks/) so they don't contend.
        const int maxParallelMetadataDownloads = 64;
        var allMetadata = new System.Collections.Concurrent.ConcurrentBag<(string blobName, BackedUpFile file)>();
        int downloaded = 0;

        // Start chunk listing immediately — it doesn't depend on metadata results
        var chunkListingTask = _blobService.ListChunkBlobsWithPropertiesAsync(cancellationToken);

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
                        allMetadata.Add((blobName, metadata));
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

        Log($"Downloaded {allMetadata.Count} metadata entries. Awaiting chunk listing...");

        // Await the chunk listing that was running concurrently with metadata downloads
        progress?.Report((0, 1, "Listing all chunks from Azure..."));
        var chunkInfoCache = await chunkListingTask;

        Log($"Listed {chunkInfoCache.Count} chunks with properties. Checking integrity...");

        // Phase 3: Detect incomplete metadata (files with missing chunks) and clean up
        var uniqueChunkHashes = allMetadata
            .SelectMany(m => m.file.Chunks.Select(c => c.Hash))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var missingChunkHashes = new HashSet<string>(
            uniqueChunkHashes.Where(h => !chunkInfoCache.ContainsKey(h)),
            StringComparer.Ordinal);

        var validMetadata = allMetadata.ToList();

        if (missingChunkHashes.Count > 0)
        {
            Log($"Found {missingChunkHashes.Count} chunks missing from Azure — checking for incomplete files");

            var incompleteEntries = allMetadata
                .Where(e => e.file.Chunks.Any(c => missingChunkHashes.Contains(c.Hash)))
                .ToList();

            if (incompleteEntries.Count > 0)
            {
                Log($"Found {incompleteEntries.Count} incomplete metadata entries — deleting from Azure");

                // Collect chunk hashes referenced by valid (complete) metadata
                var incompleteSet = new HashSet<string>(
                    incompleteEntries.Select(e => e.blobName), StringComparer.Ordinal);
                validMetadata = allMetadata.Where(e => !incompleteSet.Contains(e.blobName)).ToList();

                var validChunkHashes = new HashSet<string>(
                    validMetadata.SelectMany(e => e.file.Chunks.Select(c => c.Hash)),
                    StringComparer.Ordinal);

                // Chunks only referenced by deleted metadata that still exist in Azure
                var orphanedChunkHashes = incompleteEntries
                    .SelectMany(e => e.file.Chunks.Select(c => c.Hash))
                    .Where(h => !validChunkHashes.Contains(h) && chunkInfoCache.ContainsKey(h))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                // Build full list of blobs to delete (metadata + orphaned chunks)
                var blobsToDelete = incompleteEntries
                    .Select(e => (blobName: e.blobName, label: Path.GetFileName(e.file.LocalPath)))
                    .Concat(orphanedChunkHashes.Select(h => (blobName: $"chunks/{h}", label: h[..8] + "...")))
                    .ToList();

                // Parallel deletion of incomplete metadata and orphaned chunks
                const int maxParallelDeletes = 128;
                int deletedCount = 0;
                int deletedMetadata = 0;
                int deletedChunks = 0;

                await Parallel.ForEachAsync(
                    blobsToDelete,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = maxParallelDeletes,
                        CancellationToken = cancellationToken
                    },
                    async (item, ct) =>
                    {
                        try
                        {
                            await _blobService.DeleteBlobAsync(item.blobName, ct);
                            if (item.blobName.StartsWith("chunks/"))
                                Interlocked.Increment(ref deletedChunks);
                            else
                                Interlocked.Increment(ref deletedMetadata);
                        }
                        catch (Exception ex)
                        {
                            Log($"Failed to delete {item.blobName}: {ex.Message}");
                        }

                        var count = Interlocked.Increment(ref deletedCount);
                        if (count % 20 == 0 || count == blobsToDelete.Count)
                        {
                            progress?.Report((count, blobsToDelete.Count,
                                $"Cleaning up: {count}/{blobsToDelete.Count}"));
                        }
                    });

                Log($"Cleanup complete: {deletedMetadata} metadata entries deleted, " +
                    $"{deletedChunks} orphaned chunks deleted");
            }
        }

        // Phase 4: Build the index from valid (complete) metadata only
        // Build all entries in memory first, then batch-insert — avoids per-chunk DB read+write
        Log($"Building index from {validMetadata.Count} valid metadata entries...");
        progress?.Report((0, validMetadata.Count, "Building index..."));

        var now = DateTime.UtcNow;
        var indexEntries = new Dictionary<string, ChunkIndexEntry>(StringComparer.Ordinal);
        var processed = 0;

        foreach (var (_, file) in validMetadata)
        {
            cancellationToken.ThrowIfCancellationRequested();
            processed++;

            if (processed % 100 == 0 || processed == validMetadata.Count)
            {
                progress?.Report((processed, validMetadata.Count, $"Indexing: {Path.GetFileName(file.LocalPath)}"));
            }

            foreach (var chunk in file.Chunks)
            {
                var size = (long)chunk.Length;
                var tier = StorageTier.Hot;

                if (chunkInfoCache.TryGetValue(chunk.Hash, out var cached))
                {
                    size = cached.sizeBytes;
                    tier = cached.tier;
                }

                if (!indexEntries.TryGetValue(chunk.Hash, out var entry))
                {
                    entry = new ChunkIndexEntry
                    {
                        ChunkHash = chunk.Hash,
                        FirstUploadedAt = now,
                        OriginalUploaderPath = file.LocalPath,
                        CurrentTier = tier,
                        SizeBytes = size,
                        LastVerifiedAt = now,
                        ReferenceCount = 0,
                        ReferencingFiles = []
                    };
                    indexEntries[chunk.Hash] = entry;
                }

                entry.ReferencingFiles.Add(new ChunkFileReference
                {
                    FilePath = file.LocalPath,
                    ChunkIndex = chunk.Index,
                    ReferencedAt = now
                });
                entry.ReferenceCount = entry.ReferencingFiles.Count;
            }
        }

        // Single bulk insert — one DB operation instead of thousands
        progress?.Report((0, 1, $"Saving {indexEntries.Count} index entries..."));
        _databaseService.BulkInsertChunkIndexEntries(indexEntries.Values);

        _databaseService.SetIndexMetadata("LastFullRebuildAt", now);
        Log($"Index rebuild complete: {processed} valid files indexed, " +
            $"{indexEntries.Count} unique chunks, " +
            $"{missingChunkHashes.Count} missing chunks detected");

        // Backup the newly rebuilt index
        await BackupIndexToAzureAsync(cancellationToken);
    }

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
}
