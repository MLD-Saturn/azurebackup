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
