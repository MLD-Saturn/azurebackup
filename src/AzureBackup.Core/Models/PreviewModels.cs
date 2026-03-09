namespace AzureBackup.Core.Models;

/// <summary>
/// Type of operation being previewed.
/// </summary>
public enum OperationType
{
    /// <summary>Restoring files from Azure to local.</summary>
    Restore,
    
    /// <summary>Deleting files from Azure storage.</summary>
    DeleteFromAzure,
    
    /// <summary>Mirror sync (restore + delete local extras).</summary>
    MirrorSync,
    
    /// <summary>Backing up files to Azure.</summary>
    Backup
}


/// <summary>
/// Represents a single file action in an operation preview.
/// </summary>
public class PreviewFileAction
{
    /// <summary>The file path (source or target depending on operation).</summary>
    public string FilePath { get; set; } = string.Empty;
    
    /// <summary>Display name for the file.</summary>
    public string FileName => Path.GetFileName(FilePath);
    
    /// <summary>Directory containing the file.</summary>
    public string Directory => Path.GetDirectoryName(FilePath) ?? string.Empty;
    
    /// <summary>The action that will be taken.</summary>
    public FileActionType Action { get; set; }
    
    /// <summary>File size in bytes.</summary>
    public long FileSize { get; set; }
    
    /// <summary>Human-readable file size.</summary>
    public string FileSizeText => FormatBytes(FileSize);
    
    /// <summary>Last modified date/time.</summary>
    public DateTime LastModified { get; set; }
    
    /// <summary>Last modified formatted for display.</summary>
    public string LastModifiedText => LastModified == default ? "" : LastModified.ToString("g");
    
    /// <summary>Target path for restore/sync operations.</summary>
    public string? TargetPath { get; set; }
    
    /// <summary>Reason for this action (e.g., "File is newer", "Not in backup").</summary>
    public string Reason { get; set; } = string.Empty;
    
    /// <summary>Storage tier for this file (for backup operations).</summary>
    public StorageTier? StorageTier { get; set; }
    
    private static string FormatBytes(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB", "TB"];
        var order = 0;
        double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:0.##} {sizes[order]}";
    }
}

/// <summary>
/// Type of action for a file in the preview.
/// </summary>
public enum FileActionType
{
    /// <summary>File will be created/restored (new file).</summary>
    Create,
    
    /// <summary>File will be overwritten (exists locally).</summary>
    Overwrite,
    
    /// <summary>File will be deleted.</summary>
    Delete,
    
    /// <summary>File will be skipped (unchanged).</summary>
    Skip,
    
    /// <summary>File will be updated (modified).</summary>
    Update
}

/// <summary>
/// Preview of an operation showing all files that will be affected.
/// </summary>
public class OperationPreview
{
    /// <summary>Type of operation.</summary>
    public OperationType OperationType { get; set; }
    
    /// <summary>Human-readable description of the operation.</summary>
    public string OperationDescription { get; set; } = string.Empty;
    
    /// <summary>Source path/location.</summary>
    public string SourceDescription { get; set; } = string.Empty;
    
    /// <summary>Target path/location.</summary>
    public string TargetDescription { get; set; } = string.Empty;
    
    /// <summary>Files that will be created (new).</summary>
    public List<PreviewFileAction> FilesToCreate { get; set; } = [];
    
    /// <summary>Files that will be overwritten.</summary>
    public List<PreviewFileAction> FilesToOverwrite { get; set; } = [];
    
    /// <summary>Files that will be deleted.</summary>
    public List<PreviewFileAction> FilesToDelete { get; set; } = [];
    
    /// <summary>Files that will be skipped (unchanged).</summary>
    public List<PreviewFileAction> FilesToSkip { get; set; } = [];
    
    /// <summary>Default storage tier based on watched folder settings.</summary>
    public StorageTier DefaultStorageTier { get; set; } = StorageTier.Cool;
    
    /// <summary>User-selected storage tier override (null means use default).</summary>
    public StorageTier? SelectedStorageTier { get; set; }
    
    /// <summary>The effective storage tier to use for this operation.</summary>
    public StorageTier EffectiveStorageTier => SelectedStorageTier ?? DefaultStorageTier;
    
    /// <summary>Total count of files to create.</summary>
    public int CreateCount => FilesToCreate.Count;
    
    /// <summary>Total count of files to overwrite.</summary>
    public int OverwriteCount => FilesToOverwrite.Count;
    
    /// <summary>Total count of files to delete.</summary>
    public int DeleteCount => FilesToDelete.Count;
    
    /// <summary>Total count of files to skip.</summary>
    public int SkipCount => FilesToSkip.Count;
    
    /// <summary>Total bytes that will be transferred.</summary>
    public long TotalBytesToTransfer => FilesToCreate.Sum(f => f.FileSize) + FilesToOverwrite.Sum(f => f.FileSize);
    
    /// <summary>Total bytes that will be deleted.</summary>
    public long TotalBytesToDelete => FilesToDelete.Sum(f => f.FileSize);
    
    /// <summary>Whether this operation has any destructive actions (delete or overwrite).</summary>
    public bool HasDestructiveActions => FilesToDelete.Count > 0 || FilesToOverwrite.Count > 0;
    
    /// <summary>Whether this operation will make any changes.</summary>
    public bool HasChanges => FilesToCreate.Count > 0 || FilesToOverwrite.Count > 0 || FilesToDelete.Count > 0;
    
    /// <summary>Warning message if operation is destructive.</summary>
    public string? WarningMessage
    {
        get
        {
            if (FilesToDelete.Count > 0 && OperationType == OperationType.DeleteFromAzure)
                return $"?? WARNING: {FilesToDelete.Count} file(s) will be PERMANENTLY deleted from Azure storage. This cannot be undone!";
            if (FilesToDelete.Count > 0 && OperationType == OperationType.MirrorSync)
                return $"?? WARNING: {FilesToDelete.Count} local file(s) will be deleted to match the backup.";
            if (FilesToOverwrite.Count > 0)
                return $"?? {FilesToOverwrite.Count} existing file(s) will be overwritten.";
            if (OperationType == OperationType.Backup && (FilesToCreate.Count > 0 || FilesToOverwrite.Count > 0))
                return $"?? {FilesToCreate.Count + FilesToOverwrite.Count} file(s) will be uploaded to Azure storage.";
            return null;
        }
    }
}
