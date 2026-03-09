using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;

namespace AzureBackup.ViewModels;

/// <summary>
/// Navigation and UI commands for MainWindowViewModel.
/// </summary>
public partial class MainWindowViewModel
{
    // Tracks whether Sync view needs refresh (set by file system changes)
    private bool _syncViewNeedsRefresh;
    
    // Debounce timer for file system change events
    private System.Threading.Timer? _fileChangeDebounceTimer;
    private readonly object _fileChangeLock = new();

    #region Navigation and UI Commands

    [RelayCommand]
    private void CancelOperation()
    {
        _operationCts?.Cancel();
    }

    [RelayCommand]
    private async Task NavigateToAsync(string view)
    {
        var previousView = CurrentView;
        CurrentView = view;
        
        // Auto-refresh when switching to Sync view
        if (view == "Sync" && IsInitialized && previousView != "Sync")
        {
            // Ensure file watching is active for auto-refresh
            EnsureFileWatchingForUi();
            
            await RefreshSyncViewAsync(refreshAzure: false);
        }
    }

    /// <summary>
    /// Called when the main window gains focus. Refreshes Sync view if needed.
    /// </summary>
    public async Task OnWindowActivatedAsync()
    {
        if (CurrentView == "Sync" && IsInitialized && _syncViewNeedsRefresh)
        {
            _syncViewNeedsRefresh = false;
            await RefreshSyncViewAsync(refreshAzure: false);
        }
    }

    /// <summary>
    /// Called when file system changes are detected outside the application.
    /// Marks the Sync view for refresh.
    /// </summary>
    public void OnFileSystemChanged()
    {
        lock (_fileChangeLock)
        {
            // Debounce rapid file system changes
            _fileChangeDebounceTimer?.Dispose();
            _fileChangeDebounceTimer = new System.Threading.Timer(
                _ => ProcessFileSystemChange(),
                null,
                TimeSpan.FromSeconds(1),
                System.Threading.Timeout.InfiniteTimeSpan);
        }
    }

    private void ProcessFileSystemChange()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            if (CurrentView == "Sync" && IsInitialized)
            {
                // Refresh immediately if on Sync tab
                await RefreshSyncViewAsync(refreshAzure: false);
            }
            else
            {
                // Mark for refresh when returning to Sync tab
                _syncViewNeedsRefresh = true;
            }
        });
    }

    /// <summary>
    /// Refreshes the Sync view panels.
    /// </summary>
    /// <param name="refreshAzure">Whether to also refresh from Azure (slower).</param>
    private async Task RefreshSyncViewAsync(bool refreshAzure)
    {
        if (!IsInitialized || IsOperationInProgress)
            return;

        try
        {
            // Always refresh local files
            await RefreshLocalFilesAsync();

            // Optionally refresh Azure files
            if (refreshAzure && _blobService.IsConnected)
            {
                await RefreshFromAzureAsync();
            }
            
            // Notify property changes for filtered views
            OnPropertyChanged(nameof(FilteredLocalFiles));
            OnPropertyChanged(nameof(FilteredRestorableFiles));
        }
        catch (Exception ex)
        {
            AddLog($"Auto-refresh failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task RefreshFromAzureCommandAsync()
    {
        if (!IsInitialized)
        {
            AddLog("Please unlock first");
            return;
        }

        IsOperationInProgress = true;
        try
        {
            await RefreshFromAzureAsync();
        }
        finally
        {
            IsOperationInProgress = false;
        }
    }

    [RelayCommand]
    private void ClearLogs()
    {
        LogMessages.Clear();
        AddLog("Logs cleared");
    }

    /// <summary>
    /// Initiates the reset process - requires confirmation.
    /// </summary>
    [RelayCommand]
    private void RequestReset()
    {
        IsResetPending = true;
        AddLog("?? WARNING: Reset requested. Click 'Confirm Reset' to permanently delete all settings and data.");
    }

    /// <summary>
    /// Cancels a pending reset request.
    /// </summary>
    [RelayCommand]
    private void CancelReset()
    {
        IsResetPending = false;
        AddLog("Reset cancelled.");
    }

    /// <summary>
    /// Confirms and executes the application reset.
    /// This will securely delete all settings, credentials, and local data.
    /// </summary>
    [RelayCommand]
    private async Task ConfirmResetAsync()
    {
        if (!IsResetPending)
        {
            AddLog("Please click 'Reset Application' first.");
            return;
        }

        IsOperationInProgress = true;
        IsResetPending = false;

        try
        {
            AddLog("Securely deleting all application data...");
            
            // Perform the secure reset
            await _orchestrator.ResetApplicationAsync();

            // Clear UI state
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                // Clear all collections
                WatchedFolders.Clear();
                BackedUpFiles.Clear();
                RestorableFiles.Clear();
                
                // Reset properties
                IsInitialized = false;
                IsBackupRunning = false;
                HasExistingConfig = false;
                IsEntraIdAuthenticated = false;
                EntraIdStatus = "Not signed in";
                StorageAccountName = string.Empty;
                ConnectionString = string.Empty;
                UseEntraIdAuth = false;
                Password = string.Empty;
                PasswordConfirm = string.Empty;
                TotalFiles = 0;
                TotalSize = "0 B";
                PendingChanges = 0;
                LastBackupTime = "Never";
                EstimatedCost = "$0.00";
                
                // Clear tree view
                FileTreeRoots.Clear();
                LocalFileTreeRoots.Clear();
                
                // Notify property changes
                OnPropertyChanged(nameof(RestorableFilesEmpty));
                OnPropertyChanged(nameof(RestorableFilesCount));
                OnPropertyChanged(nameof(ShowAzureEmptyState));
                OnPropertyChanged(nameof(HasSelectedFiles));
                OnPropertyChanged(nameof(SelectedFilesCount));
                OnPropertyChanged(nameof(SelectedFilesText));
            });

            AddLog("? Application reset complete. You can now set up a new password and configure Azure connection.");
        }
        catch (Exception ex)
        {
            AddLog($"Reset failed: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"Reset error: {ex}");
        }
        finally
        {
            IsOperationInProgress = false;
        }
    }

    /// <summary>
    /// Refreshes both Backup and Restore file lists from Azure Storage.
    /// Called automatically after successful initialization.
    /// </summary>
    private async Task RefreshFromAzureAsync()
    {
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

        try
        {
            AddLog("Loading files from Azure Storage...");
            
            var files = await _restoreService.ListRestorableFilesAsync();
            
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                // Update RestorableFiles for the Restore tab
                RestorableFiles.Clear();
                foreach (var file in files.OrderByDescending(f => f.LastModified))
                {
                    RestorableFiles.Add(new BackedUpFileViewModel(file));
                }
                OnPropertyChanged(nameof(RestorableFilesEmpty));
                OnPropertyChanged(nameof(RestorableFilesCount));
                OnPropertyChanged(nameof(ShowAzureEmptyState));
                
                // Update BackedUpFiles for the Backup tab (same data, different view)
                BackedUpFiles.Clear();
                foreach (var file in files.OrderByDescending(f => f.LastModified))
                {
                    BackedUpFiles.Add(new BackedUpFileViewModel(file));
                }
                
                // Update statistics to reflect actual Azure storage
                TotalFiles = files.Count;
                TotalSize = FormatBytes(files.Sum(f => f.FileSize));
                
                // Build Azure file tree
                if (UseTreeView)
                {
                    BuildFileTree();
                }
                
                AddLog($"Loaded {files.Count} files from Azure Storage");
                OnPropertyChanged(nameof(AzureFilesSummary));
            });
        }
        catch (Exception ex)
        {
            AddLog($"Failed to load files from Azure: {ex.Message}");
        }
    }

    /// <summary>
    /// Refreshes the local file tree by scanning watched folders.
    /// </summary>
    [RelayCommand]
    private async Task RefreshLocalFilesAsync()
    {
        if (!IsInitialized)
        {
            AddLog("Please unlock first");
            return;
        }

        if (!WatchedFolders.Any(f => f.IsEnabled))
        {
            AddLog("No watched folders configured");
            return;
        }

        AddLog("Scanning local files...");

        // Get the set of files actually in Azure for validation
        // This ensures we don't show files as "backed up" if they're not actually in Azure
        HashSet<string>? azureFilePaths = null;
        if (_blobService.IsConnected)
        {
            try
            {
                var azureFiles = await _restoreService.ListRestorableFilesAsync();
                azureFilePaths = azureFiles
                    .Select(f => f.LocalPath)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                // If we can't get Azure files, continue without validation
                // This allows offline viewing of local files
            }
        }

        await Task.Run(() =>
        {
            // Get all backed up files for comparison
            var backedUpFiles = _databaseService.GetAllBackedUpFiles()
                .ToDictionary(f => f.LocalPath, StringComparer.OrdinalIgnoreCase);

            // If we have Azure file list, filter to only files that actually exist in Azure
            // This prevents showing "Backed up" for files that were deleted from Azure
            if (azureFilePaths != null)
            {
                Dictionary<string, AzureBackup.Core.Models.BackedUpFile> validatedBackups = new(StringComparer.OrdinalIgnoreCase);
                foreach (var kvp in backedUpFiles)
                {
                    if (azureFilePaths.Contains(kvp.Key))
                    {
                        validatedBackups[kvp.Key] = kvp.Value;
                    }
                    // Files in local DB but not in Azure will be treated as "New"
                }
                backedUpFiles = validatedBackups;
            }

            // Build tree from watched folders
            var watchedFolderModels = WatchedFolders
                .Where(f => f.IsEnabled)
                .Select(f => f.ToModel())
                .ToList();

            var roots = LocalFileTreeNodeViewModel.BuildTree(watchedFolderModels, backedUpFiles);

            // Extract flat list of all files from tree
            var flatFiles = roots
                .SelectMany(r => r.GetAllFiles())
                .OrderBy(f => f.FullPath)
                .ToList();

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                LocalFileTreeRoots.Clear();
                LocalFilesFlatList.Clear();
                
                foreach (var root in roots)
                {
                    LocalFileTreeRoots.Add(root);
                }
                
                foreach (var file in flatFiles)
                {
                    LocalFilesFlatList.Add(file);
                }
                
                OnPropertyChanged(nameof(LocalFilesSummary));
                AddLog($"Found {LocalFileTreeRoots.Sum(r => r.TotalFileCount)} local files");
            });
        });
    }

    /// <summary>
    /// Refreshes both local and Azure file trees.
    /// </summary>
    [RelayCommand]
    private async Task RefreshBothPanelsAsync()
    {
        if (!IsInitialized)
        {
            AddLog("Please unlock first");
            return;
        }

        IsOperationInProgress = true;
        try
        {
            // Refresh local files first (doesn't require Azure connection)
            await RefreshLocalFilesAsync();

            // Then refresh Azure files if connected
            if (_blobService.IsConnected)
            {
                await RefreshFromAzureAsync();
            }
        }
        finally
        {
            IsOperationInProgress = false;
        }
    }

    /// <summary>
    /// Formats bytes into human-readable size string.
    /// </summary>
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

    #endregion
}
