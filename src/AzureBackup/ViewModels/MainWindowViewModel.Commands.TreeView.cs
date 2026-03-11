using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AzureBackup.Core.Models;
using CommunityToolkit.Mvvm.Input;

namespace AzureBackup.ViewModels;

/// <summary>
/// Tree view and folder management commands for MainWindowViewModel.
/// </summary>
public partial class MainWindowViewModel
{
    #region Tree View Commands

    [RelayCommand]
    private void ToggleViewMode()
    {
        UseTreeView = !UseTreeView;
        AddLog(UseTreeView ? "Switched to tree view" : "Switched to flat list view");
        
        if (UseTreeView)
        {
            BuildFileTree();
        }
    }


    [RelayCommand]
    private void ExpandAllTreeNodes()
    {
        // Expand Azure file tree
        foreach (var root in FileTreeRoots)
        {
            root.ExpandAll();
        }
        
        // Expand local file tree
        foreach (var root in LocalFileTreeRoots)
        {
            root.ExpandAll();
        }
    }

    [RelayCommand]
    private void CollapseAllTreeNodes()
    {
        // Collapse Azure file tree
        foreach (var root in FileTreeRoots)
        {
            root.CollapseAll();
        }
        
        // Collapse local file tree
        foreach (var root in LocalFileTreeRoots)
        {
            root.CollapseAll();
        }
    }

    [RelayCommand]
    private void SetCustomRestorePath()
    {
        if (SelectedTreeNode == null || !SelectedTreeNode.IsFolder)
        {
            AddLog("Please select a folder to set custom restore path");
            return;
        }

        if (string.IsNullOrWhiteSpace(CustomRestoreBasePath))
        {
            AddLog("Please enter a custom restore path");
            return;
        }

        SelectedTreeNode.SetCustomRestorePathAndNotify(CustomRestoreBasePath);
        AddLog($"Set restore path for '{SelectedTreeNode.Name}': {SelectedTreeNode.FullPath} ? {CustomRestoreBasePath}");
    }

    [RelayCommand]
    private void ClearCustomRestorePath()
    {
        if (SelectedTreeNode == null)
            return;

        SelectedTreeNode.ClearCustomRestorePathRecursive();
        AddLog($"Cleared custom restore path for '{SelectedTreeNode.Name}'");
    }

    [RelayCommand]
    private void BrowseRemapFolder()
    {
        // Request the View to open a folder picker dialog for path remapping
        RemapFolderPickerRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Called by the View after a remap folder is selected from the picker.
    /// </summary>
    public void SetRemapFolderPath(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return;
        
        CustomRestoreBasePath = folderPath;
        
        // Auto-apply the path if a folder is selected
        if (SelectedTreeNode?.IsFolder == true)
        {
            SelectedTreeNode.SetCustomRestorePathAndNotify(folderPath);
            AddLog($"Set restore path for '{SelectedTreeNode.Name}': {SelectedTreeNode.FullPath} ? {folderPath}");
        }
    }

    [RelayCommand]
    private async Task RestoreSelectedTreeFilesAsync()
    {
        if (!IsInitialized)
        {
            AddLog("Please unlock first");
            return;
        }

        // Get selected files from tree
        var selectedFiles = FileTreeRoots
            .SelectMany(r => r.GetSelectedFiles())
            .ToList();

        if (selectedFiles.Count == 0)
        {
            AddLog("Please select files to restore");
            return;
        }

        // Build list of (file, target path) pairs
        // If RestoreToOriginalLocation is true, use original paths
        // Otherwise use the effective restore path (with any custom remapping)
        var filesWithPaths = selectedFiles
            .Where(f => f.File != null)
            .Select(f => (
                file: f.File!, 
                targetPath: RestoreToOriginalLocation 
                    ? f.File!.LocalPath  // Original location
                    : (!string.IsNullOrWhiteSpace(RestoreDirectory) 
                        ? Path.Combine(RestoreDirectory, Path.GetFileName(f.File!.LocalPath))
                        : f.EffectiveRestorePath)))
            .ToList();

        // Generate preview to check for overwrites
        var preview = _restoreService.PreviewRestoreWithRemapping(filesWithPaths);

        // Only show dialog if there are overwrites
        if (preview.HasDestructiveActions)
        {
            var confirmed = await ShowPreviewDialogAsync(preview);
            
            if (!confirmed)
            {
                AddLog("Restore operation cancelled by user");
                return;
            }
        }

        IsOperationInProgress = true;
        _operationCts = new CancellationTokenSource();

        try
        {
            var totalBytes = filesWithPaths.Sum(f => f.file.FileSize);
            StartProgressTracking("Restoring", filesWithPaths.Count, totalBytes);

            AddLog($"Restoring {filesWithPaths.Count} files with path remapping...");

            // File-level progress (which file we're on)
            Progress<(int current, int total, string file)> fileProgress = new(p =>
            {
                // This is handled by the byte-level progress now
            });
            
            // Byte-level progress for individual files
            Progress<(long bytesCompleted, long fileSize, int fileIndex)> byteProgress = new(p =>
            {
                var fileName = Path.GetFileName(filesWithPaths[p.fileIndex].file.LocalPath);
                UpdateFileProgress(fileName, p.bytesCompleted, p.fileSize, p.fileIndex);
            });

            var result = await _restoreService.RestoreFilesWithRemappingAsync(
                filesWithPaths, 
                overwriteExisting: true,
                fileProgress,
                byteProgress,
                _operationCts.Token);

            AddLog($"Restore complete: {result.SuccessfulFiles.Count} succeeded, {result.FailedFiles.Count} failed");
        }
        catch (OperationCanceledException)
        {
            AddLog("Restore cancelled");
        }
        finally
        {
            IsOperationInProgress = false;
            ClearProgressTracking();
        }
    }

    [RelayCommand]
    private async Task MirrorSyncToLocalAsync()
    {
        if (!IsInitialized)
        {
            AddLog("Please unlock first");
            return;
        }

        if (SelectedTreeNode == null || !SelectedTreeNode.IsFolder)
        {
            AddLog("Please select a folder to sync");
            return;
        }

        if (string.IsNullOrWhiteSpace(CustomRestoreBasePath))
        {
            AddLog("Please enter a target directory for mirror sync");
            return;
        }

        // Validate target path
        try
        {
            var normalizedPath = Path.GetFullPath(CustomRestoreBasePath);
            if (!Path.IsPathRooted(normalizedPath))
            {
                AddLog("Please enter an absolute path for the target directory");
                return;
            }
        }
        catch (Exception ex)
        {
            AddLog($"Invalid target path: {ex.Message}");
            return;
        }

        var sourceFolder = SelectedTreeNode.FullPath;
        var targetFolder = CustomRestoreBasePath;

        // Get all files under the selected folder
        var filesToSync = SelectedTreeNode.GetAllDescendants()
            .Where(n => n.IsFile && n.File != null)
            .Select(n => n.File!)
            .ToList();

        // Generate preview
        AddLog("Analyzing sync operation...");
        IsOperationInProgress = true;
        
        try
        {
            var preview = await _restoreService.PreviewMirrorSyncAsync(
                filesToSync, targetFolder, sourceFolder);

            // Show preview dialog and get user confirmation
            IsOperationInProgress = false;
            var confirmed = await ShowPreviewDialogAsync(preview);
            
            if (!confirmed)
            {
                AddLog("Mirror sync cancelled by user");
                return;
            }

            if (!preview.HasChanges)
            {
                AddLog("No changes needed - all files are up to date");
                return;
            }

            // Proceed with sync
            IsOperationInProgress = true;
            _operationCts = new CancellationTokenSource();

            AddLog($"Mirror sync: {sourceFolder} ? {targetFolder}");

            Progress<(int current, int total, string file, string action)> progress = new(p =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    ProgressValue = (double)p.current / p.total * 100;
                    ProgressText = $"{p.action}: {p.current}/{p.total} - {p.file}";
                });
            });

            var result = await _restoreService.MirrorSyncToLocalAsync(
                filesToSync,
                targetFolder,
                sourceFolder,
                progress,
                _operationCts.Token);

            AddLog($"Mirror sync complete: {result.FilesTransferred} restored, {result.FilesDeleted} deleted, " +
                   $"{result.FilesUnchanged} unchanged, {result.FilesErrored} errors");

            foreach (var error in result.Errors.Take(5))
            {
                AddLog($"  Error: {error}");
            }
            if (result.Errors.Count > 5)
            {
                AddLog($"  ... and {result.Errors.Count - 5} more errors");
            }
        }
        catch (OperationCanceledException)
        {
            AddLog("Mirror sync cancelled");
        }
        catch (Exception ex)
        {
            AddLog($"Mirror sync failed: {ex.Message}");
        }
        finally
        {
            IsOperationInProgress = false;
            ProgressValue = 0;
            ProgressText = string.Empty;
        }
    }

    /// <summary>
    /// Builds the file tree from the flat restorable files list.
    /// </summary>
    private void BuildFileTree()
    {
        var files = RestorableFiles.Select(f => f.Model).ToList();
        var treeRoots = FileTreeNodeViewModel.BuildTree(files);

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            FileTreeRoots.Clear();
            foreach (var root in treeRoots)
            {
                FileTreeRoots.Add(root);
            }
            OnPropertyChanged(nameof(ShowAzureEmptyState));
            
            // Ensure selection state is updated after tree rebuild
            NotifySelectionChanged();
        });
    }

    /// <summary>
    /// Called when tree node selection changes.
    /// </summary>
    public void OnTreeNodeSelectionChanged()
    {
        NotifySelectionChanged();
    }

    #endregion

    #region Folder Management Commands

    [RelayCommand]
    private void AddWatchedFolder()
    {
        // Request the View to open a folder picker dialog
        FolderPickerRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Called by the View after a folder is selected from the picker.
    /// </summary>
    public async void AddWatchedFolderPath(string folderPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folderPath)) return;
            
            // Check if folder already exists in the list
            if (WatchedFolders.Any(f => f.Path.Equals(folderPath, StringComparison.OrdinalIgnoreCase)))
            {
                AddLog($"Folder already in watch list: {folderPath}");
                return;
            }

            WatchedFolders.Add(new WatchedFolderViewModel(new WatchedFolder
            {
                Path = folderPath,
                IsEnabled = true,
                ExcludePatterns = []
            }));
            
            AddLog($"Added watch folder: {folderPath}");
            SaveSettings();
            
            // Refresh the local files tree to show the new folder
            await RefreshLocalFilesAsync();
        }
        catch (Exception ex)
        {
            AddLog($"Error adding watch folder: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"Error adding watch folder: {ex}");
        }
    }

    [RelayCommand]
    private async Task RemoveWatchedFolderAsync()
    {
        if (SelectedWatchedFolder != null)
        {
            var folderPath = SelectedWatchedFolder.Path;
            WatchedFolders.Remove(SelectedWatchedFolder);
            SaveSettings();
            AddLog($"Removed watched folder: {folderPath}");
            
            // Refresh the local files tree to remove the folder
            await RefreshLocalFilesAsync();
        }
    }

    /// <summary>
    /// Gets whether a local folder can be removed (a root watched folder is selected).
    /// </summary>
    public bool CanRemoveSelectedLocalFolder => 
        SelectedLocalTreeNode != null && 
        SelectedLocalTreeNode.Parent == null && 
        SelectedLocalTreeNode.IsFolder;

    [RelayCommand]
    private async Task RemoveSelectedLocalFolderAsync()
    {
        if (SelectedLocalTreeNode == null || SelectedLocalTreeNode.Parent != null)
        {
            AddLog("Please select a root watched folder to remove");
            return;
        }

        var folderPath = SelectedLocalTreeNode.FullPath;
        var folderToRemove = WatchedFolders.FirstOrDefault(f => 
            f.Path.Equals(folderPath, StringComparison.OrdinalIgnoreCase));
        
        if (folderToRemove != null)
        {
            WatchedFolders.Remove(folderToRemove);
            SaveSettings();
            AddLog($"Removed watched folder: {folderPath}");
            
            // Refresh the local files tree
            await RefreshLocalFilesAsync();
        }
    }

    #endregion
}
