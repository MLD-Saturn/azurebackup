using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;

namespace AzureBackup.ViewModels;

/// <summary>
/// Restore commands for MainWindowViewModel.
/// </summary>
public partial class MainWindowViewModel
{
    #region Restore Commands

    [RelayCommand]
    private async Task RefreshRestorableFilesAsync()
    {
        if (!IsInitialized)
        {
            AddLog("Please unlock first - go to Settings and enter your password");
            return;
        }

        // Check if blob service is connected
        if (!_blobService.IsConnected)
        {
            if (UseEntraIdAuth)
            {
                AddLog("Not connected to Azure Storage. Please sign in with Microsoft Entra ID in Settings.");
            }
            else
            {
                AddLog("Not connected to Azure Storage. Please configure your connection string in Settings.");
            }
            return;
        }

        IsOperationInProgress = true;
        AddLog("Loading files from Azure Storage...");
        
        try
        {
            var files = await _restoreService.ListRestorableFilesAsync();
            
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                RestorableFiles.Clear();
                foreach (var file in files.OrderByDescending(f => f.LastModified))
                {
                    RestorableFiles.Add(new BackedUpFileViewModel(file));
                }
                OnPropertyChanged(nameof(RestorableFilesEmpty));
                OnPropertyChanged(nameof(RestorableFilesCount));
                OnPropertyChanged(nameof(ShowAzureEmptyState));
                
                // Build tree view if enabled
                if (UseTreeView)
                {
                    BuildFileTree();
                }
                
                AddLog($"Loaded {files.Count} files from Azure Storage");
            });
        }
        catch (Exception ex)
        {
            AddLog($"Failed to load files: {ex.Message}");
        }
        finally
        {
            IsOperationInProgress = false;
        }
    }

    [RelayCommand]
    private async Task SearchFilesAsync()
    {
        if (!IsInitialized || string.IsNullOrWhiteSpace(SearchPattern))
            return;

        IsOperationInProgress = true;
        try
        {
            var files = await _restoreService.SearchFilesAsync(SearchPattern);
            
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                RestorableFiles.Clear();
                foreach (var file in files)
                {
                    RestorableFiles.Add(new BackedUpFileViewModel(file));
                }
                OnPropertyChanged(nameof(RestorableFilesEmpty));
                OnPropertyChanged(nameof(RestorableFilesCount));
                OnPropertyChanged(nameof(ShowAzureEmptyState));
                
                // Rebuild tree if in tree view mode
                if (UseTreeView)
                {
                    BuildFileTree();
                }
            });
        }
        finally
        {
            IsOperationInProgress = false;
        }
    }

    [RelayCommand]
    private async Task RestoreSelectedFileAsync()
    {
        if (!IsInitialized || SelectedRestoreFile == null)
            return;

        IsOperationInProgress = true;
        _operationCts = new CancellationTokenSource();

        try
        {
            Progress<(long current, long total)> progress = new(p =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    ProgressValue = (double)p.current / p.total * 100;
                    ProgressText = $"Restoring: {ProgressValue:F1}%";
                });
            });

            var targetPath = string.IsNullOrWhiteSpace(RestoreDirectory)
                ? null
                : Path.Combine(RestoreDirectory, Path.GetFileName(SelectedRestoreFile.LocalPath));

            await _restoreService.RestoreFileAsync(
                SelectedRestoreFile.Model, targetPath, true, progress, _operationCts.Token);
        }
        finally
        {
            IsOperationInProgress = false;
            ProgressValue = 0;
            ProgressText = string.Empty;
        }
    }

    [RelayCommand]
    private async Task RestoreAllAsync()
    {
        if (!IsInitialized || string.IsNullOrWhiteSpace(RestoreDirectory))
        {
            AddLog("Please specify a restore directory");
            return;
        }

        IsOperationInProgress = true;
        _operationCts = new CancellationTokenSource();

        try
        {
            Progress<(int current, int total, string file)> progress = new(p =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    ProgressValue = (double)p.current / p.total * 100;
                    ProgressText = $"Restoring: {p.current}/{p.total} - {Path.GetFileName(p.file)}";
                });
            });

            var result = await _restoreService.RestoreAllAsync(
                RestoreDirectory, true, progress, _operationCts.Token);

            AddLog($"Restore complete: {result.SuccessfulFiles.Count} succeeded, {result.FailedFiles.Count} failed");
        }
        finally
        {
            IsOperationInProgress = false;
            ProgressValue = 0;
            ProgressText = string.Empty;
        }
    }

    [RelayCommand]
    private async Task DeleteSelectedFileAsync()
    {
        if (!IsInitialized || SelectedRestoreFile == null)
        {
            AddLog("Please select a file to delete");
            return;
        }

        var fileToDelete = SelectedRestoreFile;
        var fileName = Path.GetFileName(fileToDelete.LocalPath);
        
        AddLog($"Deleting {fileName} from Azure...");
        IsOperationInProgress = true;

        try
        {
            var success = await _restoreService.DeleteFileAsync(fileToDelete.Model);
            
            if (success)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    RestorableFiles.Remove(fileToDelete);
                    OnPropertyChanged(nameof(RestorableFilesEmpty));
                    OnPropertyChanged(nameof(RestorableFilesCount));
                });
                AddLog($"Deleted {fileName} from Azure");
            }
            else
            {
                AddLog($"Failed to delete {fileName}");
            }
        }
        catch (Exception ex)
        {
            AddLog($"Error deleting {fileName}: {ex.Message}");
        }
        finally
        {
            IsOperationInProgress = false;
        }
    }

    [RelayCommand]
    private async Task RestoreSelectedFilesAsync()
    {
        if (!IsInitialized)
        {
            AddLog("Please unlock first");
            return;
        }

        var filesToRestore = SelectedRestoreFiles.ToList();
        if (filesToRestore.Count == 0)
        {
            AddLog("Please select files to restore");
            return;
        }

        if (string.IsNullOrWhiteSpace(RestoreDirectory))
        {
            AddLog("Please specify a restore directory");
            return;
        }

        IsOperationInProgress = true;
        _operationCts = new CancellationTokenSource();

        try
        {
            // Calculate total size for progress tracking
            var totalBytes = filesToRestore.Sum(f => f.Model.FileSize);
            StartProgressTracking("Restoring", filesToRestore.Count, totalBytes);
            
            var successCount = 0;
            var failCount = 0;

            for (var i = 0; i < filesToRestore.Count; i++)
            {
                _operationCts.Token.ThrowIfCancellationRequested();

                var file = filesToRestore[i];
                var fileName = Path.GetFileName(file.LocalPath);
                var fileSize = file.Model.FileSize;
                
                UpdateFileProgress(fileName, 0, fileSize, i);

                try
                {
                    var targetPath = Path.Combine(RestoreDirectory, fileName);
                    
                    // Create progress reporter for individual file
                    Progress<(long current, long total)> fileProgress = new(p =>
                    {
                        UpdateFileProgress(fileName, p.current, p.total, i);
                    });
                    
                    var restored = await _restoreService.RestoreFileAsync(
                        file.Model, targetPath, true, fileProgress, _operationCts.Token);
                    
                    if (restored)
                    {
                        successCount++;
                        CompleteFileProgress(fileSize);
                    }
                    else
                    {
                        failCount++;
                    }
                }
                catch (Exception ex)
                {
                    AddLog($"Failed to restore {fileName}: {ex.Message}");
                    failCount++;
                }
            }

            AddLog($"Restore complete: {successCount} succeeded, {failCount} failed");
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
    private async Task DeleteSelectedFilesAsync()
    {
        if (!IsInitialized)
        {
            AddLog("Please unlock first");
            return;
        }

        // Get selected files - handle both tree view and flat list modes
        List<BackedUpFileViewModel> filesToDelete;
        if (UseTreeView)
        {
            // In tree view mode, first check for checked (checkbox) files
            var selectedTreeFiles = FileTreeRoots
                .SelectMany(r => r.GetSelectedFiles())
                .Where(f => f.File != null)
                .Select(f => RestorableFiles.FirstOrDefault(rf => rf.Model == f.File))
                .Where(f => f != null)
                .Cast<BackedUpFileViewModel>()
                .ToList();
            
            // If no checked files, use the right-clicked/selected tree node
            if (selectedTreeFiles.Count == 0 && SelectedTreeNode != null)
            {
                // Get all files from the selected node (works for both files and folders)
                var filesFromNode = SelectedTreeNode.GetAllDescendants()
                    .Where(n => n.IsFile && n.File != null)
                    .Select(n => RestorableFiles.FirstOrDefault(rf => rf.Model == n.File))
                    .Where(f => f != null)
                    .Cast<BackedUpFileViewModel>()
                    .ToList();
                selectedTreeFiles = filesFromNode;
            }
            
            filesToDelete = selectedTreeFiles;
        }
        else
        {
            filesToDelete = SelectedRestoreFiles.ToList();
        }
        
        if (filesToDelete.Count == 0)
        {
            AddLog("Please select files to delete");
            return;
        }

        // Generate preview
        var preview = _restoreService.PreviewDeleteFromAzure(filesToDelete.Select(f => f.Model));


        // Show preview dialog and get user confirmation
        var confirmed = await ShowPreviewDialogAsync(preview);
        
        if (!confirmed)
        {
            AddLog("Delete operation cancelled by user");
            return;
        }

        AddLog($"Deleting {filesToDelete.Count} file(s) from Azure...");
        IsOperationInProgress = true;
        _operationCts = new CancellationTokenSource();

        try
        {
            var totalBytes = filesToDelete.Sum(f => f.Model.FileSize);
            StartProgressTracking("Deleting", filesToDelete.Count, totalBytes);
            
            var successCount = 0;
            var failCount = 0;

            for (var i = 0; i < filesToDelete.Count; i++)
            {
                _operationCts.Token.ThrowIfCancellationRequested();

                var file = filesToDelete[i];
                var fileName = Path.GetFileName(file.LocalPath);
                var fileSize = file.Model.FileSize;
                
                UpdateFileProgress(fileName, 0, fileSize, i);

                try
                {
                    var success = await _restoreService.DeleteFileAsync(file.Model, _operationCts.Token);
                    
                    if (success)
                    {
                        successCount++;
                        CompleteFileProgress(fileSize);
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            RestorableFiles.Remove(file);
                        });
                    }
                    else
                    {
                        failCount++;
                    }
                }
                catch (Exception ex)
                {
                    AddLog($"Failed to delete {fileName}: {ex.Message}");
                    failCount++;
                }
            }

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                // Clear selection state after deletion
                SelectedTreeNode = null;
                foreach (var file in RestorableFiles)
                {
                    file.IsSelected = false;
                }
                
                OnPropertyChanged(nameof(RestorableFilesEmpty));
                OnPropertyChanged(nameof(RestorableFilesCount));
                NotifySelectionChanged();
                
                // Rebuild tree if in tree view mode
                if (UseTreeView)
                {
                    BuildFileTree();
                }
            });

            // Refresh local files to update backup status indicators
            await RefreshLocalFilesAsync();

            AddLog($"Delete complete: {successCount} succeeded, {failCount} failed");
        }
        catch (OperationCanceledException)
        {
            AddLog("Delete cancelled");
        }
        finally
        {
            IsOperationInProgress = false;
            ClearProgressTracking();
        }
    }

    [RelayCommand]
    private void SelectAllRestorableFiles()
    {
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

    [RelayCommand]
    private void DeselectAllRestorableFiles()
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

    [RelayCommand]
    private void BrowseRestoreDirectory()
    {
        // Request the View to open a folder picker dialog for restore directory
        RestoreFolderPickerRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Called by the View after a restore folder is selected.
    /// </summary>
    public void SetRestoreDirectory(string folderPath)
    {
        if (!string.IsNullOrWhiteSpace(folderPath))
        {
            RestoreDirectory = folderPath;
            AddLog($"Restore directory set to: {folderPath}");
        }
    }

    #endregion
}
