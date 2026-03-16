using System;
using System.Collections.Generic;
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

        Program.Logger?.Log("RestoreSelectedTreeFilesAsync: Starting tree view restore");

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
        var filesWithPaths = selectedFiles
            .Where(f => f.File != null)
            .Select(f => (
                file: f.File!, 
                targetPath: RestoreToOriginalLocation 
                    ? f.File!.LocalPath
                    : (!string.IsNullOrWhiteSpace(RestoreDirectory) 
                        ? Path.Combine(RestoreDirectory, Path.GetFileName(f.File!.LocalPath))
                        : f.EffectiveRestorePath)))
            .ToList();

        Program.Logger?.Log($"RestoreSelectedTreeFilesAsync: {filesWithPaths.Count} files, totalBytes={filesWithPaths.Sum(f => f.file.FileSize)}");

        // Generate preview to check for overwrites
        var preview = _restoreService.PreviewRestoreWithRemapping(filesWithPaths);

        var confirmed = await ShowPreviewDialogAsync(preview);

        if (!confirmed)
        {
            AddLog("Restore operation cancelled by user");
            return;
        }

        // Remove files the user excluded in the preview dialog
        var excluded = preview.ExcludedFilePaths;
        if (excluded.Count > 0)
        {
            filesWithPaths = filesWithPaths
                .Where(f => !excluded.Contains(f.file.LocalPath))
                .ToList();
            Program.Logger?.Log($"RestoreSelectedTreeFilesAsync: {excluded.Count} files excluded by user, {filesWithPaths.Count} remaining");
        }

        if (filesWithPaths.Count == 0)
        {
            AddLog("All files were excluded - nothing to restore");
            return;
        }

        IsOperationInProgress = true;
        CreateOperationCts();

        try
        {
            AddLog($"Restoring {filesWithPaths.Count} files with path remapping...");
            Program.Logger?.Log($"RestoreSelectedTreeFilesAsync: Calling RestoreFilesWithRemappingAsync");

            var result = await ExecuteRestoreWithRemappingAsync(filesWithPaths, _operationCts!.Token);

            LogRestoreResult(result);
            Program.Logger?.Log($"RestoreSelectedTreeFilesAsync: Complete - {result.SuccessfulFiles.Count} OK, " +
                $"{result.CorruptedRecoveryFiles.Count} corrupted-recovered, {result.FailedFiles.Count} failed");
        }
        catch (OperationCanceledException)
        {
            AddLog("Restore cancelled");
            Program.Logger?.Log("RestoreSelectedTreeFilesAsync: Cancelled by user");
        }
        catch (Exception ex)
        {
            AddLog($"Restore failed: {ex.Message}");
            Program.Logger?.LogException(ex, "RestoreSelectedTreeFilesAsync");
        }
        finally
        {
            IsOperationInProgress = false;
            StopProgressTracking();
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
            CreateOperationCts();

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
                _operationCts!.Token);

            AddLog($"Mirror sync complete: {result.FilesTransferred} restored, {result.FilesDeleted} deleted, " +
                   $"{result.FilesUnchanged} unchanged, {result.FilesErrored} errors");

            if (result.FilesCorruptedRecovered > 0)
            {
                AddLog($"  {result.FilesCorruptedRecovered} file(s) recovered to __corrupted__ subfolder");
            }

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
            StopProgressTracking();
        }
    }

    #region Restore Helpers

    /// <summary>
    /// Executes a batch restore with standard progress tracking.
    /// Uses RestoreFilesWithRemappingAsync for corrupted recovery support.
    /// </summary>
    private async Task<AzureBackup.Core.Services.RestoreResult> ExecuteRestoreWithRemappingAsync(
        List<(BackedUpFile file, string targetPath)> filesWithPaths,
        CancellationToken cancellationToken)
    {
        var totalBytes = filesWithPaths.Sum(f => f.file.FileSize);
        StartProgressTracking("Restoring", filesWithPaths.Count, totalBytes);

        Progress<(int current, int total, string file)> fileProgress = new(p =>
        {
            // File-level progress handled by byte-level reporter below
        });

        var lastFileIndex = -1;
        Progress<(long bytesCompleted, long fileSize, int fileIndex)> byteProgress = new(p =>
        {
            if (p.fileIndex != lastFileIndex)
            {
                if (lastFileIndex >= 0)
                {
                    CompleteFileProgress(filesWithPaths[lastFileIndex].file.FileSize);
                }
                lastFileIndex = p.fileIndex;
            }

            var fileName = Path.GetFileName(filesWithPaths[p.fileIndex].file.LocalPath);
            UpdateFileProgress(fileName, p.bytesCompleted, p.fileSize, p.fileIndex);
        });

        var result = await _restoreService.RestoreFilesWithRemappingAsync(
            filesWithPaths,
            overwriteExisting: true,
            fileProgress,
            byteProgress,
            cancellationToken);

        // Mark the last file as complete so cumulative bytes are fully accurate
        if (lastFileIndex >= 0)
        {
            CompleteFileProgress(filesWithPaths[lastFileIndex].file.FileSize);
        }

        return result;
    }

    /// <summary>
    /// Logs a restore result with corrupted recovery details.
    /// </summary>
    private void LogRestoreResult(AzureBackup.Core.Services.RestoreResult result)
    {
        AddLog($"Restore complete: {result.SuccessfulFiles.Count} succeeded, {result.FailedFiles.Count} failed");

        if (result.CorruptedRecoveryFiles.Count > 0)
        {
            AddLog($"  {result.CorruptedRecoveryFiles.Count} file(s) recovered to __corrupted__ subfolder:");
            foreach (var (originalPath, recoveredPath, badChunks) in result.CorruptedRecoveryFiles)
            {
                var detail = badChunks > 0 ? $"{badChunks} chunk(s) zero-filled" : "data intact";
                AddLog($"    {Path.GetFileName(originalPath)} → {recoveredPath} ({detail})");
            }
        }
    }

    #endregion

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
    /// Gets whether a local folder can be removed (a root watched folder is selected or checked).
    /// </summary>
    public bool CanRemoveSelectedLocalFolder => 
        (SelectedLocalTreeNode != null && 
         SelectedLocalTreeNode.Parent == null && 
         SelectedLocalTreeNode.IsFolder) ||
        LocalFileTreeRoots.Any(r => r.IsSelected);

    [RelayCommand]
    private async Task RemoveSelectedLocalFolderAsync()
    {
        // First check for checked (checkbox) root folders
        var checkedRoots = LocalFileTreeRoots.Where(r => r.IsSelected).ToList();
        
        if (checkedRoots.Count > 0)
        {
            // Remove all checked root folders
            foreach (var root in checkedRoots)
            {
                var folderPath = root.FullPath;
                var folderToRemove = WatchedFolders.FirstOrDefault(f => 
                    f.Path.Equals(folderPath, StringComparison.OrdinalIgnoreCase));
                
                if (folderToRemove != null)
                {
                    WatchedFolders.Remove(folderToRemove);
                    AddLog($"Removed watched folder: {folderPath}");
                }
            }
            
            SaveSettings();
            await RefreshLocalFilesAsync();
            return;
        }
        
        // Fall back to selected (clicked) tree node
        if (SelectedLocalTreeNode == null || SelectedLocalTreeNode.Parent != null)
        {
            AddLog("Please select or check a root watched folder to remove");
            return;
        }

        var selectedFolderPath = SelectedLocalTreeNode.FullPath;
        var selectedFolderToRemove = WatchedFolders.FirstOrDefault(f => 
            f.Path.Equals(selectedFolderPath, StringComparison.OrdinalIgnoreCase));
        
        if (selectedFolderToRemove != null)
        {
            WatchedFolders.Remove(selectedFolderToRemove);
            SaveSettings();
            AddLog($"Removed watched folder: {selectedFolderPath}");
            
            // Refresh the local files tree
            await RefreshLocalFilesAsync();
        }
    }

    #endregion
}
