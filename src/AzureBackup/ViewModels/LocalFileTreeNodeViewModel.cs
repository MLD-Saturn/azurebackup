using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AzureBackup.ViewModels;

/// <summary>
/// Backup status for a local file.
/// </summary>
public enum LocalFileBackupStatus
{
    /// <summary>Status not yet determined.</summary>
    Unknown,

    /// <summary>File is backed up and unchanged.</summary>
    BackedUp,

    /// <summary>File has been modified since last backup.</summary>
    Modified,

    /// <summary>File has never been backed up.</summary>
    New,

    /// <summary>Previous backup attempt failed.</summary>
    Failed,

    /// <summary>File is excluded from backup.</summary>
    Excluded
}

/// <summary>
/// ViewModel for displaying local filesystem files in a tree structure.
/// Shows backup status for each file by comparing with Azure backup records.
/// </summary>
public partial class LocalFileTreeNodeViewModel : TreeNodeViewModelBase<LocalFileTreeNodeViewModel>
{
    /// <summary>
    /// Static event raised when any local file's selection state changes.
    /// Used to notify the main view model to update UI state.
    /// </summary>
    public static event EventHandler? SelectionChanged;

    private LocalFileBackupStatus _backupStatusValue = LocalFileBackupStatus.Unknown;

    /// <inheritdoc />
    protected override void OnSelectionPropagationComplete()
        => SelectionChanged?.Invoke(this, EventArgs.Empty);

    /// <inheritdoc />
    public override string Name { get; }

    /// <inheritdoc />
    public override string FullPath { get; }

    /// <inheritdoc />
    public override bool IsFolder { get; }

    /// <summary>
    /// File size in bytes (0 for folders).
    /// </summary>
    public long FileSize { get; }

    /// <summary>
    /// Human-readable file size.
    /// </summary>
    public string FileSizeText => IsFile ? AzureBackup.Core.FormatHelper.FormatBytes(FileSize) : string.Empty;

    /// <summary>
    /// Last modified time.
    /// </summary>
    public DateTime LastModified { get; }

    /// <summary>
    /// Backup status of this file.
    /// </summary>
    public LocalFileBackupStatus BackupStatusValue
    {
        get => _backupStatusValue;
        set
        {
            if (SetProperty(ref _backupStatusValue, value))
            {
                OnPropertyChanged(nameof(StatusIcon));
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(StatusColor));
            }
        }
    }

    /// <summary>
    /// Icon representing the backup status.
    /// </summary>
    public string StatusIcon => BackupStatusValue switch
    {
        LocalFileBackupStatus.BackedUp => "[OK]",
        LocalFileBackupStatus.Modified => "[Mod]",
        LocalFileBackupStatus.New => "[New]",
        LocalFileBackupStatus.Failed => "[Err]",
        LocalFileBackupStatus.Excluded => "[Skip]",
        _ => IsFolder ? "[Dir]" : "[File]"
    };

    /// <summary>
    /// Text describing the backup status.
    /// </summary>
    public string StatusText => BackupStatusValue switch
    {
        LocalFileBackupStatus.BackedUp => "Backed up",
        LocalFileBackupStatus.Modified => "Modified",
        LocalFileBackupStatus.New => "New",
        LocalFileBackupStatus.Failed => "Failed",
        LocalFileBackupStatus.Excluded => "Excluded",
        _ => IsFolder ? "Folder" : "Unknown"
    };

    /// <summary>
    /// Color hint for the status (for UI binding).
    /// </summary>
    public string StatusColor => BackupStatusValue switch
    {
        LocalFileBackupStatus.BackedUp => "Green",
        LocalFileBackupStatus.Modified => "Orange",
        LocalFileBackupStatus.New => "Blue",
        LocalFileBackupStatus.Failed => "Red",
        LocalFileBackupStatus.Excluded => "Gray",
        _ => "Gray"
    };

    /// <summary>
    /// Summary counts for folders.
    /// </summary>
    public int TotalFileCount { get; private set; }
    public int BackedUpCount { get; private set; }
    public int ModifiedCount { get; private set; }
    public int NewCount { get; private set; }

    /// <summary>
    /// Summary text for folders.
    /// </summary>
    public string FolderSummary
    {
        get
        {
        if (!IsFolder) return string.Empty;
            List<string> parts = new();
            if (NewCount > 0) parts.Add($"{NewCount} new");
            if (ModifiedCount > 0) parts.Add($"{ModifiedCount} modified");
            if (BackedUpCount > 0) parts.Add($"{BackedUpCount} backed up");
            return parts.Count > 0 ? string.Join(", ", parts) : $"{TotalFileCount} files";
        }
    }

    public LocalFileTreeNodeViewModel(string name, string fullPath, bool isFolder, long fileSize = 0, DateTime lastModified = default)
    {
        Name = name;
        FullPath = fullPath;
        IsFolder = isFolder;
        FileSize = fileSize;
        LastModified = lastModified;
    }

    /// <summary>
    /// Gets all file nodes in this subtree.
    /// </summary>
    public IEnumerable<LocalFileTreeNodeViewModel> GetAllFiles()
    {
        if (IsFile)
        {
            yield return this;
        }
        else
        {
            foreach (var child in Children)
            {
                foreach (var file in child.GetAllFiles())
                {
                    yield return file;
                }
            }
        }
    }

    /// <summary>
    /// Updates summary counts by traversing children.
    /// </summary>
    public void UpdateSummary()
    {
        if (!IsFolder) return;

        TotalFileCount = 0;
        BackedUpCount = 0;
        ModifiedCount = 0;
        NewCount = 0;

        foreach (var child in Children)
        {
            if (child.IsFolder)
            {
                child.UpdateSummary();
                TotalFileCount += child.TotalFileCount;
                BackedUpCount += child.BackedUpCount;
                ModifiedCount += child.ModifiedCount;
                NewCount += child.NewCount;
            }
            else
            {
                TotalFileCount++;
                switch (child.BackupStatusValue)
                {
                    case LocalFileBackupStatus.BackedUp:
                        BackedUpCount++;
                        break;
                    case LocalFileBackupStatus.Modified:
                        ModifiedCount++;
                        break;
                    case LocalFileBackupStatus.New:
                        NewCount++;
                        break;
                }
            }
        }

        OnPropertyChanged(nameof(FolderSummary));
    }

    /// <summary>
    /// Builds a tree from watched folders and their files.
    /// </summary>
    /// <param name="watchedFolders">List of watched folders</param>
    /// <param name="backedUpFiles">Dictionary of backed up files keyed by path</param>
    /// <returns>List of root nodes (one per watched folder)</returns>
    public static List<LocalFileTreeNodeViewModel> BuildTree(
        IEnumerable<AzureBackup.Core.Models.WatchedFolder> watchedFolders,
        IDictionary<string, AzureBackup.Core.Models.BackedUpFile> backedUpFiles)
    {
        List<LocalFileTreeNodeViewModel> roots = new();

        foreach (var folder in watchedFolders.Where(f => f.IsEnabled))
        {
            if (!Directory.Exists(folder.Path)) continue;

            LocalFileTreeNodeViewModel rootNode = new(
                AzureBackup.Core.PathHelper.GetDisplayName(folder.Path),
                folder.Path,
                isFolder: true);

            BuildTreeRecursive(rootNode, folder.Path, backedUpFiles, folder.ExcludePatterns);
            rootNode.UpdateSummary();
            roots.Add(rootNode);
        }

        return roots;
    }

    private static void BuildTreeRecursive(
        LocalFileTreeNodeViewModel parentNode,
        string directoryPath,
        IDictionary<string, AzureBackup.Core.Models.BackedUpFile> backedUpFiles,
        List<string> excludePatterns)
    {
        try
        {
            // Add subdirectories
            foreach (var subDir in Directory.EnumerateDirectories(directoryPath))
            {
                var dirName = Path.GetFileName(subDir);

                // Check exclusion patterns
                if (AzureBackup.Core.GlobMatcher.MatchesAny(dirName, excludePatterns)) continue;

                LocalFileTreeNodeViewModel dirNode = new(dirName, subDir, isFolder: true)
                {
                    Parent = parentNode
                };

                BuildTreeRecursive(dirNode, subDir, backedUpFiles, excludePatterns);

                // Only add non-empty directories
                if (dirNode.Children.Count > 0)
                {
                    parentNode.Children.Add(dirNode);
                }
            }

            // Add files
            foreach (var filePath in Directory.EnumerateFiles(directoryPath))
            {
                var fileName = Path.GetFileName(filePath);

                // Check exclusion patterns
                if (AzureBackup.Core.GlobMatcher.MatchesAny(fileName, excludePatterns)) continue;

                try
                {
                    FileInfo fileInfo = new(filePath);
                    LocalFileTreeNodeViewModel fileNode = new(
                        fileName, 
                        filePath, 
                        isFolder: false,
                        fileSize: fileInfo.Length,
                        lastModified: fileInfo.LastWriteTimeUtc)
                    {
                        Parent = parentNode
                    };

                    // Determine backup status
                    // Note: We use size as the primary indicator since it's fast and reliable.
                    // Timestamp comparison is unreliable due to filesystem differences.
                    // The actual backup process will verify content hash before uploading.
                    if (backedUpFiles.TryGetValue(filePath, out var backup))
                    {
                        // Only consider status if the backup was actually completed with valid data
                        var hasValidBackup = backup.Status == AzureBackup.Core.Models.BackupStatus.Completed &&
                                            !string.IsNullOrEmpty(backup.FileHash) &&
                                            backup.FileSize > 0;

                        if (backup.Status == AzureBackup.Core.Models.BackupStatus.Excluded)
                        {
                            fileNode.BackupStatusValue = LocalFileBackupStatus.Excluded;
                        }
                        else if (!hasValidBackup)
                        {
                            // No valid backup exists - treat as new (includes Failed, Pending, InProgress)
                            // Show as Failed only if there was a real attempt (has some backup data)
                            if (backup.Status == AzureBackup.Core.Models.BackupStatus.Failed && 
                                !string.IsNullOrEmpty(backup.BlobName))
                            {
                                fileNode.BackupStatusValue = LocalFileBackupStatus.Failed;
                            }
                            else
                            {
                                fileNode.BackupStatusValue = LocalFileBackupStatus.New;
                            }
                        }
                        else if (fileInfo.Length != backup.FileSize)
                        {
                            // Size differs - definitely modified (fast check, 100% reliable)
                            fileNode.BackupStatusValue = LocalFileBackupStatus.Modified;
                        }
                        else
                        {
                            // Size matches - this is a strong indicator the file is unchanged.
                            // While we could check the hash for 100% certainty, that would require
                            // reading the entire file which is too slow for UI display.
                            // The backup process will verify the hash before deciding to skip.
                            // 
                            // We no longer rely on timestamps since they're unreliable:
                            // - Different filesystem precision (NTFS vs FAT32)
                            // - File copy/move operations
                            // - Timezone issues
                            // - File touch without content change
                            fileNode.BackupStatusValue = LocalFileBackupStatus.BackedUp;
                        }
                    }
                    else
                    {
                        fileNode.BackupStatusValue = LocalFileBackupStatus.New;
                    }

                    parentNode.Children.Add(fileNode);
                }
                catch
                {
                    // Skip files we can't access
                }
            }
        }
        catch
        {
            // Skip directories we can't access
        }
    }
}
