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
/// Backup commands for MainWindowViewModel.
/// </summary>
public partial class MainWindowViewModel
{
    #region Backup Commands

    [RelayCommand]
    private async Task StartBackupAsync()
    {
        if (!IsInitialized)
        {
            AddLog("Please initialize first");
            return;
        }

        // Check if there are any watched folders
        if (!WatchedFolders.Any(f => f.IsEnabled))
        {
            AddLog("Please add at least one watched folder in Settings");
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

        // Generate preview of what will be backed up
        AddLog("Analyzing files for backup...");
        IsOperationInProgress = true;
        _operationCts = new CancellationTokenSource();

        try
        {
            var preview = await _orchestrator.PreviewBackupSyncAsync(_operationCts.Token);

            // Show preview dialog if there are files to backup
            IsOperationInProgress = false;
            
            if (!preview.HasChanges)
            {
                AddLog("All files are up to date - no backup needed");
                return;
            }

            var confirmed = await ShowPreviewDialogAsync(preview);

            if (!confirmed)
            {
                AddLog("Backup cancelled by user");
                return;
            }

            // Proceed with sync
            IsOperationInProgress = true;
            AddLog("Starting backup sync...");
            
            Progress<(int current, int total, string file, string status)> syncProgress = new(p =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    ProgressValue = (double)p.current / p.total * 100;
                    ProgressText = $"Checking: {p.current}/{p.total} - {p.file} ({p.status})";
                });
            });

            var syncResult = await _orchestrator.PerformInitialSyncAsync(syncProgress, _operationCts.Token);
            
            // Log sync summary
            if (syncResult.TotalToBackup > 0)
            {
                AddLog($"Sync complete: {syncResult.NewFilesQueued} new, {syncResult.ModifiedFilesQueued} modified, " +
                       $"{syncResult.UnchangedFiles} unchanged files");
            }
            else
            {
                AddLog($"All {syncResult.UnchangedFiles} files are up to date");
            }
            
            ProgressValue = 0;
            ProgressText = string.Empty;
        }
        catch (OperationCanceledException)
        {
            AddLog("Sync cancelled");
            IsOperationInProgress = false;
            return;
        }
        finally
        {
            IsOperationInProgress = false;
        }

        // Start the backup monitoring service
        _orchestrator.Start();
        IsBackupRunning = true;
        AddLog("Backup monitoring started - watching for file changes");
        RefreshStatistics();
        
        // Refresh local file tree to show updated status
        await RefreshLocalFilesAsync();
    }

    [RelayCommand]
    private async Task StopBackupAsync()
    {
        await _orchestrator.StopAsync();
        IsBackupRunning = false;
        AddLog("Backup monitoring stopped");
        RefreshStatistics();
    }

    [RelayCommand]
    private void PauseBackup()
    {
        _orchestrator.Pause();
        AddLog("Backup paused");
    }

    [RelayCommand]
    private void ResumeBackup()
    {
        _orchestrator.Resume();
        AddLog("Backup resumed");
    }

    [RelayCommand]
    private async Task PerformFullScanAsync()
    {
        if (!IsInitialized)
        {
            AddLog("Please initialize first");
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

        // Generate preview showing ALL files (for full scan)
        AddLog("Analyzing all files for full scan...");
        IsOperationInProgress = true;
        _operationCts = new CancellationTokenSource();
        
        try
        {
            // For full scan, we create a preview that shows all files as "to be backed up"
            var config = _databaseService.GetConfiguration();
            OperationPreview preview = new()
            {
                OperationType = OperationType.Backup,
                OperationDescription = "Force full scan - re-upload ALL files (ignoring backup history)",
                SourceDescription = $"{config.WatchedFolders.Count(f => f.IsEnabled)} watched folder(s)",
                TargetDescription = $"Azure Storage ({config.ContainerName ?? "backup"})"
            };

            // Scan all watched folders to get file count
            List<string> allFiles = new();
            foreach (var folder in config.WatchedFolders.Where(f => f.IsEnabled))
            {
                _operationCts.Token.ThrowIfCancellationRequested();
                var files = await _fileWatcherService.ScanFolderAsync(folder, _operationCts.Token);
                allFiles.AddRange(files);
            }

            // Add all files to the preview
            foreach (var filePath in allFiles)
            {
                try
                {
                    FileInfo fileInfo = new(filePath);
                    if (fileInfo.Exists)
                    {
                        preview.FilesToCreate.Add(new PreviewFileAction
                        {
                            FilePath = filePath,
                            FileSize = fileInfo.Length,
                            LastModified = fileInfo.LastWriteTime,
                            Action = FileActionType.Update,
                            Reason = "Force re-upload (full scan)"
                        });
                    }
                }
                catch { /* Skip inaccessible files */ }
            }

            // Show preview dialog
            IsOperationInProgress = false;
            
            if (!preview.HasChanges)
            {
                AddLog("No files found in watched folders");
                return;
            }

            var confirmed = await ShowPreviewDialogAsync(preview);

            if (!confirmed)
            {
                AddLog("Full scan cancelled by user");
                return;
            }

            // Proceed with full scan
            IsOperationInProgress = true;
            AddLog("?? Force Full Scan: Re-queuing ALL files (ignoring backup history)...");
            
            Progress<(int current, int total, string file)> progress = new(p =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    ProgressValue = (double)p.current / p.total * 100;
                    ProgressText = $"Scanning: {p.current}/{p.total} - {Path.GetFileName(p.file)}";
                });
            });

            await _orchestrator.PerformFullScanAsync(progress, _operationCts.Token);
            RefreshStatistics();
            AddLog("Full scan complete - all files queued for backup");
            
            // Refresh local file tree
            await RefreshLocalFilesAsync();
        }
        catch (OperationCanceledException)
        {
            AddLog("Scan cancelled");
        }
        finally
        {
            IsOperationInProgress = false;
            ProgressValue = 0;
            ProgressText = string.Empty;
        }
    }

    [RelayCommand]
    private void BackupSingleFile()
    {
        // Request the View to open a file picker dialog
        FilePickerRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Called by the View after files are selected for backup.
    /// </summary>
    public async Task BackupSelectedFilesAsync(string[] filePaths)
    {
        if (!IsInitialized)
        {
            AddLog("Please initialize first");
            return;
        }

        if (filePaths.Length == 0) return;

        IsOperationInProgress = true;
        _operationCts = new CancellationTokenSource();

        try
        {
            // Calculate total size for progress tracking
            long totalBytes = 0;
            foreach (var path in filePaths)
            {
                try
                {
                    FileInfo info = new(path);
                    if (info.Exists) totalBytes += info.Length;
                }
                catch { /* Skip inaccessible files */ }
            }

            StartProgressTracking("Backing up", filePaths.Length, totalBytes);
            
            var successCount = 0;
            var failCount = 0;

            for (var i = 0; i < filePaths.Length; i++)
            {
                _operationCts.Token.ThrowIfCancellationRequested();
                
                var file = filePaths[i];
                var fileName = Path.GetFileName(file);
                long fileSize = 0;
                
                try
                {
                    FileInfo info = new(file);
                    if (info.Exists) fileSize = info.Length;
                }
                catch { /* Proceed with 0 size */ }

                UpdateFileProgress(fileName, 0, fileSize, i);

                try
                {
                    // Create progress reporter for byte-level progress
                    var fileIndex = i;
                    var currentFileSize = fileSize;
                    Progress<(long current, long total)> fileProgress = new(p =>
                    {
                        UpdateFileProgress(fileName, p.current, currentFileSize, fileIndex);
                    });
                    
                    var success = await _orchestrator.BackupFileAsync(file, fileProgress, _operationCts.Token);
                    if (success)
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
                    AddLog($"Failed to backup {fileName}: {ex.Message}");
                    failCount++;
                }
            }

            AddLog($"Backup complete: {successCount} succeeded, {failCount} failed");
            RefreshStatistics();
            RefreshBackedUpFiles();
            
            // Refresh Azure file list after backup
            await RefreshFromAzureAsync();
        }
        catch (OperationCanceledException)
        {
            AddLog("Backup cancelled");
        }
        finally
        {
            IsOperationInProgress = false;
            ClearProgressTracking();
        }
    }

    /// <summary>
    /// Backs up a list of specific files (used by drag-drop).
    /// Shows a preview dialog and validates against actual Azure state.
    /// </summary>
    public async Task BackupSpecificFilesAsync(IEnumerable<string> filePaths)
    {
        if (!IsInitialized)
        {
            AddLog("Please initialize first");
            return;
        }

        var fileList = filePaths.ToList();
        if (fileList.Count == 0) return;

        // Get Azure file paths for validation (to detect stale local DB records)
        HashSet<string>? azureFilePaths = null;
        if (RestorableFiles.Count > 0)
        {
            azureFilePaths = new HashSet<string>(
                RestorableFiles.Select(f => f.Model.LocalPath),
                StringComparer.OrdinalIgnoreCase);
        }

        // Generate preview with Azure validation
        var preview = await _orchestrator.PreviewBackupFilesAsync(fileList, azureFilePaths);

        // Determine default storage tier from watched folder (use first file's folder)
        var firstFolder = GetWatchedFolderForFile(fileList[0]);
        preview.DefaultStorageTier = firstFolder?.StorageTier ?? Core.Models.StorageTier.Hot;

        // Show preview dialog
        var confirmed = await ShowPreviewDialogAsync(preview);

        if (!confirmed)
        {
            AddLog("Backup cancelled by user");
            return;
        }

        // Perform backup with selected tier (TODO: Pass tier to orchestrator)
        AddLog($"Backing up {fileList.Count} file(s) to {preview.EffectiveStorageTier} tier...");
        await BackupSelectedFilesAsync(fileList.ToArray());
    }

    /// <summary>
    /// Gets the WatchedFolder that contains the specified file path.
    /// </summary>
    private WatchedFolder? GetWatchedFolderForFile(string filePath)
    {
        return WatchedFolders
            .Where(f => f.IsEnabled)
            .Select(f => f.ToModel())
            .FirstOrDefault(f => filePath.StartsWith(f.Path, StringComparison.OrdinalIgnoreCase));
    }

    #endregion
}
