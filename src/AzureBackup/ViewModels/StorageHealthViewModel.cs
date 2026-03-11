using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
    private string _orphanCost = "$0.00";

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
    private string _hotTierCost = "$0.00";

    [ObservableProperty]
    private int _coolTierChunks;

    [ObservableProperty]
    private string _coolTierSize = "0 B";

    [ObservableProperty]
    private string _coolTierCost = "$0.00";

    [ObservableProperty]
    private int _coldTierChunks;

    [ObservableProperty]
    private string _coldTierSize = "0 B";

    [ObservableProperty]
    private string _coldTierCost = "$0.00";

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

    public string SelectedOrphanSize => FormatBytes(OrphanedChunks.Where(o => o.IsSelected).Sum(o => o.SizeBytes));

    #endregion

    public StorageHealthViewModel(ChunkIndexService chunkIndexService, LocalDatabaseService databaseService)
    {
        _chunkIndexService = chunkIndexService ?? throw new ArgumentNullException(nameof(chunkIndexService));
        _databaseService = databaseService ?? throw new ArgumentNullException(nameof(databaseService));
    }

    /// <summary>
    /// Refreshes the index summary statistics.
    /// </summary>
    [RelayCommand]
    private void RefreshSummary()
    {
        var summary = _chunkIndexService.GetIndexSummary();

        TotalChunks = summary.TotalChunks;
        TotalSize = FormatBytes(summary.TotalSizeBytes);
        OrphanCount = summary.OrphanCount;
        OrphanSize = FormatBytes(summary.OrphanSizeBytes);
        SharedChunks = summary.SharedChunks;
        DeduplicationSavings = FormatBytes(summary.DeduplicationSavingsBytes);

        LastRebuildTime = summary.LastFullRebuildAt?.ToString("g") ?? "Never";
        LastAzureSyncTime = summary.LastAzureSyncAt?.ToString("g") ?? "Never";

        // Tier breakdown
        if (summary.TierBreakdown.TryGetValue(StorageTier.Hot, out var hot))
        {
            HotTierChunks = hot.ChunkCount;
            HotTierSize = FormatBytes(hot.TotalSizeBytes);
            HotTierCost = $"${hot.EstimatedMonthlyCost:F4}/mo";
        }

        if (summary.TierBreakdown.TryGetValue(StorageTier.Cool, out var cool))
        {
            CoolTierChunks = cool.ChunkCount;
            CoolTierSize = FormatBytes(cool.TotalSizeBytes);
            CoolTierCost = $"${cool.EstimatedMonthlyCost:F4}/mo";
        }

        if (summary.TierBreakdown.TryGetValue(StorageTier.Cold, out var cold))
        {
            ColdTierChunks = cold.ChunkCount;
            ColdTierSize = FormatBytes(cold.TotalSizeBytes);
            ColdTierCost = $"${cold.EstimatedMonthlyCost:F4}/mo";
        }

        // Calculate total orphan cost
        var orphanEntries = _databaseService.GetOrphanedChunks();
        decimal totalOrphanCost = 0;
        foreach (var entry in orphanEntries)
        {
            var gbSize = entry.SizeBytes / (1024m * 1024m * 1024m);
            totalOrphanCost += gbSize * GetTierPricing(entry.CurrentTier);
        }
        OrphanCost = $"${totalOrphanCost:F4}/mo";

        OnPropertyChanged(nameof(HasOrphans));
        StatusMessage = $"Summary refreshed at {DateTime.Now:T}";
    }

    /// <summary>
    /// Scans for orphaned chunks in Azure.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunOperations))]
    private async Task ScanForOrphansAsync()
    {
        IsOperationInProgress = true;
        _operationCts = new CancellationTokenSource();
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

            var result = await _chunkIndexService.ScanForOrphansAsync(progress, _operationCts.Token);

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
                OrphanSize = FormatBytes(result.TotalOrphanSizeBytes);
                OrphanCost = $"${result.EstimatedMonthlyCost:F4}/mo";

                StatusMessage = $"Scan complete: {result.OrphanedChunks.Count} orphans found ({FormatBytes(result.TotalOrphanSizeBytes)})";
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
        _operationCts = new CancellationTokenSource();
        StatusMessage = $"Deleting {selectedOrphans.Count} orphaned chunks...";

        try
        {
            var progress = new Progress<(int deleted, int total, string currentChunk)>(p =>
            {
                ProgressValue = p.total > 0 ? (double)p.deleted / p.total * 100 : 0;
                ProgressText = $"Deleting: {p.deleted}/{p.total}";
            });

            var result = await _chunkIndexService.CleanupOrphansAsync(selectedOrphans, progress, _operationCts.Token);

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

            RefreshSummary();

            StatusMessage = $"Cleanup complete: {result.ChunksDeleted} deleted, {FormatBytes(result.BytesFreed)} freed";
            
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
            IsOperationInProgress = false;
            ProgressValue = 0;
            ProgressText = string.Empty;
        }
    }

    /// <summary>
    /// Rebuilds the chunk index from Azure metadata.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunOperations))]
    private async Task RebuildIndexAsync()
    {
        IsOperationInProgress = true;
        _operationCts = new CancellationTokenSource();
        StatusMessage = "Rebuilding chunk index from Azure...";

        try
        {
            var progress = new Progress<(int processed, int total, string currentFile)>(p =>
            {
                ProgressValue = p.total > 0 ? (double)p.processed / p.total * 100 : 0;
                ProgressText = $"Processing: {p.processed}/{p.total} - {p.currentFile}";
            });

            await _chunkIndexService.RebuildIndexFromAzureAsync(progress, _operationCts.Token);

            RefreshSummary();
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
            RefreshSummary();
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
                RefreshSummary();
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

    #region Helpers

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

    private static decimal GetTierPricing(StorageTier tier) => tier switch
    {
        StorageTier.Hot => 0.018m,
        StorageTier.Cool => 0.01m,
        StorageTier.Cold => 0.004m,
        _ => 0.01m
    };

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
    public string Size => FormatBytes(Entry.SizeBytes);
    public string Tier => Entry.CurrentTier.ToString();
    public string FirstUploaded => Entry.FirstUploadedAt.ToString("g");
    public string OriginalFile => string.IsNullOrEmpty(Entry.OriginalUploaderPath) 
        ? "Unknown" 
        : System.IO.Path.GetFileName(Entry.OriginalUploaderPath);

    public OrphanChunkViewModel(ChunkIndexEntry entry)
    {
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
    }

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
}
