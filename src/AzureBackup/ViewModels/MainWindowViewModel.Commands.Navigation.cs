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
        
        // Auto-refresh when switching to Storage Health view.
        // The summary computation is async (offloads the chunk-index scan
        // to a worker thread), so dispatch via the relay command which
        // returns immediately. Exceptions surface inside the command's
        // own try/catch.
        if (view == "StorageHealth" && IsInitialized && previousView != "StorageHealth")
        {
            StorageHealthViewModel?.RefreshSummaryCommand.Execute(null);
        }

        // Auto-load when switching to Migration view
        if (view == "Migration" && IsInitialized && previousView != "Migration")
        {
            if (TierMigrationViewModel is { HotFiles.Count: 0, CoolFiles.Count: 0, ColdFiles.Count: 0, ArchiveFiles.Count: 0 })
            {
                TierMigrationViewModel.LoadFilesCommand.Execute(null);
            }
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
    private async Task RefreshFromAzureUiAsync()
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
    /// Bundles every diagnostic artefact in the data directory into a single
    /// ZIP and writes it next to the data dir for easy hand-off when filing
    /// a bug report. Pre-X4 the tester had to navigate to a hidden
    /// <c>%LOCALAPPDATA%\AzureBackup</c> folder, pick the right daily log,
    /// then dive into <c>diagnostics\</c> and <c>metrics\</c> subdirectories
    /// separately. The bundle excludes the encrypted database, salt files,
    /// and migration .bak artefacts so it is safe to attach to a public
    /// issue tracker.
    /// </summary>
    [RelayCommand]
    private void ExportDiagnosticBundle()
    {
        try
        {
            // Place the bundle alongside the data dir (one level up) so it's
            // not part of the next bundle if the tester captures twice.
            var dataDir = AppMode.DataDirectory;
            var parentDir = System.IO.Path.GetDirectoryName(dataDir.TrimEnd(
                System.IO.Path.DirectorySeparatorChar,
                System.IO.Path.AltDirectorySeparatorChar)) ?? dataDir;

            var bundlePath = AzureBackup.Core.DiagnosticBundleExporter.Export(
                dataDir, parentDir, Program.Logger?.SessionId);

            AddLog($"Diagnostic bundle exported: {bundlePath}");
            StatusMessage = $"Bundle saved to {bundlePath}";
        }
        catch (Exception ex)
        {
            AddLog($"Export failed: {ex.GetType().Name}: {ex.Message}");
            StatusMessage = $"Export failed: {ex.Message}";
        }
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
            
            // Perform the secure reset (this closes and deletes the database)
            await _orchestrator.ResetApplicationAsync();

            // Clear UI state
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                // Clear all collections
                WatchedFolders.Clear();
                BackedUpFiles.Clear();
                RestorableFiles.Clear();
                LogMessages.Clear();
                
                // Reset properties
                IsInitialized = false;
                IsBackupRunning = false;
                HasExistingConfig = false;
                IsEntraIdAuthenticated = false;
                EntraIdStatus = "Not signed in";
                StorageAccountName = string.Empty;
                ConnectionString = string.Empty;
                ContainerName = "backup";
                UseEntraIdAuth = false;
                Password = string.Empty;
                PasswordConfirm = string.Empty;
                TotalFiles = 0;
                TotalSize = "0 B";
                PendingChanges = 0;

                // Clear tree views
                FileTreeRoots.Clear();
                LocalFileTreeRoots.Clear();
                LocalFilesFlatList.Clear();

                // Drop the cached Azure file-path snapshot so a subsequent
                // reconnect to a different storage account does not filter
                // local files against stale data.
                _cachedAzureFilePaths = null;

                // Reset migration flag (fresh database won't need migration)
                _needsMigration = false;
                
                // Notify property changes
                OnPropertyChanged(nameof(RestorableFilesEmpty));
                OnPropertyChanged(nameof(RestorableFilesCount));
                OnPropertyChanged(nameof(ShowAzureEmptyState));
                OnPropertyChanged(nameof(HasSelectedFiles));
                OnPropertyChanged(nameof(SelectedFilesCount));
                OnPropertyChanged(nameof(SelectedFilesText));
                OnPropertyChanged(nameof(NeedsConfiguration));
                OnPropertyChanged(nameof(NeedsUnlock));
                OnPropertyChanged(nameof(NeedsMigration));
                OnPropertyChanged(nameof(IsNewUser));
                OnPropertyChanged(nameof(PasswordSectionTitle));
                OnPropertyChanged(nameof(InitializeButtonText));
                OnPropertyChanged(nameof(UnlockAndConnectButtonText));
                OnPropertyChanged(nameof(BackupStatusText));
            });

            AddLog("? Application reset complete. You can now set up a new password and configure Azure connection.");
            AddLog("Enter a new password and your Azure connection details to get started.");
            
            // Switch to Settings view so user can set up fresh
            CurrentView = "Settings";
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

    // Cached Azure file paths from the last successful refresh.
    // Reused by RefreshLocalFilesAsync to avoid a duplicate ListRestorableFilesAsync call.
    private HashSet<string>? _cachedAzureFilePaths;

    /// <summary>
    /// Refreshes both Backup and Restore file lists from Azure Storage.
    /// Called automatically after successful initialization.
    /// Caches the result so RefreshLocalFilesAsync can reuse it.
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
            
            Progress<(int completed, int total)> progress = new(p =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    StatusMessage = $"Loading file metadata... {p.completed:N0}/{p.total:N0}";
                });
            });
            
            var files = await _restoreService.ListRestorableFilesAsync(progress: progress);
            
            // Cache the file paths for RefreshLocalFilesAsync to reuse
            _cachedAzureFilePaths = files
                .Select(f => f.LocalPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                // Sort once on the worker output rather than twice on the UI thread
                // (was: RestorableFiles.Add(... OrderByDescending ...) then again
                // for BackedUpFiles). Materialise once into a local list, then
                // populate both bound collections from the same ordered sequence.
                var ordered = files.OrderByDescending(f => f.LastModified).ToList();

                // Bulk-rebuild both bound collections via ReplaceAll so each
                // bound view receives a single Reset event instead of N Add
                // events. Pre-fix a 50K-file refresh pumped 100K Add+layout
                // round-trips through Avalonia's ItemsControl.
                RestorableFiles.ReplaceAll(ordered.Select(f => new BackedUpFileViewModel(f)));
                OnPropertyChanged(nameof(RestorableFilesEmpty));
                OnPropertyChanged(nameof(RestorableFilesCount));
                OnPropertyChanged(nameof(ShowAzureEmptyState));
                OnPropertyChanged(nameof(FilteredRestorableFiles)); // Update flat list view

                BackedUpFiles.ReplaceAll(ordered.Select(f => new BackedUpFileViewModel(f)));

                // Update statistics to reflect actual Azure storage
                TotalFiles = files.Count;
                TotalSize = AzureBackup.Core.FormatHelper.FormatBytes(files.Sum(f => f.FileSize));

                // Build Azure file tree for tree view mode
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

        // Use cached Azure file paths from RefreshFromAzureAsync if available.
        // This avoids a duplicate ListRestorableFilesAsync call (which downloads all metadata again).
        var azureFilePaths = _cachedAzureFilePaths;

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
                // B28: the tree-roots collection uses Clear() + per-item
                // Add() instead of BulkObservableCollection.ReplaceAll's
                // single Reset event. Avalonia's TreeView with a
                // HierarchicalDataTemplate drops Reset events that arrive
                // before the control is first laid out, which is exactly
                // the startup-unlock sequence: TryUnlockWithPasswordAsync
                // fills LocalFileTreeRoots while SyncView is still
                // IsVisible="False" (CurrentView only flips to "Sync"
                // AFTER unlock returns), so a single Reset fired during
                // the invisible window is silently lost and the user sees
                // an empty left pane until they click Refresh manually.
                // Add events queued before first layout are picked up
                // correctly when the TreeView later measures, which is
                // why the right-pane Azure tree (BuildFileTree, also
                // Clear+Add) works on startup. The flat list below keeps
                // ReplaceAll because the ListBox does NOT drop Reset
                // events the same way and the per-item-Add cost matters
                // there at scale (the flat list can hold tens of
                // thousands of files; the tree roots cap at the
                // watched-folder count, typically <20).
                LocalFileTreeRoots.Clear();
                foreach (var root in roots)
                {
                    LocalFileTreeRoots.Add(root);
                }

                LocalFilesFlatList.ReplaceAll(flatFiles);

                // Mirror the right-pane invalidation in RefreshFromAzureAsync
                // (line 378). Without this the flat-list view bound to
                // FilteredLocalFiles does not re-evaluate after the
                // underlying LocalFilesFlatList is rebuilt.
                OnPropertyChanged(nameof(FilteredLocalFiles));
                OnPropertyChanged(nameof(LocalFilesSummary));
                AddLog($"Found {LocalFileTreeRoots.Sum(r => r.TotalFileCount)} local files");
            });
        });
    }

    /// <summary>
    /// Refreshes both Azure and local file panes.
    /// Does not manage <see cref="IsOperationInProgress"/> — callers are responsible.
    /// Use after operations that change files on both sides (backup, restore, sync).
    /// Refreshes Azure first (caches file paths), then local (uses cached paths).
    /// </summary>
    private async Task RefreshBothFilePanesAsync()
    {
        if (_blobService.IsConnected)
        {
            await RefreshFromAzureAsync();
        }
        await RefreshLocalFilesAsync();
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
            await RefreshBothFilePanesAsync();
        }
        finally
        {
            IsOperationInProgress = false;
        }
    }

    #endregion
}
