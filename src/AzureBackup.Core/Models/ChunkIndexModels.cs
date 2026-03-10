namespace AzureBackup.Core.Models;

/// <summary>
/// Represents a chunk in the index with its reference tracking information.
/// Used for deduplication tracking and orphan detection.
/// </summary>
public class ChunkIndexEntry
{
    /// <summary>
    /// The SHA-256 hash of the chunk content (also used as blob name).
    /// This is the unique identifier for the chunk in the database.
    /// </summary>
    [LiteDB.BsonId]
    public string ChunkHash { get; set; } = string.Empty;

    /// <summary>
    /// List of file paths that reference this chunk.
    /// </summary>
    public List<ChunkFileReference> ReferencingFiles { get; set; } = [];

    /// <summary>
    /// Number of files currently referencing this chunk.
    /// </summary>
    public int ReferenceCount { get; set; }

    /// <summary>
    /// When this chunk was first uploaded.
    /// </summary>
    public DateTime FirstUploadedAt { get; set; }

    /// <summary>
    /// The file that originally created/uploaded this chunk.
    /// </summary>
    public string OriginalUploaderPath { get; set; } = string.Empty;

    /// <summary>
    /// Current storage tier of this chunk in Azure.
    /// </summary>
    public StorageTier CurrentTier { get; set; } = StorageTier.Hot;

    /// <summary>
    /// Size of the chunk in bytes (encrypted size).
    /// </summary>
    public long SizeBytes { get; set; }

    /// <summary>
    /// Last time this entry was verified against Azure.
    /// </summary>
    public DateTime LastVerifiedAt { get; set; }

    /// <summary>
    /// Whether this chunk is marked as an orphan candidate.
    /// </summary>
    public bool IsOrphanCandidate => ReferenceCount == 0;
}

/// <summary>
/// Represents a file's reference to a chunk, including when the reference was created.
/// </summary>
public class ChunkFileReference
{
    /// <summary>
    /// The local file path that references this chunk.
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// When this file started referencing this chunk.
    /// </summary>
    public DateTime ReferencedAt { get; set; }

    /// <summary>
    /// The chunk index within the file (0-based).
    /// </summary>
    public int ChunkIndex { get; set; }
}

/// <summary>
/// Summary statistics for the chunk index.
/// </summary>
public class ChunkIndexSummary
{
    /// <summary>
    /// Total number of unique chunks tracked.
    /// </summary>
    public int TotalChunks { get; set; }

    /// <summary>
    /// Total size of all chunks in bytes.
    /// </summary>
    public long TotalSizeBytes { get; set; }

    /// <summary>
    /// Number of chunks with reference count 0 (orphans).
    /// </summary>
    public int OrphanCount { get; set; }

    /// <summary>
    /// Total size of orphaned chunks in bytes.
    /// </summary>
    public long OrphanSizeBytes { get; set; }

    /// <summary>
    /// Number of chunks shared by multiple files (deduplication savings).
    /// </summary>
    public int SharedChunks { get; set; }

    /// <summary>
    /// Estimated storage saved through deduplication.
    /// </summary>
    public long DeduplicationSavingsBytes { get; set; }

    /// <summary>
    /// Breakdown of chunks by storage tier.
    /// </summary>
    public Dictionary<StorageTier, TierStatistics> TierBreakdown { get; set; } = [];

    /// <summary>
    /// When the index was last fully rebuilt from Azure.
    /// </summary>
    public DateTime? LastFullRebuildAt { get; set; }

    /// <summary>
    /// When the index was last synced to Azure backup.
    /// </summary>
    public DateTime? LastAzureSyncAt { get; set; }
}

/// <summary>
/// Statistics for a specific storage tier.
/// </summary>
public class TierStatistics
{
    /// <summary>
    /// Number of chunks in this tier.
    /// </summary>
    public int ChunkCount { get; set; }

    /// <summary>
    /// Total size of chunks in this tier.
    /// </summary>
    public long TotalSizeBytes { get; set; }

    /// <summary>
    /// Estimated monthly storage cost for this tier.
    /// </summary>
    public decimal EstimatedMonthlyCost { get; set; }
}

/// <summary>
/// Result of an orphan scan operation.
/// </summary>
public class OrphanScanResult
{
    /// <summary>
    /// List of orphaned chunks found.
    /// </summary>
    public List<ChunkIndexEntry> OrphanedChunks { get; set; } = [];

    /// <summary>
    /// Total size of orphaned chunks.
    /// </summary>
    public long TotalOrphanSizeBytes { get; set; }

    /// <summary>
    /// Estimated monthly cost of orphaned storage.
    /// </summary>
    public decimal EstimatedMonthlyCost { get; set; }

    /// <summary>
    /// When the scan was performed.
    /// </summary>
    public DateTime ScannedAt { get; set; }

    /// <summary>
    /// Number of chunks scanned.
    /// </summary>
    public int ChunksScanned { get; set; }

    /// <summary>
    /// Duration of the scan operation.
    /// </summary>
    public TimeSpan ScanDuration { get; set; }
}

/// <summary>
/// Result of a cleanup operation.
/// </summary>
public class CleanupResult
{
    /// <summary>
    /// Number of chunks deleted.
    /// </summary>
    public int ChunksDeleted { get; set; }

    /// <summary>
    /// Total bytes freed.
    /// </summary>
    public long BytesFreed { get; set; }

    /// <summary>
    /// Number of chunks that failed to delete.
    /// </summary>
    public int FailedDeletions { get; set; }

    /// <summary>
    /// Error messages for failed deletions.
    /// </summary>
    public List<string> Errors { get; set; } = [];

    /// <summary>
    /// When the cleanup was performed.
    /// </summary>
    public DateTime CleanedAt { get; set; }
}

/// <summary>
/// Represents the chunk index backup stored in Azure.
/// </summary>
public class ChunkIndexBackup
{
    /// <summary>
    /// Version of the backup format.
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// When this backup was created.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// All chunk index entries.
    /// </summary>
    public List<ChunkIndexEntry> Entries { get; set; } = [];

    /// <summary>
    /// Summary statistics at time of backup.
    /// </summary>
    public ChunkIndexSummary Summary { get; set; } = new();
}
