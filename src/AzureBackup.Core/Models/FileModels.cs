namespace AzureBackup.Core.Models;

/// <summary>
/// Represents metadata about a backed up file.
/// </summary>
public class BackedUpFile
{
    public int Id { get; set; }
    public string LocalPath { get; set; } = string.Empty;
    public string BlobName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateTime LastModified { get; set; }
    
    /// <summary>
    /// SHA-256 hash of the complete file for integrity verification.
    /// </summary>
    public string FileHash { get; set; } = string.Empty;
    
    public List<ChunkInfo> Chunks { get; set; } = [];
    public DateTime BackedUpAt { get; set; }
    public BackupStatus Status { get; set; } = BackupStatus.Pending;
    
    /// <summary>
    /// Backup metadata format version.
    /// </summary>
    public int MetadataVersion { get; set; } = 1;
    
    /// <summary>
    /// The Azure storage tier of the metadata blob.
    /// This is populated when fetching from Azure and indicates the current tier.
    /// </summary>
    [LiteDB.BsonIgnore]
    public StorageTier? CurrentStorageTier { get; set; }
}

/// <summary>
/// Represents information about a file chunk for delta sync.
/// </summary>
public class ChunkInfo
{
    public int Index { get; set; }
    public long Offset { get; set; }
    public int Length { get; set; }
    
    /// <summary>
    /// SHA-256 hash of chunk content for content-addressable storage.
    /// </summary>
    public string Hash { get; set; } = string.Empty;
    
    public string BlobName { get; set; } = string.Empty;
}

/// <summary>
/// Carries chunk metadata and raw data through the CDC-to-upload pipeline.
/// <para>
/// <c>Data</c> may be a rented ArrayPool buffer (oversized) or, for chunks
/// large enough to skip the pool under B33, an exactly-sized <c>byte[]</c>
/// allocated by the producer. Use <see cref="Length"/> for the actual data
/// extent.
/// </para>
/// <para>
/// <c>ChargedBytes</c> (B30) is the amount the producer charged to the
/// shared <see cref="MemoryBudget"/> when allocating <c>Data</c>. The
/// consumer MUST release exactly this amount when it is done with the
/// payload, regardless of <see cref="Length"/>. This decouples accounting
/// from the user-visible chunk size, so a chunk whose ArrayPool tier
/// rounded up from 80 MB to 128 MB charges (and releases) the actual
/// 128 MB residency.
/// </para>
/// <para>
/// <c>ReturnToPool</c> (B33) tells the consumer whether to return
/// <c>Data</c> to <see cref="System.Buffers.ArrayPool{T}.Shared"/> after
/// upload. <c>true</c> matches the pre-B33 behaviour. <c>false</c> means
/// <c>Data</c> was an exact-sized GC allocation that must NOT be returned
/// to the pool (returning a non-pool array silently corrupts the pool's
/// invariants).
/// </para>
/// </summary>
public record ChunkPayload(ChunkInfo Info, byte[] Data, int Length, long ChargedBytes, bool ReturnToPool);

public enum BackupStatus
{
    Pending,
    InProgress,
    Completed,
    Failed,
    Excluded
}

/// <summary>
/// Represents a file change detected by the file system watcher.
/// </summary>
public class FileChangeEvent
{
    public string FilePath { get; set; } = string.Empty;
    public FileChangeType ChangeType { get; set; }
    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;
}

public enum FileChangeType
{
    Created,
    Modified,
    Deleted,
    Renamed
}
