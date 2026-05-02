using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AzureBackup.Core;
using AzureBackup.Core.Models;
using AzureBackup.Core.Services;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzureBackup.ViewModels;

/// <summary>
/// ViewModel for the Storage Health tab, managing chunk index and orphan detection.
/// </summary>
public partial class StorageHealthViewModel : ViewModelBase
{
    private readonly ChunkIndexService _chunkIndexService;
    private readonly LocalDatabaseService _databaseService;
    private CancellationTokenSource? _operationCts;

    /// <summary>
    /// Disposes any prior <see cref="CancellationTokenSource"/> on the
    /// _operationCts slot and replaces it with a fresh one. Returns the
    /// new token. Each Scan/Cleanup/Rebuild/Backup/Restore command must
    /// call this exactly once at start so we don't leak CTS instances
    /// across consecutive runs.
    /// </summary>
    private CancellationToken BeginOperation()
    {
        var previous = Interlocked.Exchange(ref _operationCts, new CancellationTokenSource());
        previous?.Dispose();
        return _operationCts!.Token;
    }

    /// <summary>
    /// Pair to <see cref="BeginOperation"/>. Disposes the active CTS and
    /// nulls the slot so a stray <see cref="CancelOperation"/> after the
    /// command completes is a no-op rather than a swap-with-stale-cts.
    /// </summary>
    private void EndOperation()
    {
        var current = Interlocked.Exchange(ref _operationCts, null);
        current?.Dispose();
    }

    #region Observable Properties

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunOperations))]
    private bool _isOperationInProgress;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private string _progressText = string.Empty;

    // Index Summary
    [ObservableProperty]
    private int _totalChunks;

    [ObservableProperty]
    private string _totalSize = "0 B";

    [ObservableProperty]
    private int _orphanCount;

    [ObservableProperty]
    private string _orphanSize = "0 B";

    [ObservableProperty]
    private int _sharedChunks;

    [ObservableProperty]
    private string _deduplicationSavings = "0 B";

    [ObservableProperty]
    private string _lastRebuildTime = "Never";

    [ObservableProperty]
    private string _lastAzureSyncTime = "Never";

    // Tier Breakdown
    [ObservableProperty]
    private int _hotTierChunks;

    [ObservableProperty]
    private string _hotTierSize = "0 B";

    [ObservableProperty]
    private int _coolTierChunks;

    [ObservableProperty]
    private string _coolTierSize = "0 B";

    [ObservableProperty]
    private int _coldTierChunks;

    [ObservableProperty]
    private string _coldTierSize = "0 B";

    // Orphan Scan Results
    [ObservableProperty]
    private bool _hasOrphanScanResults;

    [ObservableProperty]
    private string _lastScanTime = "Never";

    [ObservableProperty]
    private string _scanDuration = "";

    public ObservableCollection<OrphanChunkViewModel> OrphanedChunks { get; } = [];

    [ObservableProperty]
    private bool _selectAllOrphans;

    #endregion

    #region Computed Properties

    public bool CanRunOperations => !IsOperationInProgress;
    
    public bool HasOrphans => OrphanCount > 0;

    public int SelectedOrphanCount => OrphanedChunks.Count(o => o.IsSelected);

    public string SelectedOrphanSize => FormatHelper.FormatBytes(OrphanedChunks.Where(o => o.IsSelected).Sum(o => o.SizeBytes));

    #endregion

    public StorageHealthViewModel(ChunkIndexService chunkIndexService, LocalDatabaseService databaseService)
    {
        _chunkIndexService = chunkIndexService ?? throw new ArgumentNullException(nameof(chunkIndexService));
        _databaseService = databaseService ?? throw new ArgumentNullException(nameof(databaseService));
    }

    /// <summary>
    /// Refreshes the index summary statistics. Runs the
    /// <see cref="ChunkIndexService.GetIndexSummary"/> aggregation on a
    /// worker thread so the UI thread stays responsive even at
    /// 500K+ chunks (the SQLite scan + sum loop dominates).
    /// </summary>
    [RelayCommand]
    private async Task RefreshSummaryAsync()
    {
        StatusMessage = "Refreshing summary...";
        try
        {
            var summary = await Task.Run(() => _chunkIndexService.GetIndexSummary());

            TotalChunks = summary.TotalChunks;
            TotalSize = FormatHelper.FormatBytes(summary.TotalSizeBytes);
            OrphanCount = summary.OrphanCount;
            OrphanSize = FormatHelper.FormatBytes(summary.OrphanSizeBytes);
            SharedChunks = summary.SharedChunks;
            DeduplicationSavings = FormatHelper.FormatBytes(summary.DeduplicationSavingsBytes);

            LastRebuildTime = summary.LastFullRebuildAt?.ToString("g") ?? "Never";
            LastAzureSyncTime = summary.LastAzureSyncAt?.ToString("g") ?? "Never";

            // Tier breakdown
            if (summary.TierBreakdown.TryGetValue(StorageTier.Hot, out var hot))
            {
                HotTierChunks = hot.ChunkCount;
                HotTierSize = FormatHelper.FormatBytes(hot.TotalSizeBytes);
            }

            if (summary.TierBreakdown.TryGetValue(StorageTier.Cool, out var cool))
            {
                CoolTierChunks = cool.ChunkCount;
                CoolTierSize = FormatHelper.FormatBytes(cool.TotalSizeBytes);
            }

            if (summary.TierBreakdown.TryGetValue(StorageTier.Cold, out var cold))
            {
                ColdTierChunks = cold.ChunkCount;
                ColdTierSize = FormatHelper.FormatBytes(cold.TotalSizeBytes);
            }

            OnPropertyChanged(nameof(HasOrphans));
            StatusMessage = $"Summary refreshed at {DateTime.Now:T}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Refresh failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Scans for orphaned chunks in Azure.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunOperations))]
    private async Task ScanForOrphansAsync()
    {
        IsOperationInProgress = true;
        var ct = BeginOperation();
        StatusMessage = "Scanning for orphaned chunks...";

        // Clear collection on UI thread
        await Dispatcher.UIThread.InvokeAsync(() => OrphanedChunks.Clear());

        try
        {
            var progress = new Progress<(int scanned, int total, string currentChunk)>(p =>
            {
                ProgressValue = p.total > 0 ? (double)p.scanned / p.total * 100 : 0;
                ProgressText = $"Scanning: {p.scanned}/{p.total}";
            });

            var result = await _chunkIndexService.ScanForOrphansAsync(progress, ct);

            // Update UI with results on UI thread
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var orphan in result.OrphanedChunks)
                {
                    OrphanedChunks.Add(new OrphanChunkViewModel(orphan));
                }

                HasOrphanScanResults = true;
                LastScanTime = result.ScannedAt.ToString("g");
                ScanDuration = $"{result.ScanDuration.TotalSeconds:F1}s";
                OrphanCount = result.OrphanedChunks.Count;
                OrphanSize = FormatHelper.FormatBytes(result.TotalOrphanSizeBytes);

                StatusMessage = $"Scan complete: {result.OrphanedChunks.Count} orphans found ({FormatHelper.FormatBytes(result.TotalOrphanSizeBytes)})";
                OnPropertyChanged(nameof(HasOrphans));
            });
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Scan cancelled";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Scan failed: {ex.Message}";
        }
        finally
        {
            EndOperation();
            IsOperationInProgress = false;
            ProgressValue = 0;
            ProgressText = string.Empty;
        }
    }

    /// <summary>
    /// Cleans up selected orphaned chunks.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunOperations))]
    private async Task CleanupOrphansAsync()
    {
        var selectedOrphans = OrphanedChunks.Where(o => o.IsSelected).Select(o => o.Entry).ToList();
        
        if (selectedOrphans.Count == 0)
        {
            StatusMessage = "No orphans selected for cleanup";
            return;
        }

        IsOperationInProgress = true;
        var ct = BeginOperation();
        StatusMessage = $"Deleting {selectedOrphans.Count} orphaned chunks...";

        try
        {
            var progress = new Progress<(int deleted, int total, string currentChunk)>(p =>
            {
                ProgressValue = p.total > 0 ? (double)p.deleted / p.total * 100 : 0;
                ProgressText = $"Deleting: {p.deleted}/{p.total}";
            });

            var result = await _chunkIndexService.CleanupOrphansAsync(selectedOrphans, progress, ct);

            // Remove deleted orphans from the list on UI thread
            var deletedHashes = selectedOrphans
                .Where(o => !result.Errors.Any(e => e.Contains(o.ChunkHash)))
                .Select(o => o.ChunkHash)
                .ToHashSet();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var toRemove = OrphanedChunks.Where(o => deletedHashes.Contains(o.Entry.ChunkHash)).ToList();
                foreach (var item in toRemove)
                {
                    OrphanedChunks.Remove(item);
                }
            });

            await RefreshSummaryAsync();

            StatusMessage = $"Cleanup complete: {result.ChunksDeleted} deleted, {FormatHelper.FormatBytes(result.BytesFreed)} freed";
            
            if (result.FailedDeletions > 0)
            {
                StatusMessage += $", {result.FailedDeletions} failed";
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Cleanup cancelled";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Cleanup failed: {ex.Message}";
        }
        finally
        {
            EndOperation();
            IsOperationInProgress = false;
            ProgressValue = 0;
            ProgressText = string.Empty;
        }
    }

    /// <summary>
    /// Raised when the user clicks "Check Data Integrity" -- the host
    /// (<see cref="MainWindowViewModel"/>) responds by ensuring the
    /// Data Integrity tab exists and switching focus to it. This view-
    /// model deliberately does NOT hold a reference to MainWindowViewModel
    /// to keep the dependency direction one-way (host owns this VM).
    /// </summary>
    public event System.EventHandler? OpenDataIntegrityRequested;

    /// <summary>
    /// Switches focus to (or creates on first click) the Data Integrity
    /// tab. The tab persists indefinitely once shown so the tester can
    /// return to historical results without re-running the check.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunOperations))]
    private void CheckDataIntegrity()
    {
        OpenDataIntegrityRequested?.Invoke(this, System.EventArgs.Empty);
    }

    /// <summary>
    /// Rebuilds the chunk index from Azure metadata.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunOperations))]
    private async Task RebuildIndexAsync()
    {
        IsOperationInProgress = true;
        var ct = BeginOperation();
        StatusMessage = "Rebuilding chunk index from Azure...";

        try
        {
            var progress = new Progress<(int processed, int total, string currentFile)>(p =>
            {
                ProgressValue = p.total > 0 ? (double)p.processed / p.total * 100 : 0;
                ProgressText = $"Processing: {p.processed}/{p.total} - {p.currentFile}";
            });

            await _chunkIndexService.RebuildIndexFromAzureAsync(progress, ct);

            await RefreshSummaryAsync();
            StatusMessage = "Index rebuild complete";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Rebuild cancelled";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Rebuild failed: {ex.Message}";
        }
        finally
        {
            EndOperation();
            IsOperationInProgress = false;
            ProgressValue = 0;
            ProgressText = string.Empty;
        }
    }

    /// <summary>
    /// Backs up the chunk index to Azure.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunOperations))]
    private async Task BackupIndexAsync()
    {
        IsOperationInProgress = true;
        StatusMessage = "Backing up index to Azure...";

        try
        {
            await _chunkIndexService.BackupIndexToAzureAsync();
            await RefreshSummaryAsync();
            StatusMessage = "Index backup complete";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Backup failed: {ex.Message}";
        }
        finally
        {
            IsOperationInProgress = false;
        }
    }

    /// <summary>
    /// Restores the chunk index from Azure backup.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunOperations))]
    private async Task RestoreIndexAsync()
    {
        IsOperationInProgress = true;
        StatusMessage = "Restoring index from Azure...";

        try
        {
            var success = await _chunkIndexService.RestoreIndexFromAzureAsync();
            
            if (success)
            {
                await RefreshSummaryAsync();
                StatusMessage = "Index restored successfully";
            }
            else
            {
                StatusMessage = "No index backup found in Azure";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Restore failed: {ex.Message}";
        }
        finally
        {
            IsOperationInProgress = false;
        }
    }

    /// <summary>
    /// Cancels the current operation.
    /// </summary>
    [RelayCommand]
    private void CancelOperation()
    {
        _operationCts?.Cancel();
        StatusMessage = "Cancelling...";
    }

    /// <summary>
    /// Selects or deselects all orphans.
    /// </summary>
    partial void OnSelectAllOrphansChanged(bool value)
    {
        foreach (var orphan in OrphanedChunks)
        {
            orphan.IsSelected = value;
        }
        OnPropertyChanged(nameof(SelectedOrphanCount));
        OnPropertyChanged(nameof(SelectedOrphanSize));
    }

    #region B44: Database file integrity diagnostic

    /// <summary>
    /// Multi-line report from the most recent
    /// <see cref="VerifyDatabaseFileAsync"/> run, or empty when no run has
    /// been performed in this session. Surfaced in the Storage Health
    /// view so the user can copy the cipher- and SQLite-pragma output
    /// into a support request.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDatabaseFileReport))]
    private string _databaseFileReport = string.Empty;

    public bool HasDatabaseFileReport => !string.IsNullOrEmpty(DatabaseFileReport);

    /// <summary>
    /// B44: runs SQLCipher's <c>cipher_integrity_check</c> and SQLite's
    /// <c>integrity_check</c> against the local catalog database file
    /// and reports the result. Use this when an operation against the
    /// catalog (e.g. starting a Data Integrity check) fails with
    /// SQLite error 11 ("database disk image is malformed") to confirm
    /// whether the catalog file is corrupted.
    ///
    /// <para>
    /// This DOES NOT verify backed-up files in Azure -- that is what
    /// the Data Integrity tab does. The two checks are independent:
    /// the catalog can be healthy while individual backups fail
    /// verification, or vice versa.
    /// </para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunOperations))]
    private async Task VerifyDatabaseFileAsync()
    {
        IsOperationInProgress = true;
        StatusMessage = "Verifying local catalog database file...";
        DatabaseFileReport = string.Empty;

        try
        {
            // Both pragmas walk every page; run on a worker so the UI
            // stays responsive on a large catalog. The pragma execution
            // already serialises against writers via the backend's
            // write lock.
            var result = await Task.Run(() => _databaseService.CheckDatabaseFileIntegrity());

            DatabaseFileReport = FormatDatabaseFileReport(result);
            StatusMessage = result.IsHealthy
                ? "Catalog database file is healthy (cipher_integrity_check and integrity_check both reported ok)."
                : "Catalog database file reported problems -- see report below.";
        }
        catch (NotSupportedException ex)
        {
            DatabaseFileReport = ex.Message;
            StatusMessage = "Verification not supported on this backend.";
        }
        catch (Exception ex)
        {
            DatabaseFileReport =
                $"Verification failed: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}" +
                $"{Environment.NewLine}" +
                $"This usually means the catalog file is so badly corrupted that even the " +
                $"SQLCipher / SQLite integrity pragmas cannot read it. Restore the catalog " +
                $"from a recent backup, or rebuild it from Azure metadata via the " +
                $"\"Rebuild Index\" action above.";
            StatusMessage = $"Verification failed: {ex.Message}";
        }
        finally
        {
            IsOperationInProgress = false;
        }
    }

    private static string FormatDatabaseFileReport(
        AzureBackup.Core.Services.Backends.DatabaseFileIntegrityResult result)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("--- PRAGMA cipher_integrity_check (SQLCipher per-page HMAC) ---");
        if (result.CipherIntegrityMessages.Count == 0)
        {
            // SQLCipher returns no rows on success - that is the healthy
            // shape, not "no rows ran". Spell it out for the user.
            sb.AppendLine("ok (no failing pages)");
        }
        else
        {
            foreach (var msg in result.CipherIntegrityMessages)
            {
                sb.AppendLine(msg);
            }
        }
        sb.AppendLine();
        sb.AppendLine("--- PRAGMA integrity_check (SQLite b-tree structure) ---");
        if (result.SqliteIntegrityMessages.Count == 0)
        {
            sb.AppendLine("(no rows returned)");
        }
        else
        {
            foreach (var msg in result.SqliteIntegrityMessages)
            {
                sb.AppendLine(msg);
            }
        }
        return sb.ToString();
    }

    #endregion

    }

    /// <summary>
/// ViewModel for an orphaned chunk in the list.
/// </summary>
public partial class OrphanChunkViewModel : ObservableObject
{
    public ChunkIndexEntry Entry { get; }

    [ObservableProperty]
    private bool _isSelected;

    public string ChunkHash => Entry.ChunkHash;
    public string ShortHash => Entry.ChunkHash.Length > 12 ? Entry.ChunkHash[..12] + "..." : Entry.ChunkHash;
    public long SizeBytes => Entry.SizeBytes;
    public string Size => FormatHelper.FormatBytes(Entry.SizeBytes);
    public string Tier => Entry.CurrentTier.ToString();
    public string FirstUploaded => Entry.FirstUploadedAt.ToString("g");
    public string OriginalFile => string.IsNullOrEmpty(Entry.OriginalUploaderPath) 
        ? "Unknown" 
        : System.IO.Path.GetFileName(Entry.OriginalUploaderPath);

    public OrphanChunkViewModel(ChunkIndexEntry entry)
    {
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
    }
}
