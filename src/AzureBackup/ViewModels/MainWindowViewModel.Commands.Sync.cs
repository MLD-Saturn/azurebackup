using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;

namespace AzureBackup.ViewModels;

/// <summary>
/// Sync view commands for MainWindowViewModel.
/// </summary>
public partial class MainWindowViewModel
{
    // Tracks whether file watching has been started for UI auto-refresh
    private bool _uiFileWatchingStarted;

    #region Sync View Commands

    /// <summary>
    /// Ensures file watching is started for UI auto-refresh.
    /// This is separate from the backup monitoring file watcher.
    /// </summary>
    private void EnsureFileWatchingForUi()
    {
        if (_uiFileWatchingStarted || !IsInitialized)
            return;

        // Start file watching for UI updates (if not already running via backup)
        if (!_fileWatcherService.IsRunning)
        {
            _fileWatcherService.Start();
        }
        
        _uiFileWatchingStarted = true;
    }

    /// <summary>
    /// Clears the sync search filter.
    /// </summary>
    [RelayCommand]
    private void ClearSyncSearch()
    {
        SyncSearchPattern = string.Empty;
    }

    /// <summary>
    /// Selects all files in both local and Azure panels.
    /// </summary>
    [RelayCommand]
    private void SelectAllSyncFiles()
    {
        // Select all local files
        if (UseTreeView)
        {
            foreach (var root in LocalFileTreeRoots)
            {
                SelectLocalTreeNodeRecursive(root, true);
            }
        }
        else
        {
            foreach (var file in LocalFilesFlatList)
            {
                file.IsSelected = true;
            }
        }

        // Select all Azure files
        if (UseTreeView)
        {
            foreach (var root in FileTreeRoots)
            {
                root.IsSelected = true;
            }
        }
        else
        {
            SelectAllFiles();
        }

        NotifySelectionChanged();
    }

    /// <summary>
    /// Deselects all files in both local and Azure panels.
    /// </summary>
    [RelayCommand]
    private void DeselectAllSyncFiles()
    {
        // Deselect all local files
        if (UseTreeView)
        {
            foreach (var root in LocalFileTreeRoots)
            {
                SelectLocalTreeNodeRecursive(root, false);
            }
        }
        else
        {
            foreach (var file in LocalFilesFlatList)
            {
                file.IsSelected = false;
            }
        }

        // Deselect all Azure files
        if (UseTreeView)
        {
            foreach (var root in FileTreeRoots)
            {
                root.IsSelected = false;
            }
        }
        else
        {
            DeselectAllFiles();
        }

        NotifySelectionChanged();
    }

    /// <summary>
    /// Deselects all local files only.
    /// </summary>
    public void DeselectAllLocalFiles()
    {
        if (UseTreeView)
        {
            foreach (var root in LocalFileTreeRoots)
            {
                SelectLocalTreeNodeRecursive(root, false);
            }
        }
        else
        {
            foreach (var file in LocalFilesFlatList)
            {
                file.IsSelected = false;
            }
        }
        NotifyLocalSelectionChanged();
    }

    /// <summary>
    /// Deselects all Azure files only.
    /// </summary>
    public void DeselectAllAzureFiles()
    {
        if (UseTreeView)
        {
            foreach (var root in FileTreeRoots)
            {
                root.IsSelected = false;
            }
        }
        else
        {
            DeselectAllFiles();
        }
        NotifySelectionChanged();
    }

    /// <summary>
    /// Recursively selects or deselects local tree nodes.
    /// </summary>
    private static void SelectLocalTreeNodeRecursive(LocalFileTreeNodeViewModel node, bool isSelected)
    {
        node.IsSelected = isSelected;
        foreach (var child in node.Children)
        {
            SelectLocalTreeNodeRecursive(child, isSelected);
        }
    }

    /// <summary>
    /// Expands all nodes in both local and Azure tree views.
    /// </summary>
    [RelayCommand]
    private void ExpandAllLocalTreeNodes()
    {
        foreach (var root in LocalFileTreeRoots)
        {
            root.ExpandAll();
        }
    }

    /// <summary>
    /// Collapses all nodes in both local and Azure tree views.
    /// </summary>
    [RelayCommand]
    private void CollapseAllLocalTreeNodes()
    {
        foreach (var root in LocalFileTreeRoots)
        {
            root.CollapseAll();
        }
    }

    /// <summary>
    /// Gets all selected local files from the tree view.
    /// </summary>
    private IEnumerable<LocalFileTreeNodeViewModel> GetSelectedLocalFiles()
    {
        if (UseTreeView)
        {
            foreach (var root in LocalFileTreeRoots)
            {
                foreach (var file in GetSelectedLocalFilesRecursive(root))
                {
                    yield return file;
                }
            }
        }
        else
        {
            foreach (var file in LocalFilesFlatList.Where(f => f.IsSelected && f.IsFile))
            {
                yield return file;
            }
        }
    }

    private static IEnumerable<LocalFileTreeNodeViewModel> GetSelectedLocalFilesRecursive(LocalFileTreeNodeViewModel node)
    {
        if (node.IsFile && node.IsSelected)
        {
            yield return node;
        }
        
        foreach (var child in node.Children)
        {
            foreach (var file in GetSelectedLocalFilesRecursive(child))
            {
                yield return file;
            }
        }
    }

    /// <summary>
    /// Returns true if any local files are selected.
    /// </summary>
    public bool HasSelectedLocalFiles => GetSelectedLocalFiles().Any();

    /// <summary>
    /// Count of selected local files.
    /// </summary>
    public int SelectedLocalFilesCount => GetSelectedLocalFiles().Count();

    /// <summary>
    /// Display text for selected local files.
    /// </summary>
    public string SelectedLocalFilesText => SelectedLocalFilesCount == 0 
        ? "" 
        : $"{SelectedLocalFilesCount} local file(s) selected";

    /// <summary>
    /// Backs up selected local files to Azure.
    /// </summary>
    [RelayCommand]
    private async Task BackupSelectedLocalFilesAsync()
    {
        if (!IsInitialized)
        {
            AddLog("Please initialize first");
            return;
        }

        if (!_blobService.IsConnected)
        {
            AddLog("Not connected to Azure Storage. Please check your connection settings.");
            return;
        }

        var selectedFiles = GetSelectedLocalFiles().ToList();
        if (selectedFiles.Count == 0)
        {
            AddLog("No local files selected for backup");
            return;
        }

        AddLog($"Preparing to backup {selectedFiles.Count} selected file(s)...");
        IsOperationInProgress = true;
        CreateOperationCts();

        try
        {
            // Create preview for selected files
            var preview = await _orchestrator.PreviewBackupFilesAsync(
                selectedFiles.Select(f => f.FullPath).ToList(),
                _operationCts!.Token);

            IsOperationInProgress = false;

            if (!preview.HasChanges)
            {
                AddLog("All selected files are already backed up - no changes needed");
                return;
            }

            var confirmed = await ShowPreviewDialogAsync(preview);
            if (!confirmed)
            {
                AddLog("Backup cancelled by user");
                return;
            }

            // Proceed with backup
            IsOperationInProgress = true;
            CurrentOperationType = "Backing up";
            TotalFilesInOperation = preview.FilesToCreate.Count + preview.FilesToOverwrite.Count;
            CompletedFilesCount = 0;
            TotalBytesToProcess = preview.TotalBytesToTransfer;
            TotalBytesProcessed = 0;
            _operationStartTime = DateTime.Now;
            _lastSpeedUpdate = DateTime.Now;

            Progress<(int fileIndex, int totalFiles, string fileName, long bytesProcessed, long totalBytes, long currentFileBytes, long currentFileSize)> progress = new(p =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    CompletedFilesCount = p.fileIndex;
                    CurrentFileName = p.fileName;
                    TotalBytesProcessed = p.bytesProcessed;
                    
                    // Overall progress (files completed)
                    if (p.totalFiles > 0)
                        ProgressValue = (double)p.fileIndex / p.totalFiles * 100;
                    
                    // Current file progress
                    if (p.currentFileSize > 0)
                    {
                        CurrentFileProgress = (double)p.currentFileBytes / p.currentFileSize * 100;
                        CurrentFileProgressText = $"{FormatBytesStatic(p.currentFileBytes)} / {FormatBytesStatic(p.currentFileSize)}";
                    }
                    else
                    {
                        CurrentFileProgress = 0;
                        CurrentFileProgressText = string.Empty;
                    }
                    
                    OnPropertyChanged(nameof(FilesProgressText));
                    OnPropertyChanged(nameof(BytesProgressText));
                    
                    // Update speed and ETA
                    UpdateSpeedAndEta();
                });
            });

            await _orchestrator.BackupFilesAsync(
                selectedFiles.Select(f => f.FullPath).ToList(),
                progress,
                _operationCts.Token);

            AddLog($"Successfully backed up {TotalFilesInOperation} file(s)");
            
            // Refresh both panels to show updated status
            await RefreshLocalFilesAsync();
            await RefreshRestorableFilesAsync();
        }
        catch (OperationCanceledException)
        {
            AddLog("Backup cancelled");
        }
        catch (Exception ex)
        {
            AddLog($"Backup failed: {ex.Message}");
        }
        finally
        {
            IsOperationInProgress = false;
            ResetProgressState();
        }
    }

    /// <summary>
    /// Resets all progress-related state.
    /// </summary>
    private void ResetProgressState()
    {
        ProgressValue = 0;
        ProgressText = string.Empty;
        CurrentOperationType = string.Empty;
        CurrentFileName = string.Empty;
        CurrentFileProgress = 0;
        CurrentFileProgressText = string.Empty;
        CompletedFilesCount = 0;
        TotalFilesInOperation = 0;
        TotalBytesProcessed = 0;
        TotalBytesToProcess = 0;
        OperationSpeed = string.Empty;
        EstimatedTimeRemaining = string.Empty;
    }

    /// <summary>
    /// Notifies that selection-related properties may have changed.
    /// </summary>
    private void NotifyLocalSelectionChanged()
    {
        OnPropertyChanged(nameof(HasSelectedLocalFiles));
        OnPropertyChanged(nameof(SelectedLocalFilesCount));
        OnPropertyChanged(nameof(SelectedLocalFilesText));
        OnPropertyChanged(nameof(CanRemoveSelectedLocalFolder));
    }

    /// <summary>
    /// Returns true if there are selections that can be synced (local files to backup or Azure files to restore).
    /// </summary>
    public bool CanSyncSelected => HasSelectedLocalFiles || HasSelectedFiles;

    /// <summary>
    /// Performs a smart sync operation: backs up selected local files AND restores selected Azure files.
    /// </summary>
    [RelayCommand]
    private async Task SyncSelectedAsync()
    {
        if (!IsInitialized)
        {
            AddLog("Please initialize first");
            return;
        }

        if (!_blobService.IsConnected)
        {
            AddLog("Not connected to Azure Storage. Please check your connection settings.");
            return;
        }

        var localFilesToBackup = GetSelectedLocalFiles().ToList();
        
        // Get selected Azure files - handle both tree view and flat list
        var azureTreeFiles = UseTreeView 
            ? FileTreeRoots.SelectMany(r => r.GetSelectedFiles()).Where(f => f.File != null).ToList()
            : new List<FileTreeNodeViewModel>();
        var azureFlatFiles = !UseTreeView
            ? RestorableFiles.Where(f => f.IsSelected).ToList()
            : new List<BackedUpFileViewModel>();
        
        var azureFilesCount = UseTreeView ? azureTreeFiles.Count : azureFlatFiles.Count;

        if (localFilesToBackup.Count == 0 && azureFilesCount == 0)
        {
            AddLog("No files selected for sync");
            return;
        }

        AddLog($"Sync operation: {localFilesToBackup.Count} file(s) to backup, {azureFilesCount} file(s) to restore");
        IsOperationInProgress = true;
        CreateOperationCts();

        try
        {
            // Phase 1: Backup local files
            if (localFilesToBackup.Count > 0)
            {
                CurrentOperationType = "Backing up local files";
                AddLog($"Phase 1: Backing up {localFilesToBackup.Count} local file(s)...");


                var backupPreview = await _orchestrator.PreviewBackupFilesAsync(
                    localFilesToBackup.Select(f => f.FullPath).ToList(),
                    _operationCts!.Token);

                if (backupPreview.HasChanges)
                {
                    TotalFilesInOperation = backupPreview.FilesToCreate.Count + backupPreview.FilesToOverwrite.Count;
                    TotalBytesToProcess = backupPreview.TotalBytesToTransfer;
                    _operationStartTime = DateTime.Now;
                    _lastSpeedUpdate = DateTime.Now;

                    Progress<(int fileIndex, int totalFiles, string fileName, long bytesProcessed, long totalBytes, long currentFileBytes, long currentFileSize)> backupProgress = new(p =>
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            CompletedFilesCount = p.fileIndex;
                            CurrentFileName = p.fileName;
                            TotalBytesProcessed = p.bytesProcessed;
                            if (p.totalFiles > 0)
                                ProgressValue = (double)p.fileIndex / p.totalFiles * 50; // First 50%
                            
                            // Current file progress
                            if (p.currentFileSize > 0)
                            {
                                CurrentFileProgress = (double)p.currentFileBytes / p.currentFileSize * 100;
                                CurrentFileProgressText = $"{FormatBytesStatic(p.currentFileBytes)} / {FormatBytesStatic(p.currentFileSize)}";
                            }
                            
                            OnPropertyChanged(nameof(FilesProgressText));
                            OnPropertyChanged(nameof(BytesProgressText));
                            
                            // Update speed and ETA
                            UpdateSpeedAndEta();
                        });
                    });

                    await _orchestrator.BackupFilesAsync(
                        localFilesToBackup.Select(f => f.FullPath).ToList(),
                        backupProgress,
                        _operationCts.Token);

                    AddLog($"Backup phase complete: {TotalFilesInOperation} file(s) backed up");
                }
                else
                {
                    AddLog("All local files are already backed up");
                }
            }

            // Phase 2: Restore Azure files
            if (azureFilesCount > 0)
            {
                CurrentOperationType = "Restoring from Azure";
                AddLog($"Phase 2: Restoring {azureFilesCount} file(s) from Azure...");

                var filesWithPaths = UseTreeView
                    ? azureTreeFiles
                        .Select(f => (file: f.File!, targetPath: RestoreToOriginalLocation ? f.File!.LocalPath : f.EffectiveRestorePath))
                        .ToList()
                    : azureFlatFiles
                        .Select(f => (file: f.Model, targetPath: RestoreToOriginalLocation ? f.Model.LocalPath : System.IO.Path.Combine(RestoreDirectory, System.IO.Path.GetFileName(f.Model.LocalPath))))
                        .ToList();

                Progress<(int current, int total, string file)> restoreProgress = new(p =>
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        ProgressValue = 50 + ((double)p.current / p.total * 50); // Second 50%
                        ProgressText = $"Restoring: {p.current}/{p.total} - {System.IO.Path.GetFileName(p.file)}";
                    });
                });

                var result = await _restoreService.RestoreFilesWithRemappingAsync(
                    filesWithPaths,
                    overwriteExisting: true,
                    restoreProgress,
                    fileByteProgress: null, // Not using byte-level progress for sync view
                    _operationCts!.Token);

                AddLog($"Restore phase complete: {result.SuccessfulFiles.Count} succeeded, {result.FailedFiles.Count} failed");
            }

            AddLog("Sync operation complete!");
            
            // Refresh both panels
            await RefreshLocalFilesAsync();
            await RefreshRestorableFilesAsync();
        }
        catch (OperationCanceledException)
        {
            AddLog("Sync cancelled");
        }
        catch (Exception ex)
        {
            AddLog($"Sync failed: {ex.Message}");
        }
        finally
        {
            IsOperationInProgress = false;
            ResetProgressState();
        }
    }

    /// <summary>
    /// Gets the file paths of all selected local files.
    /// </summary>
    public List<string> GetSelectedLocalFilePaths()
    {
        return GetSelectedLocalFiles().Select(f => f.FullPath).ToList();
    }

    /// <summary>
    /// Gets the file paths of all selected Azure files.
    /// </summary>
    public List<string> GetSelectedAzureFilePaths()
    {
        if (UseTreeView)
        {
            return FileTreeRoots
                .SelectMany(r => r.GetSelectedFiles())
                .Where(f => f.File != null)
                .Select(f => f.File!.LocalPath)
                .ToList();
        }
        else
        {
            return RestorableFiles
                .Where(f => f.IsSelected)
                .Select(f => f.LocalPath)
                .ToList();
        }
    }

    /// <summary>
    /// Backs up specific files by their paths (used for drag-drop operations).
    /// </summary>
    public async Task BackupSpecificFilesAsync(List<string> filePaths)
    {
        if (!IsInitialized)
        {
            AddLog("Please initialize first");
            return;
        }

        if (!_blobService.IsConnected)
        {
            AddLog("Not connected to Azure Storage. Please check your connection settings.");
            return;
        }

        if (filePaths.Count == 0)
        {
            AddLog("No files to backup");
            return;
        }

        AddLog($"Preparing to backup {filePaths.Count} file(s)...");
        IsOperationInProgress = true;
        CreateOperationCts();

        try
        {
            var preview = await _orchestrator.PreviewBackupFilesAsync(filePaths, _operationCts!.Token);

            IsOperationInProgress = false;

            if (!preview.HasChanges)
            {
                AddLog("All files are already backed up - no changes needed");
                return;
            }

            var confirmed = await ShowPreviewDialogAsync(preview);
            if (!confirmed)
            {
                AddLog("Backup cancelled by user");
                return;
            }

            IsOperationInProgress = true;
            CurrentOperationType = "Backing up";
            TotalFilesInOperation = preview.FilesToCreate.Count + preview.FilesToOverwrite.Count;
            CompletedFilesCount = 0;
            TotalBytesToProcess = preview.TotalBytesToTransfer;
            TotalBytesProcessed = 0;
            _operationStartTime = DateTime.Now;
            _lastSpeedUpdate = DateTime.Now;

            Progress<(int fileIndex, int totalFiles, string fileName, long bytesProcessed, long totalBytes, long currentFileBytes, long currentFileSize)> progress = new(p =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    CompletedFilesCount = p.fileIndex;
                    CurrentFileName = p.fileName;
                    TotalBytesProcessed = p.bytesProcessed;
                    
                    if (p.totalFiles > 0)
                        ProgressValue = (double)p.fileIndex / p.totalFiles * 100;
                    
                    // Current file progress
                    if (p.currentFileSize > 0)
                    {
                        CurrentFileProgress = (double)p.currentFileBytes / p.currentFileSize * 100;
                        CurrentFileProgressText = $"{FormatBytesStatic(p.currentFileBytes)} / {FormatBytesStatic(p.currentFileSize)}";
                    }
                    else
                    {
                        CurrentFileProgress = 0;
                        CurrentFileProgressText = string.Empty;
                    }
                    
                    OnPropertyChanged(nameof(FilesProgressText));
                    OnPropertyChanged(nameof(BytesProgressText));
                    
                    // Update speed and ETA
                    UpdateSpeedAndEta();
                });
            });

            await _orchestrator.BackupFilesAsync(filePaths, progress, _operationCts.Token);

            AddLog($"Successfully backed up {TotalFilesInOperation} file(s)");
            
            await RefreshLocalFilesAsync();
            await RefreshRestorableFilesAsync();
        }
        catch (OperationCanceledException)
        {
            AddLog("Backup cancelled");
        }
        catch (Exception ex)
        {
            AddLog($"Backup failed: {ex.Message}");
        }
        finally
        {
            IsOperationInProgress = false;
            ResetProgressState();
        }
    }

    #endregion
}
