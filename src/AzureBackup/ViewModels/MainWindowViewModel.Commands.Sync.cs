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
                root.IsSelected = true;
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
                root.IsSelected = false;
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
                root.IsSelected = false;
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
        await ConfirmAndExecuteBackupAsync(selectedFiles.Select(f => f.FullPath).ToList());
    }

    /// <summary>
    /// Returns true when a single root watched folder is selected on the left pane,
    /// enabling the Mirror Sync to Azure operation.
    /// </summary>
    public bool CanMirrorSyncToAzure =>
        IsInitialized && _blobService.IsConnected &&
        LocalFileTreeRoots.Count(r => r.IsSelected) == 1 &&
        LocalFileTreeRoots.First(r => r.IsSelected).IsFolder;

    /// <summary>
    /// Performs a mirror sync from the selected local watched folder to Azure:
    /// backs up new/modified files, marks deleted files as excluded.
    /// </summary>
    [RelayCommand]
    private async Task MirrorSyncToAzureAsync()
    {
        if (!IsInitialized || !_blobService.IsConnected)
        {
            AddLog("Please initialize and connect to Azure first");
            return;
        }

        var selectedRoot = LocalFileTreeRoots.FirstOrDefault(r => r.IsSelected && r.IsFolder);
        if (selectedRoot == null)
        {
            AddLog("Please select a watched folder to mirror sync");
            return;
        }

        // Find the matching WatchedFolder model
        var config = _databaseService.GetConfiguration();
        var watchedFolder = config.WatchedFolders
            .FirstOrDefault(f => f.Path.Equals(selectedRoot.FullPath, StringComparison.OrdinalIgnoreCase));

        if (watchedFolder == null)
        {
            AddLog($"Watched folder not found in configuration: {selectedRoot.FullPath}");
            return;
        }

        IsOperationInProgress = true;
        CreateOperationCts();

        try
        {
            AddLog($"Mirror sync to Azure: {watchedFolder.Path}");

            var totalFiles = selectedRoot.TotalFileCount;
            StartProgressTab("Mirror syncing to Azure", totalFiles, 0, totalFiles, 0);
            StartProgressTracking("Mirror syncing to Azure", totalFiles, 0);

            Progress<(int current, int total, string file, string action)> progress = new(p =>
            {
                ProgressValue = totalFiles > 0 ? (double)p.current / p.total * 100 : 0;
                ProgressText = $"[{p.current}/{p.total}] {p.action}: {p.file}";
            });

            var result = await _orchestrator.MirrorSyncToAzureAsync(watchedFolder, progress, _operationCts!.Token);

            ProgressTab.CompleteOperation(result.FilesTransferred, result.FilesErrored, 0, result.BytesTransferred);

            AddLog($"Mirror sync to Azure complete: {result.FilesTransferred} backed up, " +
                   $"{result.FilesDeleted} marked deleted, {result.FilesUnchanged} unchanged" +
                   (result.FilesErrored > 0 ? $", {result.FilesErrored} errors" : ""));

            foreach (var error in result.Errors.Take(10))
            {
                AddLog($"  Error: {error}");
            }

            await RefreshBothFilePanesAsync();
            RefreshStatistics();
        }
        catch (OperationCanceledException)
        {
            AddLog("Mirror sync to Azure cancelled");
            ProgressTab.MarkCancelled();
        }
        catch (Exception ex)
        {
            AddLog($"Mirror sync to Azure failed: {ex.Message}");
        }
        finally
        {
            IsOperationInProgress = false;
            StopProgressTracking();
        }
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
        OnPropertyChanged(nameof(CanMirrorSyncToAzure));
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
                AddLog($"Phase 1: Backing up {localFilesToBackup.Count} local file(s)...");

                var filePaths = localFilesToBackup.Select(f => f.FullPath).ToList();
                var backupResult = await ConfirmAndFilterBackupFilesAsync(filePaths, _operationCts!.Token);

                if (backupResult.HasValue)
                {
                    await ExecuteBackupAsync(backupResult.Value.files, backupResult.Value.preview, _operationCts.Token);
                }
                else
                {
                    AddLog("Backup phase skipped (no changes or cancelled)");
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

                var result = await ExecuteRestoreWithRemappingAsync(filesWithPaths, _operationCts!.Token);
                LogRestoreResult(result);
            }

            AddLog("Sync operation complete!");

            // Refresh both panels
            await RefreshBothFilePanesAsync();
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
            StopProgressTracking();
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

    #endregion
}
