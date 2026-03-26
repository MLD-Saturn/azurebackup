using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using AzureBackup.Core;
using AzureBackup.Core.Models;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzureBackup.ViewModels;

/// <summary>
/// ViewModel for the Progress tab that appears during backup/restore/mirror operations.
/// Manages overall progress, active file rows, small-file grouping, and completion summary.
/// All three operation types report through <see cref="OperationProgressReport"/> for unified display.
/// </summary>
public partial class ProgressTabViewModel : ViewModelBase
{
    private readonly Stopwatch _elapsed = new();
    private DateTime _lastSpeedUpdate;

    // Windowed speed tracking — keeps samples from the last 10 seconds.
    // Speed is computed from (newest - oldest) bytes over the window span,
    // which adapts smoothly to phase transitions (e.g., small → large files).
    private readonly Queue<(long elapsedMs, long bytes)> _speedSamples = new();
    private const int SpeedWindowMs = 10_000;

    // ── Overall progress ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverallProgressPercent))]
    [NotifyPropertyChangedFor(nameof(BytesProgressText))]
    private long _totalBytesProcessed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverallProgressPercent))]
    [NotifyPropertyChangedFor(nameof(BytesProgressText))]
    private long _totalBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilesProgressText))]
    private int _completedFilesCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilesProgressText))]
    private int _totalFilesCount;

    [ObservableProperty]
    private string _operationType = string.Empty;

    [ObservableProperty]
    private string _phaseDescription = string.Empty;

    [ObservableProperty]
    private string _operationSpeed = string.Empty;

    [ObservableProperty]
    private string _estimatedTimeRemaining = string.Empty;

    [ObservableProperty]
    private string _elapsedTimeText = string.Empty;

    // ── State flags ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowActiveProgress))]
    [NotifyPropertyChangedFor(nameof(ShowCompletionSummary))]
    private bool _isOperationActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowActiveProgress))]
    [NotifyPropertyChangedFor(nameof(ShowCompletionSummary))]
    private bool _isCompleted;

    /// <summary>True when the operation is running (show progress bars).</summary>
    public bool ShowActiveProgress => IsOperationActive && !IsCompleted;

    /// <summary>True when the operation finished (show summary + Ok button).</summary>
    public bool ShowCompletionSummary => IsCompleted;

    // ── Completion summary ──

    [ObservableProperty]
    private string _completionSummary = string.Empty;

    [ObservableProperty]
    private string _completionElapsed = string.Empty;

    [ObservableProperty]
    private string _completionBytes = string.Empty;

    [ObservableProperty]
    private int _completionSucceeded;

    [ObservableProperty]
    private int _completionFailed;

    [ObservableProperty]
    private int _completionCorruptedRecovery;

    [ObservableProperty]
    private bool _hasFailures;

    // ── Small file group ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSmallFileGroup))]
    private int _smallFilesTotal;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SmallFileGroupPercent))]
    [NotifyPropertyChangedFor(nameof(SmallFileGroupText))]
    private int _smallFilesCompleted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SmallFileGroupPercent))]
    [NotifyPropertyChangedFor(nameof(SmallFileGroupBytesText))]
    private long _smallFilesBytesProcessed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SmallFileGroupPercent))]
    [NotifyPropertyChangedFor(nameof(SmallFileGroupBytesText))]
    private long _smallFilesTotalBytes;

    public bool ShowSmallFileGroup => SmallFilesTotal > 0;

    public double SmallFileGroupPercent => SmallFilesTotalBytes > 0
        ? (double)SmallFilesBytesProcessed / SmallFilesTotalBytes * 100
        : 0;

    public string SmallFileGroupText => SmallFilesTotal > 0
        ? $"Small files (≤100 MB)    {SmallFilesCompleted:N0} / {SmallFilesTotal:N0} files"
        : string.Empty;

    public string SmallFileGroupBytesText => SmallFilesTotalBytes > 0
        ? $"{FormatHelper.FormatBytes(SmallFilesBytesProcessed)} / {FormatHelper.FormatBytes(SmallFilesTotalBytes)}"
        : string.Empty;

    // ── Derived properties ──

    public double OverallProgressPercent => TotalBytes > 0
        ? (double)TotalBytesProcessed / TotalBytes * 100
        : 0;

    public string BytesProgressText => TotalBytes > 0
        ? $"{FormatHelper.FormatBytes(TotalBytesProcessed)} / {FormatHelper.FormatBytes(TotalBytes)}"
        : string.Empty;

    public string FilesProgressText => TotalFilesCount > 0
        ? $"{CompletedFilesCount:N0} / {TotalFilesCount:N0} files"
        : string.Empty;

    // ── Active file rows ──

    /// <summary>
    /// Currently active (in-flight) file rows. Files appear when started,
    /// disappear on success, persist on failure. Thread-safe via UI thread dispatch.
    /// </summary>
    public ObservableCollection<ActiveFileProgressViewModel> ActiveFiles { get; } = [];

    /// <summary>
    /// Failed file rows persisted after the operation for user review.
    /// </summary>
    public ObservableCollection<ActiveFileProgressViewModel> FailedFiles { get; } = [];

    public bool HasActiveFiles => ActiveFiles.Count > 0 || ShowSmallFileGroup;

    // ── Events ──

    /// <summary>Raised when the user clicks Ok on the completion summary.</summary>
    public event EventHandler? CompletionAcknowledged;

    /// <summary>Raised when the user clicks Cancel.</summary>
    public event EventHandler? CancelRequested;

    // ── Commands ──

    [RelayCommand]
    private void AcknowledgeCompletion()
    {
        CompletionAcknowledged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void RequestCancel()
    {
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }

    // ── Public API ──

    /// <summary>
    /// Initializes the progress tab for a new operation.
    /// </summary>
    public void StartOperation(string operationType, int totalFiles, long totalBytes, int smallFileCount, long smallFileTotalBytes)
    {
        Dispatcher.UIThread.Post(() =>
        {
            OperationType = operationType;
            TotalFilesCount = totalFiles;
            TotalBytes = totalBytes;
            TotalBytesProcessed = 0;
            CompletedFilesCount = 0;
            PhaseDescription = string.Empty;
            OperationSpeed = string.Empty;
            EstimatedTimeRemaining = string.Empty;
            ElapsedTimeText = string.Empty;
            CompletionSummary = string.Empty;
            IsOperationActive = true;
            IsCompleted = false;
            HasFailures = false;
            SmallFilesTotal = smallFileCount;
            SmallFilesCompleted = 0;
            SmallFilesBytesProcessed = 0;
            SmallFilesTotalBytes = smallFileTotalBytes;
            ActiveFiles.Clear();
            FailedFiles.Clear();

            OnPropertyChanged(nameof(HasActiveFiles));
            OnPropertyChanged(nameof(ShowSmallFileGroup));
        });

        _elapsed.Restart();
        _lastSpeedUpdate = DateTime.Now;
        _speedSamples.Clear();
    }

    /// <summary>
    /// Processes a unified progress report from any operation type.
    /// Dispatches to the UI thread for ObservableCollection updates.
    /// </summary>
    public void ReportProgress(OperationProgressReport report)
    {
        Dispatcher.UIThread.Post(() => ApplyReport(report));
    }

    /// <summary>
    /// Marks the operation as complete and shows the summary.
    /// </summary>
    public void CompleteOperation(int succeeded, int failed, int corruptedRecovery, long totalBytesTransferred)
    {
        _elapsed.Stop();

        Dispatcher.UIThread.Post(() =>
        {
            IsOperationActive = false;
            IsCompleted = true;

            CompletionSucceeded = succeeded;
            CompletionFailed = failed;
            CompletionCorruptedRecovery = corruptedRecovery;
            HasFailures = failed > 0 || corruptedRecovery > 0;
            CompletionElapsed = FormatHelper.FormatDuration(_elapsed.Elapsed.TotalSeconds);
            CompletionBytes = FormatHelper.FormatBytes(totalBytesTransferred);

            var parts = new System.Collections.Generic.List<string> { $"{succeeded:N0} succeeded" };
            if (corruptedRecovery > 0) parts.Add($"{corruptedRecovery} recovered to __corrupted__");
            if (failed > 0) parts.Add($"{failed} failed");
            CompletionSummary = $"{OperationType} complete: {string.Join(", ", parts)}";

            // Move any remaining active files to failed
            foreach (var active in ActiveFiles.ToList())
            {
                if (active.Status is FileOperationStatus.Failed)
                {
                    FailedFiles.Add(active);
                }
            }
            ActiveFiles.Clear();
            OnPropertyChanged(nameof(HasActiveFiles));
        });
    }

    /// <summary>
    /// Marks the operation as cancelled and shows a cancellation summary.
    /// Call from catch(OperationCanceledException) blocks.
    /// </summary>
    public void MarkCancelled()
    {
        _elapsed.Stop();

        Dispatcher.UIThread.Post(() =>
        {
            IsOperationActive = false;
            IsCompleted = true;

            CompletionSucceeded = CompletedFilesCount;
            CompletionFailed = 0;
            CompletionCorruptedRecovery = 0;
            HasFailures = false;
            CompletionElapsed = FormatHelper.FormatDuration(_elapsed.Elapsed.TotalSeconds);
            CompletionBytes = FormatHelper.FormatBytes(TotalBytesProcessed);
            CompletionSummary = $"{OperationType} cancelled after {CompletedFilesCount:N0} file(s)";

            ActiveFiles.Clear();
            FailedFiles.Clear();
            OnPropertyChanged(nameof(HasActiveFiles));
        });
    }

    // ── Private helpers ──

    private void ApplyReport(OperationProgressReport report)
    {
        // Update overall counters (carried on every event)
        TotalBytesProcessed = report.TotalBytesProcessed;
        CompletedFilesCount = report.TotalFilesCompleted;

        switch (report.Type)
        {
            case OperationProgressType.FileStarted:
                var newFile = new ActiveFileProgressViewModel
                {
                    FileIndex = report.FileIndex,
                    FileName = report.FileName,
                    TotalBytes = report.FileSize,
                    BytesProcessed = 0,
                    Status = report.FileStatus
                };
                ActiveFiles.Add(newFile);
                OnPropertyChanged(nameof(HasActiveFiles));
                break;

            case OperationProgressType.FileProgress:
                var active = FindActiveFile(report.FileIndex);
                if (active != null)
                {
                    active.BytesProcessed = report.FileBytesProcessed;
                    active.Status = report.FileStatus;
                }
                break;

            case OperationProgressType.FileCompleted:
                var completed = FindActiveFile(report.FileIndex);
                if (completed != null)
                {
                    ActiveFiles.Remove(completed);
                    OnPropertyChanged(nameof(HasActiveFiles));
                }
                break;

            case OperationProgressType.FileFailed:
                var failed = FindActiveFile(report.FileIndex);
                if (failed != null)
                {
                    failed.Status = FileOperationStatus.Failed;
                    failed.ErrorMessage = report.ErrorMessage ?? "Unknown error";
                    // Keep in ActiveFiles — failed files persist
                }
                break;

            case OperationProgressType.SmallFileGroupProgress:
                SmallFilesCompleted = report.SmallFilesCompleted;
                SmallFilesBytesProcessed = report.SmallFilesBytesProcessed;
                break;

            case OperationProgressType.PhaseChanged:
                PhaseDescription = report.PhaseDescription ?? string.Empty;
                break;

            case OperationProgressType.OperationCompleted:
                // Handled by CompleteOperation call from the command
                break;
        }

        UpdateSpeedAndEta();
        UpdateElapsedTime();
    }

    private ActiveFileProgressViewModel? FindActiveFile(int fileIndex)
    {
        for (var i = 0; i < ActiveFiles.Count; i++)
        {
            if (ActiveFiles[i].FileIndex == fileIndex)
                return ActiveFiles[i];
        }
        return null;
    }

    private void UpdateSpeedAndEta()
    {
        var now = DateTime.Now;
        if ((now - _lastSpeedUpdate).TotalMilliseconds < 500)
            return;

        _lastSpeedUpdate = now;
        var elapsedMs = _elapsed.ElapsedMilliseconds;

        if (elapsedMs < 1000 || TotalBytesProcessed <= 0)
            return;

        // Add current sample and evict samples outside the window
        _speedSamples.Enqueue((elapsedMs, TotalBytesProcessed));
        var windowStart = elapsedMs - SpeedWindowMs;
        while (_speedSamples.Count > 2 && _speedSamples.Peek().elapsedMs < windowStart)
            _speedSamples.Dequeue();

        // Compute speed from window edges (oldest retained sample → newest)
        var oldest = _speedSamples.Peek();
        var spanMs = elapsedMs - oldest.elapsedMs;
        var spanBytes = TotalBytesProcessed - oldest.bytes;

        // Fall back to overall average if window is too short (first few seconds)
        double bytesPerSecond;
        if (spanMs >= 2000 && spanBytes > 0)
        {
            bytesPerSecond = (double)spanBytes / spanMs * 1000;
        }
        else
        {
            bytesPerSecond = (double)TotalBytesProcessed / elapsedMs * 1000;
        }

        OperationSpeed = $"{FormatHelper.FormatBytes((long)bytesPerSecond)}/s";

        if (bytesPerSecond > 0 && TotalBytes > TotalBytesProcessed)
        {
            var remainingBytes = TotalBytes - TotalBytesProcessed;
            var remainingSeconds = remainingBytes / bytesPerSecond;
            EstimatedTimeRemaining = $"{FormatHelper.FormatDuration(remainingSeconds)} remaining";
        }
        else
        {
            EstimatedTimeRemaining = string.Empty;
        }
    }

    private void UpdateElapsedTime()
    {
        if (_elapsed.IsRunning)
        {
            ElapsedTimeText = FormatHelper.FormatDuration(_elapsed.Elapsed.TotalSeconds);
        }
    }
}
