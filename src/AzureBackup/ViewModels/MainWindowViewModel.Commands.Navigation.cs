using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AzureBackup.Core;
using AzureBackup.Core.Services;
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

                _cachedAzureFilePaths = null;

                // Notify property changes
                OnPropertyChanged(nameof(RestorableFilesEmpty));
                OnPropertyChanged(nameof(RestorableFilesCount));
                OnPropertyChanged(nameof(ShowAzureEmptyState));
                OnPropertyChanged(nameof(HasSelectedFiles));
                OnPropertyChanged(nameof(SelectedFilesCount));
                OnPropertyChanged(nameof(SelectedFilesText));
                OnPropertyChanged(nameof(NeedsConfiguration));
                OnPropertyChanged(nameof(NeedsUnlock));
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

    #region B50 Catalog Quarantine Recovery

    /// <summary>
    /// B50: starts a quarantine request. Quarantine moves the corrupt
    /// local catalog database file (and its companion -wal / -shm /
    /// -journal / .salt artefacts) to a timestamped
    /// <c>.quarantine-yyyyMMdd-HHmmss</c> suffix beside the original.
    /// The corrupt bytes are NOT deleted -- they remain on disk for
    /// forensic inspection and to confirm there is nothing salvageable.
    /// The next unlock creates a fresh catalog at the same path; the
    /// user must re-enter their Azure connection string and other
    /// catalog-stored settings by hand because the encrypted
    /// connection string is stored inside the quarantined catalog and
    /// is treated as unrecoverable.
    /// </summary>
    [RelayCommand]
    private void RequestQuarantine()
    {
        IsQuarantinePending = true;
        AddLog("WARNING: Catalog quarantine requested. Click 'Confirm Quarantine' to move the corrupt " +
               "catalog aside. The bytes will be PRESERVED on disk under a timestamped suffix; you will " +
               "need to set a new password and re-enter your Azure connection string after this completes.");
    }

    /// <summary>
    /// B50: cancels a pending quarantine request.
    /// </summary>
    [RelayCommand]
    private void CancelQuarantine()
    {
        IsQuarantinePending = false;
        AddLog("Quarantine cancelled.");
    }

    /// <summary>
    /// B50: confirms and executes the catalog quarantine. The corrupt
    /// catalog is moved aside (NOT deleted) and the application drops
    /// every piece of in-memory state that depended on it, so the next
    /// unlock starts from a fresh catalog. The user must then enter a
    /// new password and re-enter their Azure connection details.
    /// </summary>
    [RelayCommand]
    private async Task ConfirmQuarantineAsync()
    {
        if (!IsQuarantinePending)
        {
            AddLog("Please click 'Quarantine Catalog' first.");
            return;
        }

        IsOperationInProgress = true;
        IsQuarantinePending = false;

        try
        {
            var dbPath = AppMode.DatabasePath;
            AddLog($"Quarantining catalog database file at {dbPath}...");

            var result = await _orchestrator.QuarantineCorruptCatalogAsync(dbPath);

            AddLog($"Catalog moved to: {result.QuarantinedDatabasePath}");
            foreach (var moved in result.MovedFiles.Skip(1))
            {
                AddLog($"  Companion moved: {moved}");
            }
            foreach (var skipped in result.SkippedFiles)
            {
                AddLog($"  WARNING -- companion NOT moved: {skipped}");
            }

            // Mirror the post-reset UI cleanup so the Settings view
            // routes the user through first-run setup again.
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                WatchedFolders.Clear();
                RestorableFiles.Clear();

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

                FileTreeRoots.Clear();
                LocalFileTreeRoots.Clear();
                LocalFilesFlatList.Clear();

                _cachedAzureFilePaths = null;

                OnPropertyChanged(nameof(RestorableFilesEmpty));
                OnPropertyChanged(nameof(RestorableFilesCount));
                OnPropertyChanged(nameof(ShowAzureEmptyState));
                OnPropertyChanged(nameof(HasSelectedFiles));
                OnPropertyChanged(nameof(SelectedFilesCount));
                OnPropertyChanged(nameof(SelectedFilesText));
                OnPropertyChanged(nameof(NeedsConfiguration));
                OnPropertyChanged(nameof(NeedsUnlock));
                OnPropertyChanged(nameof(IsNewUser));
                OnPropertyChanged(nameof(PasswordSectionTitle));
                OnPropertyChanged(nameof(InitializeButtonText));
                OnPropertyChanged(nameof(UnlockAndConnectButtonText));
                OnPropertyChanged(nameof(BackupStatusText));
            });

            AddLog("Quarantine complete. Set a new password and re-enter your Azure connection string " +
                   "to start fresh. Your backed-up files in Azure are unaffected; once you reconnect, " +
                   "you can use 'Rebuild from Azure' on the Storage Health tab to repopulate the local " +
                   "catalog from Azure metadata.");

            CurrentView = "Settings";
        }
        catch (Exception ex)
        {
            AddLog($"Quarantine failed: {ex.GetType().Name}: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"Quarantine error: {ex}");
        }
        finally
        {
            IsOperationInProgress = false;
        }
    }

    #endregion

    #region B51 Rebuild From Quarantined Catalog

    /// <summary>
    /// B51: opens the rebuild-from-quarantined-catalog form in the
    /// Settings danger zone. The form collects the quarantined DB and
    /// salt sidecar paths, the original password, and fresh Azure
    /// connection details so the orchestrator can recover the
    /// in-database PasswordSalt and rebuild the local catalog from
    /// Azure metadata.
    /// <para>
    /// B61: the two path fields are pre-populated from the most-recent
    /// quarantine pair found in <see cref="AppMode.DataDirectory"/> so
    /// the user can usually click Confirm without typing or browsing.
    /// The Browse buttons are still wired for the case where the
    /// quarantined files were copied somewhere else.
    /// </para>
    /// </summary>
    [RelayCommand]
    private void RequestRebuildFromQuarantine()
    {
        IsRebuildFromQuarantinePending = true;

        // B61: pre-populate paths from the data directory if a complete
        // quarantine pair (matched by identical timestamp suffix) exists.
        // We only overwrite empty fields so a user re-opening the form
        // after a typo doesn't lose their in-progress edits.
        var (defaultDb, defaultSalt) = QuarantineFileLocator.FindMostRecentQuarantinePair(AppMode.DataDirectory);
        if (string.IsNullOrWhiteSpace(RebuildQuarantinedDbPath) && defaultDb is not null)
        {
            RebuildQuarantinedDbPath = defaultDb;
        }
        if (string.IsNullOrWhiteSpace(RebuildQuarantinedSaltPath) && defaultSalt is not null)
        {
            RebuildQuarantinedSaltPath = defaultSalt;
        }

        if (defaultDb is not null && defaultSalt is not null)
        {
            AddLog($"Rebuild from quarantined catalog requested. Pre-filled the most recent quarantine pair from {AppMode.DataDirectory}; " +
                   "verify the paths, enter the original password, and supply the Azure connection string + container.");
        }
        else
        {
            AddLog("Rebuild from quarantined catalog requested. Provide the quarantined database file, " +
                   "the matching .salt sidecar, the original password used with the quarantined catalog, " +
                   "and the Azure connection string + container that contain the backed-up data.");
        }
    }

    /// <summary>
    /// B61: command bound to the Browse button next to the quarantined
    /// database path field. Asks the View to open an OS file picker;
    /// the View writes the result back via
    /// <see cref="SetRebuildQuarantinedDbPath"/>.
    /// </summary>
    [RelayCommand]
    private void BrowseRebuildQuarantinedDb()
    {
        RebuildQuarantinedDbPickerRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// B61: command bound to the Browse button next to the quarantined
    /// salt sidecar field.
    /// </summary>
    [RelayCommand]
    private void BrowseRebuildQuarantinedSalt()
    {
        RebuildQuarantinedSaltPickerRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// B61: View callback that writes the file-picker result into the
    /// quarantined-database path field.
    /// </summary>
    public void SetRebuildQuarantinedDbPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        RebuildQuarantinedDbPath = path;
    }

    /// <summary>
    /// B61: View callback that writes the file-picker result into the
    /// quarantined-salt path field.
    /// </summary>
    public void SetRebuildQuarantinedSaltPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        RebuildQuarantinedSaltPath = path;
    }

    /// <summary>
    /// B61: directory the file picker should default to. Returns the
    /// directory of whatever the user has already typed in the matching
    /// path field (so re-Browse stays in the same folder), falling back
    /// to <see cref="AppMode.DataDirectory"/> otherwise.
    /// </summary>
    public string GetRebuildPickerStartDirectory(bool forSalt)
    {
        var existing = forSalt ? RebuildQuarantinedSaltPath : RebuildQuarantinedDbPath;
        if (!string.IsNullOrWhiteSpace(existing))
        {
            try
            {
                var dir = Path.GetDirectoryName(existing);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    return dir;
                }
            }
            catch
            {
                // GetDirectoryName can throw on weird inputs; fall through.
            }
        }
        return AppMode.DataDirectory;
    }

    /// <summary>
    /// B51: cancels a pending rebuild-from-quarantined-catalog request
    /// and clears the secret form fields.
    /// </summary>
    [RelayCommand]
    private void CancelRebuildFromQuarantine()
    {
        IsRebuildFromQuarantinePending = false;
        RebuildQuarantinedPassword = string.Empty;
        RebuildConnectionString = string.Empty;
        AddLog("Rebuild from quarantined catalog cancelled.");
    }

    /// <summary>
    /// B51: executes the rebuild. Wraps
    /// <see cref="BackupOrchestrator.RebuildFromQuarantinedCatalogAsync"/>
    /// against the active catalog path
    /// (<see cref="AppMode.DatabasePath"/>). Recovered failures
    /// (wrong password, missing sidecar, mismatched salt size) are
    /// surfaced as user-readable log lines; the active catalog file
    /// is NOT created on a failed attempt.
    /// </summary>
    [RelayCommand]
    private async Task ConfirmRebuildFromQuarantineAsync()
    {
        if (!IsRebuildFromQuarantinePending)
        {
            AddLog("Please click 'Rebuild From Quarantined Catalog...' first.");
            return;
        }

        if (string.IsNullOrWhiteSpace(RebuildQuarantinedDbPath) ||
            string.IsNullOrWhiteSpace(RebuildQuarantinedSaltPath) ||
            string.IsNullOrWhiteSpace(RebuildQuarantinedPassword) ||
            string.IsNullOrWhiteSpace(RebuildConnectionString) ||
            string.IsNullOrWhiteSpace(RebuildContainerName))
        {
            AddLog("All five fields are required: quarantined DB path, salt sidecar path, " +
                   "password, Azure connection string, and container name.");
            return;
        }

        IsOperationInProgress = true;
        try
        {
            var dbPath = AppMode.DatabasePath;
            AddLog($"Rebuilding fresh catalog at {dbPath} from quarantined source...");
            AddLog($"  Quarantined DB:   {RebuildQuarantinedDbPath}");
            AddLog($"  Quarantined salt: {RebuildQuarantinedSaltPath}");

            await _orchestrator.RebuildFromQuarantinedCatalogAsync(
                RebuildQuarantinedDbPath,
                RebuildQuarantinedSaltPath,
                RebuildQuarantinedPassword.AsMemory(),
                RebuildConnectionString,
                RebuildContainerName,
                dbPath);

            AddLog("Rebuild complete. The fresh catalog can be unlocked with the same password " +
                   "you used for the quarantined catalog. Restart the app or unlock from the " +
                   "Settings view to continue.");

            IsRebuildFromQuarantinePending = false;
            RebuildQuarantinedPassword = string.Empty;
            RebuildConnectionString = string.Empty;
        }
        catch (InvalidPasswordException)
        {
            AddLog("Rebuild failed: the password does not match the quarantined catalog. " +
                   "The active catalog file at the rebuild target was NOT created.");
        }
        catch (FileNotFoundException ex)
        {
            AddLog($"Rebuild failed: file not found -- {ex.FileName ?? ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            AddLog($"Rebuild failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            AddLog($"Rebuild failed: {ex.GetType().Name}: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"Rebuild error: {ex}");
        }
        finally
        {
            IsOperationInProgress = false;
        }
    }

    #endregion

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
                // populate the bound collection from the ordered sequence.
                var ordered = files.OrderByDescending(f => f.LastModified).ToList();

                // Bulk-rebuild the bound collection via ReplaceAll so the
                // bound view receives a single Reset event instead of N Add
                // events. Pre-fix a 50K-file refresh pumped 100K Add+layout
                // round-trips through Avalonia's ItemsControl.
                RestorableFiles.ReplaceAll(ordered.Select(f => new BackedUpFileViewModel(f)));
                OnPropertyChanged(nameof(RestorableFilesEmpty));
                OnPropertyChanged(nameof(RestorableFilesCount));
                OnPropertyChanged(nameof(ShowAzureEmptyState));
                OnPropertyChanged(nameof(FilteredRestorableFiles)); // Update flat list view

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
