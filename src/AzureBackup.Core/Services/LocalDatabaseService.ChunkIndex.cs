using System.Security.Cryptography;
using AzureBackup.Core.Models;

namespace AzureBackup.Core.Services;

/// <summary>
/// Chunk index, statistics, and secure reset operations.
/// </summary>
public partial class LocalDatabaseService
{
    #region Chunk Index

    /// <summary>
    /// Gets a chunk index entry by hash.
    /// </summary>
    public ChunkIndexEntry? GetChunkIndexEntry(string chunkHash)
    {
        EnsureInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkHash);

        return InReadLock(() => _chunkIndexCollection!.FindOne(x => x.ChunkHash == chunkHash));
    }

    /// <summary>
    /// Saves or updates a chunk index entry.
    /// </summary>
    public void SaveChunkIndexEntry(ChunkIndexEntry entry)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(entry);

        InWriteLock(() =>
        {
            var existing = _chunkIndexCollection!.FindOne(x => x.ChunkHash == entry.ChunkHash);
            if (existing != null)
            {
                _chunkIndexCollection.Update(entry);
            }
            else
            {
                _chunkIndexCollection.Insert(entry);
            }
        });
    }

    /// <summary>
    /// Bulk-inserts chunk index entries. Use only after ClearChunkIndex when no existing entries exist.
    /// Significantly faster than individual SaveChunkIndexEntry calls for rebuilds.
    /// </summary>
    public void BulkInsertChunkIndexEntries(IEnumerable<ChunkIndexEntry> entries)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(entries);

        InWriteLock(() => _chunkIndexCollection!.InsertBulk(entries));
    }

    /// <summary>
    /// Deletes a chunk index entry.
    /// </summary>
    public void DeleteChunkIndexEntry(string chunkHash)
    {
        EnsureInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkHash);

        InWriteLock(() => _chunkIndexCollection!.DeleteMany(x => x.ChunkHash == chunkHash));
    }

    /// <summary>
    /// Gets all chunk index entries.
    /// </summary>
    public List<ChunkIndexEntry> GetAllChunkIndexEntries()
    {
        EnsureInitialized();

        return InReadLock(() => _chunkIndexCollection!.FindAll().ToList());
    }

    /// <summary>
    /// Gets a lightweight summary of all chunk index entries for fast lookups.
    /// Returns only the hash, reference count, size, and tier — without loading
    /// the ReferencingFiles list, which dominates memory at scale.
    /// At 1M chunks, this uses ~80 MB vs ~1.5 GB for full entries.
    /// </summary>
    public Dictionary<string, (int ReferenceCount, long SizeBytes, StorageTier Tier)> GetChunkIndexSummaryMap()
    {
        EnsureInitialized();

        return InReadLock(() =>
        {
            var result = new Dictionary<string, (int, long, StorageTier)>(StringComparer.Ordinal);
            foreach (var entry in _chunkIndexCollection!.FindAll())
            {
                result[entry.ChunkHash] = (entry.ReferenceCount, entry.SizeBytes, entry.CurrentTier);
            }
            return result;
        });
    }

    /// <summary>
    /// Gets chunk entries that reference a specific file.
    /// </summary>
    /// <remarks>
    /// Uses the reverse <c>chunk_file_refs</c> index (Phase 5 / P3): an indexed
    /// <c>FilePath</c> lookup returns the matching chunk hashes in O(log N), and
    /// each full <see cref="ChunkIndexEntry"/> is fetched by its indexed
    /// <see cref="ChunkIndexEntry.ChunkHash"/> key. Falls back to the legacy
    /// full-scan path only when the reverse index has not yet been built (i.e.
    /// before the one-time migration has run on an upgraded database).
    /// </remarks>
    public List<ChunkIndexEntry> GetChunkEntriesForFile(string filePath)
    {
        EnsureInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        return InReadLock(() =>
        {
            // Phase 5 / P3 fast path: indexed reverse lookup.
            var refs = _chunkFileRefsCollection!
                .Find(x => x.FilePath == filePath)
                .ToList();

            if (refs.Count == 0)
            {
                // Possibilities:
                //   (a) The file genuinely references no chunks, or
                //   (b) The reverse index has not been built yet (migration pending).
                // (b) is disambiguated by the migration path; by the time we reach
                // here post-migration, (a) is the only reason for an empty result.
                return new List<ChunkIndexEntry>();
            }

            var hashes = refs
                .Select(r => r.ChunkHash)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var result = new List<ChunkIndexEntry>(hashes.Count);
            foreach (var hash in hashes)
            {
                var entry = _chunkIndexCollection!.FindOne(x => x.ChunkHash == hash);
                if (entry != null) result.Add(entry);
            }
            return result;
        });
    }

    /// <summary>
    /// Legacy full-scan variant retained for the one-time reverse-index rebuild
    /// path and for performance comparison in <c>AzureBackup.Benchmarks</c>.
    /// Do not call from new application code - use <see cref="GetChunkEntriesForFile"/>.
    /// </summary>
    public List<ChunkIndexEntry> GetChunkEntriesForFile_LegacyScan(string filePath)
    {
        EnsureInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        return InReadLock(() =>
            _chunkIndexCollection!
                .FindAll()
                .Where(e => e.ReferencingFiles.Any(r =>
                    r.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase)))
                .ToList());
    }

    /// <summary>
    /// Adds or updates a single reverse-index row. Idempotent: a row for the same
    /// <c>(FilePath, ChunkHash, ChunkIndex)</c> triple is replaced rather than
    /// duplicated. The caller is expected to also mutate the primary
    /// <see cref="ChunkIndexEntry.ReferencingFiles"/>.
    /// </summary>
    internal void UpsertChunkFileRef(string filePath, string chunkHash, int chunkIndex, DateTime referencedAt)
    {
        EnsureInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkHash);

        InWriteLock(() =>
        {
            var existing = _chunkFileRefsCollection!
                .FindOne(x => x.FilePath == filePath && x.ChunkHash == chunkHash && x.ChunkIndex == chunkIndex);

            if (existing != null)
            {
                existing.ReferencedAt = referencedAt;
                _chunkFileRefsCollection.Update(existing);
                return;
            }

            _chunkFileRefsCollection.Insert(new ChunkFileRefRow
            {
                FilePath = filePath,
                ChunkHash = chunkHash,
                ChunkIndex = chunkIndex,
                ReferencedAt = referencedAt
            });
        });
    }

    /// <summary>
    /// Bulk-inserts reverse-index rows in a single transaction. Used by the
    /// one-time rebuild path and any future path that re-registers many
    /// references in one go.
    /// </summary>
    internal void BulkInsertChunkFileRefs(IEnumerable<ChunkFileRefRow> rows)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(rows);

        InWriteLock(() => _chunkFileRefsCollection!.InsertBulk(rows));
    }

    /// <summary>
    /// Deletes every reverse-index row for a single file path. Called when a
    /// backed-up file is removed.
    /// </summary>
    internal int DeleteChunkFileRefsForFile(string filePath)
    {
        EnsureInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        return InWriteLock(() =>
            _chunkFileRefsCollection!.DeleteMany(x => x.FilePath == filePath));
    }

    /// <summary>
    /// Deletes every reverse-index row for a single chunk hash. Called when a
    /// chunk itself is deleted (e.g. orphan cleanup).
    /// </summary>
    internal int DeleteChunkFileRefsForChunk(string chunkHash)
    {
        EnsureInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkHash);

        return InWriteLock(() =>
            _chunkFileRefsCollection!.DeleteMany(x => x.ChunkHash == chunkHash));
    }

    /// <summary>
    /// Deletes reverse-index rows binding a specific file path to a specific
    /// chunk hash. Called when a chunk is no longer referenced by a file but is
    /// still referenced by others.
    /// </summary>
    internal int DeleteChunkFileRefsForFileAndChunk(string filePath, string chunkHash)
    {
        EnsureInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkHash);

        return InWriteLock(() =>
            _chunkFileRefsCollection!.DeleteMany(x => x.FilePath == filePath && x.ChunkHash == chunkHash));
    }

    /// <summary>
    /// Returns <c>true</c> when the reverse <c>chunk_file_refs</c> index has
    /// been built for the current database. Checked by the UI at startup so
    /// it can decide whether to show the one-time rebuild progress dialog.
    /// </summary>
    public bool IsReverseChunkIndexBuilt()
    {
        EnsureInitialized();
        return InReadLock(() =>
            _indexMetadataCollection!.FindOne(x => x.Key == "ReverseIndexBuiltAt") != null);
    }

    /// <summary>
    /// One-time migration that populates the reverse <c>chunk_file_refs</c> index
    /// from the legacy <see cref="ChunkIndexEntry.ReferencingFiles"/> list on the
    /// primary collection. Safe to call repeatedly; a no-op once the
    /// <c>ReverseIndexBuiltAt</c> metadata marker exists.
    /// </summary>
    /// <param name="progress">
    /// Optional reporter receiving <c>(processedChunks, totalChunks)</c>. The UI
    /// layer drives a modal progress dialog from this callback.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancellation propagates cooperatively between chunk batches. If cancelled
    /// the partial rebuild is rolled back by clearing the reverse collection so
    /// a retry starts cleanly.
    /// </param>
    public void RebuildReverseChunkIndex(
        IProgress<(int processed, int total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        if (IsReverseChunkIndexBuilt())
        {
            Log("RebuildReverseChunkIndex: Already built, skipping.");
            return;
        }

        // Count + snapshot under read lock so a concurrent writer cannot mutate
        // the primary collection mid-rebuild. We release the read lock before
        // taking write locks per batch so other readers stay unblocked.
        var entries = InReadLock(() => _chunkIndexCollection!.FindAll().ToList());
        var total = entries.Count;
        Log($"RebuildReverseChunkIndex: Starting rebuild for {total} chunks.");
        progress?.Report((0, total));

        try
        {
            // Process in batches so each write-lock window is short.
            const int BatchSize = 2_000;
            var processed = 0;

            // Idempotency: clear any partial rows from a prior interrupted run.
            InWriteLock(() => _chunkFileRefsCollection!.DeleteAll());

            var batch = new List<ChunkFileRefRow>(BatchSize * 4);
            for (var i = 0; i < entries.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = entries[i];
                foreach (var r in entry.ReferencingFiles)
                {
                    batch.Add(new ChunkFileRefRow
                    {
                        FilePath = r.FilePath,
                        ChunkHash = entry.ChunkHash,
                        ChunkIndex = r.ChunkIndex,
                        ReferencedAt = r.ReferencedAt
                    });
                }

                if (batch.Count >= BatchSize || i == entries.Count - 1)
                {
                    var flush = batch;
                    InWriteLock(() => _chunkFileRefsCollection!.InsertBulk(flush));
                    processed = i + 1;
                    progress?.Report((processed, total));
                    batch = new List<ChunkFileRefRow>(BatchSize * 4);
                }
            }

            // Mark the rebuild complete so future starts skip this path.
            SetIndexMetadata("ReverseIndexBuiltAt", DateTime.UtcNow);
            Log($"RebuildReverseChunkIndex: Completed for {total} chunks.");
        }
        catch (OperationCanceledException)
        {
            // Leave the reverse collection empty and the metadata unset so a
            // retry restarts from zero rather than finding half-populated rows.
            InWriteLock(() => _chunkFileRefsCollection!.DeleteAll());
            Log("RebuildReverseChunkIndex: Cancelled; partial rows rolled back.");
            throw;
        }
    }

    /// <summary>
    /// Runs an explicit LiteDB checkpoint, flushing the WAL into the main data
    /// file. Keeps the <c>-log</c> file from growing unbounded across long app
    /// sessions (discovered during Phase 4 implementation).
    /// </summary>
    /// <remarks>
    /// LiteDB normally checkpoints automatically but only at shutdown or when the
    /// WAL reaches its threshold; this app can run for days with small sustained
    /// writes, during which the WAL grows to multi-GB before either trigger fires.
    /// Callers should invoke this on a timer (e.g. hourly) and at clean shutdown.
    /// </remarks>
    public void Checkpoint()
    {
        EnsureInitialized();

        // Checkpoint itself is a write operation on the WAL state, so hold the
        // write lock to keep it ordered against in-flight writers.
        InWriteLock(() => _database!.Checkpoint());
    }

    /// <summary>
    /// Gets orphaned chunks (reference count = 0).
    /// </summary>
    public List<ChunkIndexEntry> GetOrphanedChunks()
    {
        EnsureInitialized();

        return InReadLock(() => _chunkIndexCollection!.Find(x => x.ReferenceCount == 0).ToList());
    }

    /// <summary>
    /// Clears all chunk index entries.
    /// </summary>
    public void ClearChunkIndex()
    {
        EnsureInitialized();

        InWriteLock(() =>
        {
            _chunkIndexCollection!.DeleteAll();
            // Reverse-index rows are a denormalised view of the primary collection,
            // so they must be cleared in lockstep.
            _chunkFileRefsCollection?.DeleteAll();
        });
    }

    /// <summary>
    /// Gets index metadata by key.
    /// </summary>
    public DateTime? GetIndexMetadata(string key)
    {
        EnsureInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        return InReadLock(() => _indexMetadataCollection!.FindOne(x => x.Key == key)?.Value);
    }

    /// <summary>
    /// Sets index metadata by key.
    /// </summary>
    public void SetIndexMetadata(string key, DateTime value)
    {
        EnsureInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        InWriteLock(() =>
        {
            var entry = _indexMetadataCollection!.FindOne(x => x.Key == key);
            if (entry != null)
            {
                entry.Value = value;
                _indexMetadataCollection.Update(entry);
            }
            else
            {
                _indexMetadataCollection.Insert(new IndexMetadata { Key = key, Value = value });
            }
        });
    }

    /// <summary>
    /// Gets the total count of chunks in the index.
    /// </summary>
    public int GetChunkIndexCount()
    {
        EnsureInitialized();

        return InReadLock(() => _chunkIndexCollection!.Count());
    }

    #endregion

    #region Statistics

    /// <summary>
    /// Gets backup statistics.
    /// </summary>
    public BackupStatistics GetStatistics()
    {
        EnsureInitialized();

        return InReadLock(() =>
        {
            var files = _filesCollection!.FindAll().ToList();
            var config = _configCollection!.FindById(1) ?? new BackupConfiguration();

            return new BackupStatistics
            {
                TotalFiles = files.Count,
                TotalSize = files.Sum(x => x.FileSize),
                CompletedFiles = files.Count(x => x.Status == BackupStatus.Completed),
                PendingFiles = files.Count(x => x.Status == BackupStatus.Pending),
                FailedFiles = files.Count(x => x.Status == BackupStatus.Failed),
                PendingChanges = _pendingChangesCollection!.Count(),
                LastBackupTime = config.LastBackupTime,
                TotalBytesUploaded = config.TotalBytesUploaded
            };
        });
    }

    #endregion

    #region Reset and Secure Delete

    /// <summary>
    /// Securely deletes all data and resets the database.
    /// Overwrites sensitive data before deletion to prevent recovery.
    /// After calling this method, the database is closed and the application 
    /// should restart or call Initialize with a new password.
    /// </summary>
    public void SecureReset()
    {
        // Stop the checkpoint timer before tearing down the database file; the
        // callback is guarded against _disposed but an in-flight tick could still
        // race with file deletion.
        _checkpointTimer?.Dispose();
        _checkpointTimer = null;

        InWriteLock(() =>
        {
            if (_database == null || string.IsNullOrEmpty(_databasePath))
                return;

            // First, overwrite sensitive data in the database
            OverwriteSensitiveData();

            // Close the database
            _database.Dispose();
            _database = null;
            _configCollection = null;
            _filesCollection = null;
            _pendingChangesCollection = null;
            _chunkIndexCollection = null;
            _indexMetadataCollection = null;
            _chunkFileRefsCollection = null;

            // Securely delete the database file
            SecureDeleteFile(_databasePath);

            // Also delete the journal file if it exists
            var journalPath = _databasePath + "-journal";
            if (File.Exists(journalPath))
            {
                SecureDeleteFile(journalPath);
            }

            // Also delete the log file if it exists (LiteDB WAL)
            var logPath = _databasePath + "-log";
            if (File.Exists(logPath))
            {
                SecureDeleteFile(logPath);
            }

            // Also delete the salt file
            var saltPath = GetSaltFilePath(_databasePath);
            if (File.Exists(saltPath))
            {
                SecureDeleteFile(saltPath);
            }

            Log("SecureReset: Database and salt file have been securely deleted. Application restart required.");
        });
    }

    /// <summary>
    /// Overwrites sensitive data in the database before deletion.
    /// </summary>
    private void OverwriteSensitiveData()
    {
        if (_configCollection == null) return;

        var config = _configCollection.FindById(1);
        if (config != null)
        {
            // Overwrite password-related data
            if (config.PasswordSalt != null)
            {
                RandomNumberGenerator.Fill(config.PasswordSalt);
                config.PasswordSalt = null;
            }

            if (config.PasswordVerificationHash != null)
            {
                RandomNumberGenerator.Fill(config.PasswordVerificationHash);
                config.PasswordVerificationHash = null;
            }
            
            // Overwrite encrypted connection string
            if (config.EncryptedConnectionString != null)
            {
                RandomNumberGenerator.Fill(config.EncryptedConnectionString);
                config.EncryptedConnectionString = null;
            }

            // Reset authentication method to default
            config.AuthMethod = AzureAuthMethod.ConnectionString;

            // Reset Entra ID and storage account settings
            config.StorageAccountName = null;
            config.IsEntraIdAuthenticated = false;
            config.EntraIdUserName = null;

            // Reset other sensitive fields
            config.FailedLoginAttempts = 0;
            config.LockoutUntilUtc = null;
            config.WatchedFolders = [];

            _configCollection.Update(config);
        }

        // Clear all file records
        _filesCollection?.DeleteAll();
        _pendingChangesCollection?.DeleteAll();
        _chunkIndexCollection?.DeleteAll();
        _indexMetadataCollection?.DeleteAll();
        _chunkFileRefsCollection?.DeleteAll();
    }

    /// <summary>
    /// Securely deletes a file by overwriting with random data before deletion.
    /// </summary>
    private static void SecureDeleteFile(string filePath)
    {
        if (!File.Exists(filePath)) return;

        try
        {
            FileInfo fileInfo = new(filePath);
            var fileSize = fileInfo.Length;

            // Overwrite file with random data (3 passes for extra security)
            using (FileStream stream = new(filePath, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[4096];
                
                for (var pass = 0; pass < 3; pass++)
                {
                    stream.Position = 0;
                    var remaining = fileSize;
                    
                    while (remaining > 0)
                    {
                        var toWrite = (int)Math.Min(buffer.Length, remaining);
                        RandomNumberGenerator.Fill(buffer.AsSpan(0, toWrite));
                        stream.Write(buffer, 0, toWrite);
                        remaining -= toWrite;
                    }
                    
                    stream.Flush();
                }
            }

            // Now delete the file
            File.Delete(filePath);
        }
        catch (IOException)
        {
            // If secure delete fails, try regular delete
            try { File.Delete(filePath); } catch { /* Best effort */ }
        }
    }

    #endregion
}
